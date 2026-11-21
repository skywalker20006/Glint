using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Glint;

public partial class SettingsWindow : Window
{
    readonly MainWindow _main;
    bool _recording;

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        HotkeyButton.Content = App.HotkeyText();
        CurrencyBox.Text = Settings.HomeCurrency;
        StartupBox.IsChecked = Settings.LaunchAtStartup;
        LoadExclusions();
        LoadAppearance();
        LoadSearchEnabled();
        AutoUpdateBox.IsChecked = Settings.Data.AutoCheckUpdates;
        ShowUpdateState();
        Action onUpdate = () => Dispatcher.BeginInvoke(ShowUpdateState);
        Updater.Changed += onUpdate;
        Closed += (_, _) =>
        {
            Updater.Changed -= onUpdate;
            if (_recording) _main.RegisterHotkey();
            Settings.Save();
        };
    }

    // ---------- appearance ----------

    static readonly (string Name, string Hex)[] Accents =
    {
        ("Neutral", ""), ("Blue", "#3B9DFF"), ("Purple", "#8B5CF6"), ("Pink", "#EC4899"),
        ("Red", "#EF4444"), ("Orange", "#F59E0B"), ("Green", "#22C55E"), ("Teal", "#14B8A6"),
    };

    bool _loading;

    void LoadAppearance()
    {
        _loading = true;
        var s = Settings.Data;
        ThemeDark.IsChecked = s.Theme == "Dark";
        ThemeLight.IsChecked = s.Theme == "Light";
        ThemeSystem.IsChecked = s.Theme != "Dark" && s.Theme != "Light";
        GlassLive.IsChecked = s.GlassMode != "Snapshot";
        GlassSnapshot.IsChecked = s.GlassMode == "Snapshot";
        UpdateBlurEnabled();
        LoadSkyline();
        BlurSlider.Value = s.BlurStrength;
        TintSlider.Value = s.GlassOpacity;
        WidthSlider.Value = s.PanelWidth;
        TopSlider.Value = s.TopPercent;
        _loading = false;
        UpdateSliderLabels();
        BuildAccents();
    }

    /// <summary>One round swatch per accent; the chosen one gets a white ring.</summary>
    void BuildAccents()
    {
        AccentPanel.Children.Clear();
        foreach (var (name, hex) in Accents)
        {
            bool chosen = string.Equals(Settings.Data.Accent, hex, StringComparison.OrdinalIgnoreCase);
            var swatch = new System.Windows.Controls.Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 0),
                Background = hex.Length == 0
                    ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(chosen ? 2.5 : 0),
                Cursor = Cursors.Hand,
                ToolTip = name,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                Settings.Data.Accent = hex;
                Settings.Save();
                BuildAccents();
            };
            AccentPanel.Children.Add(swatch);
        }
    }

    void UpdateSliderLabels()
    {
        BlurValue.Text = ((int)BlurSlider.Value).ToString();
        TintValue.Text = (int)TintSlider.Value + "%";
        WidthValue.Text = (int)WidthSlider.Value + " px";
        TopValue.Text = (int)TopSlider.Value + "%";
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string theme })
        {
            Settings.Data.Theme = theme;
            Settings.Save();
            ((App)Application.Current).RefreshTopBar();
        }
    }

    void OnGlassClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string mode })
        {
            Settings.Data.GlassMode = mode;
            Settings.Save();
            UpdateBlurEnabled();
            ((App)Application.Current).RefreshTopBar();
        }
    }

    /// <summary>Windows sets the strength of live blur itself, so the slider only applies to snapshots.</summary>
    void UpdateBlurEnabled()
    {
        bool snapshot = Settings.Data.GlassMode == "Snapshot";
        BlurSlider.IsEnabled = snapshot;
        BlurLabel.Opacity = BlurValue.Opacity = snapshot ? 1 : 0.4;
        BlurSlider.ToolTip = snapshot ? null : "Live glass uses Windows' own blur strength. Switch to Snapshot to adjust it.";
    }

    // ---------- Skyline ----------

    static readonly Dictionary<string, string> MaterialHints = new()
    {
        ["Blur"] = "Clear frosted glass. Windows and video behind it show through, softly blurred.",
        ["Acrylic"] = "The Windows Fluent look: live blur under a denser tint with a fine grain.",
        ["Mica"] = "Tinted by your wallpaper only. Stays calm and doesn't change with windows behind it.",
        ["Solid"] = "No transparency. Used automatically if Windows transparency effects are off.",
    };

    void LoadSkyline()
    {
        var s = Settings.Data;
        TopBarBox.IsChecked = s.TopBarEnabled;
        MatBlur.IsChecked = s.BarMaterial == "Blur";
        MatAcrylic.IsChecked = s.BarMaterial == "Acrylic";
        MatMica.IsChecked = s.BarMaterial == "Mica";
        MatSolid.IsChecked = s.BarMaterial == "Solid";
        MaterialHint.Text = MaterialHints.GetValueOrDefault(s.BarMaterial, "");
        ShowMedia.IsChecked = s.BarShowMedia;
        ShowSpeed.IsChecked = s.BarShowSpeed;
        ShowVolume.IsChecked = s.BarShowVolume;
        ShowWifi.IsChecked = s.BarShowWifi;
        ShowBluetooth.IsChecked = s.BarShowBluetooth;
        ShowBattery.IsChecked = s.BarShowBattery;
        ShowNotifications.IsChecked = s.BarShowNotifications;
        ShowClaude.IsChecked = s.BarShowClaude;
        AllMonitorsBox.IsChecked = s.BarAllMonitors;
        SkylineOptions.IsEnabled = s.TopBarEnabled;
        SkylineOptions.Opacity = s.TopBarEnabled ? 1 : 0.45;
    }

    /// <summary>Reflects whether the search launcher is on: the switch, and greys out its options when it is off.</summary>
    public void LoadSearchEnabled()
    {
        bool on = Settings.Data.LauncherEnabled;
        SearchBox.IsChecked = on;
        foreach (var panel in new FrameworkElement[] { SearchOptions, SearchAppearance })
        {
            panel.IsEnabled = on;
            panel.Opacity = on ? 1 : 0.45;
        }
        HotkeyButton.Content = App.HotkeyText();
    }

    void OnSearchClick(object sender, RoutedEventArgs e)
    {
        bool on = SearchBox.IsChecked == true;
        if (_recording) StopRecording();
        bool hotkeyOk = ((App)Application.Current).SetLauncher(on);
        if (!on)
            ShowStatus(Settings.Data.TopBarEnabled
                ? $"Glint Search is off. {App.HotkeyText()} is free for other apps; Skyline keeps running."
                : "Glint Search and Skyline are both off. Glint stays in the tray, where you can turn either back on.");
        else if (hotkeyOk)
            ShowStatus($"Glint Search is on. Press {App.HotkeyText()} to open it.");
        else
            ShowStatus($"Glint Search is on, but {App.HotkeyText()} is taken by another app. Pick a different shortcut.", error: true);
    }

    void OnAllMonitorsClick(object sender, RoutedEventArgs e)
    {
        Settings.Data.BarAllMonitors = AllMonitorsBox.IsChecked == true;
        Settings.Save();
        if (Settings.Data.TopBarEnabled) ((App)Application.Current).SyncTopBars();
    }

    void OnTopBarClick(object sender, RoutedEventArgs e)
    {
        Settings.Data.TopBarEnabled = TopBarBox.IsChecked == true;
        Settings.Save();
        ((App)Application.Current).SetTopBar(Settings.Data.TopBarEnabled);
        LoadSkyline();
    }

    void OnMaterialClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string material }) return;
        Settings.Data.BarMaterial = material;
        Settings.Save();
        MaterialHint.Text = MaterialHints.GetValueOrDefault(material, "");
        ((App)Application.Current).RefreshTopBar();
    }

    /// <summary>
    /// The mascot needs hooks in Claude Code's own settings, so turning it on asks first and says exactly what
    /// will change; turning it off takes Glint's hooks out again.
    /// </summary>
    void OnClaudeMascotClick(object sender, RoutedEventArgs e)
    {
        bool on = ShowClaude.IsChecked == true;
        if (on)
        {
            string message =
                "To know when Claude is working or finished, Glint needs to add a few small hooks to Claude Code's settings:\n\n" +
                ClaudeHooks.SettingsPath + "\n\n" +
                "• They only tell Glint \"busy\" or \"ready\". Nothing you type is stored or sent anywhere.\n" +
                "• They run in the background, so Claude never waits for them.\n" +
                "• Your other Claude Code settings and hooks stay exactly as they are, and a backup is saved first.\n" +
                "• Turning the mascot off removes them again.\n\n" +
                "This works with Claude Code (terminal, desktop app's Code tab, editor extensions), not regular chats.\n\n" +
                "Add the hooks?";
            if (MessageBox.Show(this, message, "Claude mascot", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                ShowClaude.IsChecked = false;
                return;
            }
            try
            {
                ClaudeHooks.Install();
            }
            catch (Exception ex)
            {
                ShowClaude.IsChecked = false;
                ShowStatus("Couldn't set up the Claude mascot: Claude Code's settings file wasn't changed. " +
                           (ex is ClaudeHooks.RefusedException ? ex.Message : "(" + ex.Message + ")"), error: true);
                return;
            }
            ShowStatus("Claude mascot is on. Claude Code sessions started from now on will report to Glint.");
        }
        else
        {
            try
            {
                ClaudeHooks.Remove();
                ShowStatus("Claude mascot is off, and Glint's hooks were removed from Claude Code's settings.");
            }
            catch (Exception ex)
            {
                ShowStatus("Claude mascot is off, but Glint's hooks couldn't be removed: " + ex.Message, error: true);
            }
        }
        Settings.Data.BarShowClaude = on;
        Settings.Save();
        ((App)Application.Current).RefreshTopBar();
    }

    void OnItemClick(object sender, RoutedEventArgs e)
    {
        var s = Settings.Data;
        s.BarShowMedia = ShowMedia.IsChecked == true;
        s.BarShowSpeed = ShowSpeed.IsChecked == true;
        s.BarShowVolume = ShowVolume.IsChecked == true;
        s.BarShowWifi = ShowWifi.IsChecked == true;
        s.BarShowBluetooth = ShowBluetooth.IsChecked == true;
        s.BarShowBattery = ShowBattery.IsChecked == true;
        s.BarShowNotifications = ShowNotifications.IsChecked == true;
        Settings.Save();
        ((App)Application.Current).RefreshTopBar();
    }

    void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || BlurValue == null || TopValue == null) return; // fires while the window is still loading
        Settings.Data.BlurStrength = (int)BlurSlider.Value;
        Settings.Data.GlassOpacity = (int)TintSlider.Value;
        Settings.Data.PanelWidth = (int)WidthSlider.Value;
        Settings.Data.TopPercent = (int)TopSlider.Value;
        UpdateSliderLabels(); // written to disk when the window closes or Preview is pressed
    }

    void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        Settings.Save();
        _main.HideLauncher();
        _main.ShowLauncher("note");
    }

    void OnResetClick(object sender, RoutedEventArgs e)
    {
        var defaults = new SettingsData();
        var s = Settings.Data;
        s.Theme = defaults.Theme;
        s.GlassMode = defaults.GlassMode;
        s.Accent = defaults.Accent;
        s.BlurStrength = defaults.BlurStrength;
        s.GlassOpacity = defaults.GlassOpacity;
        s.PanelWidth = defaults.PanelWidth;
        s.TopPercent = defaults.TopPercent;
        Settings.Save();
        LoadAppearance();
        ((App)Application.Current).RefreshTopBar();
        ShowStatus("Appearance reset to defaults.");
    }

    // ---------- version and updates ----------

    void ShowUpdateState()
    {
        VersionText.Text = "Glint " + Updater.Short(Updater.Current);
        var available = Updater.Available;
        ViewReleaseButton.Visibility = available != null ? Visibility.Visible : Visibility.Collapsed;
        CheckNowButton.IsEnabled = !Updater.Checking;

        if (Updater.Checking) UpdateText.Text = "Checking for updates…";
        else if (available != null) UpdateText.Text = $"Version {Updater.Short(available)} is available";
        else if (Updater.Error != null) UpdateText.Text = Updater.Error;
        else if (Settings.Data.LastUpdateCheck > DateTime.MinValue) UpdateText.Text = "You're up to date";
        else UpdateText.Text = "";

        // A green tick for "you're up to date", with when that was checked on hover.
        bool upToDate = !Updater.Checking && available == null && Updater.Error == null && Settings.Data.LastUpdateCheck > DateTime.MinValue;
        UpToDateTick.Visibility = upToDate ? Visibility.Visible : Visibility.Collapsed;
        UpdateText.Margin = new Thickness(upToDate ? 6 : 10, 0, 12, 0);
        UpdateText.ToolTip = upToDate ? "Checked " + Ago(Settings.Data.LastUpdateCheck) : null;

        UpdateText.Foreground = available != null || upToDate
            ? new SolidColorBrush(Color.FromRgb(140, 220, 160))
            : (Brush)FindResource("Muted");
    }

    static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return utc.ToLocalTime().ToString("d MMM");
    }

    void OnCheckNowClick(object sender, RoutedEventArgs e) => _ = Updater.CheckAsync();

    void OnAutoUpdateClick(object sender, RoutedEventArgs e)
    {
        Settings.Data.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;
        Settings.Save();
    }

    void OnViewReleaseClick(object sender, RoutedEventArgs e)
    {
        if (Updater.AvailableUrl is { } url) SystemControl.OpenUri(url);
    }

    public void ShowStatus(string text, bool error = false)
    {
        Status.Text = text;
        Status.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 138, 128) : Color.FromRgb(140, 220, 160));
        Status.Visibility = Visibility.Visible;
    }

    // ---------- shortcut recording ----------

    void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_recording) return;
        _recording = true;
        _main.UnregisterHotkey(); // so pressing the current shortcut records it instead of opening the launcher
        HotkeyButton.Content = "Press a shortcut…  (Esc to cancel)";
    }

    void StopRecording()
    {
        _recording = false;
        HotkeyButton.Content = App.HotkeyText();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt combinations arrive as Key.System
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
            return; // still holding modifiers; wait for the real key

        ModifierKeys mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            StopRecording();
            _main.RegisterHotkey();
            return;
        }

        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (mods == ModifierKeys.None && !isFunctionKey)
        {
            ShowStatus("Hold Ctrl, Alt, Shift or Win together with the key.", error: true);
            return;
        }

        string oldMods = Settings.Data.HotkeyModifiers, oldKey = Settings.Data.HotkeyKey;
        Settings.Data.HotkeyModifiers = mods.ToString();
        Settings.Data.HotkeyKey = key.ToString();

        if (_main.RegisterHotkey())
        {
            Settings.Save();
            StopRecording();
            ((App)Application.Current).UpdateTrayText();
            ShowStatus($"Shortcut set to {App.HotkeyText()}.");
        }
        else
        {
            string attempted = App.HotkeyText();
            Settings.Data.HotkeyModifiers = oldMods;
            Settings.Data.HotkeyKey = oldKey;
            bool restored = _main.RegisterHotkey();
            StopRecording();
            ShowStatus($"{attempted} is already taken by Windows or another app." +
                       (restored ? $" Kept {App.HotkeyText()}." : ""), error: true);
        }
    }

    // ---------- other settings ----------

    void OnCurrencyChanged(object sender, RoutedEventArgs e)
    {
        string code = CurrencyBox.Text.Trim().ToUpperInvariant();
        if (code.Length == 3 && code.All(char.IsLetter))
        {
            if (code == Settings.HomeCurrency) return;
            Settings.Data.HomeCurrency = code;
            Settings.Save();
            ShowStatus($"Home currency set to {code}.");
        }
        else
        {
            CurrencyBox.Text = Settings.HomeCurrency;
        }
    }

    // ---------- excluded folders ----------

    void LoadExclusions()
    {
        var folders = Settings.Data.ExcludedFolders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        ExcludedList.ItemsSource = folders;
        NoExclusions.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddExclusion(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to leave out of Glint search", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;

        var list = Settings.Data.ExcludedFolders;
        var added = new List<string>();
        foreach (string folder in dialog.FolderNames)
        {
            string path = folder.TrimEnd('\\');
            if (path.Length <= 3)
            {
                ShowStatus($"{folder} is a whole drive. Pick a folder inside it instead.", error: true);
                continue;
            }
            // Already covered by an excluded parent (or already in the list)?
            if (list.Any(f => path.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                              path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            // Adding a parent makes any of its excluded subfolders redundant.
            list.RemoveAll(f => f.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));
            list.Add(path);
            added.Add(System.IO.Path.GetFileName(path));
        }
        if (added.Count == 0) return;
        Settings.Save();
        LoadExclusions();
        ShowStatus($"Search will now skip {string.Join(", ", added)}.");
    }

    void OnRemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string folder }) return;
        Settings.Data.ExcludedFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        LoadExclusions();
        ShowStatus($"{System.IO.Path.GetFileName(folder)} is back in search results.");
    }

    void OnRemoveGlintClick(object sender, RoutedEventArgs e)
    {
        string question =
            "This removes everything Glint has set up on this PC:\n\n" +
            "• the \"start when I sign in\" entry\n" +
            "• Glint's hooks in Claude Code's settings (your other Claude Code settings stay)\n" +
            "• Glint's settings and data in " + Settings.Dir + "\n\n" +
            "Then Glint quits and shows you Glint.exe, so you can delete it.\n\nRemove Glint?";
        if (MessageBox.Show(this, question, "Remove Glint from this PC", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var report = Uninstaller.Run();
        MessageBox.Show(this, string.Join("\n", report) + "\n\nGlint will now close. Delete Glint.exe to finish.",
            "Glint removed", MessageBoxButton.OK, MessageBoxImage.Information);
        Uninstaller.RevealExe();
        ((App)Application.Current).Quit();
    }

    void OnStartupClick(object sender, RoutedEventArgs e)
    {
        Settings.LaunchAtStartup = StartupBox.IsChecked == true;
        StartupBox.IsChecked = Settings.LaunchAtStartup;
    }
}
