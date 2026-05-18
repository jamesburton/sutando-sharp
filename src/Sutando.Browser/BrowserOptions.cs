namespace Sutando.Browser;

/// <summary>
/// Launch-time configuration for a <see cref="BrowserSession"/>.
/// </summary>
public sealed record BrowserOptions
{
    /// <summary>
    /// Playwright engine to launch — one of <c>chromium</c>, <c>firefox</c>, or <c>webkit</c>.
    /// Case-insensitive; defaults to <c>chromium</c>.
    /// </summary>
    public string BrowserType { get; init; } = "chromium";

    /// <summary>Whether to launch the browser headless. Defaults to <see langword="true"/>.</summary>
    public bool Headless { get; init; } = true;

    /// <summary>Optional override for the browser context's <c>User-Agent</c> header.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Viewport width in CSS pixels. Defaults to 1280.</summary>
    public int ViewportWidth { get; init; } = 1280;

    /// <summary>Viewport height in CSS pixels. Defaults to 800.</summary>
    public int ViewportHeight { get; init; } = 800;

    /// <summary>
    /// Default per-operation timeout applied to navigation / clicks / fills / etc.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
