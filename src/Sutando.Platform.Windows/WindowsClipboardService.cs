using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Sutando.Platform.Windows;

/// <summary>
/// Win32 clipboard wrapper. Marshals <c>CF_UNICODETEXT</c> via <c>GlobalAlloc(GMEM_MOVEABLE)</c>
/// — the only memory shape <c>SetClipboardData</c> accepts.
/// </summary>
/// <remarks>
/// <para>
/// The clipboard is per-session global state; <c>OpenClipboard</c> can briefly fail if another
/// process has it open. We retry a small number of times with a short back-off, which matches what
/// the Win32 SDK samples do.
/// </para>
/// <para>
/// We pass <see cref="IntPtr.Zero"/> as the owner HWND. Best practice is to pass a real window
/// handle so the clipboard's "last owner" is tracked, but the docs explicitly permit Zero — the data
/// is then attributed to the current task. That's acceptable for a CLI/agent context where no UI
/// window exists.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardService : IClipboardService
{
    private const int MaxOpenRetries = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    /// <inheritdoc />
    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
        {
            return null;
        }

        await OpenClipboardWithRetryAsync(ct).ConfigureAwait(false);
        try
        {
            var hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (hData == IntPtr.Zero)
            {
                return null;
            }

            var locked = NativeMethods.GlobalLock(hData);
            if (locked == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // PtrToStringUni walks until the first L'\0' — exactly what CF_UNICODETEXT promises.
                return Marshal.PtrToStringUni(locked);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hData);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <inheritdoc />
    public async Task SetTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ct.ThrowIfCancellationRequested();

        // +1 for the terminating null wide char.
        var byteCount = (text.Length + 1) * sizeof(char);
        var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (hGlobal == IntPtr.Zero)
        {
            throw new InvalidOperationException("GlobalAlloc failed — could not allocate clipboard buffer.");
        }

        var transferredOwnership = false;
        try
        {
            var dest = NativeMethods.GlobalLock(hGlobal);
            if (dest == IntPtr.Zero)
            {
                throw new InvalidOperationException("GlobalLock failed.");
            }

            try
            {
                // Materialise the string as little-endian UTF-16 bytes (which is what
                // CF_UNICODETEXT expects on Windows) plus an explicit two-byte null terminator,
                // then copy into the GlobalAlloc'd buffer via Marshal.Copy. This is the form
                // every Windows clipboard sample uses and avoids any unsafe-pointer aliasing.
                var bytes = Encoding.Unicode.GetBytes(text);
                Marshal.Copy(bytes, 0, dest, bytes.Length);
                Marshal.WriteInt16(dest, bytes.Length, 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hGlobal);
            }

            await OpenClipboardWithRetryAsync(ct).ConfigureAwait(false);
            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    throw new InvalidOperationException("EmptyClipboard failed.");
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    throw new InvalidOperationException("SetClipboardData failed.");
                }

                // The OS owns hGlobal now — must NOT free it.
                transferredOwnership = true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            if (!transferredOwnership)
            {
                NativeMethods.GlobalFree(hGlobal);
            }
        }
    }

    private static async Task OpenClipboardWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxOpenRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return;
            }

            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Failed to open the clipboard after {MaxOpenRetries} attempts — another process may be holding it open.");
    }
}
