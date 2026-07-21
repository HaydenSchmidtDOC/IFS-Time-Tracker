using System.Windows.Media;
using WinColor = System.Windows.Media.Color;
using DrawColor = System.Drawing.Color;

namespace TimeTracker.App.UI;

/// <summary>Hex-colour parsing shared across the tray icon, pill, and swatches.</summary>
public static class ColorUtil
{
    public static WinColor Parse(string hex, WinColor fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return (WinColor)ColorConverter.ConvertFromString(hex);
        }
        catch { return fallback; }
    }

    public static WinColor Parse(string hex) => Parse(hex, Colors.SteelBlue);

    public static SolidColorBrush Brush(string hex) => new(Parse(hex));

    public static DrawColor ToDrawing(WinColor c) => DrawColor.FromArgb(c.A, c.R, c.G, c.B);

    public static DrawColor ToDrawing(string hex) => ToDrawing(Parse(hex));
}
