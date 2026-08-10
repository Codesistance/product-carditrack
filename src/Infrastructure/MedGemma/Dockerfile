FROM ollama/ollama:latest AS builder
ARG MODEL_TAG
# `ollama pull` exiting 0 only means the transfer succeeded — it does not guarantee the
# server will resolve MODEL_TAG at generate time (hf.co references have silently 404'd in
# prod despite a clean pull, per the incident on 2026-08-10). Assert the tag is actually
# queryable before shipping the image, so a resolution mismatch fails the build instead of
# every runtime call.
RUN ollama serve & SERVER_PID=$! && \
    sleep 5 && \
    ollama pull ${MODEL_TAG} && \
    ollama list | grep -qF "${MODEL_TAG}" || \
      { echo "FATAL: '${MODEL_TAG}' was pulled but 'ollama list' does not show it under that exact name — generate/chat calls would 404 against it. Check case and tag format against what Ollama actually registered:" >&2; ollama list >&2; exit 1; } && \
    kill $SERVER_PID

FROM ollama/ollama:latest
COPY --from=builder /root/.ollama /root/.ollama
# Cloud Run requires port 8080; Ollama defaults to 11434
ENV OLLAMA_HOST=0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["ollama", "serve"]
