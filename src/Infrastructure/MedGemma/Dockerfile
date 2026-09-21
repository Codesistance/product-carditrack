# Pinned, not `:latest`. The weights are baked into this image and CI deploys it by immutable
# tag, so the running service never drifts — but a rebuild would have, silently, because the base
# floated: the same Dockerfile built twice could ship two different Ollama versions. The digest is
# what pins it; the version tag beside it is there so a reader knows which version that is.
# 0.34.2 carries the CVE-2026-85180 redirect fix, which is also why this file no longer pulls a
# tag: the fix refuses the cross-host redirect that tag's blobs arrive by. Bump both parts together.
FROM ollama/ollama:0.34.2@sha256:da6e0dc5651df159e45686fd663c4dbe1624a52c44d7280eeac1551d8f865532 AS builder
ARG MODEL_TAG
RUN test -n "${MODEL_TAG}" || { echo "FATAL: MODEL_TAG build arg is empty — pass --build-arg MODEL_TAG=\$(cat src/Infrastructure/MedGemma/.model-version)." >&2; exit 1; }

# Everything the model is made of comes in through the build context, from one directory:
# the Modelfile (template and params, reviewed in git), weights.sha256 (the pin), and weights/
# (the GGUFs the workflow fetched from our bucket, or a developer fetched with the script).
# Nothing in this stage talks to a registry.
WORKDIR /build
COPY src/Infrastructure/MedGemma/Modelfile src/Infrastructure/MedGemma/weights.sha256 ./
COPY src/Infrastructure/MedGemma/weights/ ./weights/

# The pin. Every file the Modelfile can name has a line here, and the bytes have to match it
# before Ollama sees them — so a wrong download, a truncated copy, or a re-upload of different
# weights under the same name fails the build instead of being served. The workflow checked the
# same manifest after downloading; this is the check that cannot be skipped by building some
# other way.
RUN cd weights && sha256sum -c ../weights.sha256

# `ollama create` needs a running server. Register the model under the exact name the hosts will
# ask for, then assert that name is what got registered: a name Ollama normalises differently
# from what AI__Private__Model carries would 404 on every generate/chat call at runtime, and
# the incident of 2026-08-10 (a pulled tag that then failed to resolve) is why this is asserted
# at build time rather than discovered on the first request.
RUN ollama serve & SERVER_PID=$! && \
    sleep 5 && \
    ollama create "${MODEL_TAG}" -f Modelfile && \
    ollama list | awk -v tag="${MODEL_TAG}" 'NR>1 && $1==tag {found=1} END {exit !found}' || \
      { echo "FATAL: 'ollama create ${MODEL_TAG}' succeeded but 'ollama list' does not show that exact name — generate/chat calls would 404 against it. What Ollama registered:" >&2; ollama list >&2; exit 1; }; \
    kill $SERVER_PID

# The runtime stage carries the registered model and none of the loose files it was built from.
FROM ollama/ollama:0.34.2@sha256:da6e0dc5651df159e45686fd663c4dbe1624a52c44d7280eeac1551d8f865532
COPY --from=builder /root/.ollama /root/.ollama
# Cloud Run requires port 8080; Ollama defaults to 11434
ENV OLLAMA_HOST=0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["ollama", "serve"]
