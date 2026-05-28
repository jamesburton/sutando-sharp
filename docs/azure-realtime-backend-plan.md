# Azure GPT Realtime voice backend — port plan

Tracks the .NET port of [upstream PR #1306](https://github.com/sonichi/sutando/pull/1306)
("feat(voice): opt-in Azure GPT Realtime voice backend"). Upstream adds a
`VOICE_BACKEND=gpt-realtime` switch that swaps Gemini Live for an Azure-hosted
GPT Realtime transport while leaving the default Gemini path untouched. This
doc describes how the same capability lands in `sutando-sharp` against our
existing realtime abstractions.

Upstream is in **draft** status pending a peer change in `bodhi-realtime-agent`
that adds a `protocolVersion: 'legacy'` knob — Azure realtime *preview* rejects
the bodhi GA session shape. The .NET port has no equivalent dependency
(we don't use bodhi), so the legacy-protocol blocker does NOT apply here. The
30-minute session cap DOES.

## What the upstream PR introduces

| Surface | Upstream file | Purpose |
|---|---|---|
| New backend module | `src/voice-backends/azure-realtime.ts` (79 lines) | Builds an `LLMTransport` whose underlying OpenAI client is `AzureOpenAI`, routed through `OpenAIRealtimeWS.azure(client, {deploymentName})`. |
| Backend selector | `src/voice-agent.ts` (+17/−6) | Reads `VOICE_BACKEND` env, swaps `transport:` config when `gpt-realtime`. Default path unchanged. |
| Launch banner / passthrough | `src/startup.sh` (+14/−3) | Logs which backend is active; passes `VOICE_BACKEND` through. |
| Config knobs | `.env.example` (+16) | New `VOICE_BACKEND`, `AZURE_OPENAI_*`, `AZURE_REALTIME_*` vars. |
| Docs | `docs/azure-voice-backend.md` (51 lines) | Operator-facing setup + known limitations. |
| Dependency | `package.json` (+2) | Declares `openai` (was only transitive via bodhi). |

Env vars introduced (verbatim):

- `VOICE_BACKEND` — `gemini` (default) | `gpt-realtime`
- `AZURE_OPENAI_API_KEY`
- `AZURE_OPENAI_ENDPOINT` — `https://<resource>.openai.azure.com`
- `AZURE_REALTIME_DEPLOYMENT` — default `gpt-realtime`
- `AZURE_REALTIME_API_VERSION` — default `2025-04-01-preview` (first version to accept `session.type`)
- `AZURE_REALTIME_VOICE` — default `alloy`

## How it fits sutando-sharp's existing architecture

Our realtime stack already has the right seam — we don't need new abstractions,
only a third implementation behind the existing ones:

```
                 IRealtimeTransportFactory                 (Sutando.Voice)
                          │
       ┌──────────────────┼───────────────────────┐
       ▼                  ▼                       ▼
GeminiLiveTransportFactory  LocalPipelineTransportFactory  AzureOpenAIRealtimeTransportFactory  ← NEW
       │                  │                       │
       ▼                  ▼                       ▼
  IRealtimeClient    IRealtimeClient         IRealtimeClient
       │                  │                       │
       ▼                  ▼                       ▼
GeminiLiveRealtimeClient  LocalPipelineRealtimeClient  AzureOpenAIRealtimeClient  ← NEW
                                                  (lives in Sutando.Realtime alongside Gemini)
```

Backend selection in `VoiceServer.Build` already resolves between cloud and
local via `ResolveUseLocal`; add a parallel `ResolveBackend` that returns
`gemini | local | azure-realtime` and registers the matching factory.

## Files to create / modify

### New
- `src/Sutando.Realtime/AzureOpenAIRealtimeClient.cs`
  Mirrors `GeminiLiveRealtimeClient` — holds default API key / endpoint /
  deployment, mints a `IRealtimeClientSession` per `CreateSessionAsync`.
- `src/Sutando.Realtime/AzureOpenAIRealtimeClientSession.cs`
  Mirrors `GeminiLiveRealtimeClientSession` — wraps the .NET OpenAI SDK's
  `RealtimeConversationClient` (from `Azure.AI.OpenAI` 2.x). Same lifecycle:
  lazy connect through `AsyncOnceGate`, single-reader channel for inbound
  messages, `MeaiToSutandoEventAdapter` for shape translation.
- `src/Sutando.Voice/AzureOpenAIRealtimeTransportFactory.cs`
  `IRealtimeTransportFactory` impl returning `new AzureOpenAIRealtimeClient(...)`.
- `tests/Sutando.Tests/Realtime/AzureOpenAIRealtimeClientSessionTests.cs`
  Mirrors the existing `GeminiLiveIntegrationTests` shape — primarily a live
  test gated on `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_ENDPOINT`. Plus unit
  tests for env-var resolution + the same connect-race regression covered by
  `AsyncOnceGateTests`.
- `docs/azure-realtime-backend.md` — operator-facing setup + caveats (Windows-
  flavoured port of upstream's `docs/azure-voice-backend.md`).

### Modify
- `src/Sutando.Voice/VoiceOptions.cs` — add `Backend` (enum: `Gemini` |
  `Azure`), `AzureEndpoint`, `AzureDeployment`, `AzureApiVersion`,
  `AzureVoice`. Default `Backend = Gemini` preserves current behaviour.
- `src/Sutando.Voice/VoiceServer.cs` — refactor the existing
  `ResolveUseLocal` branch into a three-way `ResolveBackend` (gemini / local
  / azure) and register the matching factory. Read env vars
  (`SUTANDO_VOICE_BACKEND`, `AZURE_OPENAI_API_KEY`, etc.) in
  `ApplyOverrides`.
- `src/Sutando.Cli/Program.cs` — extend the `voice` help text to mention
  `--backend gemini|local|azure` and the Azure env vars.
- `src/Sutando.Cli/Commands.cs` — accept `--backend <name>` and a few short
  aliases (`--azure` toggle paralleling `--local`).

### Dependencies
- `Azure.AI.OpenAI` 2.x — provides `AzureOpenAIClient`. Adds an
  experimental warning we'll `<NoWarn>` like we already do for MEAI's
  `[Experimental("MEAI001")]` in `Sutando.Realtime.csproj`.
- `OpenAI` 2.x (transitive via `Azure.AI.OpenAI`, but worth declaring
  explicitly — upstream PR does the same).

## Env-var parity with upstream

Use Sutando-prefixed names that map 1:1 onto upstream's names so an operator's
`.env` is translatable:

| Upstream | sutando-sharp |
|---|---|
| `VOICE_BACKEND` | `SUTANDO_VOICE_BACKEND` (existing pattern matches `SUTANDO_VOICE_LOCAL`) |
| `AZURE_OPENAI_API_KEY` | `AZURE_OPENAI_API_KEY` (vendor-neutral; keep verbatim) |
| `AZURE_OPENAI_ENDPOINT` | `AZURE_OPENAI_ENDPOINT` |
| `AZURE_REALTIME_DEPLOYMENT` | `AZURE_REALTIME_DEPLOYMENT` |
| `AZURE_REALTIME_API_VERSION` | `AZURE_REALTIME_API_VERSION` |
| `AZURE_REALTIME_VOICE` | `AZURE_REALTIME_VOICE` |

The `AZURE_*` vars stay vendor-neutral so cloud-skills code reading the same
keys keeps working.

## What's deliberately out of scope (matches upstream's "intentionally NOT")

- **Local pipeline GPT Realtime path.** Upstream skips wiring the local-voice
  pipeline through Azure-Foundry hosting; we do the same. Our local pipeline
  uses Whisper/Llama/Kokoro directly — no Azure indirection needed.
- **30-minute session reconnection.** Azure caps realtime sessions at ~30 min
  (`close 1001 session_expired`). Upstream defers in-place reconnect; so do
  we. The session ending is observable at our `VoiceSession.State`
  transitioning to `Disconnected` — operators can re-handshake by opening a
  new browser WS for now.

## Caveats specific to the .NET port

1. **No bodhi blocker.** Upstream's draft status traces to needing
   `protocolVersion: 'legacy'` support in the bodhi NPM package. We don't use
   bodhi; we wrap `RealtimeConversationClient` directly. The .NET port can
   ship without waiting for that.
2. **`RealtimeConversationClient` is `[Experimental]`.** OpenAI's .NET SDK
   marks the realtime API as `[Experimental("OPENAI002")]`. Suppress that
   warning in `Sutando.Realtime.csproj` (same pattern as `MEAI001`).
3. **Schema shape parity.** The Gemini Live setup envelope and the OpenAI
   Realtime session.update envelope have different tool-schema rules.
   Currently the bridge ships `{type:"object", properties:{}}` which is safe
   for both. Per-skill schemas (deferred TODO in
   `Sutando.Voice.Skills/SkillVoiceTool.cs`) need separate translators for
   the two backends when that work lands.
4. **Voice name space.** Gemini has its own voice list (`Aoede`, `Puck`,
   etc.); OpenAI Realtime has `alloy`, `echo`, `fable`, etc. They are not
   interchangeable. `VoiceOptions.VoiceName` carries one or the other based
   on the active backend — document this in the backend-selector switch.

## Build order

1. Add the `Azure.AI.OpenAI` dependency to `Sutando.Realtime.csproj` + the
   experimental-warning suppression.
2. `AzureOpenAIRealtimeClient` + `AzureOpenAIRealtimeClientSession` (port the
   Gemini-side connection lifecycle pattern, including the `AsyncOnceGate`
   guard for the connect race).
3. `AzureOpenAIRealtimeTransportFactory` in `Sutando.Voice`.
4. Backend resolution in `VoiceServer.Build` / `VoiceOptions`.
5. CLI flag + help text.
6. Live integration test gated on `AZURE_OPENAI_*` env vars.
7. Operator docs.
8. Update `docs/upstream-feature-gap.md` to flip the new "Azure GPT Realtime"
   row to ✓ and `docs/feature-gap-delta-2026-05-25.md` (or its successor)
   with the wave outcome.

## Why this is worth doing soon

The current voice debugging (Gemini Live transport closing pre-setup-complete)
suggests our Gemini path may be hitting a model-name / quota / setup issue
that's hard to diagnose without an alternate backend to A/B against. Landing
Azure as a parallel transport gives operators a fallback AND turns into a
diagnostic: if Azure works and Gemini doesn't, the failure is provider-side,
not in our `VoiceSession` / `VoiceWebSocketHandler` plumbing.

That makes Azure realtime a **two-for-one** — feature parity with upstream
PR #1306 plus a diagnostic lever for the current voice issue.
