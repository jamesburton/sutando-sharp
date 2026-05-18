using System.Runtime.Versioning;

namespace Sutando.Platform.Windows.Internal;

/// <summary>
/// Lightweight wrapper over <c>EnumDisplayMonitors</c> + <c>GetSystemMetrics</c> that returns
/// monitor bounds in the same virtual-screen coordinate system <see cref="WindowsScreenCaptureService"/>
/// uses to issue BitBlts.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MonitorEnumerator
{
    /// <summary>Enumerates every connected display and returns its virtual-screen bounding rect.</summary>
    internal static IReadOnlyList<NativeMethods.RECT> EnumerateAll()
    {
        var collected = new List<NativeMethods.RECT>();

        // EnumDisplayMonitors invokes the callback on the calling thread before returning,
        // so closing over `collected` is safe — no GC reference resurrection.
        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData)
        {
            collected.Add(lprcMonitor);
            return true;
        }

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero))
        {
            // Fall back to virtual-screen bounds — better than throwing.
            collected.Add(GetVirtualScreenBounds());
        }

        if (collected.Count == 0)
        {
            collected.Add(GetVirtualScreenBounds());
        }

        return collected;
    }

    /// <summary>Returns the bounds of the primary display — index 0 by convention on Windows.</summary>
    internal static NativeMethods.RECT GetPrimaryBounds()
    {
        // The primary monitor on Windows is always anchored at (0, 0) in virtual-screen space,
        // so SM_CXSCREEN/SM_CYSCREEN (0 and 1) describe it directly.
        var width = NativeMethods.GetSystemMetrics(0);
        var height = NativeMethods.GetSystemMetrics(1);
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
    }

    /// <summary>Returns the bounding rect of the entire virtual desktop (all monitors combined).</summary>
    internal static NativeMethods.RECT GetVirtualScreenBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new NativeMethods.RECT { Left = x, Top = y, Right = x + cx, Bottom = y + cy };
    }
}
