namespace Sutando.Platform;

/// <summary>
/// Native OS notifications. Maps to WinRT toast on Windows, <c>osascript display notification</c>
/// on macOS, and libnotify (<c>notify-send</c>) on Linux.
/// </summary>
public interface INotificationService
{
    /// <summary>Show an informational notification.</summary>
    /// <param name="title">Short title.</param>
    /// <param name="body">Optional longer body text.</param>
    /// <param name="ct">Cancellation.</param>
    Task ShowAsync(string title, string? body = null, CancellationToken ct = default);
}
