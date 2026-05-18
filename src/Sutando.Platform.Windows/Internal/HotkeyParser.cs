using System.Runtime.Versioning;

namespace Sutando.Platform.Windows.Internal;

/// <summary>
/// Translates a textual hotkey binding (e.g. <c>Ctrl+Shift+C</c>) into Win32 modifier flags and a
/// virtual-key code. Accepts the common upstream spellings — <c>Ctrl</c>/<c>Control</c>,
/// <c>Alt</c>/<c>Option</c>, <c>Shift</c>, <c>Win</c>/<c>Cmd</c>/<c>Super</c> — so a single binding
/// string works across platforms.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HotkeyParser
{
    /// <summary>Parses <paramref name="binding"/> into Win32 modifier flags and a virtual key code.</summary>
    /// <exception cref="FormatException">Thrown when the binding cannot be interpreted.</exception>
    public static (uint Modifiers, uint VirtualKey) Parse(string binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);

        var tokens = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new FormatException($"Empty hotkey binding: '{binding}'.");
        }

        uint mods = 0;
        uint? vk = null;
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (TryParseModifier(token, out var mod))
            {
                mods |= mod;
                continue;
            }

            if (vk is not null)
            {
                throw new FormatException($"Hotkey binding '{binding}' contains more than one non-modifier key.");
            }

            vk = ParseKey(token);
        }

        if (vk is null)
        {
            throw new FormatException($"Hotkey binding '{binding}' has no non-modifier key.");
        }

        return (mods, vk.Value);
    }

    private static bool TryParseModifier(string token, out uint mod)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                mod = NativeMethods.MOD_CONTROL;
                return true;
            case "alt":
            case "option":
                mod = NativeMethods.MOD_ALT;
                return true;
            case "shift":
                mod = NativeMethods.MOD_SHIFT;
                return true;
            case "win":
            case "cmd":
            case "super":
            case "meta":
                mod = NativeMethods.MOD_WIN;
                return true;
            default:
                mod = 0;
                return false;
        }
    }

    /// <summary>Maps a key name to a Win32 virtual-key code. See VK_* constants in <c>winuser.h</c>.</summary>
    private static uint ParseKey(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
        }

        // Function keys F1..F24.
        if ((token.StartsWith('F') || token.StartsWith('f')) && token.Length is >= 2 and <= 3
            && int.TryParse(token.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return (uint)(0x70 + (fn - 1)); // VK_F1 = 0x70
        }

        return token.ToLowerInvariant() switch
        {
            "space" or "spacebar" => 0x20, // VK_SPACE
            "enter" or "return" => 0x0D,   // VK_RETURN
            "escape" or "esc" => 0x1B,     // VK_ESCAPE
            "tab" => 0x09,                 // VK_TAB
            "backspace" => 0x08,           // VK_BACK
            "delete" or "del" => 0x2E,     // VK_DELETE
            "insert" or "ins" => 0x2D,     // VK_INSERT
            "home" => 0x24,                // VK_HOME
            "end" => 0x23,                 // VK_END
            "pageup" or "pgup" => 0x21,    // VK_PRIOR
            "pagedown" or "pgdn" => 0x22,  // VK_NEXT
            "left" => 0x25,                // VK_LEFT
            "up" => 0x26,                  // VK_UP
            "right" => 0x27,               // VK_RIGHT
            "down" => 0x28,                // VK_DOWN
            _ => throw new FormatException($"Unknown hotkey component: '{token}'."),
        };
    }
}
