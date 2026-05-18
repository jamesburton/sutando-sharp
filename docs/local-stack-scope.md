# Local-stack recon — answers to the open questions

Scoping doc for `docs/local-stack.md`. Each section pins down one of the six
recon questions and ends with a recommendation. Sources are linked inline.

## If you only read one thing

Six decisions the next implementer can act on without caveats:

1. **LLM weight quant**: `Qwen/Qwen3-8B-AWQ` (Apache 2.0, official Qwen release) for
   HTTP serving via vLLM/SGLang; `Qwen3-8B-GGUF` Q4_K_M for in-process via LlamaSharp.
   "Turboquant" is a **KV-cache** technique, not a weight format — treat it as a
   future long-context optimisation, not a primary pick.
2. **In-process LLM path**: `LlamaSharp` 0.27+ (Qwen3 supported since 0.24). Use
   `onnxruntime-genai` only if a future need for the unified ONNX deployment story
   outweighs LlamaSharp's headroom on RAM/perf.
3. **STT**: `Whisper.net` for in-process; `speaches` (formerly faster-whisper-server)
   over its OpenAI-compatible HTTP endpoint for remote.
4. **TTS**: `KokoroSharp` NuGet for in-process; `kokoro-fastapi` over OpenAI-compatible
   `/v1/audio/speech` for remote. No DIY ONNX wiring required.
5. **Pipeline orchestration**: ship **Shape B** (C# port of the Pipecat pattern in
   `Sutando.Pipeline`) first. The .NET ecosystem now covers every stage; the Pipecat
   sidecar premise has weakened.
6. **Multi-container host**: **.NET Aspire** AppHost. Use `AddDockerfile` for the
   Python services (vLLM, kokoro-fastapi, speaches). OTLP dashboard + service
   discovery are real wins over hand-rolled `docker-compose.yml`.

---

## 1. The "turboquant" Qwen3-8B variant — what it actually is

