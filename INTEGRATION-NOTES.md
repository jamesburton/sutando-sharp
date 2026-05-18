# Windows platform adapter — integration notes

Notes for whoever merges the `platform-windows:` branch.

## New project

`src/Sutando.Platform.Windows/Sutando.Platform.Windows.csproj`

- Target framework: `net10.0-windows10.0.19041.0` (overrides the repo-wide `net10.0` from `Directory.Build.props`).
- Restores on non-Windows machines via `<EnableWindowsTargeting>true</EnableWindowsTargeting>`; will not *build* off-Windows.
- All public types are marked `[SupportedOSPlatform("windows")]` so analyzer-gated callers see the platform requirement.
- Package references introduced:
  - `System.Drawing.Common` 9.0.0 — required for BitBlt-driven screen capture (unbundled since .NET 6).
  - `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 — toast notifications without WindowsAppSDK / WinUI3.

## Sutando.sln — NOT modified

Multiple agents are adding platform projects in separate worktrees; rather than fight over the .sln,
this branch leaves `Sutando.sln` untouched. The merger should add a single entry for
`Sutando.Platform.Windows` and prefer the following solution-configuration mapping so the project
only builds on Windows agents:

```
{<NEW-GUID>}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
{<NEW-GUID>}.Debug|Any CPU.Build.0   = Debug|Any CPU    # only on Windows runners
{<NEW-GUID>}.Release|Any CPU.ActiveCfg = Release|Any CPU
{<NEW-GUID>}.Release|Any CPU.Build.0   = Release|Any CPU
```

If macOS / Linux CI needs to keep evaluating the .sln, drop the `.Build.0` lines so those agents
restore but don't compile the project. `EnableWindowsTargeting` covers restore on its own.

## Sutando.Tests.csproj — MODIFIED

To exercise the Windows adapter without a separate test project, `Sutando.Tests.csproj` now
multi-targets on Windows:

- non-Windows: `net10.0` (unchanged behaviour)
- Windows: `net10.0;net10.0-windows10.0.19041.0`

The Windows-specific test sources live in `tests/Sutando.Tests/Platform/Windows/` and are
excluded from the non-Windows TFM via a conditional `<Compile Remove>` so they don't compile
without the Windows adapter project reference.

The csproj clears the `Directory.Build.props`-supplied `<TargetFramework>` before re-assigning it
conditionally; if `Directory.Build.props` later moves to TFM-by-property-only, that workaround
becomes redundant.

## Other agents' worktrees

This branch does not touch:

- `Directory.Build.props`
- any other `src/Sutando.*` project
- the bridge contract docs
- the foundation test files (`tests/Sutando.Tests/*.cs` at the root level)

So a merge with the macOS or Linux adapter branches should be conflict-free on every file other
than `Sutando.sln` (untouched here) and potentially `Sutando.Tests.csproj` if another branch also
needs to multi-target. The pattern this branch uses (`$(OS)`-conditional TFMs) extends cleanly: add
`net10.0-osx` / `net10.0` macOS or Linux-windows-equivalent TFMs in a sibling `<PropertyGroup>`.

## Caveats

- The hotkey service spins up a dedicated message-pump thread per service instance — disposing the
  service joins that thread with a 5-second timeout. Hosts should `Dispose()` the adapter on
  shutdown.
- Toast notifications go through `Microsoft.Toolkit.Uwp.Notifications`, which is in maintenance
  mode. If/when `CommunityToolkit.WinUI.Notifications` stabilises without dragging in the full
  WindowsAppSDK, swap the package; the call surface is similar.
- Screen capture currently uses GDI. Switching to `Windows.Graphics.Capture` requires a D3D11
  device + WinRT interop — deferred until a real use case demands DWM-accurate captures of
  hardware-accelerated windows.
