using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Sutando.Platform.Windows.Internal;

namespace Sutando.Platform.Windows;

/// <summary>
/// GDI-based screen-capture implementation. We deliberately use BitBlt against the screen DC rather
/// than <c>Windows.Graphics.Capture</c> — the WinRT path requires a D3D11 device, Win32-WinRT interop
/// (<c>IGraphicsCaptureItemInterop</c>), and async frame pumping, which is excessive for the
/// occasional still-frame use case Sutando needs. PrintWindow + <c>PW_RENDERFULLCONTENT</c> handles
/// most Chromium / Electron / hardware-accelerated windows that the older PrintWindow flag missed.
/// </summary>
/// <remarks>
/// PNG output runs through <see cref="System.Drawing.Bitmap"/> which is Windows-only post-.NET 6 —
/// see <c>System.Drawing.Common</c> SDK guidance. The <see cref="SupportedOSPlatformAttribute"/>
/// here silences CA1416 and signals to consumers that this type is gated on Windows.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    /// <inheritdoc />
    public Task<string> CapturePrimaryAsync(string? targetPath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var bounds = MonitorEnumerator.GetPrimaryBounds();
        var path = ResolveTargetPath(targetPath, "primary");
        CaptureRegionToPng(bounds, path);
        return Task.FromResult(path);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CaptureAllAsync(string? targetDir = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var monitors = MonitorEnumerator.EnumerateAll();
        var directory = targetDir ?? Path.Combine(Path.GetTempPath(), "sutando-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var results = new List<string>(monitors.Count);
        for (var i = 0; i < monitors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, $"display-{i}.png");
            CaptureRegionToPng(monitors[i], path);
            results.Add(path);
        }

        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    /// <inheritdoc />
    public Task<string> CaptureDisplayAsync(int displayIndex, string? targetPath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var monitors = MonitorEnumerator.EnumerateAll();
        if (displayIndex < 0 || displayIndex >= monitors.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayIndex),
                displayIndex,
                $"Display index out of range — only {monitors.Count} display(s) detected.");
        }

        var path = ResolveTargetPath(targetPath, $"display-{displayIndex}");
        CaptureRegionToPng(monitors[displayIndex], path);
        return Task.FromResult(path);
    }

    /// <inheritdoc />
    public Task<string?> CaptureForegroundWindowAsync(string? targetPath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return Task.FromResult<string?>(null);
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return Task.FromResult<string?>(null);
        }

        var path = ResolveTargetPath(targetPath, "foreground");

        // PrintWindow renders into the supplied DC. We allocate a memory DC + DIB and let the
        // window paint into it — works across DWM-composited windows on Win10+ when paired with
        // PW_RENDERFULLCONTENT.
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bmp))
        {
            var hdc = graphics.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT))
                {
                    return Task.FromResult<string?>(null);
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        bmp.Save(path, ImageFormat.Png);
        return Task.FromResult<string?>(path);
    }

    /// <summary>Resolves the caller-supplied path or invents a temp-file path with a stable suffix.</summary>
    private static string ResolveTargetPath(string? targetPath, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return targetPath;
        }

        var file = $"sutando-capture-{suffix}-{Guid.NewGuid():N}.png";
        return Path.Combine(Path.GetTempPath(), file);
    }

    /// <summary>BitBlt the desktop DC for the given screen region into a 32bpp bitmap and write PNG.</summary>
    private static void CaptureRegionToPng(NativeMethods.RECT region, string path)
    {
        var width = region.Width;
        var height = region.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Refusing to capture zero-sized region: {width}x{height}.");
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(NULL) returned a null handle — cannot access the screen device context.");
        }

        var memDc = IntPtr.Zero;
        var memBmp = IntPtr.Zero;
        var oldBmp = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            memBmp = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memBmp == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleBitmap failed.");
            }

            oldBmp = NativeMethods.SelectObject(memDc, memBmp);
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, region.Left, region.Top, NativeMethods.SRCCOPY))
            {
                throw new InvalidOperationException("BitBlt failed.");
            }

            using var bmp = Image.FromHbitmap(memBmp);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bmp.Save(path, ImageFormat.Png);
        }
        finally
        {
            if (memBmp != IntPtr.Zero)
            {
                if (oldBmp != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memDc, oldBmp);
                }

                NativeMethods.DeleteObject(memBmp);
            }

            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
