# Local-inference adapters — integration notes

Four new adapter projects have been added under `src/`. The `Sutando.sln` was deliberately
left untouched (per the task brief); the projects enter the build graph today via
`<ProjectReference>` from `tests/Sutando.Tests/Sutando.Tests.csproj` only. This document
captures the wiring the operator should run when ready to register them in the solution.

## New projects

| Project | Role |
|---|---|
| `src/Sutando.LocalInference.WhisperNet` | In-process STT — wires Whisper.net's `WhisperSpeechToTextClient` (already implements MEAI's `ISpeechToTextClient`) into DI. |
| `src/Sutando.LocalInference.KokoroSharp` | In-process TTS — `KokoroSharpTextToSpeechClient : ITextToSpeechClient`, wraps KokoroSharp 0.6.7's `KokoroWavSynthesizer`. |
| `src/Sutando.LocalInference.LlamaSharp` | In-process chat — `LlamaCppChatClient : IChatClient`, wraps LlamaSharp 0.27's `InteractiveExecutor.AsChatClient()`. |
| `src/Sutando.LocalInference.Silero` | In-process VAD — `SileroVadDetector : IVadDetector` running Silero v5 ONNX via `Microsoft.ML.OnnxRuntime`. Ships a `SileroModelLocator` helper + `AddSileroVadAutoDownload` extension that fetches the ~2 MB MIT-licensed model from upstream GitHub into a per-user cache directory on first use. |

## Add to the solution

```pwsh
dotnet sln Sutando.sln add src/Sutando.LocalInference.WhisperNet/Sutando.LocalInference.WhisperNet.csproj
dotnet sln Sutando.sln add src/Sutando.LocalInference.KokoroSharp/Sutando.LocalInference.KokoroSharp.csproj
dotnet sln Sutando.sln add src/Sutando.LocalInference.LlamaSharp/Sutando.LocalInference.LlamaSharp.csproj
dotnet sln Sutando.sln add src/Sutando.LocalInference.Silero/Sutando.LocalInference.Silero.csproj
```

## Optional `Sutando.Cli` wiring suggestions

None of the adapters are wired into `Sutando.Cli` yet — they're consumer libraries that any
host (CLI, Voice WS server, Realtime pipeline, AppHost) can compose via DI. When you do reach
for the CLI, the natural surface is a small `sutando local-inference smoke` subcommand that
proves each adapter loads its model and emits one round-trip, e.g.:

```text
sutando local-inference smoke --whisper-model <path-to-ggml-bin> [--audio sample.wav]
sutando local-inference smoke --kokoro-model <path-to-kokoro.onnx> --text "Hello."
sutando local-inference smoke --llama-model  <path-to-qwen3.gguf>   --prompt "Say hi."
sutando local-inference smoke --silero-model <path-to-silero_vad.onnx>
```

That's a follow-up task; the abstractions and DI shape are sufficient to wire those commands
when the time comes. For now the projects ship as composable libraries with their own DI
extensions (`AddWhisperNet`, `AddKokoroSharp`, `AddLlamaCppChat`, `AddSileroVad`).

## Test integration

`tests/Sutando.Tests/Sutando.Tests.csproj` already references all four adapters (added in this
same commit). Tests cover:

- DI registration shape (every adapter — always-run).
- Constructor / argument validation (every adapter — always-run).
- Silero VAD state-machine transitions via mocked probability values
  (`SileroVadStateMachineTests`) — always-run, no ONNX file needed.
- Live model integration paths (`[SkippableFact]`) — skip when the relevant env var is unset:
  - `WHISPER_NET_MODEL_PATH` + `WHISPER_NET_SAMPLE_WAV` for Whisper.net live transcription.
  - `KOKORO_ONNX_MODEL_PATH` for KokoroSharp synthesis + streaming.
  - `LLAMACPP_GGUF_MODEL_PATH` for LlamaSharp inference.
  - `SILERO_ONNX_MODEL_PATH` for Silero VAD end-to-end.

All `[SkippableFact]` tests skip cleanly in CI; the always-run tests pass on both `net10.0`
and `net10.0-windows10.0.19041.0`.

## Package dependency notes

- `Sutando.LocalInference.LlamaSharp` force-pins
  `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.5 to avoid the NU1605 downgrade
  triggered by LlamaSharp 0.27's transitive 10.0.5+ requirement.
- All adapters suppress `MEAI001` (experimental MEAI surfaces) and `SUTANDO001` (our own
  abstractions' experimental marker) at the project level.
- `Microsoft.Extensions.AI.Abstractions` is pinned at 10.6.0 on every adapter to match the
  abstractions project — transitive resolution would land there anyway, but the explicit pin
  is documentation.
