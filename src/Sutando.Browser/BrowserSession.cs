using System.Globalization;
using Microsoft.Playwright;

namespace Sutando.Browser;

/// <summary>
/// Async-disposable wrapper around a Playwright <see cref="IBrowser"/> + <see cref="IBrowserContext"/> + <see cref="IPage"/>.
/// Mirrors the upstream <c>src/browser.mjs</c> contract: launch once, run a sequence of typed
/// <see cref="BrowserAction"/>s, dispose to release the engine.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private static readonly string DefaultArtifactDirectory =
        Path.Combine(Path.GetTempPath(), "sutando-screenshots");

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly BrowserOptions _options;
    private bool _disposed;

    private BrowserSession(
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        IPage page,
        BrowserOptions options)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        _page = page;
        _options = options;
    }

    /// <summary>The active Playwright page. Exposed for advanced callers that need to drop down past the action grammar.</summary>
    public IPage Page => _page;

    /// <summary>The owning Playwright browser context.</summary>
    public IBrowserContext Context => _context;

    /// <summary>The owning Playwright browser.</summary>
    public IBrowser Browser => _browser;

    /// <summary>
    /// Launch a new Playwright browser + context + page using <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Launch configuration; pass <c>new BrowserOptions()</c> for defaults.</param>
    /// <param name="ct">Cancellation token observed during driver startup.</param>
    /// <returns>A ready-to-use <see cref="BrowserSession"/>. Callers MUST dispose it.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="BrowserOptions.BrowserType"/> is not one of chromium / firefox / webkit.</exception>
    public static async Task<BrowserSession> LaunchAsync(BrowserOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        try
        {
            var launchOptions = new BrowserTypeLaunchOptions { Headless = options.Headless };

            var browserType = options.BrowserType?.ToLowerInvariant() ?? "chromium";
            browser = browserType switch
            {
                "chromium" => await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false),
                "firefox" => await playwright.Firefox.LaunchAsync(launchOptions).ConfigureAwait(false),
                "webkit" => await playwright.Webkit.LaunchAsync(launchOptions).ConfigureAwait(false),
                _ => throw new ArgumentException(
                    $"Unsupported browser type '{options.BrowserType}'. Expected chromium, firefox, or webkit.",
                    nameof(options)),
            };

            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = options.ViewportWidth,
                    Height = options.ViewportHeight,
                },
                UserAgent = options.UserAgent,
            };
            context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);

            var timeoutMs = (float)options.DefaultTimeout.TotalMilliseconds;
            context.SetDefaultTimeout(timeoutMs);
            context.SetDefaultNavigationTimeout(timeoutMs);

            page = await context.NewPageAsync().ConfigureAwait(false);

            return new BrowserSession(playwright, browser, context, page, options);
        }
        catch
        {
            // Roll back any partially constructed Playwright objects before bubbling the failure.
            if (page is not null)
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
            if (context is not null)
            {
                await context.CloseAsync().ConfigureAwait(false);
            }
            if (browser is not null)
            {
                await browser.CloseAsync().ConfigureAwait(false);
            }
            playwright.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Execute a single typed browser action against the active page.
    /// </summary>
    /// <param name="action">The action to run.</param>
    /// <param name="ct">Cancellation token observed between operations.</param>
    /// <returns>Result populated according to the action's kind; empty for actions with no return payload (click, fill, select, wait, navigate).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the session has already been disposed.</exception>
    public async Task<BrowserActionResult> ExecuteAsync(BrowserAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        switch (action)
        {
            case BrowserAction.Navigate nav:
                await _page.GotoAsync(nav.Url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = (float)_options.DefaultTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
                return new BrowserActionResult();

            case BrowserAction.Text:
                var text = await _page.InnerTextAsync("body").ConfigureAwait(false);
                return new BrowserActionResult(Text: text);

            case BrowserAction.Screenshot shot:
                var screenshotPath = shot.OutPath ?? BuildArtifactPath("browser", "png");
                EnsureDirectory(screenshotPath);
                await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = shot.FullPage,
                }).ConfigureAwait(false);
                return new BrowserActionResult(OutputPath: screenshotPath);

            case BrowserAction.Pdf pdf:
                var pdfPath = pdf.OutPath ?? BuildArtifactPath("page", "pdf");
                EnsureDirectory(pdfPath);
                await _page.PdfAsync(new PagePdfOptions
                {
                    Path = pdfPath,
                    Format = "A4",
                }).ConfigureAwait(false);
                return new BrowserActionResult(OutputPath: pdfPath);

            case BrowserAction.Html:
                var html = await _page.ContentAsync().ConfigureAwait(false);
                return new BrowserActionResult(Html: html);

            case BrowserAction.Click click:
                await _page.ClickAsync(click.Selector).ConfigureAwait(false);
                return new BrowserActionResult();

            case BrowserAction.Fill fill:
                await _page.FillAsync(fill.Selector, fill.Value).ConfigureAwait(false);
                return new BrowserActionResult();

            case BrowserAction.Select sel:
                await _page.SelectOptionAsync(sel.Selector, sel.Value).ConfigureAwait(false);
                return new BrowserActionResult();

            case BrowserAction.Wait wait:
                // Honour cancellation while sleeping — Playwright's WaitForTimeoutAsync ignores
                // the framework token, so use Task.Delay for cooperative cancellation.
                await Task.Delay(wait.Duration, ct).ConfigureAwait(false);
                return new BrowserActionResult();

            default:
                throw new NotSupportedException(
                    $"Browser action type '{action.GetType().Name}' is not yet implemented.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Dispose in reverse order: page → context → browser → playwright. Swallow per-step
        // failures so a flaky page close can't strand the engine process.
        try
        {
            await _page.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // best effort
        }
        try
        {
            await _context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // best effort
        }
        try
        {
            await _browser.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // best effort
        }
        _playwright.Dispose();
    }

    private static string BuildArtifactPath(string prefix, string extension)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        return Path.Combine(DefaultArtifactDirectory, $"{prefix}-{timestamp}.{extension}");
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
