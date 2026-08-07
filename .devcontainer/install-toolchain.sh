#!/usr/bin/env bash
# Installs the CardiTrack build toolchain onto a Debian/Ubuntu container.
#
# Single source of truth for tool versions, shared by:
#   - .devcontainer/Dockerfile        (baked into the dev container image)
#   - .devcontainer/bootstrap.sh      (run at session start in cloud containers
#                                      whose base image we don't control)
#
# Idempotent: every step short-circuits when the tool is already present at the
# wanted version, so re-running it on a warm container costs a few seconds.
#
# Network note: the .NET SDK comes from the Ubuntu archive rather than
# packages.microsoft.com / builds.dotnet.microsoft.com, because restricted
# egress policies (Claude Code cloud containers, locked-down CI) commonly allow
# the distro mirrors while blocking Microsoft's download hosts. The Microsoft
# installer is kept as a fallback for environments where it is reachable.
#
# Usage:
#   sudo ./install-toolchain.sh            # core toolchain
#   INSTALL_GCLOUD=1 ./install-toolchain.sh  # also install the gcloud CLI
set -euo pipefail

# ── Version pins ─────────────────────────────────────────────────────────────
# Keep DOTNET_MAJOR/TERRAFORM_VERSION in step with .github/workflows/_env.yml
# (dotnet_version: 10.0.x, tf_version: 1.14.x) and infrastructure/versions.tf
# (required_version >= 1.14.7).
DOTNET_MAJOR="${DOTNET_MAJOR:-10.0}"
TERRAFORM_VERSION="${TERRAFORM_VERSION:-1.14.9}"
DOTNET_EF_VERSION="${DOTNET_EF_VERSION:-10.0.*}"

# Set to 1 to additionally install the Google Cloud CLI. Off by default: its
# apt host (packages.cloud.google.com) is unreachable from restricted networks,
# and only deployment work needs it.
INSTALL_GCLOUD="${INSTALL_GCLOUD:-0}"

# Set to 1 to additionally install the MAUI Android layer (maui-android
# workload, JDK, Android SDK) so CardiTrack.Mobile builds. Off by default: it
# adds several GB, and the Android SDK download host (dl.google.com) is
# unreachable from restricted networks. CI builds mobile on dedicated runners.
INSTALL_MAUI="${INSTALL_MAUI:-0}"
ANDROID_SDK_ROOT_DIR="${ANDROID_SDK_ROOT_DIR:-${HOME}/Android/Sdk}"

TOOLS_PREFIX="${TOOLS_PREFIX:-/usr/local/bin}"
REPO_ROOT="${REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

# Temp dirs are cleaned up once, on exit — a RETURN trap would stay registered
# and re-fire on every later function return.
_scratch_dirs=()
cleanup() { [ "${#_scratch_dirs[@]}" -gt 0 ] && rm -rf "${_scratch_dirs[@]}"; return 0; }
trap cleanup EXIT

log()  { printf '\033[0;36m[toolchain]\033[0m %s\n' "$*"; }
warn() { printf '\033[0;33m[toolchain] WARN:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[0;31m[toolchain] ERROR:\033[0m %s\n' "$*" >&2; exit 1; }

# Re-exec through sudo only when we are not already root and sudo exists.
as_root() {
  if [ "$(id -u)" -eq 0 ]; then
    "$@"
  elif command -v sudo >/dev/null 2>&1; then
    sudo "$@"
  else
    die "need root (or sudo) to run: $*"
  fi
}

APT_UPDATED=0
apt_update_once() {
  [ "$APT_UPDATED" -eq 1 ] && return 0
  log "Refreshing apt indexes"
  # Third-party PPAs baked into some base images may be blocked by the network
  # policy. Their failures are warnings, not errors, so tolerate a non-zero exit
  # here and let the actual install step fail loudly if a package is missing.
  as_root env DEBIAN_FRONTEND=noninteractive apt-get update -qq || \
    warn "apt-get update reported errors (unreachable third-party repos are expected); continuing"
  APT_UPDATED=1
}

apt_install() {
  apt_update_once
  as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "$@"
}

