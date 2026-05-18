namespace Sutando.Platform;

/// <summary>Global-hotkey registration. Implemented per-OS where the kernel/window-server allows it.</summary>
public interface IHotkeyService
{
    /// <summary>Register a global hotkey. The returned <see cref="IDisposable"/> unregisters when disposed.</summary>
    /// <param name="binding">Logical hotkey description (e.g. <c>Ctrl+Shift+C</c>).</param>
    /// <param name="handler">Async callback invoked on key press; exceptions are logged and swallowed.</param>
    /// <returns>A handle whose <see cref="IDisposable.Dispose"/> unregisters the hotkey.</returns>
    IDisposable Register(string binding, Func<CancellationToken, Task> handler);
}
