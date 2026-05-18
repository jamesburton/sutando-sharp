using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Sutando.Platform.Windows.Internal;

namespace Sutando.Platform.Windows;

/// <summary>
/// Global-hotkey registration via Win32 <c>RegisterHotKey</c>. The Win32 docs require <c>WM_HOTKEY</c>
/// messages to be delivered to either a thread queue or a specific window — and either way the
/// receiving thread MUST be running a message pump. To keep that pump lifetime fully owned by this
/// service (so it dies with the service, regardless of how the host app is threaded), we spin up a
/// dedicated background thread, create a hidden message-only window on it, and pump
/// <c>GetMessage</c>/<c>DispatchMessage</c> until shutdown.
/// </summary>
/// <remarks>
/// <para>
/// All <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> calls happen on the pump thread — both APIs
/// require the call to come from the same thread that owns the window. Cross-thread registration
/// requests are posted via <c>PostMessage(WM_APP_*)</c>.
/// </para>
/// <para>
/// Shutdown protocol: <see cref="Dispose"/> posts <c>WM_APP_SHUTDOWN</c> and then joins the pump
/// thread. The pump unregisters everything, destroys the window, unregisters the class, then exits.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHotkeyService : IHotkeyService, IDisposable
{
    private static int _instanceCounter;

    private readonly Thread _pumpThread;
    private readonly ManualResetEventSlim _pumpReady = new(initialState: false);
    private readonly ConcurrentDictionary<int, Registration> _registrations = new();
    private readonly NativeMethods.WndProc _wndProcDelegate;
    private readonly string _windowClassName;

    private IntPtr _hInstance;
    private IntPtr _hWnd;
    private uint _pumpThreadId;
    private ushort _classAtom;
    private int _nextHotkeyId;
    private bool _disposed;
    private Exception? _pumpStartupError;

    /// <summary>Construct the service and start the dedicated message-pump thread.</summary>
    /// <exception cref="InvalidOperationException">If the pump thread fails to start.</exception>
    public WindowsHotkeyService()
    {
        // Unique class name per instance — registering the same WNDCLASS twice in the same module
        // fails, and the same Sutando process could in theory instantiate multiple services.
        var id = Interlocked.Increment(ref _instanceCounter);
        _windowClassName = $"SutandoHotkeyWindow_{Environment.ProcessId}_{id}";
        _wndProcDelegate = WndProc;

        _pumpThread = new Thread(PumpThreadEntry)
        {
            Name = $"Sutando-Hotkey-Pump-{id}",
            IsBackground = true,
        };
        _pumpThread.Start();

        // Wait for the pump to publish either a ready signal or a startup error.
        _pumpReady.Wait();
        if (_pumpStartupError is not null)
        {
            throw new InvalidOperationException("Failed to start hotkey message pump.", _pumpStartupError);
        }
    }

    /// <inheritdoc />
    public IDisposable Register(string binding, Func<CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (modifiers, vk) = HotkeyParser.Parse(binding);
        var id = Interlocked.Increment(ref _nextHotkeyId);

        // The pump thread does the actual RegisterHotKey call so it owns the registration.
        // We synchronously wait for the result via a TaskCompletionSource posted across.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new RegisterRequest(id, modifiers | NativeMethods.MOD_NOREPEAT, vk, tcs);

        var handle = GCHandle.Alloc(request);
        try
        {
            if (!NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WM_APP_REGISTER, GCHandle.ToIntPtr(handle), IntPtr.Zero))
            {
                handle.Free();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PostThreadMessage(register) failed.");
            }

            // Wait synchronously — registration is fast and the contract is sync.
            if (!tcs.Task.GetAwaiter().GetResult())
            {
                throw new InvalidOperationException($"RegisterHotKey failed for binding '{binding}' (id={id}).");
            }
        }
        catch
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }

            throw;
        }

        var reg = new Registration(id, handler, binding);
        _registrations[id] = reg;
        return new HotkeyHandle(this, id);
    }

    /// <summary>Unregister a previously-registered hotkey by id; safe to call multiple times.</summary>
    private void Unregister(int id)
    {
        if (!_registrations.TryRemove(id, out _))
        {
            return;
        }

        if (_pumpThreadId == 0)
        {
            return;
        }

        // Fire-and-forget — UnregisterHotKey can't fail in a way the caller cares about.
        NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WM_APP_UNREGISTER, new IntPtr(id), IntPtr.Zero);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Snapshot the registrations so we can ask the pump to unregister them in bulk.
        foreach (var key in _registrations.Keys)
        {
            NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WM_APP_UNREGISTER, new IntPtr(key), IntPtr.Zero);
        }

        _registrations.Clear();

        if (_pumpThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
        }

        if (_pumpThread.IsAlive)
        {
            _pumpThread.Join(TimeSpan.FromSeconds(5));
        }

        _pumpReady.Dispose();
    }

    /// <summary>Pump-thread entry. Creates the window class, message-only window, then runs the loop.</summary>
    private void PumpThreadEntry()
    {
        try
        {
            _pumpThreadId = (uint)Environment.CurrentManagedThreadId;
            // We need the real OS thread id for PostThreadMessage, not the managed id.
            _pumpThreadId = GetCurrentNativeThreadId();

            _hInstance = NativeMethods.GetModuleHandle(null);

            var wndClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = _hInstance,
                lpszClassName = Marshal.StringToHGlobalUni(_windowClassName),
            };

            try
            {
                _classAtom = NativeMethods.RegisterClassEx(ref wndClass);
                if (_classAtom == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(wndClass.lpszClassName);
            }

            _hWnd = NativeMethods.CreateWindowEx(
                dwExStyle: 0,
                lpClassName: _windowClassName,
                lpWindowName: null,
                dwStyle: 0,
                x: 0,
                y: 0,
                nWidth: 0,
                nHeight: 0,
                hWndParent: NativeMethods.HWND_MESSAGE, // message-only window — never shown.
                hMenu: IntPtr.Zero,
                hInstance: _hInstance,
                lpParam: IntPtr.Zero);

            if (_hWnd == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for message-only window.");
            }

            _pumpReady.Set();

            // Drain the queue. GetMessage returns 0 on WM_QUIT, -1 on error, >0 otherwise.
            while (true)
            {
                var result = NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (result == 0 || result == -1)
                {
                    break;
                }

                // Handle our app-level synthetic messages before dispatching to the window proc.
                if (msg.Hwnd == IntPtr.Zero)
                {
                    if (HandleThreadMessage(msg))
                    {
                        if (msg.Message == NativeMethods.WM_APP_SHUTDOWN)
                        {
                            break;
                        }

                        continue;
                    }
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _pumpStartupError = ex;
            _pumpReady.Set();
        }
        finally
        {
            if (_hWnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hWnd);
                _hWnd = IntPtr.Zero;
            }

            if (_classAtom != 0)
            {
                NativeMethods.UnregisterClass(_windowClassName, _hInstance);
                _classAtom = 0;
            }
        }
    }

    /// <summary>Returns the real OS thread id of the current thread (PostThreadMessage requires this, not the managed id).</summary>
    private static uint GetCurrentNativeThreadId() => GetCurrentThreadIdNative();

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadIdNative();

    /// <summary>Handles app-level synthetic messages we post to the pump thread. Returns true if the message was consumed.</summary>
    private bool HandleThreadMessage(NativeMethods.MSG msg)
    {
        // Cross-thread register request: WParam carries a GCHandle to a RegisterRequest payload.
        if (msg.Message == NativeMethods.WM_APP_REGISTER)
        {
            var handle = GCHandle.FromIntPtr(msg.WParam);
            var request = (RegisterRequest)handle.Target!;
            try
            {
                var ok = NativeMethods.RegisterHotKey(_hWnd, request.Id, request.Modifiers, request.VirtualKey);
                request.Completion.TrySetResult(ok);
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
            finally
            {
                handle.Free();
            }

            return true;
        }

        if (msg.Message == NativeMethods.WM_APP_UNREGISTER)
        {
            NativeMethods.UnregisterHotKey(_hWnd, msg.WParam.ToInt32());
            return true;
        }

        if (msg.Message == NativeMethods.WM_APP_SHUTDOWN)
        {
            return true;
        }

        return false;
    }

    /// <summary>WNDPROC — receives WM_HOTKEY from the OS and fans out to handler tasks.</summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_registrations.TryGetValue(id, out var reg))
            {
                // Run the handler off the pump thread so it can't stall message processing or
                // accidentally re-enter the clipboard / hotkey APIs from within the WNDPROC.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await reg.Handler(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Swallow per the contract: "exceptions are logged and swallowed."
                        // A real logger plugs in here once the host wires logging through DI.
                    }
                });
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Per-registration state held while a hotkey is live.</summary>
    private sealed record Registration(int Id, Func<CancellationToken, Task> Handler, string Binding);

    /// <summary>Cross-thread register-request packet posted to the pump thread.</summary>
    private sealed record RegisterRequest(int Id, uint Modifiers, uint VirtualKey, TaskCompletionSource<bool> Completion);

    /// <summary>The <see cref="IDisposable"/> returned to callers — unregistering the hotkey on disposal.</summary>
    private sealed class HotkeyHandle : IDisposable
    {
        private readonly WindowsHotkeyService _owner;
        private readonly int _id;
        private bool _disposed;

        public HotkeyHandle(WindowsHotkeyService owner, int id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Unregister(_id);
        }
    }
}
