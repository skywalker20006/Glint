using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Forms = System.Windows.Forms;

namespace Glint;

/// <summary>
/// A macOS-style bar docked to the top of one screen (one bar per monitor). It registers as a Windows "app bar",
/// so maximised windows stop below it instead of covering it, the same way they stop above the taskbar.
/// </summary>
public partial class TopBar : Window
{
    const double BarHeight = 30; // device-independent pixels
    const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_TOP = 1;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A;
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

    readonly Action _toggleLauncher;
    readonly DispatcherTimer _clockTimer = new();
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    IntPtr _hwnd;
    int _callbackMessage;
    bool _registered, _fullscreenHidden;
    GlassBackdrop? _glass;
    Native.RECT _rect;

    /// <summary>The monitor this bar sits on, as Windows names it (e.g. DISPLAY2).</summary>
    public string DeviceName { get; }

    public TopBar(Action toggleLauncher, string deviceName)
    {
        InitializeComponent();
        _toggleLauncher = toggleLauncher;
        DeviceName = deviceName;

        _clockTimer.Tick += (_, _) => UpdateClock();
        _statusTimer.Tick += (_, _) => { UpdateStatus(); KeepGlassUnderBar(); };

        SourceInitialized += (_, _) => OnSourceInitialized();
        Closed += (_, _) => Unregister();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        RadioControl.Changed += OnRadiosChanged;
    }

    void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Never take focus from the app you're working in, and stay out of Alt+Tab.
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
            Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        _callbackMessage = RegisterWindowMessage("Glint.TopBar.AppBar");
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;

