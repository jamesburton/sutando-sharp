# Microsoft.Extensions.AI + Agent Framework — scope for `Sutando.LocalInference.Abstractions`

Scoping doc for task #16. Answers one question: should the planned local-inference
abstractions project define its own interfaces, build on Microsoft.Extensions.AI
(MEAI), or adopt Microsoft Agent Framework types? The state of the .NET AI ecosystem
in May 2026 is materially different from when `local-stack.md` was drafted — both
MEAI and Agent Framework have since shipped speech and realtime surfaces that
overlap our planned contracts.

## If you only read one thing

Five decisive calls the next implementer can act on without further research:

1. **Delete `IChatCompletion` from the plan. Use `Microsoft.Extensions.AI.IChatClient`
   directly.** GA in MEAI 10.6 (not experimental), exact-shape match (streaming,
   tool calling, multi-modal, history, cancellation, DI builder, OpenTelemetry).
2. **Delete `ISpeechToText` from the plan. Adopt `Microsoft.Extensions.AI.ISpeechToTextClient`.**
   `[Experimental("MEAI001")]` — accept the warning. **Whisper.net 1.9 already ships
   `WhisperSpeechToTextClient : ISpeechToTextClient`** — adopting gives us the
   in-process STT adapter for free.
3. **Delete `ITextToSpeech` from the plan. Adopt `Microsoft.Extensions.AI.ITextToSpeechClient`.**
   Same experimental status as STT. KokoroSharp doesn't yet implement it, so we
   write `KokoroSharpTextToSpeechClient : ITextToSpeechClient` — same adapter
   work either way, with a clean migration path if KokoroSharp adopts the interface.
4. **Keep `IVadDetector` as our own type, but shape it like MEAI would shape it.**
   MEAI has `VoiceActivityDetectionOptions` (a config blob inside `RealtimeSessionOptions`),
   not a standalone PCM-in / event-out abstraction. Silero ONNX needs the latter.
   Mirror MEAI conventions (`CancellationToken` last, `IAsyncEnumerable<VadEvent>`,
   `[Experimental("SUTANDO001")]`) so any future MEAI `IVoiceActivityDetector`
   would be a drop-in replacement.
5. **Keep `Sutando.Core.IAgentExecutor` distinct from `Microsoft.Agents.AI.AIAgent`.**
   They live at different layers — `IAgentExecutor` is bridge-coupled (TaskEnvelope
   AccessTier, NoSend, DedupedTo, AlreadyReplied). `AIAgent.RunAsync(string,
   AgentSession)` is generic conversational. A future executor implementation
   can *compose* an `AIAgent` internally; do not collapse them by subtyping.

---

## 1. State of Microsoft.Extensions.AI (May 2026)

`Microsoft.Extensions.AI.Abstractions` is at **10.6.0** ([nuget.org](https://www.nuget.org/packages/Microsoft.Extensions.AI.Abstractions/)).
The official MS Learn overview ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai))
still lists only `IChatClient`, `IEmbeddingGenerator<TInput,TEmbedding>`, and
`IImageGenerator` (experimental). The doc lags the assembly — direct namespace
inspection ([Microsoft.Extensions.AI namespace](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai?view=net-10.0-pp))
turns up substantially more:

| Capability | Interface | Status | Layer |
|---|---|---|---|
| Chat / LLM | `IChatClient` | **GA** | Abstractions |
| Embeddings | `IEmbeddingGenerator<TInput,TEmbedding>` | **GA** | Abstractions |
| Image gen | `IImageGenerator` | `[Experimental("MEAI001")]` | Abstractions |
| Speech-to-text | `ISpeechToTextClient` | `[Experimental("MEAI001")]` | Abstractions |
| Text-to-speech | `ITextToSpeechClient` | `[Experimental("MEAI001")]` | Abstractions |
| Realtime sessions | `IRealtimeClient` + `IRealtimeClientSession` | `[Experimental("MEAI001")]` | Abstractions |

