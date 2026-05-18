# sutando-sharp

A .NET 10 port of [sutando](https://github.com/sonichi/sutando) — a personal AI agent — packaged for zero-install execution via `dnx`.

## Status

🚧 **Early scaffold.** Building toward feature parity with upstream sutando, with Windows as the P0 platform and macOS / Linux to follow. See `docs/` for design notes once they land.

## What sutando is

Sutando is a personal AI agent that runs alongside you: voice, vision, screen, meetings, phone, chat. Four cooperating processes coordinate via a file bridge (`tasks/` → `results/`), backed by Claude Code (or the Anthropic API) as the core reasoning engine and Gemini Live for realtime voice. Upstream is Mac-only by design and built on TypeScript + Python + Swift.

## Why a .NET port

- **Cross-platform from one toolchain.** Windows / macOS / Linux share the same managed core; only screen-capture / hotkeys / notifications get platform-specific adapters.
- **Zero-install via `dnx`.** Users run `dnx Sutando.Cli` (or `dotnet dnx Sutando.Cli`) to execute the latest published version without a local install. After `dotnet tool install -g Sutando.Cli` it's just `sutando`.
- **Pluggable agent executor.** Talk to the user's existing Claude Code subscription via CLI shell-out, or hit the Anthropic API directly — same `IAgentExecutor` interface.

## Run (once published)

```pwsh
# Run the latest published tool without installing it
dotnet dnx Sutando.Cli --prerelease

# Or install once, run anywhere
dotnet tool install -g Sutando.Cli
sutando --help
```

## Build from source

```pwsh
dotnet build
dotnet pack -c Release -o nuget-local
dotnet dnx Sutando.Cli --source ./nuget-local --prerelease -- --help
```

## Relationship to upstream

This is an independent rewrite under the same MIT license. Upstream tracked at `..\ThirdParty\sutando`. We follow upstream's architecture (file-bridge core, skill ecosystem, channel adapters) but reimplement in C# with cross-platform abstractions from day 1.

## License

MIT — same as upstream.
