using System.Runtime.Versioning;
using Sutando.Platform.Windows;

namespace Sutando.Tests.Platform.Windows;

/// <summary>
/// Non-interactive smoke tests for <see cref="WindowsNotificationService"/>. We can't assert the
/// user actually saw a toast pop up, only that the call doesn't throw on a baseline Win10+ host.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationServiceTests
{
    [Fact]
    public async Task ShowAsync_TitleOnly_DoesNotThrow()
    {
        var svc = new WindowsNotificationService();
        await svc.ShowAsync($"sutando-smoke-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task ShowAsync_TitleAndBody_DoesNotThrow()
    {
        var svc = new WindowsNotificationService();
        await svc.ShowAsync(
            title: $"sutando-smoke-{Guid.NewGuid():N}",
            body: "Body line; you can delete this notification — it came from a CI test.");
    }

    [Fact]
    public async Task ShowAsync_NullTitle_Throws()
    {
        var svc = new WindowsNotificationService();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await svc.ShowAsync(null!));
    }
}
