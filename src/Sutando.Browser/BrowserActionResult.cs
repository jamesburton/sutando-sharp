namespace Sutando.Browser;

/// <summary>
/// Outcome of a single <see cref="BrowserSession.ExecuteAsync"/> call. Each action populates
/// at most one of the fields — the rest stay <see langword="null"/>.
/// </summary>
/// <param name="Text">Visible page text — populated for <see cref="BrowserAction.Text"/>.</param>
/// <param name="OutputPath">On-disk path written by the action — populated for <see cref="BrowserAction.Screenshot"/> and <see cref="BrowserAction.Pdf"/>.</param>
/// <param name="Html">Outer HTML of the page — populated for <see cref="BrowserAction.Html"/>.</param>
public sealed record BrowserActionResult(
    string? Text = null,
    string? OutputPath = null,
    string? Html = null);
