FROM ollama/ollama:latest AS builder
ARG MODEL_TAG
# This image once carried a second, non-medical tag for the rewrite slot. That slot runs on
# Gemini via Vertex AI now (DPIA row A20, decision D6) and its Cloud Run service is gone, so the
# tag was multi-GB of model blobs baked into every MedGemma build for a host nothing called.
# `ollama pull` exiting 0 only means the transfer succeeded — it does not guarantee the
# server will resolve a tag at generate time (hf.co references have silently 404'd in prod
# despite a clean pull, per the incident on 2026-08-10). Assert each tag is actually queryable
# before shipping the image, so a resolution mismatch fails the build instead of every runtime
# call.
RUN test -n "${MODEL_TAG}" || { echo "FATAL: MODEL_TAG build arg is empty — pass --build-arg MODEL_TAG=<tag>." >&2; exit 1; }
RUN ollama serve & SERVER_PID=$! && \
    sleep 5 && \
    for TAG in "${MODEL_TAG}"; do \
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
