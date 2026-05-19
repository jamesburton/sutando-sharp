# kokoro-fastapi — OpenAI-compatible TTS for Kokoro 82M.
#
# Upstream: https://github.com/remsky/Kokoro-FastAPI
# Images:
#   ghcr.io/remsky/kokoro-fastapi-cpu:latest — CPU-only, ~1.5 GB pull, runs anywhere.
#   ghcr.io/remsky/kokoro-fastapi-gpu:latest — CUDA build, lower latency on a GPU.
#
# Endpoint (OpenAI-compatible): /v1/audio/speech. Listens on :8880 inside the
# container.
#
# Default voice is af_bella; override via the `TtsVoice` Aspire parameter.
# Voice mixing is supported by passing a voice expression like
# `af_bella(0.7)+af_sky(0.3)` directly in the `voice` request parameter.
#
# To switch this service to the GPU image, replace the FROM line below with
# `ghcr.io/remsky/kokoro-fastapi-gpu:latest`. We default to the CPU image
# because Kokoro is small (~50 MB ONNX) and CPU latency is already acceptable —
# the GPU build is a workstation optimisation, not a correctness requirement.

FROM ghcr.io/remsky/kokoro-fastapi-cpu:latest

ENV KOKORO_DEFAULT_VOICE=af_bella

EXPOSE 8880
