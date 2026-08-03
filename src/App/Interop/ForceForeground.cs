using System.Runtime.InteropServices;

namespace TimeTracker.App.Interop;

/// <summary>
/// Reliably makes a window the true OS foreground/active window — not just frontmost in
/// z-order. Window.Activate() (which just calls SetForegroundWindow under the hood) was proving
/// unreliable specifically when switching between the main window and the Timesheets window:
/// whichever one you switched TO would visually end up on top, but the OS still treated the
/// OTHER one as active, so the first click or keypress went nowhere until you clicked it
/// directly. A Topmost-toggle workaround (and later, deferring it to run after the other
/// window's Hide() via Dispatcher.BeginInvoke) each narrowed the failure window but didn't
/// close it — consistent with Windows' actual SetForegroundWindow restriction: a background
/// thread's request is silently ignored unless it currently owns the foreground, or its input
/// state is attached to the thread that does. AttachThreadInput below satisfies that
/// unconditionally, which plain SetForegroundWindow calls (from Activate() or otherwise) don't.
/// </summary>
public static class ForceForeground
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

    public static void Apply(IntPtr hwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd) return;

        uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint currentThread = GetCurrentThreadId();

        bool attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
