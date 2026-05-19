# vLLM — OpenAI-compatible LLM serving for Qwen/Qwen3-8B-AWQ.
#
# Upstream: https://github.com/vllm-project/vllm
# Image: vllm/vllm-openai:latest (GPU build; ~6 GB pull, requires NVIDIA CUDA 12+).
#
# Aspire's AddDockerfile resource builds this image at `aspire run` time so an
# operator-supplied build context is the customisation seam — add corporate
# proxy ENVs, alternate base image, multi-stage compression, etc., here without
# touching AppHost.cs.
#
# Model selection comes through the LLM_MODEL env var (set by Aspire from the
# `LlmModel` parameter in AppHost.cs); the model is downloaded from Hugging Face
# on first boot and cached in the named volume mounted at /root/.cache/huggingface.
#
# CPU-only laptops should switch the FROM to the official cpu variant by setting
# RUNTIME_DEVICE=cpu and patching this file — vLLM's CPU build lives at
# vllm/vllm-openai:cpu and the supported subset of models is narrower; this is
# documented in docs/local-stack-scope.md.

FROM vllm/vllm-openai:latest

ENV LLM_MODEL=Qwen/Qwen3-8B-AWQ \
    HF_HUB_ENABLE_HF_TRANSFER=1

EXPOSE 8000

# vLLM's container entrypoint already runs `vllm serve`; we extend it to honour
# our LLM_MODEL knob. Quantization (`awq`) is inferred from the model's
# config.json on Hugging Face, so no extra flag is required.
CMD ["--model", "Qwen/Qwen3-8B-AWQ", "--host", "0.0.0.0", "--port", "8000"]