# ── Base utilities ───────────────────────────────────────────────────────────
install_base_packages() {
  local missing=()
  command -v curl    >/dev/null 2>&1 || missing+=(curl)
  command -v unzip   >/dev/null 2>&1 || missing+=(unzip)
  command -v git     >/dev/null 2>&1 || missing+=(git)
  command -v gpg     >/dev/null 2>&1 || missing+=(gnupg)
  command -v psql    >/dev/null 2>&1 || missing+=(postgresql-client)
  command -v jq      >/dev/null 2>&1 || missing+=(jq)
  command -v openssl >/dev/null 2>&1 || missing+=(openssl)

  if [ "${#missing[@]}" -eq 0 ]; then
    log "Base utilities already present"
    return 0
  fi
  log "Installing base utilities: ${missing[*]}"
  apt_install ca-certificates "${missing[@]}"
}

# ── .NET SDK ─────────────────────────────────────────────────────────────────
dotnet_major_installed() {
  command -v dotnet >/dev/null 2>&1 || return 1
  dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_MAJOR}\."
}

install_dotnet() {
  if dotnet_major_installed; then
    log ".NET SDK ${DOTNET_MAJOR} already installed ($(dotnet --version))"
    return 0
  fi

  log "Installing .NET SDK ${DOTNET_MAJOR} from the Ubuntu archive"
  if apt_install "dotnet-sdk-${DOTNET_MAJOR}"; then
    dotnet_major_installed && { log ".NET SDK $(dotnet --version) installed"; return 0; }
  fi

  warn "Ubuntu archive install failed; falling back to dotnet-install.sh"
  local script; script="$(mktemp)"
  if curl -fsSL --retry 3 --max-time 120 https://dot.net/v1/dotnet-install.sh -o "$script"; then
    as_root bash "$script" --channel "${DOTNET_MAJOR}" --install-dir /usr/share/dotnet
    as_root ln -sf /usr/share/dotnet/dotnet "${TOOLS_PREFIX}/dotnet"
    rm -f "$script"
  else
    rm -f "$script"
    die ".NET SDK ${DOTNET_MAJOR} unavailable: the Ubuntu archive install failed and dot.net is unreachable"
  fi

  dotnet_major_installed || die ".NET SDK ${DOTNET_MAJOR} still not on PATH after install"
  log ".NET SDK $(dotnet --version) installed"
}

# ── dotnet-ef (EF Core migrations) ───────────────────────────────────────────
install_dotnet_ef() {
  local tools_dir="${HOME}/.dotnet/tools"
  export PATH="${PATH}:${tools_dir}"

  if command -v dotnet-ef >/dev/null 2>&1; then
    log "dotnet-ef already installed ($(dotnet-ef --version 2>/dev/null | tail -1))"
    return 0
  fi

  log "Installing dotnet-ef ${DOTNET_EF_VERSION}"
  # NuGet may be unreachable on restricted networks; migrations tooling is a
  # convenience, so warn rather than fail the whole bootstrap.
  if ! dotnet tool install --global dotnet-ef --version "${DOTNET_EF_VERSION}" 2>&1; then
    warn "dotnet-ef install failed (NuGet unreachable?); run 'dotnet tool install --global dotnet-ef' later"
    return 0
  fi
  log "dotnet-ef installed to ${tools_dir}"
}

# ── Terraform ────────────────────────────────────────────────────────────────
install_terraform() {
  if command -v terraform >/dev/null 2>&1 && \
     [ "$(terraform version -json 2>/dev/null | jq -r '.terraform_version' 2>/dev/null)" = "${TERRAFORM_VERSION}" ]; then
    log "Terraform ${TERRAFORM_VERSION} already installed"
    return 0
  fi

  local arch
  case "$(dpkg --print-architecture)" in
    amd64) arch=amd64 ;;
    arm64) arch=arm64 ;;
    *)     die "unsupported architecture for Terraform: $(dpkg --print-architecture)" ;;
  esac

  local base="https://releases.hashicorp.com/terraform/${TERRAFORM_VERSION}"
  local zip="terraform_${TERRAFORM_VERSION}_linux_${arch}.zip"
  local tmp; tmp="$(mktemp -d)"
  _scratch_dirs+=("$tmp")

  log "Downloading Terraform ${TERRAFORM_VERSION} (linux/${arch})"
  curl -fsSL --retry 3 --max-time 180 "${base}/${zip}" -o "${tmp}/${zip}" \
    || die "failed to download ${base}/${zip}"
  curl -fsSL --retry 3 --max-time 60 "${base}/terraform_${TERRAFORM_VERSION}_SHA256SUMS" -o "${tmp}/SHA256SUMS" \
    || die "failed to download Terraform checksums"

  log "Verifying checksum"
  ( cd "$tmp" && grep " ${zip}\$" SHA256SUMS | sha256sum -c - ) \
    || die "Terraform checksum verification failed for ${zip}"

  unzip -oq "${tmp}/${zip}" -d "$tmp"
  as_root install -m 0755 "${tmp}/terraform" "${TOOLS_PREFIX}/terraform"
  log "Terraform $(terraform version | head -1 | awk '{print $2}') installed to ${TOOLS_PREFIX}"
}

