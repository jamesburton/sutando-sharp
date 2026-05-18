namespace Sutando.Platform;

/// <summary>Cross-platform text clipboard. Image clipboard support comes later if a skill demands it.</summary>
public interface IClipboardService
{
    /// <summary>Read the current text contents of the clipboard, or <see langword="null"/> if it doesn't hold text.</summary>
    Task<string?> GetTextAsync(CancellationToken ct = default);

    /// <summary>Replace the clipboard contents with the given text.</summary>
    Task SetTextAsync(string text, CancellationToken ct = default);
}
