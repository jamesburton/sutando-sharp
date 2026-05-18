# Platform strategy — MAUI consideration

Right now sutando-sharp ships per-OS adapter projects (`Sutando.Platform.Windows`
landed first; `Sutando.Platform.Mac` and `Sutando.Platform.Linux` planned). The
contract lives in `Sutando.Platform.Abstractions`; each adapter implements the
interfaces against native OS APIs.

This is the right shape for **server / headless** sutando — the proactive loop,
the voice WebSocket server, the Twilio webhook, the dashboard. Those processes
run wherever .NET runs (Win / Mac / Linux). Each project ships only the bits it
needs; non-Windows hosts compile a stub assembly for `Sutando.Platform.Windows`
so the build graph stays whole. Linux / mac analogues will follow the same pattern.

## Where MAUI might compress the matrix

[.NET MAUI](https://learn.microsoft.com/en-us/dotnet/maui/) is the official
cross-platform UI/runtime layer covering **Windows, macOS, iOS, and Android**
from a single build. `Microsoft.Maui.Essentials` (formerly Xamarin.Essentials)
exposes a uniform API surface for several capabilities we currently abstract
per-OS:

| Capability | Per-OS today | MAUI Essentials? | Notes |
|---|---|---|---|
| Clipboard text | `WindowsClipboardService` (Win32) | ✅ `Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard` | Uniform read/write |
| Native notifications | Windows toast via UWP toolkit | ⚠ Partial — push notifications, not desktop toast | Desktop-toast story incomplete |
| File / media picker | Not yet implemented | ✅ `FilePicker`, `MediaPicker` | Worth adopting when needed |
| Screen capture | GDI BitBlt today | ❌ Not in scope for MAUI | Desktop-only OS API |
| Global hotkeys | `RegisterHotKey` P/Invoke | ❌ Not in scope for MAUI | Desktop-only OS API |
| App activation / process launch | `Process.Start` (cross-plat already) | ⚠ `Launcher` for URIs/apps | Marginal |
| Device display info | Not yet implemented | ✅ `DeviceDisplay` | Useful for the dashboard |

**Verdict:** MAUI is a partial fit — useful for *some* of the per-OS work, but
the headline platform features (screen capture, global hotkeys, desktop-style
toast) sit outside its surface and still need OS-specific implementations.
MAUI also has **no Linux story**, which we explicitly target.

## Recommendation

Don't restructure now. Keep the per-OS adapter pattern as the canonical shape.
At an appropriate milestone (probably after the Mac adapter lands so we have two
concrete implementations to compare), evaluate carving a `Sutando.Platform.Maui`
adapter that handles the capabilities Essentials covers cleanly (clipboard,
device info, file/media picker, share/launcher). The OS-specific pieces
(screen capture, hotkeys, toast) stay in their per-OS projects regardless.

If we ever want a **native sutando GUI app** (a desktop dashboard, an iOS / Android
companion to the agent), MAUI becomes interesting on its own merits — separate
decision from the platform-adapter taxonomy.

## Tracking

- Revisit: when `Sutando.Platform.Mac` lands and we have two adapters to compare.
- Owner: TBD.
- Blocking: nothing — current per-OS design works.
