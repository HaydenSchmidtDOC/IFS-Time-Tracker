using System.Windows.Media;
using Microsoft.Win32;

namespace TimeTracker.App.UI;

/// <summary>Reads the Windows system accent colour so the app's highlight matches it.</summary>
public static class SystemAccent
{
    /// <summary>
    /// DWM stores the current accent as a DWORD at HKCU\...\DWM\AccentColor, byte-order R,G,B,A
    /// (no WinRT/UWP projection needed — same registry-reading approach already used for the
    /// light/dark theme check).
    /// </summary>
    public static bool TryGetAccentColor(out Color color)
    {
        color = default;
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int raw)
            {
                var b = BitConverter.GetBytes(raw);
                color = Color.FromRgb(b[0], b[1], b[2]);
                return true;
            }
        }
        catch { /* fall through to theme default */ }
        return false;
    }

    /// <summary>Pick readable near-black or near-white ink for text/icons drawn on top of a background.</summary>
    public static Color ReadableInk(Color bg)
    {
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance > 0.55 ? Color.FromRgb(0x0A, 0x10, 0x15) : Colors.White;
    }
}
