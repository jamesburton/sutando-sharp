# Sutando.Proactive — Integration Notes

This document tracks how `Sutando.Proactive` plugs into the rest of the solution and what
the follow-up integration phase has to wire up. It exists so the next contributor can pick
up the file without re-deriving the rationale from commit history.

## Why this library exists

`docs/upstream-feature-gap.md` row `proactive-loop` calls this out as the single biggest
gap between sutando-sharp and the upstream Python/TypeScript project:

> The autonomous build loop — runs every 5 min, drives every other skill. **Highest-impact
> missing piece** — nothing autonomous lights up without it.

The same doc pairs it with `schedule-crons` ("Cron driver. Required before any periodic
skill is useful.") and proposes a new `Sutando.Proactive` project to host both. That is
this library.

This slice ports the **scheduling chassis** only. Both upstream skills are decomposable
into "managed plumbing" (load config → tick a clock → fire a callback → keep a hosted
service alive) and "LLM-driven body" (the 11-step proactive procedure: pick the
highest-ROI work, run the executor, post heartbeats, …). The plumbing is in this csproj.
The body stays on the LLM side and plugs in via `IProactivePass`.

## What ships now

| Type | Role |
|------|------|
| `CronEntry` | Record mirroring upstream `crons.json` shape (`name`, `cron`, `prompt` xor `prompt_skill`). |
| `CronConfigLoader` | Reads `{workspace}/crons.json`, falls back to `crons.example.json`. Invalid rows are skipped with a warning, not fatal. |
| `ICronScheduler` / `CronScheduler` | NCrontab-backed scheduler driven through `TimeProvider` (testable with `FakeTimeProvider`). Re-arms after each fire; exceptions from the callback never kill the scheduler. |
| `IProactivePass` / `ProactivePassContext` | The host-pluggable per-pass contract. Carries workspace, triggering cron entry, services, UTC start time. |
| `NoopProactivePass` | Default `IProactivePass` — logs and exits. Useful as a sentinel until the host wires its own. |
| `ProactiveBackgroundService` | `BackgroundService` that loads config, starts the scheduler, dispatches a fresh `IProactivePass` from a per-fire DI scope. Honours `IHostApplicationLifetime` for clean shutdown. |
| `AddSutandoProactive` | DI extension method registering the above. |

## What is out of scope (deliberately)

This is plumbing; the upstream 11-step proactive procedure stays LLM-driven. Specifically
**not** in this csproj:

- **Executor wiring.** No reference to `Sutando.Core`, no `IAgentExecutor` coupling. The
  host owns the executor and surfaces it to its `IProactivePass` via the
  `ProactivePassContext.Services` IServiceProvider.
- **The 11-step loop body** — status-file writes, task-folder polling, health checks,
  build-log updates, bot2bot heartbeats. All of that lives in the host's
  `IProactivePass` implementation (or in a future `Sutando.Proactive.DefaultPass`
  follow-up if we decide some of it should be common).
- **Quota awareness.** The upstream proactive loop reads `quota-tracker` to decide pass
  depth; that lives in the host's pass body if/when ported.
- **Task-watcher start.** Upstream `schedule-crons` step 4 ensures a streaming task
  watcher is alive. `Sutando.Bridge` already owns the watcher pattern (`TaskWatcher`);
  the integrating host is the right place to coordinate "if no watcher, start one,"
  not the cron scheduler.
- **CLI verbs** (`sutando proactive`, `sutando cron`). Per the task brief, the CLI
  wiring is a separate commit.

## Solution wiring (deferred)

Per the task brief, `Sutando.sln` is **deliberately unmodified** in this slice. The new
project is pulled into `dotnet build Sutando.sln` transitively through
`tests/Sutando.Tests/Sutando.Tests.csproj`, which now includes:

```xml
<ProjectReference Include="..\..\src\Sutando.Proactive\Sutando.Proactive.csproj" />
```

This matches the pattern already used by `Sutando.Pipeline` (see
`src/Sutando.Pipeline/INTEGRATION-NOTES.md`). When the integrator picks this up, add
the csproj to the solution directly:

```pwsh
dotnet sln Sutando.sln add src/Sutando.Proactive/Sutando.Proactive.csproj
```

## CLI wiring (deferred — follow-up commit)

The follow-up commit that lands `sutando proactive` will:

1. Reference `Sutando.Proactive` from `src/Sutando.Cli/Sutando.Cli.csproj`.
2. In whichever DI bootstrap the CLI uses (probably alongside the existing skill
   registration), call:

   ```csharp
   builder.Services.AddSutandoProactive();
   // optionally:
   builder.Services.AddScoped<IProactivePass, YourPassImplementation>();
   ```

3. Add a `sutando proactive run` verb that boots a `Host`, registers the services
   above, and parks until Ctrl+C. The `BackgroundService` does the rest.
4. Optionally add a `sutando cron list` verb that just calls
   `new CronConfigLoader().Load(workspace)` and prints the entries — useful for
   confirming a `crons.json` is being picked up.

## Plug-point contract: `IProactivePass`

A pass receives a `ProactivePassContext` with:

- `Workspace` — the resolved `WorkspaceDirectory` (read tasks/, write to state/, etc.).
- `Services` — the per-pass DI scope. Resolve `IAgentExecutor`, an `HttpClient`, an
  `ISkillRegistry`, etc. here; the host registers them, this library doesn't.
- `TriggeringEntry` — the `CronEntry` that fired, or `null` for ad-hoc invocations.
- `UtcNow` — the wall-clock time the pass started.

The pass is expected to be **non-throwing for non-fatal errors** — exceptions are caught
and logged so the scheduler keeps running, but the pass itself should treat a single
failure as "log and continue, fire next time."

> **TODO (follow-up):** if/when we want a pass to surface skills by name (e.g. honour
> `CronEntry.PromptSkill` → invoke that skill), the pass implementation will need to
> resolve `ISkillRegistry` from `context.Services`. That requires the host to take a
> dependency on `Sutando.Skills` and register the registry — which it already does in
> `Sutando.Cli`. No change needed to this library; the surface area is intentionally
> kept minimal.

## Constraints honoured (per task brief)

- `Sutando.sln` unchanged.
- `Sutando.Cli` unchanged.
- `Sutando.Skills` unchanged (no extensions to `ISkill` / `SkillContext` / `SkillRegistry`).
- No dependency on `Sutando.Core` (no `IAgentExecutor` reference).
- New NuGet dependencies (`NCrontab`, `Microsoft.Extensions.Hosting.Abstractions`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`) are pulled from `nuget.org`.
  `Microsoft.Extensions.TimeProvider.Testing` is added to `tests/Sutando.Tests/` only,
  not to the production project.
- Test layout under `tests/Sutando.Tests/Proactive/` mirrors the
  `tests/Sutando.Tests/Skills/Cloud/` pattern.

## Design note: `StartAsync` override vs. `ExecuteAsync`

`ProactiveBackgroundService` does its setup (load cron config, start the scheduler) in an
override of `BackgroundService.StartAsync`, **not** in `ExecuteAsync` as the conventional
`BackgroundService` pattern would have it. The reason: the base `StartAsync` calls
`ExecuteAsync` via `Task.Run(...)`, which means `await host.StartAsync()` returns before
`ExecuteAsync` has executed even a single line. Any caller that fires immediately after
`StartAsync` — production schedulers, but more visibly the test that calls
`fake.Advance(...)` — races the thread-pool launch and sees no work scheduled.

Performing setup synchronously in our `StartAsync` override before delegating to
`base.StartAsync(...)` guarantees "await StartAsync returned → scheduler is live." The
scheduler then drives its timers off `TimeProvider`, so cron firings are clock-driven
and the inert `ExecuteAsync` just parks on the shutdown signal.

> Future maintainers: don't "fix" this by moving setup back into `ExecuteAsync`. It will
> reintroduce the race. The source comment on `StartAsync` also explains this.

## Notes for the integrator

- **NuGet:** `NCrontab` (3.3.3) is a new transitive dependency. It restores cleanly from
  `nuget.org` with the existing `NuGet.config`; nothing extra needs to land in
  `nuget-local/`. The package is small (~30 KB), Apache 2.0, and the de-facto cron
  parser for .NET — well-established choice over Quartz.NET for this lightweight use case.
- **UTC throughout.** Cron expressions are parsed and evaluated against
  `TimeProvider.GetUtcNow()`. If you ever need wall-clock-local semantics (e.g.
  "morning briefing at 06:57 local"), convert at the edge — the upstream config uses
  UTC and we keep parity.
- **`TimeProvider` not `PeriodicTimer`.** `PeriodicTimer.WaitForNextTickAsync()` reads
  the system clock unconditionally and can't be driven by a fake — `CronScheduler` uses
  `TimeProvider.CreateTimer` so `FakeTimeProvider` in tests can advance time
  deterministically.
