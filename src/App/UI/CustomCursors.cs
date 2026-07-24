using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace TimeTracker.App.UI;

/// <summary>
/// A couple of small custom cursors WPF's built-in <see cref="Cursors"/> set doesn't offer at the
/// size wanted — <see cref="Cursors.Cross"/> renders as a fairly large native "+", too heavy for
/// a subtle hover affordance over the calendar view's empty grid space (see TimesheetWindow's
/// drag-to-add). Built once, lazily, from a tiny GDI+ bitmap rather than shipping a .cur asset —
/// the shape is simple enough that drawing it in code is less overhead than another embedded
/// resource, and it means the size/weight can be tuned by eye without a separate asset pipeline.
/// </summary>
internal static class CustomCursors
{
    private static Cursor? _smallPlus;

    /// <summary>A small "+" — same idea as <see cref="Cursors.Cross"/>, deliberately smaller and
    /// lighter for a hover-only "you can click-drag here" affordance rather than an active tool
    /// cursor. Distinct on purpose from <see cref="Cursors.SizeNS"/> (used by the calendar view's
    /// resize grips) so the two gestures don't read as the same cursor.</summary>
    public static Cursor SmallPlus => _smallPlus ??= BuildSmallPlus();

    private static Cursor BuildSmallPlus()
    {
        const int size = 16;
        const int armLen = 5; // half-length of each arm out from centre
        using var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int c = size / 2;
            // A dark outline first, then a thinner white core on top — legible over both light
            // and dark block colours, same "readable regardless of background" reasoning as the
            // live-block pulse border elsewhere in this app.
            using (var dark = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 0, 0, 0), 3))
            {
                g.DrawLine(dark, c, c - armLen, c, c + armLen);
                g.DrawLine(dark, c - armLen, c, c + armLen, c);
            }
            using (var light = new System.Drawing.Pen(System.Drawing.Color.White, 1.4f))
            {
                g.DrawLine(light, c, c - armLen, c, c + armLen);
                g.DrawLine(light, c - armLen, c, c + armLen, c);
            }
        }

        var handle = new SafeIconHandle(bmp.GetHicon());
        return CursorInteropHelper.Create(handle);
    }

    /// <summary>Owns the native HICON GetHicon() allocates — WPF's CursorInteropHelper just wraps
    /// whatever handle it's given, it doesn't take ownership, so without this the icon would leak
    /// for the life of the process (harmless for one lazily-built, process-lifetime cursor, but
    /// cheap to do properly regardless).</summary>
    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