"Turboquant" in the source doc points at the **ICLR 2026 paper "TurboQuant:
Online Vector Quantization with Near-optimal Distortion Rate"** ([arXiv:2504.19874](https://arxiv.org/abs/2504.19874)).
It is a **KV-cache** quantization technique — random orthogonal rotation (Walsh-Hadamard
in practice) followed by optimal scalar quantization, ~6× KV memory reduction,
neutral quality at 3.5 bits/channel ([decode-the-future explainer](https://decodethefuture.org/en/turboquant-vector-quantization-kv-cache/)).
It is **orthogonal to** weight quantization (AWQ/GPTQ/GGUF) — you apply
TurboQuant on top of whatever weight quant your serving stack uses. There are
[llama.cpp](https://github.com/Pascal-SAPUI5/llama.cpp-turboquant) and
[vLLM](https://github.com/varjoranta/turboquant-vllm) integrations in flight, but
neither is upstream yet.

So the actual question is: which **weight** quant does the community converge
on for Qwen3-8B? Three candidates:

| Format | Size | Quality vs FP16 | Serving stacks |
|---|---|---|---|
| **AWQ W4-G128** ([Qwen/Qwen3-8B-AWQ](https://huggingface.co/Qwen/Qwen3-8B-AWQ), official, Apache 2.0) | ~5.5 GB | ~95% quality retention, best of the W4 set ([Local AI Master](https://localaimaster.com/blog/quantization-explained)) | vLLM ≥0.8.5, SGLang ≥0.4.6, TGI |
| **GGUF Q4_K_M** ([Qwen/Qwen3-8B-GGUF](https://huggingface.co/Qwen/Qwen3-8B-GGUF), official) | ~4.7 GB | ~92% retention, robust on CPU and consumer GPUs | llama.cpp, **LlamaSharp**, LM Studio, Ollama |
| **GPTQ W4** (community) | ~5.3 GB | ~90% retention, marginally behind AWQ on reasoning | vLLM, TGI |

**Recommendation:** primary = **`Qwen/Qwen3-8B-AWQ`** for the HTTP/container path
(vLLM or SGLang); secondary = **`Qwen3-8B-GGUF` Q4_K_M** for the in-process .NET
path. Keep TurboQuant on the roadmap as a KV-cache optimisation for long-context
turns (it lands as a server-side flag in whichever stack ships it first); do
not block on it.

## 2. ONNX feasibility for Qwen3-8B vs LlamaSharp

No official ONNX export of Qwen3-8B exists at time of writing. The
`onnx-community` org ships [Qwen3-4B-ONNX](https://huggingface.co/onnx-community/Qwen3-4B-ONNX/tree/main)
and smaller variants; the 8B is not in the published set.
[Optimum's `onnx-export` has known issues with some Qwen3 variants](https://github.com/huggingface/optimum/issues/2351),
though `ORTModelForCausalLM.from_pretrained("Qwen/Qwen3-8B", export=True)` is
the documented escape hatch. Exporting our own gets us a ~30 GB FP16 ONNX, which
then needs `onnxruntime-genai` quantisation passes to be usable — non-trivial
ops work on our side.

By contrast, [**LlamaSharp**](https://github.com/SciSharp/LLamaSharp) (0.27.0,
April 2026) is a maintained .NET wrapper around llama.cpp that takes the
official GGUF off Hugging Face and exposes a streaming `Executor` API. Qwen3
support [landed in 0.24](https://github.com/SciSharp/LLamaSharp/releases).
Both CPU and CUDA backends ship as NuGets; in-process from C# is one
`LLamaWeights.LoadFromFile()` call. `onnxruntime-genai` has a
[C# API](https://onnxruntime.ai/docs/genai/api/csharp.html) with a comparable
streaming model but requires a pre-quantised ONNX which doesn't exist for our
target.

**Recommendation:** ship `LlamaSharp` as the in-process LLM adapter
(`LlamaCppChat`). Revisit `onnxruntime-genai` only if a unified ONNX runtime
story for VAD+STT+TTS+LLM becomes a hard requirement.

## 3. STT for .NET — Whisper.net wins for in-process; speaches for HTTP

Trade-offs across the four candidates:

| Option | Shape | Throughput | Quants | Maturity | Licence |
|---|---|---|---|---|---|
| [**Whisper.net**](https://github.com/sandrohanea/whisper.net) | In-process NuGet wrapping whisper.cpp | Comparable to whisper.cpp; ~equivalent to faster-whisper on CPU | INT8 / Q5_0 / Q4_0 GGML | Actively maintained, v1.9.0 with .NET 10 + `Microsoft.Extensions.AI` integration | MIT |
| [**faster-whisper / speaches**](https://github.com/speaches-ai/speaches) | HTTP service (rebranded from faster-whisper-server) | ~4× whisper.cpp on GPU (NVIDIA only — no ROCm) | INT8 via CTranslate2 | Mature, OpenAI-compatible API, streaming transcription | MIT |
| **whisper.cpp** (raw) | Native binary | Reference for CPU | Same as Whisper.net | Same upstream as Whisper.net | MIT |
| **`Microsoft.ML.OnnxRuntime` + whisper.onnx** | In-process | Slower than CTranslate2/whisper.cpp on GPU; comparable on CPU | INT8 ONNX | Works but DIY; no ergonomic wrapper | MIT |

faster-whisper is the *reference* on quality (CTranslate2 backend, identical
weights to whisper). whisper.cpp tracks it closely. The .NET ergonomic gap
between Whisper.net and a custom ONNX runtime adapter is large; the perf gap
is small.

**Recommendation:** `Whisper.net` for in-process (`WhisperNetStt`), `speaches`
for HTTP (`OpenAiCompatibleSttClient` — same OpenAI shape we already need for
the LLM endpoint, so the adapter is trivially reusable).

## 4. Kokoro TTS — KokoroSharp obsoletes the DIY-ONNX option

Three deployment shapes:

| Option | Status | Voices | Streaming | Multilingual | Licence |
|---|---|---|---|---|---|
| [**KokoroSharp**](https://github.com/Lyrcaxis/KokoroSharp) (NuGet 0.6.7) | Maintained, plug-and-play, ONNX runtime under the hood | All v1.0 voices bundled, voice-mixing supported | Yes (text-segment streaming, chunks emitted as generated) | English (US/UK), Mandarin, Japanese, Hindi, Spanish, French, Italian, pt-BR | MIT |
| [**kokoro-fastapi**](https://github.com/remsky/Kokoro-FastAPI) | Maintained Docker image, OpenAI-compatible `/v1/audio/speech` | All v1.0 voices | Yes (HTTP chunked transfer encoding, OpenAI streaming-response shape) | Same as KokoroSharp | Apache 2.0 |
| **Pipecat's Kokoro service** | Bundled in pipecat-ai's service catalog | Same model | Yes, via Pipecat frame pipeline | Same | BSD-2-Clause (Pipecat) |

The discovery that **`KokoroSharp` exists as a complete NuGet with bundled
voices** removes the rationale for writing our own `Microsoft.ML.OnnxRuntime`
adapter. The doc's "raw Kokoro ONNX" option is strictly worse than the
NuGet — same runtime under the hood, but somebody else maintains the
phonemiser bridge to eSpeak NG.

**Recommendation:** `KokoroSharp` for in-process (`KokoroSharpTts`),
`kokoro-fastapi` over its OpenAI shape for HTTP (`OpenAiCompatibleTtsClient`).
Skip the Pipecat-service-only path; we have no reason to depend on the Pipecat
runtime just for Kokoro.

## 5. Pipecat .NET interop — port the pattern, do not adopt the sidecar

[Pipecat](https://github.com/pipecat-ai/pipecat)'s official client SDKs are
JavaScript, React, React Native, Swift, Kotlin, C++, and ESP32. **No .NET SDK
exists, official or community.** The wire format is
[`ProtobufFrameSerializer`](https://reference-server.pipecat.ai/en/latest/api/pipecat.transports.websocket.server.html)
over WebSocket — binary frames, no published versioned protocol spec, and the
project is pre-1.0 (current ~v0.0.50). The `.proto` files live in
`pipecat/serializers/` and have churned with releases (e.g.
[PR #791](https://github.com/pipecat-ai/pipecat/pull/791) fixing binary-frame
handling). Adopting it from .NET means either generating C# stubs from
upstream `.proto` and pinning a Pipecat version (and re-pinning on every
upgrade), or vendoring the `.proto` definitions into sutando.

Set against the current ecosystem — `LlamaSharp` + `Whisper.net` + `KokoroSharp`
+ Silero ONNX all available as NuGets — Shape B (the C# pipeline port) is the
**smaller** bet today than Shape A (Pipecat-as-sidecar). The pipeline pattern
itself is not large: `IPipelineStage`, frame contracts, backpressure between
stages. The Pipecat service-catalog argument (Cartesia / Deepgram / ElevenLabs
for free) only matters once we want those services; we don't yet.

**Recommendation:** invert the doc's recommendation. Ship **Shape B** first
(`Sutando.Pipeline`). Add a Pipecat sidecar adapter later only if a real
consumer asks for a service that exists in the Pipecat catalog but not yet in
Sutando.

## 6. Aspire vs docker-compose for the multi-container host

[.NET Aspire 13](https://aspire.dev/) (released alongside .NET 10) is the
inflection point. Concrete value over hand-rolled `docker-compose.yml`:

- **Service discovery** wired automatically — `WithReference()` injects
  connection strings and OTLP endpoints between services. With Compose you do
  this yourself via env-vars-in-YAML.
- **OTLP dashboard** runs locally with zero config and aggregates traces +
  metrics + logs from every service in the AppHost — including non-.NET
  containers, as long as they emit OTLP.
- **AddDockerfile / AddPythonProject** handles non-.NET workloads cleanly —
  vLLM, kokoro-fastapi, speaches all drop in as `builder.AddDockerfile(...)`.
- **Dev-loop** — F5 from the AppHost project boots the entire fleet; Compose
  needs a separate orchestrator script.
- **Compose-out** — Aspire 13 generates `docker-compose.yml` for production
  deployment, so we're not locked in.

Production readiness in 2026 is "yes for Azure Container Apps; functional but
manual for raw Kubernetes / AWS / GCP" — fine for our case where the AppHost
is primarily a developer / operator convenience and prod deployment is
opinionated per-operator anyway.

**Recommendation:** adopt Aspire for `Sutando.AppHost`. Use `AddDockerfile` for
the three Python services; the AppHost itself stays a thin .NET project. The
generated `docker-compose.yml` is the fallback for operators who don't want
Aspire on their host.

---

## Implementation order

The draft phases #15-#21 in `local-stack.md` are roughly sound but should be
re-sequenced because the .NET stage components (LlamaSharp, Whisper.net,
KokoroSharp, Silero ONNX) all exist as NuGets, while the HTTP adapters need
external services standing. Build the in-process path first — it proves the
pipeline end-to-end on a developer laptop without spinning up containers.

**Revised order:**

1. **#15 Recon** — this doc. Done.
2. **#16 `Sutando.LocalInference.Abstractions`** — `ISpeechToText`,
   `ITextToSpeech`, `IChatCompletion`, `IVadDetector`, audio-frame contracts.
   Unchanged from the original plan.
3. **#17 In-process ONNX adapters** (was #18) — `WhisperNetStt`,
   `KokoroSharpTts`, `SileroVadOnnx`, `LlamaCppChat`. All four ship as NuGets;
   no containers required. Lets us prove the abstractions on a laptop.
4. **#18 `Sutando.Pipeline`** (was #19, Shape B chosen) — port the pipeline
   pattern. Now driven by a concrete consumer (the in-process adapters), so
   the contract is grounded.
5. **#19 HTTP adapters** (was #17) — `OpenAiCompatibleChatCompletion`,
   `OpenAiCompatibleSttClient` (speaches), `OpenAiCompatibleTtsClient`
   (kokoro-fastapi). Reuses one OpenAI-shaped client base across all three.
6. **#20 `Sutando.AppHost`** — Aspire orchestration. `AddDockerfile` for vLLM
   + speaches + kokoro-fastapi; the sutando agent itself as a .NET project.
   OTLP wired for free.
7. **#21 End-to-end demo** — `sutando voice --local` mode. Two flavours: pure
   in-process (laptop), and AppHost-orchestrated (workstation with GPU box on
   LAN). Same `LocalPipelineTransport` plugs into both.

Phase 5 (Pipecat sidecar adapter) from the original plan is dropped. Revisit
only on real consumer pull.
