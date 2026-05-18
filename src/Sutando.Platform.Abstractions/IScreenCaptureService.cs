namespace Sutando.Platform;

/// <summary>
/// Cross-platform screen-capture contract. Implementations save captured images to disk
/// and return the path — matches upstream Sutando's screen-capture-server protocol,
/// where consumers reference the file by path (so it can be sent to vision LLMs).
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>Capture the primary display.</summary>
    /// <param name="targetPath">Optional output path; when <see langword="null"/>, a temp file is created.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Absolute path to the saved capture.</returns>
    Task<string> CapturePrimaryAsync(string? targetPath = null, CancellationToken ct = default);

    /// <summary>Capture every connected display, returning one path per display.</summary>
    Task<IReadOnlyList<string>> CaptureAllAsync(string? targetDir = null, CancellationToken ct = default);

    /// <summary>Capture a specific display by ordinal index.</summary>
    Task<string> CaptureDisplayAsync(int displayIndex, string? targetPath = null, CancellationToken ct = default);

    /// <summary>Capture the foreground window. Returns <see langword="null"/> if no window is focused or the OS forbids it.</summary>
    Task<string?> CaptureForegroundWindowAsync(string? targetPath = null, CancellationToken ct = default);
}
