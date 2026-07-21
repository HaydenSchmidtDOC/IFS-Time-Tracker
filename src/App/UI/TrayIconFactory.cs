using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TimeTracker.App.UI;

/// <summary>
/// Renders the system-tray icon at runtime, tinted to the live project's colour so the
/// current job reads at a glance. When stopped it draws a hollow grey chip.
/// </summary>
public static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Create a tray icon. Caller owns the returned Icon and should Dispose it.</summary>
    public static Icon Create(Color color, bool running)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var rect = new Rectangle(3, 3, 26, 26);
            using var path = RoundedRect(rect, 8);

            if (running)
            {
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);
                // subtle top sheen
                using var sheen = new LinearGradientBrush(rect,
                    Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
                g.FillPath(sheen, path);
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(150, 130, 140, 150), 2.5f);
                g.DrawPath(pen, path);
            }
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
