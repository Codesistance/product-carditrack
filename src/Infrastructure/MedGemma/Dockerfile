FROM ollama/ollama:latest AS builder
ARG MODEL_TAG
# Plain, non-medical model used to rewrite MedGemma's clinical output into caregiver-facing
# language (docs/llm_design.md), and to run member chat's malicious-check and query-plan steps.
# Still built into this one image alongside MODEL_TAG — Ollama already serves multiple resident
# models from one process, selected per-request by the `model` field — but as of the Rewrite
# Cloud Run split (member-chat planning notes, 2026-08-20) it is deployed onto its OWN service
# instance (see infrastructure/deployments/cloud_run.tf's "rewrite" resource), not the MedGemma
# one: co-locating both models' inference on one Cloud Run instance was what let a Rewrite call
# contend with MedGemma for the same saturated CPU. The instance running MODEL_TAG never receives
# a REWRITE_MODEL_TAG request in practice and vice versa; both tags stay in one image for now to
# avoid a second build pipeline, not because they still share compute.
ARG REWRITE_MODEL_TAG
# `ollama pull` exiting 0 only means the transfer succeeded — it does not guarantee the
# server will resolve a tag at generate time (hf.co references have silently 404'd in prod
# despite a clean pull, per the incident on 2026-08-10). Assert each tag is actually queryable
# before shipping the image, so a resolution mismatch fails the build instead of every runtime
# call. Both tags are pulled against the same server process rather than one each, since a
# second `ollama serve` start/stop buys nothing.
RUN test -n "${MODEL_TAG}" || { echo "FATAL: MODEL_TAG build arg is empty — pass --build-arg MODEL_TAG=<tag>." >&2; exit 1; }
RUN test -n "${REWRITE_MODEL_TAG}" || { echo "FATAL: REWRITE_MODEL_TAG build arg is empty — pass --build-arg REWRITE_MODEL_TAG=<tag>." >&2; exit 1; }
RUN ollama serve & SERVER_PID=$! && \
    sleep 5 && \
    for TAG in "${MODEL_TAG}" "${REWRITE_MODEL_TAG}"; do \
      ollama pull "${TAG}" && \
      ollama list | awk -v tag="${TAG}" 'NR>1 && $1==tag {found=1} END {exit !found}' || \
        { echo "FATAL: '${TAG}' was pulled but 'ollama list' does not show it under that exact name — generate/chat calls would 404 against it. Check case and tag format against what Ollama actually registered:" >&2; ollama list >&2; exit 1; }; \
    done && \
    kill $SERVER_PID

FROM ollama/ollama:latest
COPY --from=builder /root/.ollama /root/.ollama
# Cloud Run requires port 8080; Ollama defaults to 11434
ENV OLLAMA_HOST=0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["ollama", "serve"]
