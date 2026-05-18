namespace Sutando.Platform;

/// <summary>
/// Aggregate platform-adapter façade. Concrete implementations live in
/// <c>Sutando.Platform.Windows</c>, <c>Sutando.Platform.Mac</c>, and
/// <c>Sutando.Platform.Linux</c>.
/// </summary>
/// <remarks>
/// Each leaf service may be absent on a given platform (e.g. global hotkeys on Linux).
/// In that case the property returns <see langword="null"/> and callers handle the
/// missing capability gracefully — Sutando works in headless mode when GUI bits are
/// unavailable.
/// </remarks>
public interface IPlatformAdapter
{
    /// <summary>Short platform identifier — <c>windows</c>, <c>mac</c>, or <c>linux</c>.</summary>
    string PlatformId { get; }

    /// <summary>Captures bitmaps of the desktop or a specific window; <see langword="null"/> if unsupported.</summary>
    IScreenCaptureService? ScreenCapture { get; }

    /// <summary>Native notification surface (toast / NSUserNotification / libnotify); <see langword="null"/> if unsupported.</summary>
    INotificationService? Notifications { get; }

    /// <summary>Cross-platform clipboard.</summary>
    IClipboardService Clipboard { get; }

    /// <summary>Global-hotkey registration; <see langword="null"/> if unsupported on this OS.</summary>
    IHotkeyService? Hotkeys { get; }
}
