# P10 hardening — integration notes for the merger

Branch: `worktree-agent-aeb5d0311061c12a4` (forked from `wip/foundation`).

## Solution file

**No `Sutando.sln` changes required.** No new projects were introduced; every change lands
inside existing projects.

## Project-reference changes (a quick clean restore will pick these up)

- `src/Sutando.Core/Sutando.Core.csproj`
  - Adds a `ProjectReference` to `..\Sutando.Skills\Sutando.Skills.csproj` so `TaskRunner`
    can optionally route tasks through a `SkillRegistry` before falling through to the
    `IAgentExecutor`. The constructor parameter is optional + named, so every existing call
    site compiles unchanged.

No other `.csproj` reference graph changes.

## New public surface (callable from any project that already references `Sutando.Workspace`)

- `Sutando.Workspace.WorkspaceInitializer` + `WorkspaceInitializerOptions` +
  `InitializationResult` + `PrerequisiteCheck` + `IPrerequisiteProbe` +
  `DefaultPrerequisiteProbe`. Powers `sutando init`. Living in `Sutando.Workspace`
  keeps the orchestration testable via plain project reference (referencing the CLI
  exe from the test project breaks `WebApplicationFactory<Program>` host discovery).

- `Sutando.Core.TaskRunner` now exposes a `static string? ExtractTrigger(string body)`
  helper used by the trigger heuristic (first whitespace-delimited token of the body,
  lowercased). Public + static so tests and future routing code can exercise it.

## Commits

Three commits in order:

1. `1bdee2c core: route tasks through SkillRegistry before the agent executor`
2. `6137ca4 cli: add `sutando init` wizard for fresh-install ergonomics`
3. (CI commit, applied next.)

## Test signal

- Baseline before P10: 472 passing / 14 skipped on the merger's machine; locally
  measured at 473/13.
- After P10 (excluding the pre-existing host-flaky `WindowsClipboardServiceTests`):
  509 passing / 12 skipped. Delta: +36 tests across both TFMs from the three new
  test classes (`TaskRunnerTests` skill-routing additions + the theory-table for
  `ExtractTrigger`; `WorkspaceInitializerTests`).
- `WindowsClipboardServiceTests` is environment-flaky (clipboard-history tools
  intercept SetText; the test's `Skip.IfNot` probe is itself race-prone). Not
  introduced by this branch.

## Manual smoke that the merger may want to repeat

```pwsh
# Build + test
dotnet build Sutando.sln -c Release
dotnet test  Sutando.sln -c Release --no-build

# `sutando init` end-to-end (writes a fresh workspace under $TMP)
$env:SUTANDO_WORKSPACE = Join-Path $env:TEMP "sutando-init-merge-smoketest"
dotnet run --project src/Sutando.Cli -c Release --no-build -- init --yes
```

Expect: workspace with `tasks/ results/ state/ state/cores/ notes/ data/ logs/`
plus a `.env.example` containing 17 known env-var placeholders, and a heartbeat
file at `state/cores/<host>.alive`.

## CI workflow changes (`.github/workflows/ci.yml`)

- Added `actions/cache@v4` for `~/.nuget/packages` before restore. Cuts cold-restore
  cost roughly in half on warm cache hits.
- Added `dotnet workload restore` is **not** needed — the Tests project's conditional
  TFM already keeps the Windows TFM off non-Windows runners.
- Added Playwright `chromium` install on every OS (after build). The skipped browser
  integration tests will pick it up locally if you unskip them.
- `dotnet test` now emits TRX with `LogFileName=test-results-$(TargetFramework).trx`
  so the per-OS upload artifact captures both TFMs on the Windows runner without
  collision.
- `Pack` + `Smoke test (dnx round-trip)` now run only on `windows-latest` (single
  canonical nupkg). Smoke test pins to the freshly-built version via a job-scoped
  `VersionSuffix=ci.${{ github.run_number }}` env var.
- Workflow YAML validated by `yaml.safe_load`.

Nothing else for the merger — just `git merge` and re-restore.
