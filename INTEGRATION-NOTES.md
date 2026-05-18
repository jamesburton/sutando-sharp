# Sutando.Browser — integration notes

This worktree adds a new `src/Sutando.Browser/` library. To honour the "do not
modify `Sutando.sln`" constraint, the solution file was left untouched. The
project still builds because `tests/Sutando.Tests/Sutando.Tests.csproj` has a
`<ProjectReference>` to it, which pulls it into the test build graph.

At merge time the following touch-ups should land:

## 1. Add `Sutando.Browser` to `Sutando.sln`

Generate a new project GUID and add it under the existing `src` solution
folder (`{827E0CD3-B72D-47B6-A68D-7590B98EB39B}`). Easiest via the CLI from the
repo root:

```pwsh
dotnet sln Sutando.sln add src/Sutando.Browser/Sutando.Browser.csproj --solution-folder src
```

Then commit the resulting `Sutando.sln` change.

## 2. Wire the CLI verb

`src/Sutando.Browser/BrowserCommand.cs` exposes
`BrowserCommand.RunAsync(string[] args, BrowserOptions? options = null, CancellationToken ct = default)`
ready for `Sutando.Cli` to call. When the CLI verb is added:

- Add a `<ProjectReference Include="..\Sutando.Browser\Sutando.Browser.csproj" />`
  to `src/Sutando.Cli/Sutando.Cli.csproj`.
- Register a `browser` subcommand whose handler forwards its args (URL +
  optional action strings) to `BrowserCommand.RunAsync`.

## 3. Playwright runtime install

`Microsoft.Playwright` (v1.59.0) ships an in-package driver but the actual
browser binaries are downloaded on first use. CI runners that exercise the
integration tests will need:

```pwsh
pwsh bin/Debug/net10.0/playwright.ps1 install
```

The integration tests in `tests/Sutando.Tests/Browser/BrowserSessionIntegrationTests.cs`
are marked `[Fact(Skip = "requires playwright install; enable locally")]`
specifically so that CI without the browser blobs still passes; remove the
`Skip` argument once Playwright install is part of CI.

## 4. Grammar deviations from upstream `src/browser.mjs`

`BrowserAction.Parse` is strict where upstream was permissive. Worth noting in
docs / changelog:

- **`wait:<ms>`** — strict non-negative-integer parse. Upstream uses
  `parseInt(action.slice(5)) || 2000` and silently falls back to `2000ms` for
  empty / non-numeric input; we throw `FormatException` instead.
- **Unknown verbs** — upstream logs to stderr and continues; we throw
  `FormatException` so callers fail fast.
- **Bare verbs with trailing colon** (e.g. `text:`) — upstream's exact-match
  branch would also reject these, but we make the error message explicit.
- **`Navigate`** is a C#-only action type. Upstream takes the URL as the first
  positional CLI argument, never as a colon expression, so `Parse` never emits
  a `Navigate` and we throw if anyone sends `navigate:...`.