`IChatClient` matches our planned `IChatCompletion` exactly. `GetResponseAsync` and
`GetStreamingResponseAsync` accept `IEnumerable<ChatMessage>` + `ChatOptions` +
`CancellationToken` and return `ChatResponse` / `IAsyncEnumerable<ChatResponseUpdate>`.
Tool / function calling is first-class via `AIFunction` and `AIFunctionFactory.Create`.
`ChatClientBuilder` exposes middleware composition; `UseOpenTelemetry`,
`UseFunctionInvocation`, and `UseLogging` extension methods exist already. There
is **no benefit** to defining our own `IChatCompletion` — we'd be re-inventing GA
types and giving up the entire MEAI middleware ecosystem.

`ISpeechToTextClient` exposes `GetTextAsync(Stream, SpeechToTextOptions, CT)` and
`GetStreamingTextAsync(...)` returning `IAsyncEnumerable<SpeechToTextResponseUpdate>`
([ISpeechToTextClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.ispeechtotextclient?view=net-10.0-pp)).
A `DataContent` overload supports byte payloads. The builder / Logging / OpenTelemetry
/ DelegatingSpeechToTextClient stack is in place — identical pattern to `IChatClient`.

`ITextToSpeechClient` exposes `GetAudioAsync(string, TextToSpeechOptions, CT)` and
`GetStreamingAudioAsync(...)` returning streaming audio chunks
([ITextToSpeechClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.itexttospeechclient?view=net-10.0-pp)).
Same builder / middleware stack.

`IRealtimeClient` exposes `CreateSessionAsync(RealtimeSessionOptions, CT)` returning
an `IRealtimeClientSession`. Concrete `OpenAIRealtimeClient` ships in the box, and
`FunctionInvokingRealtimeClient` is a delegating middleware that auto-invokes tools
on a realtime stream — a near-exact match for what `Sutando.Realtime` does today.

`VoiceActivityDetectionOptions` exists as a config struct on `RealtimeSessionOptions`
([namespace listing](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai?view=net-10.0-pp)),
but **no standalone `IVoiceActivityDetector` / `IVadDetector` abstraction exists**.
VAD is treated as a server-side feature of the realtime session, not a pluggable
.NET interface. Silero-style PCM-in / event-stream-out has no MEAI equivalent.

## 2. State of Microsoft Agent Framework (May 2026)

