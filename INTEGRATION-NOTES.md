# Integration notes (from parallel-agent worktrees)

These notes were produced by isolated worktree agents that built new projects
in parallel against `a89cf32`. The instructions below are tracked here for the
duration of the integration; the file is deleted once `Sutando.sln`, the CLI
wiring, and the documentation are all in place.

---

## Sutando.Browser — integration notes

This worktree adds a new `src/Sutando.Browser/` library. The agent left
`Sutando.sln` untouched; the project still builds because
`tests/Sutando.Tests/Sutando.Tests.csproj` has a `<ProjectReference>` to it,
which pulls it into the test build graph.

### 1. Add `Sutando.Browser` to `Sutando.sln`

```pwsh
dotnet sln Sutando.sln add src/Sutando.Browser/Sutando.Browser.csproj --solution-folder src
```

### 2. Wire the CLI verb

`src/Sutando.Browser/BrowserCommand.cs` exposes
`BrowserCommand.RunAsync(string[] args, BrowserOptions? options = null, CancellationToken ct = default)`
ready for `Sutando.Cli` to call. When the CLI verb is added:

- Add a `<ProjectReference Include="..\Sutando.Browser\Sutando.Browser.csproj" />`
  to `src/Sutando.Cli/Sutando.Cli.csproj`.
- Register a `browser` subcommand whose handler forwards its args (URL +
  optional action strings) to `BrowserCommand.RunAsync`.

### 3. Playwright runtime install

`Microsoft.Playwright` (v1.59.0) ships an in-package driver but the actual
browser binaries are downloaded on first use. CI runners that exercise the
integration tests will need:

```pwsh
pwsh bin/Debug/net10.0/playwright.ps1 install
```

The integration tests in `tests/Sutando.Tests/Browser/BrowserSessionIntegrationTests.cs`
are marked `[Fact(Skip = "requires playwright install; enable locally")]` so
CI without the browser blobs still passes; remove the `Skip` argument once
Playwright install is part of CI.

### 4. Grammar deviations from upstream `src/browser.mjs`

`BrowserAction.Parse` is strict where upstream was permissive:

- **`wait:<ms>`** — strict non-negative-integer parse. Upstream uses
  `parseInt(action.slice(5)) || 2000` and silently falls back to `2000ms` for
  empty / non-numeric input; we throw `FormatException` instead.
- **Unknown verbs** — upstream logs to stderr and continues; we throw
  `FormatException` so callers fail fast.
- **Bare verbs with trailing colon** (e.g. `text:`) — upstream's exact-match
  branch would also reject these; we make the error message explicit.
- **`Navigate`** is a C#-only action type. Upstream takes the URL as the first
  positional CLI argument, never as a colon expression, so `Parse` never emits
  a `Navigate` and we throw if anyone sends `navigate:...`.

---

## Sutando.Channels.Cli — integration notes

This worktree adds a new project `Sutando.Channels.Cli` plus a `CliChatChannel`
implementation but does not wire the new project into `Sutando.sln` or
`Sutando.Cli`.

### 1. Add the project to `Sutando.sln`

```pwsh
dotnet sln Sutando.sln add src/Sutando.Channels.Cli/Sutando.Channels.Cli.csproj --solution-folder src
```

(Or splice in by hand using the pre-generated GUID `{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}`.)

### 2. Add the project reference to `src/Sutando.Cli/Sutando.Cli.csproj`

```xml
<ProjectReference Include="..\Sutando.Channels.Cli\Sutando.Channels.Cli.csproj" />
```

### 3. Wire the `chat` subcommand

Add to `Commands.cs`:

```csharp
public static async Task<int> ChatAsync(string version, string[] args)
{
    var ws = WorkspaceDirectory.Resolve();

    var timeout = TimeSpan.FromMinutes(5);
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--timeout"
            && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            && secs > 0)
        {
            timeout = TimeSpan.FromSeconds(secs);
            break;
        }
    }

    var channel = new Sutando.Channels.Cli.CliChatChannel(
        ws,
        new Sutando.Channels.Cli.CliChatChannelOptions
        {
            Version = version,
            ResultTimeout = timeout,
        });
    using var cts = NewSigIntCts();
    await channel.RunAsync(cts.Token).ConfigureAwait(false);
    return 0;
}
```

Add to `Program.cs` dispatch:

```csharp
"chat" => await Commands.ChatAsync(version, args).ConfigureAwait(false),
```

Plus a help-banner line:

```
chat [--timeout <s>]      Interactive REPL: send chat tasks and wait for results.
```

---

## Sutando.Platform.Windows — integration notes

New project at `src/Sutando.Platform.Windows/Sutando.Platform.Windows.csproj`.

- TFM `net10.0-windows10.0.19041.0` (overrides repo-wide `net10.0`).
- Restores on non-Windows machines via `<EnableWindowsTargeting>true</EnableWindowsTargeting>`; will not *build* off-Windows.
- Public types are `[SupportedOSPlatform("windows")]`.
- New package references:
  - `System.Drawing.Common` 9.0.0 — BitBlt-driven screen capture.
  - `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 — toast notifications without WindowsAppSDK / WinUI3.

### 1. Add to `Sutando.sln`

```pwsh
dotnet sln Sutando.sln add src/Sutando.Platform.Windows/Sutando.Platform.Windows.csproj --solution-folder src
```

If we want cross-platform CI to skip *compiling* the Windows project but still
restore the .sln, hand-edit the resulting `ProjectConfigurationPlatforms`
section to drop `.Build.0` lines for non-Windows runners. The
`EnableWindowsTargeting` property already covers restore on its own.

### 2. `Sutando.Tests.csproj` multi-targeting

The Windows agent reshaped `tests/Sutando.Tests/Sutando.Tests.csproj` so the
test assembly multi-targets `net10.0` plus `net10.0-windows10.0.19041.0` on
Windows hosts. The csproj clears `Directory.Build.props`'s default
`<TargetFramework>` before re-assigning it conditionally.

Windows-specific test sources live in `tests/Sutando.Tests/Platform/Windows/`
and are excluded from the non-Windows TFM via `<Compile Remove="Platform\Windows\**\*.cs" />`.

### Caveats

- The hotkey service spins up a dedicated message-pump thread per service
  instance — disposing the service joins that thread with a 5-second timeout.
  Hosts should `Dispose()` the adapter on shutdown.
- Toast notifications go through `Microsoft.Toolkit.Uwp.Notifications`, which
  is in maintenance mode. If/when `CommunityToolkit.WinUI.Notifications`
  stabilises without dragging in WindowsAppSDK, swap the package.
- Screen capture currently uses GDI. Switching to `Windows.Graphics.Capture`
  requires a D3D11 device + WinRT interop — deferred until a real use case
  demands DWM-accurate captures of hardware-accelerated windows.
