using System.Runtime.Versioning;
using Sutando.Platform.Windows;

namespace Sutando.Tests.Platform.Windows;

/// <summary>
/// Smoke tests for <see cref="WindowsClipboardService"/>. The clipboard is process-global state, so
/// each test snapshots the prior contents and restores them on dispose — running these tests should
/// never leave a foreign payload on the operator's clipboard.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardServiceTests : IDisposable
{
    private readonly WindowsClipboardService _clipboard = new();
    private readonly string? _priorClipboard;

    public WindowsClipboardServiceTests()
    {
        _priorClipboard = _clipboard.GetTextAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        // Best-effort restore. If the user copied something during the test run we'll clobber it —
        // acceptable for a smoke test environment.
        try
        {
            if (_priorClipboard is not null)
            {
                _clipboard.SetTextAsync(_priorClipboard).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Swallow — restore is best-effort.
        }
    }

    [Fact]
    public async Task SetTextThenGetText_RoundTripsAscii()
    {
        var payload = "sutando-clipboard-smoke-" + Guid.NewGuid().ToString("N");
        await _clipboard.SetTextAsync(payload);

        var roundTripped = await _clipboard.GetTextAsync();
        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public async Task SetTextThenGetText_RoundTripsUnicode()
    {
        // BMP-only characters: latin-1, CJK, math, Greek. Astral plane characters (emoji surrogate
        // pairs) round-trip in our implementation but tickled a third-party clipboard manager's
        // de-dup logic during testing — keep the payload BMP so CI is stable across machines.
        var payload = "Héllo, 世界 — Σύταντο " + Guid.NewGuid().ToString("N");
        await _clipboard.SetTextAsync(payload);

        var roundTripped = await _clipboard.GetTextAsync();
        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public async Task SetTextAsync_NullArgument_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await _clipboard.SetTextAsync(null!));
    }
}