# ── MAUI Android layer (optional) ────────────────────────────────────────────
install_maui() {
  if ! java -version >/dev/null 2>&1; then
    log "Installing OpenJDK 21 (required by the Android build tooling)"
    apt_install openjdk-21-jdk-headless
  fi

  if dotnet workload list 2>/dev/null | grep -q '^maui-android'; then
    log "maui-android workload already installed"
  else
    log "Installing the maui-android workload (several GB — this takes a while)"
    dotnet workload install maui-android --skip-manifest-update \
      || die "maui-android workload install failed"
  fi

  if [ -d "${ANDROID_SDK_ROOT_DIR}/platforms" ]; then
    log "Android SDK already present at ${ANDROID_SDK_ROOT_DIR}"
    return 0
  fi

  # Pulls platform/build-tools from dl.google.com. Restricted networks block it,
  # and the workload alone is still enough for restore and IDE analysis, so a
  # failure here is a warning.
  log "Installing the Android SDK into ${ANDROID_SDK_ROOT_DIR}"
  local mobile_proj="${REPO_ROOT}/src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj"
  if dotnet build "$mobile_proj" -f net10.0-android -t:InstallAndroidDependencies \
       -p:AndroidSdkDirectory="${ANDROID_SDK_ROOT_DIR}" \
       -p:AcceptAndroidSDKLicenses=True --nologo 2>&1 | tail -5; then
    log "Android SDK installed"
  else
    warn "Android SDK install failed (dl.google.com unreachable?); CardiTrack.Mobile will restore but not build"
  fi
}

# ── Google Cloud CLI (optional) ──────────────────────────────────────────────
install_gcloud() {
  if command -v gcloud >/dev/null 2>&1; then
    log "gcloud already installed ($(gcloud version 2>/dev/null | head -1))"
    return 0
  fi

  log "Installing the Google Cloud CLI"
  local keyring=/usr/share/keyrings/cloud.google.gpg
  if ! curl -fsSL --retry 2 --max-time 60 https://packages.cloud.google.com/apt/doc/apt-key.gpg -o /tmp/gcloud.key; then
    warn "packages.cloud.google.com unreachable; skipping gcloud (deployment runs in CI, not here)"
    return 0
  fi
  as_root gpg --dearmor --yes -o "$keyring" /tmp/gcloud.key
  rm -f /tmp/gcloud.key
  echo "deb [signed-by=${keyring}] https://packages.cloud.google.com/apt cloud-sdk main" \
    | as_root tee /etc/apt/sources.list.d/google-cloud-sdk.list >/dev/null
  APT_UPDATED=0  # new repo added — force a refresh
  apt_install google-cloud-cli || warn "gcloud install failed; skipping"
}

main() {
  log "CardiTrack toolchain — .NET ${DOTNET_MAJOR}, Terraform ${TERRAFORM_VERSION}"
  install_base_packages
  install_dotnet
  install_dotnet_ef
  install_terraform
  if [ "$INSTALL_GCLOUD" = "1" ]; then
    install_gcloud
  fi

  log "Done."
  log "  dotnet    $(dotnet --version 2>/dev/null || echo 'MISSING')"
  log "  terraform $(terraform version 2>/dev/null | head -1 | awk '{print $2}' || echo 'MISSING')"
}

main "$@"
