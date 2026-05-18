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
