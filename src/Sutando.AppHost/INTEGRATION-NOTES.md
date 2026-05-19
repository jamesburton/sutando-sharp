# Sutando.AppHost integration notes

`Sutando.AppHost` is a .NET Aspire 13 orchestrator for the local-inference
fleet (vLLM + speaches + kokoro-fastapi + the Sutando.Cli agent). It is built
in this worktree but is **not registered in `Sutando.sln`** — the integrating
merger needs to add it (per task constraint: leave `Sutando.sln` and
`Sutando.Cli` UNMODIFIED inside this worktree).

This file enumerates what the merger has to do, in order, to take the AppHost
the rest of the way to a green build + green tests on `main`.

---

## 1. Add the project to the solution

```pwsh
dotnet sln Sutando.sln add src/Sutando.AppHost/Sutando.AppHost.csproj
```

That is the only `Sutando.sln` mutation required. No project-reference edits
needed on `Sutando.Cli.csproj` either — the AppHost references the CLI, not
the other way around.

## 2. (Optional) wire a `sutando apphost` CLI verb

The AppHost is a standalone executable (`Sutando.AppHost.exe`) so an operator
can launch it directly with `dotnet run --project src/Sutando.AppHost`. If we
want to expose it as a `sutando apphost` subcommand for ergonomics, the
forwarder in `src/Sutando.Cli/Commands.cs` is a one-method add along the
existing pattern:

```csharp
// In Commands.cs — match the existing voice/api/dashboard verb pattern.
public static async Task<int> RunAppHostAsync(string[] args, CancellationToken ct)
{
    // The static DistributedApplication entry-point. Equivalent to
    // `dotnet run --project src/Sutando.AppHost` but in-process so we
    // inherit the parent CLI's logging configuration.
    await Aspire.Hosting.DistributedApplication
        .CreateBuilder(args)
        .Build()
        .RunAsync(ct);
    return 0;
}
```

This requires `Sutando.Cli` to take a `ProjectReference` on `Sutando.AppHost`,
which is fine post-merger (the constraint is "do not modify Sutando.Cli IN
THIS WORKTREE", not "never modify"). The merger can either land the verb in
the same merge commit or defer it to a follow-up — both are clean.

## 3. README updates

The README has a per-component env-var table (under "Configuration") and a
"Build from source" block. Both need additions:

### New env-var rows for the Configuration table

| Env var | Used by |
|---|---|
| `LLM_MODEL` | `sutando apphost` — vLLM model id (default `Qwen/Qwen3-8B-AWQ`) |
| `STT_MODEL` | `sutando apphost` — speaches whisper model (default `Systran/faster-whisper-medium.en`) |
| `TTS_VOICE` | `sutando apphost` — kokoro-fastapi voice id (default `af_bella`) |
| `RUNTIME_DEVICE` | `sutando apphost` — `gpu` (default) or `cpu` |

(These names match the Aspire `AddParameter` resource names — Aspire normalises
`LlmModel` → `LLM_MODEL` etc. at runtime.)

### New "Run the local-inference stack" section

```pwsh
# Boot the full local-inference fleet (vLLM + speaches + kokoro-fastapi + agent)
# under Aspire's dev-loop with OTLP dashboard at https://localhost:17181
dotnet run --project src/Sutando.AppHost

# Or via the Aspire CLI for nicer logging / lifecycle controls
aspire run --apphost src/Sutando.AppHost/Sutando.AppHost.csproj

# Export a docker-compose.yml for hosts without Aspire
aspire publish --apphost src/Sutando.AppHost/Sutando.AppHost.csproj \
               --output-path aspire-output

# Then on the target host:
cd aspire-output && docker compose up -d
```

The first boot will pull ~7 GB of container images and download model weights
on first request. GPU mode requires NVIDIA Container Toolkit on the host.

## 4. Build / test verification

The AppHost is not in `Sutando.sln`, so the existing `dotnet build Sutando.sln`
and `dotnet test tests/Sutando.Tests/Sutando.Tests.csproj` paths from the
README:

- The solution build is unaffected (verified in-worktree: 0 warnings, 0
  errors).
- The tests project picks up the AppHost via a `ProjectReference` added in
  this commit (see `tests/Sutando.Tests/Sutando.Tests.csproj` diff). So
  `dotnet test tests/Sutando.Tests/Sutando.Tests.csproj` continues to be the
  one-line test command — the new tests under `tests/Sutando.Tests/AppHost/`
  run as part of the same suite.

Post-merge, after step 1 above, `dotnet build Sutando.sln` will also build
the AppHost.

## 5. Pinned versions

| Package | Version | Reason |
|---|---|---|
| `Aspire.AppHost.Sdk` | `13.2.0` | Matches `dotnet new aspire-apphost` template default on .NET SDK 10.0.300. |
| `Aspire.Hosting.Docker` | `13.3.3` | Required for `aspire publish` → `docker-compose.yml`; only 13.3.x ships the compose publisher. The minor mismatch with the 13.2 SDK is intentional and verified-clean. |
| `Aspire.Hosting.Testing` | `13.3.3` | Test-time only; aligned with the Docker integration. |

If the merger lands after Aspire 14 ships, `aspire update` rebases all three
in one shot.

## 6. Service-discovery contract (for task #17, `Sutando.LocalInference.OpenAI`)

The AppHost emits the following environment variables into the `agent`
container at start time (via Aspire's `WithReference(EndpointReference)`):

| Variable | Value | Maps to |
|---|---|---|
| `services__llm__http__0` | `http://localhost:<dynamic-port>` | vLLM OpenAI-compatible API |
| `services__stt__http__0` | `http://localhost:<dynamic-port>` | speaches `/v1/audio/transcriptions` |
| `services__tts__http__0` | `http://localhost:<dynamic-port>` | kokoro-fastapi `/v1/audio/speech` |

`Sutando.LocalInference.OpenAI` (in flight as task #17) will own the
`HttpClient` → `IChatClient` / `ISpeechToTextClient` / `ITextToSpeechClient`
conversion. Until that ships, an agent can read these via standard
`IConfiguration`:

```csharp
var llmBaseUrl = builder.Configuration["services:llm:http:0"];
// ...construct an HttpClient against llmBaseUrl/v1/chat/completions
```

The AppHost itself is decoupled from #17 — it ships and tests independently.

## 7. Known gaps

- **vLLM CPU mode**: the GPU image (`vllm/vllm-openai:latest`) is the default.
  CPU-only operators need to swap the `FROM` line in
  `dockerfiles/llm/Dockerfile` to `vllm/vllm-openai:cpu` — the
  `RUNTIME_DEVICE=cpu` env var is read by the AppHost but isn't yet branched
  in the Dockerfile (that's deferred until we have a real CPU-only smoke run
  to validate the build).
- **Health checks**: vLLM exposes `/health`, speaches `/health`, and
  kokoro-fastapi `/health`. Wiring `WithHealthCheck()` against each is a
  follow-up that doesn't block the orchestration shape.
- **GPU pinning per container**: deferred. All three GPU containers currently
  share the host's default GPU; pinning is a `WithContainerRuntimeArgs("--gpus", ...)`
  call away when we need it.
