using System.Runtime.Versioning;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Sutando.Platform.Windows;

/// <summary>
/// Toast notification surface backed by <c>Microsoft.Toolkit.Uwp.Notifications</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Toolkit's <see cref="ToastContentBuilder"/> happily works for unpackaged desktop apps on
/// Windows 10+, building the toast XML and dispatching through the system toast notifier without
/// requiring AUMID registration. If the toast plumbing isn't available (e.g. running under a Server
/// SKU or in a session without a desktop) the call surfaces a <see cref="PlatformNotSupportedException"/>
/// rather than silently dropping the notification — the contract is "show or throw".
/// </para>
/// <para>
/// We deliberately do NOT wait on a confirmation event from the OS — <c>Show()</c> returns once the
/// notification is queued. The async signature on <see cref="INotificationService.ShowAsync"/> is for
/// symmetry with future platforms whose APIs really are async.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationService : INotificationService
{
    /// <inheritdoc />
    public Task ShowAsync(string title, string? body = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        ct.ThrowIfCancellationRequested();

        var builder = new ToastContentBuilder().AddText(title);
        if (!string.IsNullOrWhiteSpace(body))
        {
            builder.AddText(body);
        }

        // .Show() invokes ToastNotificationManagerCompat — it dispatches the toast synchronously
        // but is cheap and non-blocking from the caller's perspective.
        builder.Show();
        return Task.CompletedTask;
    }
}
