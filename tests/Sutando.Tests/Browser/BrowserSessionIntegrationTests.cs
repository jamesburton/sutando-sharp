using Sutando.Browser;

namespace Sutando.Tests.Browser;

/// <summary>
/// Live Playwright integration coverage. Skipped by default because these tests require
/// <c>playwright install</c> to have been run locally (and download ~300MB of browsers).
/// Drop the <c>Skip</c> argument when working on the browser layer.
/// </summary>
public sealed class BrowserSessionIntegrationTests
{
    private const string SkipReason = "requires playwright install; enable locally";

    [Fact(Skip = SkipReason)]
    public async Task LaunchAsync_ChromiumDefaults_NavigatesAndReadsText()
    {
        var options = new BrowserOptions();
        await using var session = await BrowserSession.LaunchAsync(options);
        await session.ExecuteAsync(new BrowserAction.Navigate("https://example.com"));
        var result = await session.ExecuteAsync(new BrowserAction.Text());
        Assert.NotNull(result.Text);
        Assert.Contains("Example", result.Text!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = SkipReason)]
    public async Task ExecuteAsync_Screenshot_WritesFileToReturnedPath()
    {
        var options = new BrowserOptions();
        await using var session = await BrowserSession.LaunchAsync(options);
        await session.ExecuteAsync(new BrowserAction.Navigate("https://example.com"));
        var result = await session.ExecuteAsync(new BrowserAction.Screenshot());
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath!));
    }

    [Fact(Skip = SkipReason)]
    public async Task ExecuteAsync_Html_ReturnsOuterHtml()
    {
        var options = new BrowserOptions();
        await using var session = await BrowserSession.LaunchAsync(options);
        await session.ExecuteAsync(new BrowserAction.Navigate("https://example.com"));
        var result = await session.ExecuteAsync(new BrowserAction.Html());
        Assert.NotNull(result.Html);
        Assert.Contains("<html", result.Html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = SkipReason)]
    public async Task DisposeAsync_IsIdempotent()
    {
        var session = await BrowserSession.LaunchAsync(new BrowserOptions());
        await session.DisposeAsync();
        await session.DisposeAsync();
    }
}
