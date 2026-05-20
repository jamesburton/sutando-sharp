# Sutando.Pipeline — Integration Notes

This document tracks how `Sutando.Pipeline` plugs into the rest of the solution and what the
follow-up integration phase has to wire up. It exists so the next contributor can pick up
the file without re-deriving the rationale from commit history.

## Solution wiring

`Sutando.sln` is **deliberately unmodified** in this slice — per the task brief. The new
project is pulled into `dotnet build Sutando.sln` transitively through
`tests/Sutando.Tests/Sutando.Tests.csproj`, which now includes:

```xml
<ProjectReference Include="..\..\src\Sutando.Pipeline\Sutando.Pipeline.csproj" />
```

When the follow-up integration phase wires `Sutando.Pipeline` into the consumer projects
(`Sutando.Voice` for the WebSocket-driven local pipeline; eventually `Sutando.Realtime` as
an alternative transport), add the project to `Sutando.sln` directly with:

```pwsh
dotnet sln Sutando.sln add src/Sutando.Pipeline/Sutando.Pipeline.csproj
```

Until then, the transitive path keeps both build and test on the happy path.

## CLI wiring (deferred)

There is no new CLI verb associated with this slice — `sutando voice --local` (the planned
mode that boots the full local pipeline) is the natural integration point and will be added
in the follow-up phase that lands the local-inference end-to-end demo (`#21` in
`docs/local-stack-scope.md`).

When that lands:

- `src/Sutando.Voice/Sutando.Voice.csproj` adds a `<ProjectReference>` to `Sutando.Pipeline`.
- The voice-server boot path detects `--local` and builds a `Pipeline` rather than a
  `GeminiLiveTransport` per connection.
- `src/Sutando.Cli/Commands.cs` grows a `--local` switch on the `voice` verb, threaded
  through to the voice-server boot.

## Interruption-propagation convention

This is the load-bearing semantic decision for the pipeline. It is **not** ambiguous in
this slice — we picked one convention and every stage in `src/Sutando.Pipeline/Stages/`
implements it. Future stage authors must follow the same shape.

**Convention:**

1. `ControlFrame.Interrupt` is an in-band signal — it flows downstream as a normal
   `PipelineFrame` through the same channel-per-link plumbing.
2. Stages with in-flight per-turn work (`ChatStage`, `TextToSpeechStage`, future
   streaming-STT variants) own a per-turn `CancellationTokenSource` that is linked to
   the pipeline-level `CancellationToken`.
3. On observing an `Interrupt` frame in its input, a stage:
   - cancels its per-turn CTS (which aborts the in-flight `IChatClient` /
     `ITextToSpeechClient` call),
   - discards / flushes any per-turn buffer it owns (the chat stage records the partial
     assistant response in history; the TTS stage drops its accumulated text buffer),
   - forwards the `Interrupt` frame downstream so the next stage can react too,
   - resumes consuming its input stream for the next turn.
4. The pipeline-level `CancellationToken` is **not** cancelled by an interrupt — only
   per-turn CTSs are. The pipeline stays live.

This mirrors Pipecat's frame-based interruption (`InterruptionFrame` → `_start_interruption`)
without copying its protocol verbatim. Stages that don't have per-turn work simply forward
the frame downstream and continue — the "transparent composition" rule.

## Constraints honoured

- `Sutando.sln` is unchanged.
- `Sutando.Cli` is unchanged.
- Top-level `README.md` is unchanged.
- All tests pass on both `net10.0` and `net10.0-windows10.0.19041.0`.
- No new files outside `src/Sutando.Pipeline/` and `tests/Sutando.Tests/Pipeline/`.
- `MEAI001` + `SUTANDO001` suppressed at the csproj level so the experimental MEAI surfaces
  (`ISpeechToTextClient`, `ITextToSpeechClient`) consumed by the concrete stages don't
  pollute consumers with per-call warnings.