        Dock();          // first, so the Mica material knows where the bar sits
        RadioControl.Refresh();
        ApplyTheme();    // also fills in the status icons, speed and volume
        UpdateClock();
        _statusTimer.Start();
        StartMedia();
        StartClaude();
    }

    // ---------- docking ----------

    /// <summary>This bar's monitor; the primary one if it has just been unplugged (the bar is closed soon after).</summary>
    Forms.Screen MyScreen() =>
        Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == DeviceName) ?? Forms.Screen.PrimaryScreen!;

    /// <summary>Claims a strip at the top of this bar's monitor and moves the bar into it.</summary>
    void Dock()
    {
        var screen = MyScreen().Bounds;
        double scale = DpiScale();
        int height = (int)Math.Round(BarHeight * scale);

        var data = NewData();
        data.uEdge = ABE_TOP;
        data.rc = new Native.RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Top + height };
        if (_registered)
        {
            // Ask the shell where the strip may go (another app bar might already be at the top), then claim it.
            SHAppBarMessage(ABM_QUERYPOS, ref data);
            data.rc.Bottom = data.rc.Top + height;
            SHAppBarMessage(ABM_SETPOS, ref data);
        }
        _rect = data.rc;

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _rect.Left, _rect.Top,
            _rect.Right - _rect.Left, _rect.Bottom - _rect.Top, Native.SWP_NOACTIVATE);
        PlaceGlass();
    }

    double DpiScale()
    {
        var screen = MyScreen().Bounds;
        IntPtr monitor = Native.MonitorFromPoint(new Native.POINT { X = screen.Left + 1, Y = screen.Top + 1 }, 2);
        return Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    void Unregister()
    {
        _glass?.Hide();
        _volumeFlyout?.Dispose();
        _networkFlyout?.Dispose();
        _mediaFlyout?.Dispose();
        _sparkTimer.Stop();
        _claude?.Dispose();
        SetWaveRunning(false);
        _clockTimer.Stop();
        _statusTimer.Stop();
        _fastTimer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
        RadioControl.Changed -= OnRadiosChanged;
        if (!_registered) return;
        // Gives the reserved strip back so windows can use the full screen height again.
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMessage && _callbackMessage != 0)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    Dock();
                    break;
                case ABN_FULLSCREENAPP:
                    // Step aside for full-screen games and videos, and come back when they leave.
                    SetFullscreenHidden(lParam != IntPtr.Zero);
                    break;
            }
        }
        else if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    void SetFullscreenHidden(bool hide)
    {
        if (hide == _fullscreenHidden) return;
        _fullscreenHidden = hide;
        if (hide)
        {
            _glass?.Hide();
            _volumeFlyout?.Close();
            _networkFlyout?.Close();
            _mediaFlyout?.Close();
            SetWaveRunning(false);
            _sparkTimer.Stop();
            ReadyFlash.BeginAnimation(OpacityProperty, null);
            ReadyFlash.Opacity = 0;
            Hide();
        }
        else
        {
            Show();
            Dock();
            UpdateNowPlaying(); // restart the waveform
            ShowClaudeState();
        }
    }

    // ---------- glass ----------

    /// <summary>Re-reads theme, material and which items to show. Called on open and whenever they change.</summary>
    public void ApplyTheme()
    {
        var s = Settings.Data;
        bool dark = s.Theme switch { "Dark" => true, "Light" => false, _ => SystemControl.IsDarkMode };

        // With Windows transparency effects off, every material becomes solid, as Windows' own surfaces do.
        string material = TransparencyEnabled() ? s.BarMaterial : "Solid";

        bool live = material is "Blur" or "Acrylic";
        if (live) _glass ??= GlassBackdrop.TryCreate();
        else
        {
            _glass?.Hide();
            _glass = null;
        }
        if (live && _glass == null) material = "Solid";

        MicaLayer.Visibility = Visibility.Collapsed;
        NoiseLayer.Visibility = Visibility.Collapsed;
        byte alpha;
        Color tint;
        switch (material)
        {
            case "Blur":
                // Clear frosted glass: a light tint so the colours behind come through.
                alpha = dark ? (byte)0x59 : (byte)0x73;
                tint = dark ? Color.FromRgb(0x1B, 0x1B, 0x20) : Color.FromRgb(0xF4, 0xF4, 0xF7);
                break;
            case "Acrylic":
                // Fluent acrylic: the same live blur under a much denser tint, plus a fine grain.
                alpha = dark ? (byte)0xB0 : (byte)0xB8;
                tint = dark ? Color.FromRgb(0x24, 0x24, 0x28) : Color.FromRgb(0xF6, 0xF6, 0xF8);
                NoiseLayer.Fill = _noise ??= new ImageBrush(Glass.Noise())
                {
                    TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 128, 128),
                };
                NoiseLayer.Opacity = dark ? 0.55 : 0.35;
                NoiseLayer.Visibility = Visibility.Visible;
                break;
            case "Mica":
                // Mica: the wallpaper, heavily frosted and mostly covered by the base colour.
                if (UpdateMica())
                {
                    MicaLayer.Visibility = Visibility.Visible;
                    alpha = dark ? (byte)0xC4 : (byte)0x9E;
                    tint = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
                }
                else goto default;
                break;
            default:
                alpha = 0xF2;
                tint = dark ? Color.FromRgb(0x1C, 0x1C, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF5);
                break;
        }

        SetBrush("BarTint", Color.FromArgb(alpha, tint.R, tint.G, tint.B));
        SetBrush("BarText", dark ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0x10, 0x10, 0x14));
        SetBrush("BarHover", dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        SetBrush("BarLine", dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        ChargingBolt.Foreground = new SolidColorBrush(dark ? Colors.Black : Colors.White);
        _volumeFlyout?.ApplyTheme(dark);
        _networkFlyout?.ApplyTheme(dark);
        _mediaFlyout?.ApplyTheme(dark);

        UpdateNowPlaying();
        if (ClaudeSpark.Data != null) UpdateClaudeVisibility();
        GlintButton.Visibility = s.LauncherEnabled ? Visibility.Visible : Visibility.Collapsed;
        SpeedPanel.Visibility = s.BarShowSpeed ? Visibility.Visible : Visibility.Collapsed;
        VolumeButton.Visibility = s.BarShowVolume ? Visibility.Visible : Visibility.Collapsed;
        WifiPanel.Visibility = s.BarShowWifi ? Visibility.Visible : Visibility.Collapsed;
        NotificationsButton.Visibility = s.BarShowNotifications ? Visibility.Visible : Visibility.Collapsed;
        BluetoothVisible = s.BarShowBluetooth;
        BatteryVisible = s.BarShowBattery;
        UpdateStatus();
        UpdateFast();
        PlaceGlass();
    }

    ImageBrush? _noise;
    string? _micaKey;
    bool BluetoothVisible = true, BatteryVisible = true;

    /// <summary>Builds the Mica image from the current wallpaper; cached until the wallpaper or screen changes.</summary>
    bool UpdateMica()
    {
        var screen = MyScreen().Bounds;
        string key = $"{screen.Width}x{screen.Height}|{WallpaperStamp()}";
        if (key == _micaKey && MicaLayer.Fill != null) return true;

        var img = Glass.MicaWallpaper(screen.Width, screen.Height);
        if (img == null) return false;
        // Show just the strip of the frosted wallpaper that sits under the bar.
        double barFraction = Math.Clamp((_rect.Bottom - _rect.Top) / (double)screen.Height, 0.005, 1);
        MicaLayer.Fill = new ImageBrush(img)
        {
            Viewbox = new Rect(0, 0, 1, barFraction), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill,
        };
        _micaKey = key;
        return true;
    }

    static string WallpaperStamp()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string path = k?.GetValue("WallPaper") as string ?? "";
            return path + "|" + (System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path).Ticks : 0);
        }
        catch { return ""; }
    }

    static bool TransparencyEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (k?.GetValue("EnableTransparency") as int? ?? 1) != 0;
        }
        catch { return true; }
    }

    void PlaceGlass()
    {
        if (_glass == null || _hwnd == IntPtr.Zero || _fullscreenHidden) return;
        _glass.Place(_hwnd, _rect.Left, _rect.Top, _rect.Right - _rect.Left, _rect.Bottom - _rect.Top, 0);
    }

    /// <summary>Other always-on-top windows can end up between the bar and its glass; put the glass back.</summary>
    void KeepGlassUnderBar()
    {
        if (!_fullscreenHidden) PlaceGlass();
    }

    void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop) _micaKey = null; // wallpaper may have changed
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    // ---------- clock ----------

    void UpdateClock()
    {
        var now = DateTime.Now;
        Clock.Text = now.ToString("ddd d MMM") + "   " + now.ToString("t");
        // Tick again right after the next minute begins, so the time never lags.
        _clockTimer.Interval = TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 50);
        _clockTimer.Start();
    }

    // ---------- status icons ----------

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateStatus);
    void OnNetworkChanged(object? sender) => Dispatcher.BeginInvoke(UpdateStatus);
    void OnRadiosChanged() => Dispatcher.BeginInvoke(UpdateStatus);

    void UpdateStatus()
    {
        RadioControl.Refresh(); // raises Changed (and so calls back here) only when something actually changed
        UpdateBluetooth();
        UpdateNetwork();
        UpdateBattery();
    }

    void UpdateBluetooth()
    {
        bool? on = RadioControl.IsOn(RadioKind.Bluetooth);
        BluetoothIcon.Visibility = on == null || !BluetoothVisible ? Visibility.Collapsed : Visibility.Visible; // no adapter
        BluetoothIcon.Opacity = on == true ? 1 : 0.35;
        BluetoothIcon.ToolTip = on == true ? "Bluetooth is on" : "Bluetooth is off";
    }

    void UpdateNetwork()
    {
        string tip;
        string glyph = "";
        double level = 0; // 0..1, how much of the Wi-Fi fan is lit

        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            var connectivity = profile?.GetNetworkConnectivityLevel() ?? NetworkConnectivityLevel.None;

            if (profile != null && profile.IsWlanConnectionProfile)
            {
                byte bars = profile.GetSignalBars() ?? 5;
                level = Math.Clamp(bars / 5.0, 0.2, 1);
                tip = $"Wi-Fi: {profile.ProfileName}";
                if (connectivity != NetworkConnectivityLevel.InternetAccess) tip += " (no internet)";
            }
            else if (profile != null && connectivity != NetworkConnectivityLevel.None)
            {
                glyph = ""; // ethernet
                level = 1;
                tip = "Wired connection";
            }
            else
            {
                tip = RadioControl.IsOn(RadioKind.WiFi) == false ? "Wi-Fi is off" : "Not connected";
            }
        }
        catch
        {
            tip = "Network status unavailable";
        }

        WifiIcon.Text = WifiBack.Text = glyph;
        WifiBack.Visibility = glyph == "" ? Visibility.Visible : Visibility.Collapsed;
        // Light the fan from the bottom point outwards, like the macOS Wi-Fi strength indicator.
        WifiIcon.OpacityMask = level >= 1 ? null : new RadialGradientBrush
        {
            Center = new Point(0.5, 0.92), GradientOrigin = new Point(0.5, 0.92), RadiusX = 1.0, RadiusY = 1.0,
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, level * 0.85),
                new GradientStop(Colors.Transparent, level * 0.85 + 0.001),
            },
        };
        WifiIcon.Opacity = level > 0 ? 1 : 0;
        WifiBack.Opacity = level > 0 ? 0.3 : 0.35;
        WifiIcon.Parent.SetValue(ToolTipProperty, tip);
    }

    void UpdateBattery()
    {
        var power = Forms.SystemInformation.PowerStatus;
        if (!BatteryVisible || power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || power.BatteryLifePercent > 1.01f)
        {
            BatteryPanel.Visibility = Visibility.Collapsed; // desktop PC
            return;
        }
        BatteryPanel.Visibility = Visibility.Visible;

        int percent = (int)Math.Round(power.BatteryLifePercent * 100);
        bool plugged = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        bool charging = plugged && power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.Charging);

        BatteryText.Text = percent + "%";
        BatteryFill.Width = Math.Max(1.5, 16 * percent / 100.0);
        BatteryFill.Background = charging ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
            : percent <= 20 && !plugged ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : (Brush)FindResource("BarText");
        ChargingBolt.Visibility = plugged ? Visibility.Visible : Visibility.Collapsed;

        string tip = charging ? $"{percent}%, charging" : plugged ? $"{percent}%, plugged in" : $"{percent}% remaining";
        if (!plugged && power.BatteryLifeRemaining > 0)
        {
            var left = TimeSpan.FromSeconds(power.BatteryLifeRemaining);
            tip += $" (about {(int)left.TotalHours}h {left.Minutes:00}m)";
        }
        BatteryPanel.ToolTip = tip;
    }

    // ---------- network speed and volume (refreshed every second) ----------

    readonly NetSpeed _speed = new();
    readonly DispatcherTimer _fastTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    VolumeFlyout? _volumeFlyout;

    void UpdateFast()
    {
        var s = Settings.Data;
        if (!s.BarShowSpeed && !s.BarShowVolume) { _fastTimer.Stop(); return; }
        if (!_fastTimer.IsEnabled)
        {
            _fastTimer.Tick -= OnFastTick;
            _fastTimer.Tick += OnFastTick;
            _fastTimer.Start();
        }
        OnFastTick(null, EventArgs.Empty);
    }

    void OnFastTick(object? sender, EventArgs e)
    {
        if (_fullscreenHidden) return;
        if (Settings.Data.BarShowSpeed && _speed.Sample() is var (down, up))
        {
            UpText.Text = $"↑ {NetSpeed.Format(up)} Mbps";
            DownText.Text = $"↓ {NetSpeed.Format(down)} Mbps";
        }
        else if (UpText.Text.Length == 0)
        {
            UpText.Text = "↑ 0 Mbps";
            DownText.Text = "↓ 0 Mbps";
        }
        if (Settings.Data.BarShowVolume) UpdateVolume();
        if (_networkFlyout?.IsOpen == true) _networkFlyout.Sync();
    }

    NetworkFlyout? _networkFlyout;

    void OnSpeedClick(object sender, MouseButtonEventArgs e)
    {
        _networkFlyout ??= new NetworkFlyout(_speed.UsageSinceBoot);
        if (_networkFlyout.IsOpen)
        {
            _networkFlyout.Close();
            return;
        }
        var corner = SpeedPanel.PointToScreen(new Point(SpeedPanel.ActualWidth / 2, SpeedPanel.ActualHeight));
        _networkFlyout.ApplyTheme(IsDark());
        _networkFlyout.Open((int)corner.X, _rect.Bottom + (int)Math.Round(6 * DpiScale()), _rect.Right);
    }

    static bool IsDark() => Settings.Data.Theme switch { "Dark" => true, "Light" => false, _ => SystemControl.IsDarkMode };

    void UpdateVolume()
    {
        int? level = SystemControl.Volume;
        bool muted = SystemControl.Muted == true;
        if (level == null)
        {
            VolumeIcon.Text = "";
            VolumeButton.ToolTip = "No sound output device";
            return;
        }
        VolumeIcon.Text = muted ? "" : level == 0 ? "" : level <= 33 ? "" : level <= 66 ? "" : "";
        VolumeButton.ToolTip = muted ? $"Muted ({level}%)" : $"Volume {level}%";
    }

    void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (SystemControl.Volume is not int level) return;
        SystemControl.SetVolume(level + (e.Delta > 0 ? 2 : -2));
        UpdateVolume();
        _volumeFlyout?.Sync();
        e.Handled = true;
    }

    void OnVolumeClick(object sender, MouseButtonEventArgs e)
    {
        _volumeFlyout ??= new VolumeFlyout(UpdateVolume);
        if (_volumeFlyout.IsOpen)
        {
            _volumeFlyout.Close();
            return;
        }
        // Open just below the icon, in physical pixels.
        var corner = VolumeButton.PointToScreen(new Point(VolumeButton.ActualWidth / 2, VolumeButton.ActualHeight));
        _volumeFlyout.ApplyTheme(Settings.Data.Theme switch { "Dark" => true, "Light" => false, _ => SystemControl.IsDarkMode });
        _volumeFlyout.Open((int)corner.X, _rect.Bottom + (int)Math.Round(6 * DpiScale()), _rect.Right);
    }

    // ---------- now playing (Spotify) ----------

    const int BarCount = 5;
    static readonly double[] BarWeight = { 0.55, 0.85, 1.0, 0.8, 0.6 };   // middle bars reach highest
    static readonly double[] BarSpeed = { 7.1, 9.3, 6.2, 8.4, 10.6 };     // each bar wobbles at its own rate
    readonly System.Windows.Shapes.Rectangle[] _bars = new System.Windows.Shapes.Rectangle[BarCount];
    readonly double[] _barLevel = new double[BarCount];
    readonly MediaWatcher _media = new();
    MediaFlyout? _mediaFlyout;
    bool _waveRunning;
    double _envelope;

    void StartMedia()
    {
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new System.Windows.Shapes.Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5 };
            System.Windows.Controls.Canvas.SetLeft(_bars[i], 1.25 + i * 5.5);
            Wave.Children.Add(_bars[i]);
            SetBar(i, 3);
        }
        _waveTimer.Tick += OnWaveFrame;
        _media.Changed += () => Dispatcher.BeginInvoke(UpdateNowPlaying);
        _media.Start();
    }

    void UpdateNowPlaying()
    {
        var track = _media.Current;
        bool show = track != null && Settings.Data.BarShowMedia && !_fullscreenHidden;
        NowPlaying.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _mediaFlyout?.Update(track);
        if (!show)
        {
            SetWaveRunning(false);
            return;
        }

        ArtBrush.ImageSource = track!.Art;
        SongText.Text = track.Title;
        ArtistText.Text = track.Artist.Length > 0 ? "  ·  " + track.Artist : "";
        NowPlaying.ToolTip = track.Playing ? $"{track.Title} — {track.Artist}" : $"Paused: {track.Title} — {track.Artist}";
        NowPlaying.Opacity = track.Playing ? 1 : 0.7;

        var fill = new SolidColorBrush(track.Accent);
        fill.Freeze();
        foreach (var bar in _bars) if (bar != null) bar.Fill = fill;
        WaveGlow.Color = track.Accent;
        SetWaveRunning(track.Playing);
    }

    readonly AudioBands _audio = new();
    // 30 updates a second: smooth to the eye, and half the redraws of following the 60 Hz display.
    readonly System.Windows.Threading.DispatcherTimer _waveTimer = new(System.Windows.Threading.DispatcherPriority.Render);
    readonly System.Diagnostics.Stopwatch _waveClock = new();
    double _lastWave;
    readonly double[] _bandPeak = { 0.5, 0.5, 0.5, 0.5, 0.5 };    // slow-moving top of each band's range
    readonly double[] _bandFloor = { 0.3, 0.3, 0.3, 0.3, 0.3 };   // slow-moving bottom of each band's range

    void SetWaveRunning(bool run)
    {
        if (run == _waveRunning) return;
        _waveRunning = run;
        if (run)
        {
            _audio.Start(); // listens only while a song plays and Skyline is showing it
            _waveTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / 30);
            _waveClock.Restart();
            _lastWave = 0;
            _waveTimer.Start();
        }
        else
        {
            _waveTimer.Stop();
            _audio.Stop();
            // Paused: the bars settle into small resting dots.
            for (int i = 0; i < BarCount; i++)
            {
                _barLevel[i] = 0;
                if (_bars[i] != null) SetBar(i, 3);
            }
        }
    }

    /// <summary>
    /// Runs every frame while a song plays. Left to right the bars are bass, low-mid, mid, high-mid and
    /// treble, each following the real energy in its frequency band, so kicks move the left bars and
    /// hi-hats the right. If audio capture isn't available, falls back to a loudness-driven animation.
    /// </summary>
    void OnWaveFrame(object? sender, EventArgs e)
    {
        double now = _waveClock.Elapsed.TotalSeconds;
        double dt = _lastWave == 0 ? 1 / 30.0 : Math.Min(0.1, now - _lastWave);
        _lastWave = now;

        if (_audio.Working) BandFrame(dt);
        else LoudnessFrame(now, dt);

        for (int i = 0; i < BarCount; i++)
            SetBar(i, 3 + _barLevel[i] * 13); // 3 px resting dot up to the full 16 px
    }

    /// <summary>
    /// Sets a bar's height, centred in the waveform. Skips changes too small to see, so a steady note
    /// doesn't make Skyline redraw at all.
    /// </summary>
    void SetBar(int i, double height)
    {
        var bar = _bars[i];
        if (!double.IsNaN(bar.Height) && Math.Abs(bar.Height - height) < 0.35) return;
        bar.Height = height;
        System.Windows.Controls.Canvas.SetTop(bar, (16 - height) / 2);
    }

    void BandFrame(double dt)
    {
        for (int i = 0; i < BarCount; i++)
        {
            double level = _audio.Bands[i];

            // Each band gets its own range, tracked over the last few seconds, so a quiet treble band still
            // moves as much as a loud bass band, like the Dynamic Island. The top falls slowly and the
            // bottom rises slowly, so the range follows the song as it gets louder or softer.
            _bandPeak[i] = level > _bandPeak[i] ? level : _bandPeak[i] - (_bandPeak[i] - level) * (1 - Math.Exp(-dt / 2.5));
            _bandFloor[i] = level < _bandFloor[i] ? level : _bandFloor[i] + (level - _bandFloor[i]) * (1 - Math.Exp(-dt / 4.0));
            double range = Math.Max(0.12, _bandPeak[i] - _bandFloor[i]);
            double target = Math.Clamp((level - _bandFloor[i]) / range, 0, 1);
            if (level < 0.05) target = 0; // true silence (a gap between songs): rest

            // Snap up on a hit, fall back a little slower: punchy but never jittery.
            double tau = target > _barLevel[i] ? 0.025 : 0.11;
            _barLevel[i] += (target - _barLevel[i]) * (1 - Math.Exp(-dt / tau));
        }
    }

    /// <summary>Fallback: overall loudness from the speakers' meter, with a per-bar wobble for shape.</summary>
    void LoudnessFrame(double t, double dt)
    {
        double peak = Math.Min(1, SystemControl.OutputPeak * 1.6);
        double k = peak > _envelope ? 1 - Math.Exp(-dt / 0.03) : 1 - Math.Exp(-dt / 0.25);
        _envelope += (peak - _envelope) * k;
        for (int i = 0; i < BarCount; i++)
        {
            double wobble = 0.55 + 0.45 * Math.Sin(t * BarSpeed[i] + i * 1.7) * Math.Sin(t * BarSpeed[i] * 0.37 + i);
            double target = Math.Clamp(_envelope * BarWeight[i] * (0.45 + 0.75 * wobble), 0, 1);
            _barLevel[i] += (target - _barLevel[i]) * (1 - Math.Exp(-dt / 0.06));
        }
    }

    void OnNowPlayingClick(object sender, MouseButtonEventArgs e)
    {
        _mediaFlyout ??= new MediaFlyout(_media);
        if (_mediaFlyout.IsOpen)
        {
            _mediaFlyout.Close();
            return;
        }
        var corner = NowPlaying.PointToScreen(new Point(NowPlaying.ActualWidth / 2, NowPlaying.ActualHeight));
        _mediaFlyout.ApplyTheme(IsDark());
        _mediaFlyout.Update(_media.Current);
        _mediaFlyout.Open((int)corner.X, _rect.Bottom + (int)Math.Round(6 * DpiScale()), _rect.Right);
    }

    // ---------- Claude mascot ----------

    ClaudeStatus? _claude;
    readonly System.Windows.Threading.DispatcherTimer _sparkTimer = new(System.Windows.Threading.DispatcherPriority.Render);
    readonly System.Diagnostics.Stopwatch _sparkClock = new();

    ClaudeStatus.State? _lastClaudeState;

    /// <summary>
    /// The moment Claude finishes: five warm orange flashes across the whole bar over about five seconds,
    /// fading out, after which the mascot keeps pulsing on its own.
    /// </summary>
    void FlashBar()
    {
        var flash = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        var ease = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        void Key(double seconds, double opacity) => flash.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(
            opacity, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), ease));
        Key(0.00, 0.00);
        Key(0.22, 0.62);
        Key(0.60, 0.14);
        Key(0.85, 0.62);
        Key(1.25, 0.14);
        Key(1.50, 0.62);
        Key(1.90, 0.14);
        Key(2.15, 0.62);
        Key(2.55, 0.14);
        Key(2.80, 0.62);
        Key(5.00, 0.00); // slow fade back to the glass
        ReadyFlash.BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>An eight-pointed spark, long and short petals alternating, centred on (0,0).</summary>
    static Geometry SparkShape()
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int i = 0; i < 8; i++)
        {
            double inner = 1.4, outer = i % 2 == 0 ? 8.2 : 6.0, width = i % 2 == 0 ? 2.7 : 2.3;
            var petal = new RectangleGeometry(new Rect(-width / 2, -outer, width, outer - inner), width / 2, width / 2)
            {
                Transform = new RotateTransform(i * 45),
            };
            group.Children.Add(petal);
        }
        group.Freeze();
        return group;
    }

    void StartClaude()
    {
        ClaudeSpark.Data = SparkShape();
        _sparkTimer.Tick += OnSparkTick;
        UpdateClaudeVisibility();
    }

    /// <summary>Starts or stops watching Claude to match the setting; nothing runs while it's off.</summary>
    void UpdateClaudeVisibility()
    {
        bool show = Settings.Data.BarShowClaude;
        ClaudeButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _claude == null)
        {
            _claude = new ClaudeStatus();
            _lastClaudeState = null; // the first reading only sets the scene; it never flashes
            _claude.Changed += () => Dispatcher.BeginInvoke(ShowClaudeState);
        }
        else if (!show && _claude != null)
        {
            _claude.Dispose();
            _claude = null;
        }
        ShowClaudeState();
    }

    void ShowClaudeState()
    {
        var state = _claude?.Current ?? ClaudeStatus.State.Idle;
        if (state == ClaudeStatus.State.Ready && _lastClaudeState is { } before && before != ClaudeStatus.State.Ready
            && Settings.Data.BarShowClaude && !_fullscreenHidden)
            FlashBar();
        _lastClaudeState = state;
        bool animate = state != ClaudeStatus.State.Idle && Settings.Data.BarShowClaude && !_fullscreenHidden;

        ClaudeSpark.Opacity = state == ClaudeStatus.State.Idle ? 0.55 : 1;
        ClaudeButton.ToolTip = state switch
        {
            ClaudeStatus.State.Busy => "Claude is working…",
            ClaudeStatus.State.Ready => "Claude is ready for you. Click to open Claude.",
            _ => "Claude is idle",
        };

        if (animate)
        {
            // Spinning steps along at 12 a second like a terminal spinner; the pulse needs a little more to look smooth.
            _sparkTimer.Interval = TimeSpan.FromMilliseconds(state == ClaudeStatus.State.Busy ? 1000.0 / 12 : 1000.0 / 15);
            if (!_sparkTimer.IsEnabled) { _sparkClock.Restart(); _sparkTimer.Start(); }
        }
        else
        {
            _sparkTimer.Stop();
        }
        if (state != ClaudeStatus.State.Ready) { SparkGlow.Opacity = 0; SparkScale.ScaleX = SparkScale.ScaleY = 1; }
        if (state != ClaudeStatus.State.Busy) SparkSpin.Angle = 0;
        OnSparkTick(null, EventArgs.Empty);
    }

    void OnSparkTick(object? sender, EventArgs e)
    {
        double t = _sparkClock.Elapsed.TotalSeconds;
        switch (_claude?.Current)
        {
            case ClaudeStatus.State.Busy:
                // A steady turn with a gentle breathe in the petals.
                SparkSpin.Angle = (t * 150) % 360;
                double breathe = 0.88 + 0.12 * Math.Sin(t * 5.2);
                SparkScale.ScaleX = SparkScale.ScaleY = breathe;
                break;
            case ClaudeStatus.State.Ready:
                // A warm orange heartbeat: grows slightly and glows, about once a second.
                double beat = 0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 1.15);
                SparkScale.ScaleX = SparkScale.ScaleY = 1 + 0.16 * beat;
                SparkGlow.Opacity = 0.25 + 0.75 * beat;
                break;
        }
    }

    /// <summary>Brings the Claude app to the front and stops the "ready" pulse.</summary>
    void OnClaudeClick(object sender, MouseButtonEventArgs e)
    {
        _claude?.Acknowledge();
        try
        {
            var claude = System.Diagnostics.Process.GetProcessesByName("Claude")
                .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (claude == null) return;
            IntPtr hwnd = claude.MainWindowHandle;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            Native.SetForegroundWindow(hwnd);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int cmd);

    // ---------- clicks ----------

    void OnGlintClick(object sender, MouseButtonEventArgs e) => _toggleLauncher();

    // Windows' own shortcuts for its panels: one press opens the panel, the next closes it,
    // so clicking a Skyline item a second time closes what it opened.
    const byte KeyN = 0x4E, KeyA = 0x41;

    /// <summary>Opens or closes the Windows notification centre (Win+N), the same panel as the clock.</summary>
    void OnNotificationsClick(object sender, MouseButtonEventArgs e) => PressWinPlus(KeyN);

    void OnSettingsClick(object sender, MouseButtonEventArgs e) => ((App)Application.Current).OpenSettings();

    /// <summary>Opens or closes the Windows calendar and notifications panel (Win+N).</summary>
    void OnClockClick(object sender, MouseButtonEventArgs e) => PressWinPlus(KeyN);

    /// <summary>Opens or closes Windows Quick Settings, where Wi-Fi, Bluetooth and volume live (Win+A).</summary>
    void OnStatusClick(object sender, MouseButtonEventArgs e) => PressWinPlus(KeyA);

    static void PressWinPlus(byte vk)
    {
        const byte VK_LWIN = 0x5B;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, 2, UIntPtr.Zero);
    }

    // ---------- interop ----------

    APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
