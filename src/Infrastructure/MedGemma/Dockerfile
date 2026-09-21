# Pinned, not `:latest`. The weights are baked into this image and CI deploys it by immutable
# tag, so the running service never drifts — but a rebuild would have, silently, because the base
# floated: the same Dockerfile built twice could ship two different Ollama versions. The digest is
# what pins it; the version tag beside it is there so a reader knows which version that is.
# 0.34.2 is also the first version this repo can state carries the CVE-2026-85180 redirect fix
# (research/queue/2026-09-20-ollama-ssrf-status-conflict.md) — bump both parts together.
FROM ollama/ollama:0.34.2@sha256:da6e0dc5651df159e45686fd663c4dbe1624a52c44d7280eeac1551d8f865532 AS builder
ARG MODEL_TAG
ARG MODEL_DIGEST
# This image once carried a second, non-medical tag for the rewrite slot. That slot runs on
# Gemini via Vertex AI now (DPIA row A20, decision D6), so the tag was multi-GB of model blobs
# baked into every MedGemma build for a host nothing calls. The host itself is torn down
# separately — nothing here depends on when that lands.
# `ollama pull` exiting 0 only means the transfer succeeded — it does not guarantee the
# server will resolve a tag at generate time (hf.co references have silently 404'd in prod
# despite a clean pull, per the incident on 2026-08-10). Assert each tag is actually queryable
# before shipping the image, so a resolution mismatch fails the build instead of every runtime
# call.
RUN test -n "${MODEL_TAG}" || { echo "FATAL: MODEL_TAG build arg is empty — pass --build-arg MODEL_TAG=<tag>." >&2; exit 1; }
RUN test -n "${MODEL_DIGEST}" || { echo "FATAL: MODEL_DIGEST build arg is empty — pass --build-arg MODEL_DIGEST=\$(cat src/Infrastructure/MedGemma/.model-digest)." >&2; exit 1; }
RUN ollama serve & SERVER_PID=$! && \
    sleep 5 && \
    for TAG in "${MODEL_TAG}"; do \
      ollama pull "${TAG}" && \
      ollama list | awk -v tag="${TAG}" 'NR>1 && $1==tag {found=1} END {exit !found}' || \
        { echo "FATAL: '${TAG}' was pulled but 'ollama list' does not show it under that exact name — generate/chat calls would 404 against it. Check case and tag format against what Ollama actually registered:" >&2; ollama list >&2; exit 1; }; \
    done && \
    kill $SERVER_PID

# The weights are the product of a *mutable* tag. Neither registry Ollama pulls from serves a
# manifest by digest, and `ollama pull` rejects a `model@sha256:...` reference outright ("invalid
# model name"), so the tag cannot be pinned at pull time the way a container base image can.
# What can be pinned is the result: Ollama stores the manifest it pulled verbatim, so its sha256
# is a stable identity for "these exact weights, template and params". Assert it here and upstream
# drift fails the build instead of shipping — a rebuild either reproduces today's model or stops.
# The manifest lists every layer by digest, so this one hash covers the weights, the template and
# the sampler params together; a tag repointed at a different quantisation, a re-uploaded GGUF or
# a changed default temperature all move it.
RUN set -e; \
    MANIFEST="$(find /root/.ollama/models/manifests -type f)"; \
    COUNT="$(find /root/.ollama/models/manifests -type f | wc -l)"; \
    if [ "${COUNT}" -ne 1 ]; then \
      echo "FATAL: expected exactly one model manifest under /root/.ollama/models/manifests, found ${COUNT}:" >&2; \
      echo "${MANIFEST}" >&2; \
      exit 1; \
    fi; \
    ACTUAL="$(sha256sum "${MANIFEST}" | cut -d' ' -f1)"; \
    if [ "${ACTUAL}" != "${MODEL_DIGEST}" ]; then \
      echo "FATAL: '${MODEL_TAG}' no longer resolves to the recorded weights." >&2; \
      echo "  expected (src/Infrastructure/MedGemma/.model-digest): ${MODEL_DIGEST}" >&2; \
      echo "  actual   (pulled just now):                           ${ACTUAL}" >&2; \
      echo "The upstream tag moved. Review what changed before shipping it: if the new weights are" >&2; \
      echo "wanted, put the actual value in .model-digest and say why in the commit; if not, this" >&2; \
      echo "build has just stopped an unreviewed model reaching caregivers." >&2; \
      exit 1; \
    fi; \
    echo "Model manifest digest verified: ${ACTUAL}"

FROM ollama/ollama:0.34.2@sha256:da6e0dc5651df159e45686fd663c4dbe1624a52c44d7280eeac1551d8f865532
COPY --from=builder /root/.ollama /root/.ollama
# Cloud Run requires port 8080; Ollama defaults to 11434
ENV OLLAMA_HOST=0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["ollama", "serve"]
