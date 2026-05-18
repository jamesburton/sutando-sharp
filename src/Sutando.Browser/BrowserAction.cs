using System.Globalization;

namespace Sutando.Browser;

/// <summary>
/// Discriminated union of browser actions understood by <see cref="BrowserSession.ExecuteAsync"/>.
/// Subtypes are <see langword="sealed"/> records — pattern-match on the action type to dispatch.
/// </summary>
/// <remarks>
/// The string grammar mirrors upstream <c>src/browser.mjs</c>:
/// <list type="bullet">
///   <item><c>text</c> — return visible body text.</item>
///   <item><c>screenshot</c> — full-page PNG; returns the on-disk path.</item>
///   <item><c>pdf</c> — render the page to PDF; returns the on-disk path.</item>
///   <item><c>html</c> — return the outer HTML.</item>
///   <item><c>click:&lt;selector&gt;</c> — click a CSS selector.</item>
///   <item><c>fill:&lt;selector&gt;:&lt;value&gt;</c> — fill an input.</item>
///   <item><c>select:&lt;selector&gt;:&lt;value&gt;</c> — choose an option.</item>
///   <item><c>wait:&lt;ms&gt;</c> — sleep for N milliseconds.</item>
/// </list>
/// <see cref="Navigate"/> is a C#-only action (the upstream CLI takes the URL as a leading
/// positional argument, not as part of the colon grammar) and is therefore not emitted by
/// <see cref="Parse"/>.
/// </remarks>
public abstract record BrowserAction
{
    private protected BrowserAction()
    {
    }

    /// <summary>
    /// Parse a single upstream-style colon-delimited action expression into a typed action.
    /// </summary>
    /// <param name="expression">The raw expression — e.g. <c>"text"</c>, <c>"click:#submit"</c>, <c>"fill:#email:me@x.com"</c>.</param>
    /// <returns>The strongly-typed <see cref="BrowserAction"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown when the expression cannot be parsed — unknown verb, missing colon, empty selector, non-integer wait, etc.</exception>
    public static BrowserAction Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // Bare verbs first — exact-match, no trailing colon allowed.
        switch (expression)
        {
            case "text":
                return new Text();
            case "screenshot":
                return new Screenshot();
            case "pdf":
                return new Pdf();
            case "html":
                return new Html();
        }

        // Colon-prefixed verbs.
        var firstColon = expression.IndexOf(':');
        if (firstColon < 0)
        {
            throw new FormatException(
                $"Unknown browser action '{expression}'. Expected one of: text, screenshot, pdf, html, click:<sel>, fill:<sel>:<val>, select:<sel>:<val>, wait:<ms>.");
        }

        var verb = expression[..firstColon];
        var rest = expression[(firstColon + 1)..];

        return verb switch
        {
            "click" => ParseClick(rest),
            "fill" => ParseFill(rest),
            "select" => ParseSelect(rest),
            "wait" => ParseWait(rest),
            _ => throw new FormatException(
                $"Unknown browser action verb '{verb}'. Expected one of: click, fill, select, wait."),
        };
    }

    private static Click ParseClick(string rest)
    {
        // Matches upstream `action.slice(6)` — selector keeps any further colons verbatim.
        if (rest.Length == 0)
        {
            throw new FormatException("Browser action 'click' requires a selector — e.g. 'click:#submit'.");
        }
        return new Click(rest);
    }

    private static Fill ParseFill(string rest)
    {
        // Upstream: parts[1] = selector; parts.slice(2).join(':') = value.
        // I.e. selector is the segment between the FIRST and SECOND colon; value is everything
        // after the second colon, with any further colons preserved.
        var (selector, value) = SplitSelectorValue(rest, verb: "fill");
        return new Fill(selector, value);
    }

    private static Select ParseSelect(string rest)
    {
        var (selector, value) = SplitSelectorValue(rest, verb: "select");
        return new Select(selector, value);
    }

    private static (string Selector, string Value) SplitSelectorValue(string rest, string verb)
    {
        var sep = rest.IndexOf(':');
        if (sep < 0)
        {
            throw new FormatException(
                $"Browser action '{verb}' requires '<selector>:<value>' — e.g. '{verb}:#input:hello'.");
        }
        var selector = rest[..sep];
        if (selector.Length == 0)
        {
            throw new FormatException($"Browser action '{verb}' requires a non-empty selector.");
        }
        var value = rest[(sep + 1)..];
        return (selector, value);
    }

    private static Wait ParseWait(string rest)
    {
        // Strict: non-negative integer milliseconds. Deviates from upstream's permissive
        // `parseInt(...) || 2000` fallback — documented in INTEGRATION-NOTES.md.
        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms < 0)
        {
            throw new FormatException(
                $"Browser action 'wait' requires a non-negative integer milliseconds value; got '{rest}'.");
        }
        return new Wait(TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>Navigate the active page to <paramref name="Url"/>.</summary>
    /// <param name="Url">Absolute URL to load.</param>
    public sealed record Navigate(string Url) : BrowserAction;

    /// <summary>Read the visible body text of the active page.</summary>
    public sealed record Text : BrowserAction;

    /// <summary>Capture a screenshot of the active page.</summary>
    /// <param name="OutPath">Optional output path; when <see langword="null"/> the session picks a temp path.</param>
    /// <param name="FullPage">Whether to capture the full scrollable page. Defaults to <see langword="true"/>.</param>
    public sealed record Screenshot(string? OutPath = null, bool FullPage = true) : BrowserAction;

    /// <summary>Render the active page as a PDF.</summary>
    /// <param name="OutPath">Optional output path; when <see langword="null"/> the session picks a temp path.</param>
    public sealed record Pdf(string? OutPath = null) : BrowserAction;

    /// <summary>Read the active page's outer HTML.</summary>
    public sealed record Html : BrowserAction;

    /// <summary>Click an element matched by <paramref name="Selector"/>.</summary>
    /// <param name="Selector">CSS / Playwright locator selector.</param>
    public sealed record Click(string Selector) : BrowserAction;

    /// <summary>Fill an input matched by <paramref name="Selector"/> with <paramref name="Value"/>.</summary>
    /// <param name="Selector">CSS / Playwright locator selector.</param>
    /// <param name="Value">Value to type into the input.</param>
    public sealed record Fill(string Selector, string Value) : BrowserAction;

    /// <summary>Choose <paramref name="Value"/> in a <c>&lt;select&gt;</c> matched by <paramref name="Selector"/>.</summary>
    /// <param name="Selector">CSS / Playwright locator selector.</param>
    /// <param name="Value">Option value to select.</param>
    public sealed record Select(string Selector, string Value) : BrowserAction;

    /// <summary>Sleep for <paramref name="Duration"/>.</summary>
    /// <param name="Duration">How long to wait.</param>
    public sealed record Wait(TimeSpan Duration) : BrowserAction;
}
