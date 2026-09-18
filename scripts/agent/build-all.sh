#!/usr/bin/env bash
# Build everything, from wherever this runs.
#
#   1. The server solution filter (API, Web, Worker, pipeline hosts, tests) — Release,
#      the configuration CI uses. Warnings are counted: a warning-free build is the
#      enforced lint gate (AGENTS.md).
#   2. CardiTrack.Mobile for Android, locally, when the maui-android workload and an
#      Android SDK are present (INSTALL_MAUI=1 ./.devcontainer/install-toolchain.sh).
#      Release with the trimmer, AOT and R8 off: the same compile-only recipe the old
#      PR job used, because XamlC only reports on Release.
#   3. The platforms this machine cannot build — iOS always, Android too when step 2
#      was skipped — through CI: dispatch CI / Deploy Apps → Dev on the current branch
#      with only the mobile ticks on, and wait for it. A branch dispatch runs the
#      unsigned compile gates (Release Android, Debug iOS simulator) and ships
#      nothing; signed builds, the archive and the tag are main-only. Needs
#      `gh` with a GH_TOKEN that can dispatch workflows (fine-grained PAT: Actions
#      read and write, Contents read — see AGENTS.md), and the branch pushed.
#
# Usage:
#   scripts/agent/build-all.sh                # all three
#   scripts/agent/build-all.sh --no-ci        # local only
#   scripts/agent/build-all.sh --platform both   # force what CI builds (android|ios|both)
#
# Exit status is non-zero if any step that ran failed. Steps that could not run
# are reported as SKIPPED with the reason, never as passed.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

CI=1
PLATFORM=auto
while [ $# -gt 0 ]; do
  case "$1" in
    --no-ci) CI=0 ;;
    --platform) PLATFORM="$2"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

log()  { printf '\033[0;36m[build-all]\033[0m %s\n' "$*"; }
RESULTS=()
record() { RESULTS+=("$1: $2"); }
FAILED=0

MOBILE=src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj

# ── 1. Server ────────────────────────────────────────────────────────────────
log "Building CardiTrack.Server.slnf (Release)"
if OUT=$(dotnet build CardiTrack.Server.slnf -c Release --nologo 2>&1); then
  WARNINGS=$(printf '%s\n' "$OUT" | grep -c ': warning ' || true)
  if [ "$WARNINGS" -gt 0 ]; then
    printf '%s\n' "$OUT" | grep ': warning ' | sort -u | head -40
    record "server" "BUILT WITH $WARNINGS WARNING(S) — the lint gate is a warning-free build"
    FAILED=1
  else
    record "server" "OK (warning-free)"
  fi
else
  printf '%s\n' "$OUT" | tail -40
  record "server" "FAILED"
  FAILED=1
fi

# ── 2. Android, locally ──────────────────────────────────────────────────────
ANDROID_LOCAL=0
# Where an Android SDK may live: the env vars, the Linux default the toolchain
# script installs to, and the two usual Windows locations (the dev box).
android_sdk_present() {
  local d
  for d in "${ANDROID_SDK_ROOT_DIR:-}" "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" "$HOME/Android/Sdk" \
           "${LOCALAPPDATA:-}/Android/Sdk" "/c/Program Files (x86)/Android/android-sdk"; do
    [ -n "$d" ] && [ -d "$d/platforms" ] && return 0
  done
  return 1
}
# maui-android on Linux; on Windows the Visual Studio install lists the bundle as
# maui-windows (or plain maui), and both carry the Android target.
if dotnet workload list 2>/dev/null | grep -Eq '^maui(-android|-windows)?[[:space:]]' && android_sdk_present; then
  log "Building CardiTrack.Mobile for Android (Release, compile-only)"
  if dotnet build "$MOBILE" -f net10.0-android -c Release --nologo \
       -p:RunAOTCompilation=false -p:AndroidLinkMode=None -p:PublishTrimmed=false -p:AndroidLinkTool= 2>&1 | tail -15; then
    ANDROID_LOCAL=1
    record "android (local)" "OK"
  else
    record "android (local)" "FAILED"
    FAILED=1
  fi
else
  record "android (local)" "SKIPPED — maui-android workload or Android SDK missing; run INSTALL_MAUI=1 ./.devcontainer/install-toolchain.sh"
fi

