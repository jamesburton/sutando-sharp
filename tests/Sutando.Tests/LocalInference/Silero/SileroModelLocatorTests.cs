using System.Diagnostics.CodeAnalysis;
using Sutando.LocalInference.Silero;

namespace Sutando.Tests.LocalInference.Silero;

/// <summary>
/// Tests for the Silero model-locator helper. The actual download is environment-dependent
/// (network access, GitHub uptime); we keep that path behind <see cref="SkippableFactAttribute"/>
/// so CI without internet doesn't fail.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests target Sutando's experimental local-inference surface.")]
public sealed class SileroModelLocatorTests
{
    [Fact]
    public void ResolveCacheDirectory_ReturnsStablePath()
    {
        var a = SileroModelLocator.ResolveCacheDirectory();
        var b = SileroModelLocator.ResolveCacheDirectory();

        Assert.False(string.IsNullOrEmpty(a));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveCacheDirectory_ContainsExpectedSegments()
    {
        // The path is "<LocalApplicationData or temp-fallback>/sutando/local-inference".
        // We don't pin the root because it's platform-dependent (LocalAppData on Windows /
        // ~/.local/share on Linux) but we do pin the trailing segments — that's the contract.
        var dir = SileroModelLocator.ResolveCacheDirectory();
        Assert.EndsWith(Path.Combine("sutando", "local-inference"), dir);
    }

    [Fact]
    public void Constants_AreNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(SileroModelLocator.ModelUrl));
        Assert.False(string.IsNullOrWhiteSpace(SileroModelLocator.DefaultFileName));
        Assert.EndsWith(".onnx", SileroModelLocator.DefaultFileName);
    }

    [SkippableFact]
    public async Task EnsureModelAsync_DownloadsModel_WhenSileroDownloadIsEnabled()
    {
        // Opt-in via env var so CI without network doesn't pull ~2 MB on every run. Set
        // SUTANDO_SILERO_AUTODOWNLOAD_TESTS=1 locally to exercise this.
        var enabled = Environment.GetEnvironmentVariable("SUTANDO_SILERO_AUTODOWNLOAD_TESTS");
        Skip.IfNot(string.Equals(enabled, "1", StringComparison.Ordinal),
            "Set SUTANDO_SILERO_AUTODOWNLOAD_TESTS=1 to enable the live download integration test.");

        var path = await SileroModelLocator.EnsureModelAsync();
        Assert.True(File.Exists(path));

        var info = new FileInfo(path);
        Assert.True(info.Length > 1_000_000, "Expected the Silero model to be > 1 MB; got " + info.Length);
    }
}
