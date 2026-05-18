using System.Runtime.Versioning;
using Sutando.Platform.Windows;

namespace Sutando.Tests.Platform.Windows;

/// <summary>
/// File-output smoke tests for <see cref="WindowsScreenCaptureService"/>. We don't validate the
/// pixel contents — assert that BitBlt produced a non-empty PNG. Headless CI agents without an
/// attached display will produce a tiny / black image but the file should still exist.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCaptureServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task CapturePrimaryAsync_WritesNonEmptyPng()
    {
        var svc = new WindowsScreenCaptureService();
        var target = Path.Combine(Path.GetTempPath(), $"sutando-test-primary-{Guid.NewGuid():N}.png");
        _tempFiles.Add(target);

        var result = await svc.CapturePrimaryAsync(target);

        Assert.Equal(target, result);
        Assert.True(File.Exists(result));
        Assert.True(new FileInfo(result).Length > 0);
    }

    [Fact]
    public async Task CaptureAllAsync_WritesAtLeastOneFile()
    {
        var svc = new WindowsScreenCaptureService();
        var dir = Path.Combine(Path.GetTempPath(), $"sutando-test-all-{Guid.NewGuid():N}");
        _tempDirs.Add(dir);

        var results = await svc.CaptureAllAsync(dir);

        Assert.NotEmpty(results);
        foreach (var path in results)
        {
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
        }
    }

    [Fact]
    public async Task CaptureDisplayAsync_Index0_Succeeds()
    {
        var svc = new WindowsScreenCaptureService();
        var target = Path.Combine(Path.GetTempPath(), $"sutando-test-display0-{Guid.NewGuid():N}.png");
        _tempFiles.Add(target);

        var result = await svc.CaptureDisplayAsync(0, target);

        Assert.True(File.Exists(result));
        Assert.True(new FileInfo(result).Length > 0);
    }

    [Fact]
    public async Task CaptureDisplayAsync_NegativeIndex_Throws()
    {
        var svc = new WindowsScreenCaptureService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await svc.CaptureDisplayAsync(-1));
    }

    [Fact]
    public async Task CaptureForegroundWindowAsync_ReturnsPathOrNull()
    {
        // We can't depend on a foreground window existing on a CI agent, so we accept either a valid
        // PNG path or null. Both are documented contract outcomes.
        var svc = new WindowsScreenCaptureService();
        var target = Path.Combine(Path.GetTempPath(), $"sutando-test-fg-{Guid.NewGuid():N}.png");
        _tempFiles.Add(target);

        var result = await svc.CaptureForegroundWindowAsync(target);

        if (result is not null)
        {
            Assert.True(File.Exists(result));
            Assert.True(new FileInfo(result).Length > 0);
        }
    }
}
