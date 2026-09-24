using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TimeTracker.App.Interop;

/// <summary>
/// Detects when the user steps away — either the session locks, or there is no keyboard/mouse
/// input for the configured threshold — and raises <see cref="IdleEnded"/> on return with the
/// UTC instant the idle span began, so the app can offer to keep or discard that time.
/// </summary>
public sealed class IdleWatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isRunning;
    private readonly Func<DateTime> _utcNow;

    private DateTime? _idleStartUtc;
    private bool _locked;
    // Last input tick recorded at the moment the session unlocked. Until the user provides fresh
    // input, GetLastInputInfo keeps reporting the pre-lock time, which would otherwise make Poll
    // re-detect the SAME away span that SessionUnlock already handled (double "you were away").
    private uint? _lastInputTickAtUnlock;

    public int ThresholdMinutes { get; set; }

    /// <summary>Raised on return from an idle/locked span longer than the threshold.</summary>
    public event Action<DateTime>? IdleEnded;

    public IdleWatcher(Func<bool> isRunning, int thresholdMinutes, Func<DateTime>? utcNow = null)
    {
        _isRunning = isRunning;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        ThresholdMinutes = thresholdMinutes;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Poll();

        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public void Start() => _timer.Start();

    private void Poll()
    {
        if (!_isRunning())
        {
            _idleStartUtc = null; // not tracking -> nothing to reclaim
            return;
        }

        var idleMs = GetIdleMilliseconds();
        var now = _utcNow();

        // After an unlock, ignore the stale pre-lock idle time until the user actually provides
        // fresh input. Otherwise Poll would re-fire the same away span SessionUnlock already
        // handled (double "you were away" prompt). See _lastInputTickAtUnlock.
        if (_lastInputTickAtUnlock is uint unlockTick && GetLastInputTick() == unlockTick)
        {
            _idleStartUtc = null;
            return;
        }
        _lastInputTickAtUnlock = null;

        if (idleMs >= ThresholdMinutes * 60_000L)
        {
            // Mark the moment idleness began (only once per away span).
            _idleStartUtc ??= now.AddMilliseconds(-idleMs);
        }
        else if (_idleStartUtc is DateTime start && !_locked)
        {
            // Activity resumed after an idle span.
            FireIfSignificant(start, now);
            _idleStartUtc = null;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _locked = true;
                if (_isRunning()) _idleStartUtc ??= _utcNow(); // away from lock instant
                break;

            case SessionSwitchReason.SessionUnlock:
                _locked = false;
                // Remember the input tick at unlock so Poll can ignore the stale pre-lock idle
                // time until the user provides fresh input (see Poll).
                _lastInputTickAtUnlock = GetLastInputTick();
                if (_idleStartUtc is DateTime start)
                {
                    FireIfSignificant(start, _utcNow());
                    _idleStartUtc = null;
                }
                break;
        }
    }

    private void FireIfSignificant(DateTime start, DateTime now)
    {
        if ((now - start).TotalMinutes >= ThresholdMinutes)
            IdleEnded?.Invoke(start);
    }

    private static long GetIdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    private static uint GetLastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return info.dwTime;
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
