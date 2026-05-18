using System.Runtime.Versioning;
using Sutando.Platform.Windows;

namespace Sutando.Tests.Platform.Windows;

/// <summary>
/// Façade-level sanity tests for <see cref="WindowsPlatformAdapter"/>. Confirms it surfaces every
/// service the abstraction promises and that <c>PlatformId</c> is the canonical "windows" string.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformAdapterTests
{
    [Fact]
    public void PlatformId_IsWindows()
    {
        using var adapter = new WindowsPlatformAdapter();
        Assert.Equal("windows", adapter.PlatformId);
    }

    [Fact]
    public void AllServices_AreNonNullOnWindows()
    {
        using var adapter = new WindowsPlatformAdapter();
        Assert.NotNull(adapter.ScreenCapture);
        Assert.NotNull(adapter.Notifications);
        Assert.NotNull(adapter.Clipboard);
        Assert.NotNull(adapter.Hotkeys);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var adapter = new WindowsPlatformAdapter();
        adapter.Dispose();
        adapter.Dispose();
    }
}
