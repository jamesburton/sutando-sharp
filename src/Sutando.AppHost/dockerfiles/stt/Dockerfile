# speaches — OpenAI-compatible STT (formerly faster-whisper-server).
#
# Upstream: https://github.com/speaches-ai/speaches
# Image: ghcr.io/speaches-ai/speaches:latest (CUDA build; auto-detects CPU at
# runtime and falls back to CTranslate2's CPU INT8 backend if no GPU is visible).
#
# Endpoints (OpenAI-compatible): /v1/audio/transcriptions, /v1/audio/translations,
# /v1/models. Listens on :8000 inside the container.
#
# Model selection (WHISPER__MODEL) maps to Systran/faster-whisper-<size> on
# Hugging Face — the default is medium.en per the scope doc. Override via the
# `SttModel` Aspire parameter in AppHost.cs.

FROM ghcr.io/speaches-ai/speaches:latest

ENV WHISPER__MODEL=Systran/faster-whisper-medium.en

EXPOSE 8000
