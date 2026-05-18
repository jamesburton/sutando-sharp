using System.Runtime.Versioning;

namespace Sutando.Platform.Windows;

/// <summary>
/// Composes Sutando's Windows-specific platform services into a single façade. Construct once and
/// hand to consumers; the services live for the adapter's lifetime so the hotkey pump and any
/// future long-lived OS handles stay valid.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformAdapter : IPlatformAdapter, IDisposable
{
    private readonly WindowsHotkeyService _hotkeys;

    /// <summary>Constructs the adapter and eagerly initialises the hotkey pump.</summary>
    public WindowsPlatformAdapter()
    {
        ScreenCapture = new WindowsScreenCaptureService();
        Notifications = new WindowsNotificationService();
        Clipboard = new WindowsClipboardService();
        _hotkeys = new WindowsHotkeyService();
    }

    /// <inheritdoc />
    public string PlatformId => "windows";

    /// <inheritdoc />
    public IScreenCaptureService? ScreenCapture { get; }

    /// <inheritdoc />
    public INotificationService? Notifications { get; }

    /// <inheritdoc />
    public IClipboardService Clipboard { get; }

    /// <inheritdoc />
    public IHotkeyService? Hotkeys => _hotkeys;

    /// <summary>Disposes the hotkey pump and any other transient OS resources.</summary>
    public void Dispose() => _hotkeys.Dispose();
}
