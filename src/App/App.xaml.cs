using System.Windows;
using System.Windows.Input;
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

    /// <summary>Fires once a second so open windows can repaint the running timer.</summary>
    public event Action? Tick;
    /// <summary>Fires when tracking state changes (start/switch/stop, project edits).</summary>
    public event Action? StateChanged;

    public static new App Current => (App)Application.Current;

    /// <summary>True once Quit has been chosen — lets MainWindow tell "hide to tray" from a real exit.</summary>
    public bool IsQuitting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, "TimeTracker.SingleInstance", out bool isNew);
        _ownsInstanceMutex = isNew;
        // When another instance already holds the mutex, .NET does NOT grant this thread
        // ownership even though initiallyOwned:true was requested — ReleaseMutex() must not
        // be called on it later (OnExit), or it throws on this duplicate-launch path.
        if (!isNew) { Shutdown(); return; }

        ApplyTheme();
        ApplySystemAccent();

        Paths = new AppPaths();
        Store = new JsonStore(Paths);
        Settings = Store.LoadSettings();
        if (!string.IsNullOrWhiteSpace(Settings.DataFolderOverride))
        {
            Paths = new AppPaths(Settings.DataFolderOverride);
            Store = new JsonStore(Paths);
        }
        Log = new CsvLog(Paths);
        Tracker = new TrackerService(Store, Log);
        Exporter = new IfsExporter(Log, Paths);

        Tracker.Changed += () => { UpdateTray(); StateChanged?.Invoke(); };

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
        _uiTimer.Tick += (_, _) => { Tick?.Invoke(); UpdateTrayTooltip(); };
        _uiTimer.Start();

        UpdateTray();
        ShowMainWindow();
    }

    // ---------------- theme ----------------

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

        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative)
        };
        Resources.MergedDictionaries.Insert(0, dict);
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
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            { "Accent", new SolidColorBrush(accent) },
            { "AccentInk", new SolidColorBrush(ink) },
        });
    }

    // ---------------- tray ----------------

    private void BuildTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "IFS Time Tracker",
            Visible = true,
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
        if (Tracker.IsRunning && Tracker.Active is { } ending && ending.Code != project.Code)
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

    private string MaybeAskNote(Project ending)
    {
        if (!Settings.PromptForNote) return "";
        return NotePrompt.Ask(ending) ?? "";
    }

    public void OpenSwitcher()
    {
        if (_switcher is { IsVisible: true }) { _switcher.Activate(); return; }
        _switcher = new SwitcherWindow();
        _switcher.Closed += (_, _) => _switcher = null;
        _switcher.Show();
        _switcher.Activate();
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

    public void QuitApp()
    {
        IsQuitting = true;
        _uiTimer?.Stop();
        _idle?.Dispose();
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        _pill?.Close();
        _main?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
