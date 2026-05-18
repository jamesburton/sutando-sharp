using System.Runtime.Versioning;
using Sutando.Platform.Windows;

namespace Sutando.Tests.Platform.Windows;

/// <summary>
/// Lifecycle tests for <see cref="WindowsHotkeyService"/>. We use uncommon binding combinations
/// (Ctrl+Shift+Alt+F12 etc) to avoid colliding with existing system hotkeys on the developer's
/// machine. We never actually press the hotkey — these tests just verify register + unregister
/// round-trips without leaking the message-pump thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHotkeyServiceTests
{
    [Fact]
    public void Construct_AndDispose_DoesNotThrow()
    {
        using var svc = new WindowsHotkeyService();
        // Just exercising start + stop of the pump thread.
    }

    [Fact]
    public void Register_ThenDispose_Unregisters()
    {
        using var svc = new WindowsHotkeyService();
        var handle = svc.Register("Ctrl+Shift+Alt+F12", _ => Task.CompletedTask);
        Assert.NotNull(handle);
        handle.Dispose();
    }

    [Fact]
    public void Register_DoubleDispose_IsSafe()
    {
        using var svc = new WindowsHotkeyService();
        var handle = svc.Register("Ctrl+Shift+Alt+F11", _ => Task.CompletedTask);
        handle.Dispose();
        handle.Dispose();
    }

    [Fact]
    public void Register_AfterServiceDisposed_Throws()
    {
        var svc = new WindowsHotkeyService();
        svc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => svc.Register("Ctrl+Shift+Alt+F10", _ => Task.CompletedTask));
    }

    [Fact]
    public void Register_InvalidBinding_Throws()
    {
        using var svc = new WindowsHotkeyService();
        Assert.Throws<FormatException>(() => svc.Register("NotAKey+Banana", _ => Task.CompletedTask));
    }

    [Fact]
    public void Register_NullHandler_Throws()
    {
        using var svc = new WindowsHotkeyService();
        Assert.Throws<ArgumentNullException>(() => svc.Register("Ctrl+Shift+Alt+F9", null!));
    }
}
