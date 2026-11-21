using System.Windows;
using System.Windows.Input;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Glint;

public partial class App : Application
{
    public static bool Quitting { get; private set; }

    Mutex? _mutex;
    Forms.NotifyIcon? _tray;
    MainWindow? _main;
    SettingsWindow? _settings;
    TopBar? _topBar;

    /// <summary>Releases the top bar's reserved screen strip if Windows ends the session while Glint is running.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SetTopBar(false);
        base.OnSessionEnding(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch just asks the running one to show itself.
        _mutex = new Mutex(true, "Glint.SingleInstance", out bool isFirst);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Glint.ShowSignal");
        var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Glint.QuitSignal");
        if (!isFirst)
        {
            // "Glint.exe --quit" asks the running copy to exit cleanly (releasing the top bar's screen space).
            if (e.Args.Contains("--quit")) quitSignal.Set();
            else showSignal.Set();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--quit"))
        {
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            quitSignal.WaitOne();
            Dispatcher.BeginInvoke(Quit);
        })
        { IsBackground = true, Name = "Glint.QuitSignal" }.Start();

        Settings.Load();
        AppIndex.RefreshIfStale();
        Currency.EnsureFresh();

        _main = new MainWindow();
        _main.Init();
        bool hotkeyOk = _main.RegisterHotkey();
        if (!hotkeyOk && Settings.FirstRun)
        {
            // The default is taken on this machine: settle on the first free alternative.
            foreach (var mods in new[] { "Alt, Shift", "Control, Shift", "Alt, Windows" })
            {
                Settings.Data.HotkeyModifiers = mods;
                if (hotkeyOk = _main.RegisterHotkey()) break;
            }
            if (!hotkeyOk) Settings.Data.HotkeyModifiers = new SettingsData().HotkeyModifiers;
        }
        if (Settings.FirstRun) Settings.Save();

        CreateTray();
        SetTopBar(Settings.Data.TopBarEnabled);
        // Already opted in to the Claude mascot? Keep its hooks pointing at this Glint.exe (e.g. after a move).
        if (Settings.Data.BarShowClaude) Task.Run(ClaudeHooks.RepairIfOutOfDate);

        new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                Dispatcher.BeginInvoke(() =>
                {
                    // Opening Glint again with the search switched off brings up Settings instead.
                    if (Settings.Data.LauncherEnabled) _main.ShowLauncher();
                    else OpenSettings();
                });
            }
        })
        { IsBackground = true, Name = "Glint.ShowSignal" }.Start();

        // "--query <text>" opens with a query already typed (handy for testing and scripts).
        int qi = Array.IndexOf(e.Args, "--query");
        string initialQuery = qi >= 0 && qi + 1 < e.Args.Length ? e.Args[qi + 1] : "";

        if (!hotkeyOk)
        {
            OpenSettings();
            _settings!.ShowStatus($"{HotkeyText()} is already used by another app. Pick a different shortcut.", error: true);
        }
        else if (!e.Args.Contains("--background"))
        {
            if (Settings.Data.LauncherEnabled) _main.ShowLauncher(initialQuery);
            else if (!Settings.Data.TopBarEnabled) OpenSettings(); // otherwise nothing would show that Glint started
        }
    }

    public static string HotkeyText()
    {
        var parts = new List<string>();
        if (Enum.TryParse<ModifierKeys>(Settings.Data.HotkeyModifiers, out var mods))
        {
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        }
        parts.Add(Settings.Data.HotkeyKey);
        return string.Join(" + ", parts);
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open Glint", null, (_, _) => _main!.ShowLauncher());
        menu.Items.Add(open);
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        var search = new Forms.ToolStripMenuItem("Glint Search") { CheckOnClick = true, Checked = Settings.Data.LauncherEnabled };
        search.Click += (_, _) => SetLauncher(search.Checked);
        menu.Items.Add(search);
        var skyline = new Forms.ToolStripMenuItem("Skyline") { CheckOnClick = true, Checked = Settings.Data.TopBarEnabled };
        skyline.Click += (_, _) =>
        {
            Settings.Data.TopBarEnabled = skyline.Checked;
            Settings.Save();
            SetTopBar(skyline.Checked);
        };
        menu.Opening += (_, _) =>
        {
            skyline.Checked = Settings.Data.TopBarEnabled;
            search.Checked = Settings.Data.LauncherEnabled;
            open.Visible = Settings.Data.LauncherEnabled;
        };
        menu.Items.Add(skyline);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = TrayText(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button != Forms.MouseButtons.Left) return;
            if (Settings.Data.LauncherEnabled) _main!.ShowLauncher();
            else OpenSettings();
        };
    }

    /// <summary>Shows a Windows notification from the tray icon. Safe to call from any thread.</summary>
    public static void Notify(string title, string text)
    {
        // Notifications are easy to miss, so keep a small log of them next to the settings file.
        try
        {
            if (!Settings.Frozen) System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "notifications.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {title}: {text}{Environment.NewLine}");
        }
        catch { }
        if (Current is not App app) return;
        app.Dispatcher.BeginInvoke(() => app._tray?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None));
    }

    public void UpdateTrayText()
    {
        if (_tray != null) _tray.Text = TrayText();
    }

    static string TrayText() => Settings.Data.LauncherEnabled ? $"Glint ({HotkeyText()})" : "Glint";

    /// <summary>
    /// Switches the search launcher on or off; Skyline is left as it is. Returns false if it was switched on
    /// but its shortcut is taken by another app (the launcher is still on, reachable from the tray and Skyline).
    /// </summary>
    public bool SetLauncher(bool enabled)
    {
        Settings.Data.LauncherEnabled = enabled;
        Settings.Save();
        if (!enabled) _main!.HideLauncher();
        bool hotkeyOk = _main!.RegisterHotkey(); // registers, or releases the shortcut when off
        UpdateTrayText();
        RefreshTopBar();
        _settings?.LoadSearchEnabled();
        return hotkeyOk;
    }

    /// <summary>Glint's icon (Assets/Glint.ico) at the size Windows uses for tray icons on this screen.</summary>
    static Drawing.Icon MakeIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/Glint.ico")).Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>Shows or removes the top bar. It is an independent window; the launcher works either way.</summary>
    public void SetTopBar(bool enabled)
    {
        if (enabled && _topBar == null)
        {
            _topBar = new TopBar(() => _main!.ToggleLauncher());
            _topBar.Show();
        }
        else if (!enabled && _topBar != null)
        {
            _topBar.Close(); // gives the reserved strip at the top of the screen back to Windows
            _topBar = null;
        }
    }

    public void RefreshTopBar() => _topBar?.ApplyTheme();

    public void OpenSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_main!);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    public void Quit()
    {
        Quitting = true;
        _main?.UnregisterHotkey();
        SetTopBar(false);
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Shutdown();
    }
}
