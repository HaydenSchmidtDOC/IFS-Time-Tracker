using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TimeTracker.App.Interop;
using TimeTracker.App.UI;
using TimeTracker.Core;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TimeTracker.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    public AppPaths Paths { get; private set; } = null!;
    public JsonStore Store { get; private set; } = null!;
    public CsvLog Log { get; private set; } = null!;
    public Settings Settings { get; private set; } = null!;
    public TrackerService Tracker { get; private set; } = null!;
    public IfsExporter Exporter { get; private set; } = null!;

    private HotKeyManager _hotkeys = null!;
    private IdleWatcher _idle = null!;
    private DispatcherTimer _uiTimer = null!;

    private WinForms.NotifyIcon _tray = null!;
    private Drawing.Icon? _trayIcon;

    private MainWindow _main = null!;
    private PillWindow _pill = null!;
    private SwitcherWindow? _switcher;
    private TimesheetWindow? _timesheets;

    /// <summary>Fires once a second so open windows can repaint the running timer.</summary>
    public event Action? Tick;
    /// <summary>Fires when tracking state changes (start/switch/stop, project edits).</summary>
    public event Action? StateChanged;
    /// <summary>Fires when Windows' theme/accent colour actually changes (see
    /// OnSystemUserPreferenceChanged). Most themed colours are DynamicResource lookups that pick
    /// this up on their own for free; this is only for the handful of spots that instead cache a
    /// FindResource'd brush on a persistent (Hide/Show-reused, not recreated) window and only
    /// repaint it on their own existing refresh cadence — e.g. PillWindow's idle-dot fallback
    /// colour, which otherwise wouldn't repaint until the next actual start/stop/switch.</summary>
    public event Action? ThemeChanged;

    public static new App Current => (App)Application.Current;

    /// <summary>True once Quit has been chosen — lets MainWindow tell "hide to tray" from a real exit.</summary>
    public bool IsQuitting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort safety net: WPF kills the whole process on any unhandled exception on the
        // UI thread by default, with no dialog and nothing to diagnose from afterwards. For a
        // time tracker that's a real cost beyond the crash itself — an active tracking session
        // dies with it. Logging + staying open trades "might be in a slightly odd state" for
        // "definitely didn't just lose your running timer", which is the right side to err on
        // here; it isn't a substitute for fixing the underlying bug.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _instanceMutex = new Mutex(true, "TimeTracker.SingleInstance", out bool isNew);
        _ownsInstanceMutex = isNew;
        // When another instance already holds the mutex, .NET does NOT grant this thread
        // ownership even though initiallyOwned:true was requested — ReleaseMutex() must not
        // be called on it later (OnExit), or it throws on this duplicate-launch path.
        if (!isNew) { Shutdown(); return; }

        ApplyTheme();
        ApplySystemAccent();
        // Windows raises this (via a dedicated message-only window SystemEvents owns) whenever
        // theme/accent settings change in Settings > Personalization — General covers light/dark
        // mode, Color covers the accent colour. Re-running the same two Apply* calls used at
        // startup is enough to re-skin the whole app live: every themed colour in the app is a
        // DynamicResource lookup (see ApplySystemAccent's remarks), and WPF's DynamicResource
        // resolution already re-notifies every consumer on its own once the dictionaries backing
        // it change — no per-window/per-control code needed here beyond that.
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;

        Paths = new AppPaths();
        Store = new JsonStore(Paths);
        Settings = Store.LoadSettings();
        if (!string.IsNullOrWhiteSpace(Settings.DataFolderOverride))
        {
            Paths = new AppPaths(Settings.DataFolderOverride);
            Store = new JsonStore(Paths);
            Settings = Store.LoadSettings(); // re-read from the override folder's own settings.json,
                                              // not the bootstrap copy next to the exe that only
                                              // told us where to look
        }
        // Never let a settings combination leave the app with literally no visible surface —
        // no tray icon, no pill, and a hidden main window would be unrecoverable without killing
        // the process from Task Manager.
        if (Settings.StartMinimized && !Settings.ShowTrayIcon && !Settings.PillVisible)
            Settings.StartMinimized = false;

        Log = new CsvLog(Paths);
        Tracker = new TrackerService(Store, Log);
        Exporter = new IfsExporter(Log, Paths);

        Tracker.Changed += () => { UpdateTray(); StateChanged?.Invoke(); };
        Tracker.StartRefused += collision => InfoPrompt.Show("Can't start tracking", CollisionMessage(collision, ended: false));
        // No LiveSessionCollided subscription here (unlike StartRefused above) — that fires only
        // AFTER the block is already banked, too late to fold a note-prompt into the same window.
        // CheckForLiveCollisionWithPrompt below drives that flow directly instead.

        BuildTray();

        _main = new MainWindow();
        _pill = new PillWindow();
        if (Settings.PillVisible) _pill.Show();

        _hotkeys = new HotKeyManager();
        _hotkeys.Register(ModifierKeys.Control, Key.Up, OpenSwitcher);
        _hotkeys.Register(ModifierKeys.Control, Key.Down, OpenSwitcher);

        _idle = new IdleWatcher(() => Tracker.IsRunning, Settings.IdleThresholdMinutes);
        _idle.IdleEnded += OnIdleEnded;
        _idle.Start();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // Checked first, before Tick fires — if it stops the session, everything Tick refreshes
        // (pill, timesheet, tray) should already see the post-stop state, not lag a tick behind.
        _uiTimer.Tick += (_, _) => { CheckForLiveCollisionWithPrompt(); Tick?.Invoke(); UpdateTrayTooltip(); };
        _uiTimer.Start();

        UpdateTray();
        if (!Settings.StartMinimized) ShowMainWindow();
    }

    // ---------------- theme ----------------

    /// <summary>Whether the app resolved to the light theme — consulted by windows (the
    /// timesheet view) that need to match their native OS chrome to it.</summary>
    public bool IsLightTheme { get; private set; } = true;

    // Tracks the dictionary each Apply* last inserted, so re-running them on a live theme/accent
    // change replaces it in place instead of layering another copy on top every time.
    private ResourceDictionary? _themeDict;
    private ResourceDictionary? _accentDict;

    private void ApplyTheme()
    {
        bool light = true;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) light = v != 0;
        }
        catch { /* default light */ }
        IsLightTheme = light;

        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative)
        };
        if (_themeDict is not null) Resources.MergedDictionaries.Remove(_themeDict);
        Resources.MergedDictionaries.Insert(0, dict);
        _themeDict = dict;
    }

    /// <summary>
    /// Override the theme's static Accent/AccentInk with the user's actual Windows accent
    /// colour, if readable. Every "blue highlight" in the app (save buttons, the selected-
    /// project border, pill toggle hover) is a DynamicResource lookup on these two keys, so
    /// adding a dictionary on top of the theme is enough to re-skin all of them at once.
    /// </summary>
    private void ApplySystemAccent()
    {
        if (!SystemAccent.TryGetAccentColor(out var accent)) return;
        var ink = SystemAccent.ReadableInk(accent);
        var dict = new ResourceDictionary
        {
            { "Accent", new SolidColorBrush(accent) },
            { "AccentInk", new SolidColorBrush(ink) },
        };
        if (_accentDict is not null) Resources.MergedDictionaries.Remove(_accentDict);
        Resources.MergedDictionaries.Add(dict);
        _accentDict = dict;
    }

    /// <summary>Re-skins the whole app the moment Windows' theme/accent colour actually changes,
    /// rather than only picking it up on the next launch. SystemEvents raises this off the UI
    /// thread (its own message-only window), so the actual work is marshalled back via
    /// Dispatcher; the two Apply* calls are cheap (a handful of resource lookups), and open
    /// windows using a borderless/no-native-chrome style don't need anything beyond that — except
    /// the one DWM caption-colour attribute (DarkTitleBar), which isn't resource-driven and so is
    /// explicitly refreshed for every currently-open window.</summary>
    private void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
        Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme();
            ApplySystemAccent();
            foreach (Window w in Windows)
            {
                if (!w.IsLoaded) continue;
                try { DarkTitleBar.Apply(new WindowInteropHelper(w).Handle, !IsLightTheme); }
                catch (InvalidOperationException) { /* handle not yet created — nothing to re-skin */ }
            }
            ThemeChanged?.Invoke();
        });
    }

    // ---------------- tray ----------------

    private void BuildTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "IFS Time Tracker",
            Visible = Settings.ShowTrayIcon,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Switch project…", null, (_, _) => OpenSwitcher());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Stop", null, (_, _) => StopWithPrompt());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitApp());
        _tray.ContextMenuStrip = menu;
    }

    private void UpdateTray()
    {
        var color = Tracker.Active is { } p ? ColorUtil.ToDrawing(p.Color) : Drawing.Color.Gray;
        var newIcon = TrayIconFactory.Create(color, Tracker.IsRunning);
        _tray.Icon = newIcon;
        _trayIcon?.Dispose();
        _trayIcon = newIcon;
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        string text;
        if (Tracker.IsRunning && Tracker.Active is { } p)
            text = $"{p.Code} · ASN {p.Asn}\n{FormatElapsed(Tracker.CurrentElapsedSeconds)}";
        else
            text = "IFS Time Tracker — stopped";
        // NotifyIcon.Text is capped at 63 chars.
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Shared wording for both tracking-collision moments — same facts, phrased for
    /// whichever triggered it. The refused-start case (ended: false, see Tracker.StartRefused)
    /// always shows as its own InfoPrompt; the auto-stopped case (ended: true) is normally folded
    /// into NotePrompt's own explanation line instead (see CheckForLiveCollisionWithPrompt) and
    /// only shows as a standalone InfoPrompt when note-prompting is turned off.</summary>
    private string CollisionMessage(TimeBlock collision, bool ended)
    {
        var p = Tracker.FindByBlock(collision);
        string who = p is not null
            ? (string.IsNullOrWhiteSpace(p.Name) ? p.Code : $"{p.Code} · {p.Name}")
            : collision.ProjectCode;
        return ended
            ? $"Recording reached an already-logged {who} entry at {collision.StartLocal:HH:mm}, so it was stopped there automatically."
            : $"You already have {who} logged for {collision.StartLocal:HH:mm}–{collision.EndLocal:HH:mm} today.";
    }

    public static string FormatElapsed(long seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    // ---------------- coordination ----------------

    public void ShowMainWindow()
    {
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; _main.Topmost = false;
    }

    /// <summary>Start or switch to a project, prompting for a note on any block being ended.</summary>
    public void StartOrSwitch(Project project)
    {
        if (Tracker.IsRunning && Tracker.Active is { } ending && ending.Id != project.Id)
        {
            var note = MaybeAskNote(ending);
            Tracker.StartOrSwitch(project, note);
        }
        else
        {
            Tracker.StartOrSwitch(project);
        }
    }

    public void StopWithPrompt()
    {
        if (Tracker.IsRunning && Tracker.Active is { } ending)
            Tracker.Stop(MaybeAskNote(ending));
        else
            Tracker.Stop();
    }

    /// <summary>Resume the last project (pill Start toggle when stopped).</summary>
    public void ResumeLast() => Tracker.ResumeLast();

    /// <summary>Prompt for an optional note if Settings.PromptForNote is on, else "". Internal
    /// (not private) so other flows that bank a block outside the normal start/switch/stop path
    /// — e.g. AddTimeDialog's manual entry — go through the same configured behaviour.</summary>
    internal string MaybeAskNote(Project ending)
    {
        if (!Settings.PromptForNote) return "";
        return NotePrompt.Ask(ending) ?? "";
    }

    /// <summary>Runs once a second (see the _uiTimer.Tick hookup) to notice a live session that's
    /// grown into an existing block ahead of it, and — unlike the old CheckForLiveCollision, which
    /// stopped it with an empty note before anyone could react — gives the same note-prompting
    /// chance a normal Stop gets first. When notes are enabled, that's ONE window doing double
    /// duty: NotePrompt's explanation line is swapped for the collision explanation instead of a
    /// separate "Session ended" popup stacking on top. Notes off skips straight to the plain
    /// heads-up, since there's nothing to collect.</summary>
    private void CheckForLiveCollisionWithPrompt()
    {
        if (Tracker.PeekLiveCollision() is not { } collision) return;
        string note = "";
        if (Settings.PromptForNote && Tracker.Active is { } ending)
            note = NotePrompt.Ask(ending, explanation: CollisionMessage(collision, ended: true)) ?? "";
        Tracker.FinishLiveCollision(collision, note);
        if (!Settings.PromptForNote) InfoPrompt.Show("Session ended", CollisionMessage(collision, ended: true));
    }

    public void OpenSwitcher()
    {
        if (_switcher is { IsVisible: true }) { _switcher.Activate(); return; }
        _hotkeys.PauseAll(); // let Ctrl+Up/Down work as plain navigation while it's open — see HotKeyManager.PauseAll
        _switcher = new SwitcherWindow();
        _switcher.Closed += (_, _) => { _switcher = null; _hotkeys.ResumeAll(); };
        _switcher.Show();
        _switcher.Activate();
    }

    /// <summary>Open the weekly timesheet view, animating it growing out of the main window.</summary>
    public void OpenTimesheets()
    {
        _timesheets ??= new TimesheetWindow();
        if (_timesheets.IsVisible) { _timesheets.Activate(); return; }
        _timesheets.AnimateOpenFrom(_main);
        _main.Hide();
    }

    /// <summary>Shrink the timesheet view back into the main window and reveal it again.</summary>
    public void CloseTimesheets()
    {
        if (_timesheets is not { IsVisible: true } ts) return;

        // The two windows should feel like one app: if the timesheet window got dragged/resized
        // elsewhere, the main window follows it back rather than reappearing at its original
        // launch position. Repositioning happens while main is still hidden, so it's invisible.
        var wa = TimesheetWindow.WorkAreaFor(ts);
        double mainLeft = TimesheetWindow.Clamp(ts.Left + ts.Width / 2 - _main.Width / 2, wa.Left, wa.Right - _main.Width);
        double mainTop = TimesheetWindow.Clamp(ts.Top + ts.Height / 2 - _main.Height / 2, wa.Top, wa.Bottom - _main.Height);
        _main.Left = mainLeft;
        _main.Top = mainTop;

        ts.AnimateCloseTo(_main, () =>
        {
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        });
    }

    private void OnIdleEnded(DateTime idleStartUtc)
    {
        if (Tracker.Active is not { } p) return;
        var away = DateTime.UtcNow - idleStartUtc;
        bool keep = IdlePrompt.Ask(p, away);
        if (!keep) Tracker.DiscardIdleSince(idleStartUtc);
    }

    /// <summary>Re-read the idle threshold from Settings into the running watcher.</summary>
    public void RefreshIdleThreshold() => _idle.ThresholdMinutes = Settings.IdleThresholdMinutes;

    /// <summary>Show or hide the floating pill and persist the preference.</summary>
    public void SetPillVisible(bool visible)
    {
        Settings.PillVisible = visible;
        Store.SaveSettings(Settings);
        if (visible) _pill.Show(); else _pill.Hide();
    }

    /// <summary>Show or hide the system-tray icon and persist the preference.</summary>
    public void SetTrayIconVisible(bool visible)
    {
        Settings.ShowTrayIcon = visible;
        Store.SaveSettings(Settings);
        _tray.Visible = visible;
    }

    public void QuitApp()
    {
        IsQuitting = true;
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        _uiTimer?.Stop();
        _idle?.Dispose();
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        _pill?.Close();
        _timesheets?.Close();
        _main?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Paths?.DataFolder ?? AppContext.BaseDirectory;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n";
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch { /* logging must never be what actually crashes the app */ }

        MessageBox.Show(
            $"Something went wrong, and it's been written to crash.log in the data folder.\n\n" +
            "The app is staying open so you don't lose an active tracking session — but if things " +
            "look off, it's worth saving your work and restarting.\n\n" + e.Exception.Message,
            "IFS Time Tracker — unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
