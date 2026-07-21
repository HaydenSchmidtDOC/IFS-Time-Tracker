using System.Windows.Media;
using WinColor = System.Windows.Media.Color;
using DrawColor = System.Drawing.Color;

namespace TimeTracker.App.UI;

/// <summary>Hex/HSL colour helpers shared across the tray icon, pill, and colour picker.</summary>
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

    public static string ToHex(WinColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Build an RGB colour from hue (degrees, any range — wraps), saturation and lightness (0..1).</summary>
    public static WinColor FromHsl(double hue, double saturation, double lightness)
    {
        double h = ((hue % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = lightness - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return WinColor.FromRgb(
            (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Extract the hue (0..360 degrees) of a colour.</summary>
    public static double GetHue(WinColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta < 1e-6) return 0;
        double h;
        if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * (((b - r) / delta) + 2);
        else h = 60 * (((r - g) / delta) + 4);
        return h < 0 ? h + 360 : h;
    }
}
