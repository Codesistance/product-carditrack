#!/usr/bin/env bash
# Fetch the MedGemma weights this platform serves, and refuse anything but the pinned bytes.
#
# The image build used to `ollama pull` a Hugging Face tag at build time. That stopped working
# when Ollama shipped its CVE-2026-85180 fix — the tag redirects blob downloads to a different
# host, and a patched Ollama refuses the redirect — and it was never a pin anyway: a mutable
# tag, a third party's upload. So the weights are fetched once, checked against the hashes in
# src/Infrastructure/MedGemma/weights.sha256, and kept in our own bucket. This script is the
# fetch; the vendoring workflow runs it and uploads, and a developer runs it before
# `docker compose --profile full up`.
#
# Plain curl follows the redirect that Ollama's pull refuses. The hash check is what makes that
# safe: a redirect to the wrong place yields the wrong bytes, and the wrong bytes do not pass.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
model_dir="$here/src/Infrastructure/MedGemma"
out_dir="${1:-$model_dir/weights}"

# shellcheck source=/dev/null
. "$model_dir/weights.env"
: "${HF_REPO:?weights.env must set HF_REPO}" "${HF_REVISION:?weights.env must set HF_REVISION}"

mkdir -p "$out_dir"

# One file per line of weights.sha256, so a file can only be fetched if it is also pinned.
while read -r expected name; do
  [ -n "$name" ] || continue
  target="$out_dir/$name"
  if [ -f "$target" ] && printf '%s  %s\n' "$expected" "$target" | sha256sum -c --status; then
    echo "present, hash ok: $name"
    continue
  fi
  url="https://huggingface.co/$HF_REPO/resolve/$HF_REVISION/$name"
  echo "fetching $name @ ${HF_REVISION:0:12}"
  # --fail-with-body surfaces HF's error text on a 4xx instead of saving it as the model.
  curl --fail-with-body --location --retry 3 --retry-delay 5 --progress-bar \
    --output "$target.part" "$url"
  mv "$target.part" "$target"
done < "$model_dir/weights.sha256"

# The whole manifest, in one place, once — the same check the image build repeats.
( cd "$out_dir" && sha256sum -c "$model_dir/weights.sha256" )
echo "weights verified in $out_dir"