# ── 3. iOS (and Android if not built above), through CI ──────────────────────
if [ "$CI" -eq 1 ]; then
  if [ "$PLATFORM" = "auto" ]; then
    if [ "$ANDROID_LOCAL" -eq 1 ]; then PLATFORM=ios; else PLATFORM=both; fi
  fi
  BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
  REASON=""
  if ! command -v gh >/dev/null 2>&1; then REASON="gh is not installed"; fi
  if [ -z "$REASON" ] && ! gh auth status >/dev/null 2>&1; then REASON="gh is not authenticated (set GH_TOKEN to a fine-grained PAT with Actions: read and write, Contents: read)"; fi
  if [ -z "$REASON" ] && [ -z "$BRANCH" -o "$BRANCH" = "HEAD" ]; then REASON="detached HEAD — check out a branch"; fi
  HEAD_SHA=$(git rev-parse HEAD 2>/dev/null || echo "")
  # Ask origin itself rather than the local tracking ref, which is absent after a
  # push with no upstream and stale until the next fetch. CI builds what origin
  # has, so a local commit that is not there yet would be "verified" by a run
  # of something else.
  if [ -z "$REASON" ]; then
    REMOTE_SHA=$(git ls-remote --heads origin "$BRANCH" 2>/dev/null | cut -f1)
    if [ -z "$REMOTE_SHA" ]; then REASON="branch $BRANCH is not on origin — push it first"
    elif [ "$REMOTE_SHA" != "$HEAD_SHA" ]; then REASON="origin/$BRANCH is at ${REMOTE_SHA:0:8}, local HEAD is ${HEAD_SHA:0:8} — push first"; fi
  fi
  if [ -z "$REASON" ] && [ -n "$(git status --porcelain)" ]; then log "note: working tree has uncommitted changes; CI builds what is pushed, not what is here"; fi
  if [ -n "$REASON" ]; then
    record "mobile ($PLATFORM, CI)" "SKIPPED — $REASON"
  else
    case "$PLATFORM" in
      ios)  CI_ANDROID=false ;;
      both) CI_ANDROID=true ;;
      android) CI_ANDROID=true ;;
      *) echo "unknown --platform $PLATFORM (android|ios|both)" >&2; exit 2 ;;
    esac
    CI_IOS=true; [ "$PLATFORM" = "android" ] && CI_IOS=false
    log "Dispatching CI / Deploy Apps → Dev on $BRANCH (mobile only: android=$CI_ANDROID ios=$CI_IOS)"
    SINCE=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    if gh workflow run deploy-apps-dev.yml --ref "$BRANCH" \
         -f api=false -f web=false -f worker=false -f pipeline=false -f webhook=false \
         -f "mobile_android=$CI_ANDROID" -f "mobile_ios=$CI_IOS" -f mobile_windows=false; then
      RUN_ID=""
      for _ in $(seq 1 30); do
        sleep 4
        # The run for *this* dispatch: same commit, created after it. The commit
        # check keeps another agent's dispatch of the same branch, or a run left
        # from before, from being mistaken for ours.
        RUN_ID=$(gh run list --workflow deploy-apps-dev.yml --branch "$BRANCH" --event workflow_dispatch --limit 10 \
                   --json databaseId,createdAt,headSha --jq "map(select(.headSha == \"$HEAD_SHA\" and .createdAt >= \"$SINCE\")) | .[0].databaseId // empty")
        [ -n "$RUN_ID" ] && break
      done
      if [ -z "$RUN_ID" ]; then
        record "mobile ($PLATFORM, CI)" "DISPATCHED but the run did not appear in 2 minutes — check gh run list --workflow deploy-apps-dev.yml"
        FAILED=1
      else
        log "Run $RUN_ID: $(gh run view "$RUN_ID" --json url --jq .url)"
        if gh run watch "$RUN_ID" --exit-status --interval 20 >/dev/null; then
          record "mobile ($PLATFORM, CI)" "OK — run $RUN_ID"
        else
          gh run view "$RUN_ID" --json jobs --jq '.jobs[] | "\(.conclusion // .status)\t\(.name)"'
          record "mobile ($PLATFORM, CI)" "FAILED — run $RUN_ID"
          FAILED=1
        fi
      fi
    else
      record "mobile ($PLATFORM, CI)" "FAILED — dispatch rejected (does GH_TOKEN have Actions: read and write on this repository?)"
      FAILED=1
    fi
  fi
else
  record "mobile (CI)" "SKIPPED — --no-ci"
fi

echo
log "Summary"
for r in "${RESULTS[@]}"; do printf '  %s\n' "$r"; done
exit $FAILED