Microsoft Agent Framework **1.0 shipped on 3 April 2026** under MIT licence
([devblogs.microsoft.com/agent-framework](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/),
[Visual Studio Magazine](https://visualstudiomagazine.com/articles/2026/04/06/microsoft-ships-production-ready-agent-framework-1-0-for-net-and-python.aspx)).
It is the convergence of Semantic Kernel + AutoGen, ships as `Microsoft.Agents.AI`,
and has committed to API stability and LTS. Production-ready in .NET 10.

Core type is the abstract `AIAgent`, with `ChatClientAgent` as the canonical
implementation that wraps an `IChatClient` ([agent framework docs](https://learn.microsoft.com/en-us/agent-framework/overview/)).
The layering is unambiguous: **Agent Framework sits on top of MEAI**. An `AIAgent`
holds an `IChatClient`, has `RunAsync(string, AgentSession)` and `RunStreamingAsync`,
plus pluggable memory (`AgentSession`, `InMemoryAgentSession`), middleware,
function tools, workflows, and A2A interop. The .NET surface is mature; samples
under `dotnet/samples/02-agents/` cover every provider integration we'd want.

Our `Sutando.Core.IAgentExecutor` sits at a **different layer** than `AIAgent`.
`IAgentExecutor.ExecuteAsync(TaskEnvelope, CT) → AgentResult` carries bridge-coupled
semantics (`AccessTier`, `NoSend`, `DedupedTo`, `AlreadyReplied`, `Attachments`,
`TimedOut`) that aren't conversational. `AIAgent.RunAsync` is a chat primitive.
The right composition is "an `IAgentExecutor` implementation may use an `AIAgent`
internally"; collapsing them into one interface forfeits the bridge protocol
shape. Keep them distinct.

## 3. Compatibility table

| Sutando abstraction | MEAI / Agent Framework type | Recommendation |
|---|---|---|
| `IAgentExecutor` (Sutando.Core) | `AIAgent` (Agent Framework) | **Keep distinct.** Different layer — `IAgentExecutor` is bridge-coupled; `AIAgent` is conversational. Future `ChatClientAgentExecutor` can compose an `AIAgent`. |
| `IChatCompletion` (planned) | `IChatClient` (GA) | **Adopt directly. Do not define `IChatCompletion`.** |
| `ISpeechToText` (planned) | `ISpeechToTextClient` (Experimental) | **Adopt directly.** Whisper.net 1.9 already ships `WhisperSpeechToTextClient`. |
| `ITextToSpeech` (planned) | `ITextToSpeechClient` (Experimental) | **Adopt directly.** Wrap KokoroSharp as `KokoroSharpTextToSpeechClient`. |
| `IVadDetector` (planned) | none (only config blob) | **Define our own, MEAI-shaped.** Future-drop-in for any MEAI VAD interface. |
| `IRealtimeTransport` (Sutando.Realtime) | `IRealtimeClient` (Experimental) | **Keep for now.** Migration is out of scope for #16 — track as separate follow-up. |
| `VoiceSession` (Sutando.Realtime) | `AgentSession` (Agent Framework) | **Keep.** `VoiceSession` is a reconnect/resumption state machine; `AgentSession` is a memory carrier. Different concerns. |

## 4. Recommendations

1. **Adopt `IChatClient` as our chat surface.** Reference `Microsoft.Extensions.AI`
   (not just Abstractions) from `Sutando.LocalInference.Abstractions` so consumers
   get `ChatClientBuilder`, `UseFunctionInvocation`, telemetry, caching middleware
   for free. The `OpenAiCompatibleChatCompletion` planned adapter becomes a thin
   wrapper around an `OpenAI.Chat.OpenAIClient.AsIChatClient(...)` extension already
   provided by `Microsoft.Extensions.AI.OpenAI`. For vLLM / llama-server / LM Studio
   this is effectively zero code.

2. **`IAgentExecutor` stays. Don't pull Agent Framework into `Sutando.Core` yet.**
   The Agent Framework is overkill for `Sutando.Core.IAgentExecutor`'s current
   contract (single-shot task execution against the bridge). If a future executor
   wants memory, multi-turn, or multi-agent orchestration, that executor can take
   a dependency on `Microsoft.Agents.AI` and wrap an `AIAgent`. Pulling Agent
   Framework into `Sutando.Core` today would bloat the dependency tree of every
   consumer without benefit.

3. **For STT / TTS — adopt MEAI directly, suppress `MEAI001`.** Yes, they're
   experimental. The cost is `[SuppressMessage("Usage", "MEAI001")]` at the
   project level (one line). The benefit is: Whisper.net 1.9 already implements
   the interface, so the in-process STT path needs zero adapter code. The
   ecosystem signal — a major .NET STT NuGet aligning on MEAI v10.6 — is decisive.
   For TTS, we're writing one wrapper for KokoroSharp regardless of which
   interface we target; targeting `ITextToSpeechClient` means future
   KokoroSharp versions that ship native MEAI support let us delete the wrapper.

4. **For VAD — define our own, mirror MEAI.** No MEAI equivalent exists. Shape
   the interface so the migration cost to a hypothetical future MEAI
   `IVoiceActivityDetector` is mechanical (rename, re-export). Concretely:
   `IAsyncEnumerable<VadEvent>` return type, `Stream` or `IAsyncEnumerable<AudioChunk>`
   input, options blob, CancellationToken last. Mark the type
   `[Experimental("SUTANDO001")]` so the convention matches MEAI's own marking
   of evolving surfaces.

5. **Effort reduction is real, not theoretical.** Replacing four custom
   interfaces (`IChatCompletion`, `ISpeechToText`, `ITextToSpeech`, plus all the
   custom `ChatMessage` / `ChatChunk` / `AudioChunk` companion types) with three
   MEAI types collapses `Sutando.LocalInference.Abstractions` from a ~15-file
   project to a ~3-file project. The Whisper.net adapter goes from "implement
   our STT interface around the Whisper.net API" to "reference `WhisperSpeechToTextClient`."

### Risks

- **Experimental APIs may churn between MEAI 10.x → 11.** Blast radius is bounded
  because `Sutando.LocalInference.Abstractions` (and the four adapter projects)
  are the only crates that take the dependency. A solution-level
  `<NoWarn>MEAI001</NoWarn>` in `Directory.Build.props` documents and confines
  the suppression. If 10.6 → 11 breaks shape, we update the abstractions project
  and the four adapters; consumers downstream of the abstractions don't care.
- **NuGet version conflicts.** Whisper.net 1.9, KokoroSharp 0.6.7, LlamaSharp
  0.27, and Google_GenerativeAI.Live likely all transitively reference MEAI;
  with .NET 10 alignment they should agree on MEAI 10.x, but verify in the
  package-graph during phase #17.
- **`IRealtimeClient` is tempting now.** It looks like a `Sutando.Realtime`
  drop-in (OpenAI realtime impl, function-invoking middleware, audio frame
  types). Resist consolidating in #16. The Gemini Live transport is bespoke
  enough that the conversion is a real project; do it as a separate task with
  its own plan.
- **Licence: MIT for both MEAI and Agent Framework.** No conflict with our
  current dependencies; safe to redistribute.
- **NuGet dependency weight:** `Microsoft.Extensions.AI` pulls in
  `Microsoft.Extensions.AI.Abstractions`, `System.Text.Json`, `Microsoft.Bcl.AsyncInterfaces`.
  All already in our graph via existing transitive references.

## 5. Concrete impact on task #16

Given the above, `Sutando.LocalInference.Abstractions` becomes much smaller than
the original plan. The .csproj structure:

- **References:** `Microsoft.Extensions.AI.Abstractions` (10.6.0+), plus
  `Microsoft.Extensions.AI` if we want builder helpers in the same project.
- **No re-exports of MEAI types** — adapter projects and consumers reference
  MEAI directly. This avoids creating a "tax" type alias layer.
- **What this project owns:**
  - `IVadDetector` — `IAsyncEnumerable<VadEvent> AnalyzeAsync(IAsyncEnumerable<AudioFrame> source, VadOptions options, CancellationToken ct)`.
  - `VadEvent` (record: speech-start / speech-end / energy-update + timestamp).
  - `VadOptions` (sensitivity threshold, min-speech-ms, silence-hangover-ms).
  - `AudioFrame` (sample-rate, channels, encoding, PCM byte payload) — used by
    VAD only. STT / TTS use MEAI's `DataContent` / `Stream` / `RealtimeAudioFormat`.
  - Optional `ServiceCollectionExtensions.AddSutandoLocalInference()` DI helper
    that wires the standard adapter set, with overrideable factory delegates.

**Explicitly NOT in this project:**

- `IChatCompletion`, `ChatMessage`, `ChatChunk`, `ChatOptions` — use MEAI types.
- `ISpeechToText`, `TranscriptionResult`, `AudioFormat` (for STT) — use MEAI types.
- `ITextToSpeech`, `VoiceConfig`, `AudioChunk` (for TTS) — use MEAI types.

**Renamings in `docs/local-stack.md`:**

- "`IChatCompletion`" sections become "`IChatClient` (Microsoft.Extensions.AI)".
- "`ISpeechToText`" sections become "`ISpeechToTextClient` (Microsoft.Extensions.AI)".
- "`ITextToSpeech`" sections become "`ITextToSpeechClient` (Microsoft.Extensions.AI)".
- The adapter list keeps the same set of concrete classes (`WhisperNetStt` is
  literally `Whisper.net`'s `WhisperSpeechToTextClient`; `KokoroSharpTts` becomes
  `KokoroSharpTextToSpeechClient`; `LlamaCppChat` becomes a class implementing
  `IChatClient` using LlamaSharp; `FasterWhisperHttpStt` /
  `OpenAiCompatibleChatCompletion` / `KokoroHttpTts` use the OpenAI-flavoured
  `IChatClient` / `ISpeechToTextClient` / `ITextToSpeechClient` already shipped
  by `Microsoft.Extensions.AI.OpenAI`).

**Follow-up tasks to track (not part of #16):**

- "Realtime convergence" — migrate `Sutando.Realtime.IRealtimeTransport` onto
  `IRealtimeClient` once Gemini Live ships a MEAI adapter or we write one.
- "Executor convergence" — consider a `ChatClientAgentExecutor` that wraps an
  Agent Framework `AIAgent` and implements `IAgentExecutor`, once we have a
  concrete need (memory across tasks, multi-agent dispatch).

The load-bearing decision is **collapse three of the four planned interfaces into
MEAI types**. Everything else in this report follows from that.
