# Integration notes — Sutando.Api + Sutando.Dashboard

This worktree adds two new ASP.NET Core 10 minimal-API projects:

- `src/Sutando.Api/`         — HTTP task submission on port `7843`.
- `src/Sutando.Dashboard/`   — read-only status dashboard with SignalR live updates on port `7844`.

Per the task spec, **`Sutando.sln` and `Sutando.Cli` are unmodified**. Everything needed to
wire these projects into the solution and the CLI lives here.

---

## 1. Add to solution

From the repo root, run:

```pwsh
dotnet sln Sutando.sln add src/Sutando.Api/Sutando.Api.csproj
dotnet sln Sutando.sln add src/Sutando.Dashboard/Sutando.Dashboard.csproj
```

Both projects are non-packable (no `dotnet pack` impact) and target plain `net10.0`.

## 2. Wire into `Sutando.Cli`

### 2a. ProjectReferences

Add the following two lines to the existing `<ItemGroup>` in
`src/Sutando.Cli/Sutando.Cli.csproj`:

```xml
<ProjectReference Include="..\Sutando.Api\Sutando.Api.csproj" />
<ProjectReference Include="..\Sutando.Dashboard\Sutando.Dashboard.csproj" />
```

### 2b. Add the verbs in `src/Sutando.Cli/Commands.cs`

```csharp
using Sutando.Api;
using Sutando.Dashboard;

// ... inside the static Commands class:

public static async Task<int> ApiAsync(string[] args)
{
    using var cts = NewSigIntCts();
    // Drop the leading "api" verb so --port etc. flow through cleanly.
    var forwarded = args.Length > 1 ? args[1..] : [];
    await ApiCommand.RunAsync(forwarded, cts.Token).ConfigureAwait(false);
    return 0;
}

public static async Task<int> DashboardAsync(string[] args)
{
    using var cts = NewSigIntCts();
    var forwarded = args.Length > 1 ? args[1..] : [];
    await DashboardCommand.RunAsync(forwarded, cts.Token).ConfigureAwait(false);
    return 0;
}
```

### 2c. Dispatch in `src/Sutando.Cli/Program.cs`

Add two arms to the existing `switch` in `Program.cs`:

```csharp
"api" => await Commands.ApiAsync(args).ConfigureAwait(false),
"dashboard" => await Commands.DashboardAsync(args).ConfigureAwait(false),
```

### 2d. Help banner

Add two lines inside `PrintHelp` in `src/Sutando.Cli/Program.cs`, alongside the existing
COMMANDS list:

```csharp
Console.WriteLine("  api [--port <n>]          Serve the HTTP task-submission API (default :7843, $SUTANDO_API_TOKEN for auth).");
Console.WriteLine("  dashboard [--port <n>]    Serve the read-only status dashboard + SignalR hub (default :7844).");
```

---

## 3. Configuration

Both verbs accept a `--port <n>` flag, fall back to a config key
(`ApiPort` / `DashboardPort`), then to an env var
(`SUTANDO_API_PORT` / `SUTANDO_DASHBOARD_PORT`), then to the spec defaults.

The API reads `SUTANDO_API_TOKEN` for bearer auth. When unset it runs open and logs a
warning on startup. The dashboard has no auth surface — by intent (`localhost` read-only).

Workspace resolution honours `SUTANDO_WORKSPACE` exactly like the rest of the suite.

---

## 4. Known limitations to follow up on

- **Env-var mutation in DI registration.** Both `ApiCommand.ConfigureServices` and
  `DashboardCommand.ConfigureServices` set `Environment.SetEnvironmentVariable(SUTANDO_WORKSPACE, ...)`
  when the test-only `WorkspaceRoot` config key is provided. This is a code smell — the
  cleanest fix is to add a `WorkspaceDirectory.FromPath(string)` factory method to
  `Sutando.Workspace` next time someone touches that project, then have these projects
  use it directly instead of mutating the env var. The test isolation via
  `[Collection("Workspace")]` (`tests/Sutando.Tests/Api/WorkspaceCollection.cs`) makes
  this acceptable today.

- **SignalR client transport.** Production browsers will negotiate WebSockets fine, but
  `WebApplicationFactory.Server.CreateHandler()` does not support them, so the dashboard
  tests force `HttpTransportType.LongPolling`. Keep this in mind when extending hub tests.

- **Skipped upstream surface.** Out of scope for this slice but worth noting for parity:
  Twilio voice/SMS/transcription webhooks, voicemail handling, agent-to-agent
  `callback_url` (SSRF gating included), `/voice/toggle`, `/answer` for pending
  questions, `/media/`, identity / avatar, contextual chips. Slot in as separate
  follow-up verbs when needed.

---

## 5. Tests

Tests live under `tests/Sutando.Tests/Api/` and `tests/Sutando.Tests/Dashboard/`. The test
project's csproj was updated to add:

- `PackageReference` for `Microsoft.AspNetCore.Mvc.Testing` and
  `Microsoft.AspNetCore.SignalR.Client` (matching the .NET 10 versions).
- `ProjectReference` to both new projects.

The two new test classes belong to the `[Collection("Workspace")]` collection so they
serialize across the assembly (see Known Limitations above).

Run them with:

```pwsh
dotnet test tests/Sutando.Tests/Sutando.Tests.csproj
```

The new tests pass on both `net10.0` and `net10.0-windows10.0.19041.0`.
