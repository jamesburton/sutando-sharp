# Bodhi → .NET Port Scoping

Research target: [`sonichi/bodhi_realtime_agent`](https://github.com/sonichi/bodhi_realtime_agent) @ `ffec0cd` (the commit sutando pins). All numbers below are from that exact tree.

## 1. Size & Language Mix

Pure TypeScript, no native bindings. `src/` is **7,491 LOC** across 51 files; tests add ~12,200 LOC. Top files:

| File | LOC | Role |
|------|-----|------|
| `core/voice-session.ts` | 1,022 | Top-level orchestrator (the `VoiceSession` class sutando uses) |
| `transport/openai-realtime-transport.ts` | 721 | OpenAI Realtime (skippable if sutando stays Gemini-only) |
| `transport/gemini-live-transport.ts` | 707 | Gemini Live wrapper (the critical one) |
| `transport/elevenlabs-stt-provider.ts` | 334 | Optional STT |
| `agent/*` (subagent runtime) | ~850 | Background subagent system (Vercel AI SDK) |

## 2. Gemini Live Wire Protocol — Surface Implemented

**Key finding:** bodhi does NOT speak the Gemini Live WebSocket protocol directly. It wraps `@google/genai`'s `live.connect()` (the official Google JS SDK), which owns framing, auth, JSON/proto serialization, and reconnect plumbing. bodhi's job is message routing and lifecycle.

Inbound messages handled in `GeminiLiveTransport.handleMessage` (lines 568–666):

- `setupComplete` → resolves the connect-promise, emits `onSessionReady(sessionId)`
- `serverContent.modelTurn.parts[].inlineData` → base64 PCM audio out (`onAudioOutput`)
- `serverContent.inputTranscription` / `outputTranscription`
- `serverContent.interrupted` / `turnComplete`
- `serverContent.groundingMetadata`
- `toolCall.functionCalls[]` → `onToolCall`
- `toolCallCancellation.ids[]` → `onToolCallCancel`
- `goAway.timeLeft` (server-initiated shutdown warning)
- `sessionResumptionUpdate.newHandle` (used to resume after reconnect)

Outbound (via SDK `Session` methods):

- `sendRealtimeInput({ audio })` — 16 kHz PCM input
- `sendRealtimeInput({ text })` — also used for text turns (legacy `sendClientContent` was abandoned; see comment at line 297 about `gemini-3.x-flash-live-preview` rejecting it with WS close 1011)
- `sendRealtimeInput({ video })` / `{ media }` — image attachments
- `sendToolResponse({ functionResponses })`
- `session.close()`

## 3. Tool Model

`ToolDefinition` (`types/tool.ts`, 59 LOC) is the public contract:

```ts
{ name; description; parameters: z.ZodSchema; execution: 'inline'|'background';
  pendingMessage?; timeout?; execute(args, ctx): Promise<unknown>; }
```

Registration: tools passed in `VoiceSession` config → forwarded to transport → converted by `zod-to-schema.ts` (112 LOC) into Gemini `functionDeclarations`. Tool calls flow: Gemini → `onToolCall` → `ToolCallRouter` (245 LOC, dispatches inline vs. background) → `ToolExecutor` (206 LOC, validates args, runs `execute`) → `sendToolResponse` back. Background tools hand off to a Vercel-AI-SDK subagent runtime.

## 4. Audio Handling

Mixed. Gemini transport stays at the message level — base64 PCM in/out, never decoded. `client-transport.ts` (the `ws` server bodhi exposes to its browser/CLI client) receives raw `Buffer` binary frames and uses a 56-LOC `AudioBuffer` ring to buffer during transfers/reconnects. No DSP, no resampling, no codecs. Audio format is fixed: 16 kHz in, 24 kHz out, mono, 16-bit PCM.

## 5. Dependencies

| npm package | Purpose | .NET equivalent |
|---|---|---|
| `@google/genai` ^1.41 | Gemini SDK incl. Live WS | **`Google_GenerativeAI.Live` v3.6.6** (Apr 2026, 38k downloads) or **`Ai.Tlbx.VoiceAssistant.Provider.Google`** — both have explicit Gemini Live WS support |
| `ws` ^8.18 | WebSocket server (browser client side) | `System.Net.WebSockets` (BCL) / Kestrel WS middleware |
| `zod` ^3.24 | Schema validation + Gemini fn-decl source | DataAnnotations + System.Text.Json schema, or `NJsonSchema` |
| `ai` ^4.3 + `@ai-sdk/google` ^1.2 | Vercel AI SDK for subagents | Semantic Kernel or Microsoft.Extensions.AI (rough parity) |
| `openai` ^6.25 | OpenAI Realtime transport | `OpenAI` official .NET SDK (Realtime preview API) |
| `dotenv` | env loading | `Microsoft.Extensions.Configuration` |
| `write-file-atomic` | atomic JSON writes | trivial helper, 30 LOC |
| `@anthropic-ai/claude-agent-sdk` (optional) | example only | `Anthropic.SDK` or claude-code subprocess |

**Critical confirmed:** Gemini Live for .NET exists as a maintained package. This is the biggest unknown removed — we do not have to reimplement the wire protocol.

## 6. Public API Surface (what sutando consumes)

Stated imports in `voice-agent.ts`: only `VoiceSession`, `MainAgent`, `ToolDefinition`. Looks tiny — but the file (1,416 LOC) reaches into bodhi internals:

- `session.sessionManager.state` and `.transitionTo('CLOSED')`
- `session.conversationContext.items` (private `_items` mutated via `length = 0`)
- `session.eventBus.subscribe('turn.end' | 'turn.interrupted')`
- `session.transport.sendFile(...)` per video frame
- `session.clientConnected`, `session.close('user_hangup')`

A "drop-in" .NET `VoiceSession` shim is insufficient — the port must reproduce the session state machine, EventBus, ConversationContext semantics, and reconnect-replay path.

## 7. Lifecycle / State Machine

`SessionManager` (133 LOC) defines an explicit state table: `IDLE → CONNECTING → CONNECTED → CLOSED`, with CLOSED→CONNECTING as the only re-entry. Reconnect deadline is hard-coded to 30s (`VoiceSession.RECONNECT_DEADLINE_MS`). Recovery flow:

1. `goAway` from server (or WS close/error) → `handleGoAway`/`handleTransportClose`
2. Disconnect, then `connect()` with stored `resumptionHandle` (Gemini-side resume) OR full replay of `ReplayItem[]` (text/tool-call/tool-result/file/transfer) via `replayHistory`
3. Greeting suppressed via `_skipNextGreeting` so the agent doesn't say "Hi!" mid-conversation

Error recovery is non-trivial — there are clear scars in the code (sutando's voice-agent.ts has comments like "fix/reconnect-promise-deadline" — that's the merge in this exact commit) showing this area has been hardened over many iterations.

## 8. Effort Estimate

**MEDIUM, upper end. ~2,500–3,500 LOC C#, 3–4 weeks to reach sutando-parity.**

- **Transport-only** (`GeminiLiveTransport` + `LLMTransport` interface + `AudioBuffer` + zod→JsonSchema equivalent + tool types) ≈ 1,200 LOC TS → ~800–1,000 LOC C#, **~1 week**. Leaves sutando rewriting orchestration itself.
- **VoiceSession-parity** (orchestrator + state machine + EventBus + ConversationContext + TranscriptManager + ToolCallRouter + ClientTransport + reconnect/replay + DirectiveManager + InteractionModeManager) ≈ 3,500 LOC TS → ~2,500 LOC C# plus tests. **3–4 weeks**, including reconnect/replay edge cases bodhi has spent commits hardening.
- **Full feature-parity** (+ OpenAI Realtime + STT providers + behaviors + memory distillation + subagent runtime on SK/M.E.AI) is **6+ weeks** — LARGE bucket. Probably unnecessary; sutando uses the Gemini path only.

Compressors: SDK owns the wire protocol; no audio DSP; design is already provider-agnostic (clean `LLMTransport` interface); ~1,070 LOC of TS interfaces map directly to C# records.

Expanders: deep sutando→bodhi coupling (Section 6); reconnect/replay is empirically subtle (multiple fix commits at this SHA); no direct Vercel-AI-SDK equivalent — Semantic Kernel needs an adapter.

**Suggested first slice:** transport + minimal `VoiceSession` shell (~1 week), then iterate on reconnect/replay and EventBus parity against actual sutando call sites.
