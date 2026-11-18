using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Glint;

public partial class MainWindow : Window
{
    // Layout constants in device-independent pixels; must match MainWindow.xaml.
    const double PanelMaxH = 600, MarginX = 60, MarginTop = 40, WinH = 730;
    const int HotkeyId = 0x4711;
    const int MaxApps = 4, MaxFiles = 5, MaxHints = 3;

    readonly ObservableCollection<ResultItem> _results = new();
    IntPtr _hwnd;
    bool _visible;
    int _version;    // bumped on every keystroke so stale file-search results are dropped
    bool _userMoved; // the user picked a row with the arrow keys; don't move the selection under them

    const double CornerRadius = 28;
    GlassBackdrop? _glass;
    bool _glassFailed;  // live glass could not be created on this machine; stop retrying
    bool _live;         // this showing uses the live backdrop rather than the snapshot
    double _scale = 1;  // DPI scale of the monitor the launcher is on
    int _winX, _panelTopPx;

    // While an actions / confirmation list is showing, the search results it replaced are parked here.
    List<ResultItem>? _parked;
    int _parkedIndex;

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        Deactivated += (_, _) => HideLauncher();
        // If the launcher was opened before the first app scan finished, fill the apps in when it does.
        Action refreshRows = () => Dispatcher.BeginInvoke(() =>
        {
            if (_visible && Query.Text.Length > 0 && !_userMoved && _parked == null) UpdateResults();
        });
        AppIndex.Loaded += refreshRows;
        // Radio states are read in the background on open; redraw so "currently on/off" is never stale.
        RadioControl.Changed += refreshRows;
    }

    /// <summary>Creates the native window (still hidden) so the hotkey can be registered against it.</summary>
    public void Init()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        // keep the launcher out of Alt+Tab
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
            Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
    }

    // ---------- hotkey ----------

    public bool RegisterHotkey()
    {
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        if (!Enum.TryParse<ModifierKeys>(Settings.Data.HotkeyModifiers, out var mods) ||
            !Enum.TryParse<Key>(Settings.Data.HotkeyKey, out var key))
            return false;
        // ModifierKeys flag values are identical to the Win32 MOD_* values.
        return Native.RegisterHotKey(_hwnd, HotkeyId, (uint)mods | Native.MOD_NOREPEAT,
            (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public void UnregisterHotkey() => Native.UnregisterHotKey(_hwnd, HotkeyId);

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleLauncher();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- show / hide ----------

    public void ToggleLauncher()
    {
        if (_visible) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher(string initialQuery = "")
    {
        var s = Settings.Data;
        double panelW = Math.Clamp(s.PanelWidth, 520, 960);
        double winW = panelW + 2 * MarginX;

        // Open on whichever monitor the mouse is on.
        Native.GetCursorPos(out var cursor);
        IntPtr monitor = Native.MonitorFromPoint(cursor, 2 /* nearest */);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref mi);
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var work = mi.rcWork;
        int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
        int w = (int)Math.Round(winW * scale), h = (int)Math.Round(WinH * scale);
        double top = workH * Math.Clamp(s.TopPercent, 2, 60) / 100.0;
        int panelTop = work.Top + (int)Math.Max(8 * scale, Math.Min(top, workH - (PanelMaxH + 20) * scale));
        int x = work.Left + (workW - w) / 2;
        int y = panelTop - (int)Math.Round(MarginTop * scale);

        ApplyTheme();
        AppIndex.RefreshIfStale();
        Currency.EnsureFresh();
        RadioControl.Refresh();

        _scale = scale;
        _winX = x;
        _panelTopPx = panelTop;

        if (!_visible)
        {
            _live = PrepareLiveGlass();
            Backdrop.Width = panelW;
            // Snapshot mode: grab the pixels behind the panel before the window appears, then frost them.
            Backdrop.Source = _live ? null : Glass.Capture(x + (int)Math.Round(MarginX * scale), panelTop,
                (int)Math.Round(panelW * scale), (int)Math.Round(PanelMaxH * scale), s.BlurStrength);
        }

        Query.Text = initialQuery;
        Query.CaretIndex = initialQuery.Length;
        UpdateResults();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        _visible = true;

        Activate();
        Native.SetForegroundWindow(_hwnd);
        Query.Focus();
        Keyboard.Focus(Query);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(130);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));

        if (_live)
        {
            // The glass is a separate window, so it fades in step but the panel doesn't zoom over it.
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            UpdateLayout();
            PlaceGlass();
            _glass!.FadeIn(duration);
        }
        else
        {
            _glass?.Hide();
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, 1, duration) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, 1, duration) { EasingFunction = ease });
        }
    }

    public void HideLauncher()
    {
        if (!_visible) return;
        _visible = false;
        _version++;
        _glass?.Hide();
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 just hides; the app lives in the tray until Quit.
        if (!App.Quitting)
        {
            e.Cancel = true;
            HideLauncher();
        }
        base.OnClosing(e);
    }

    void ApplyTheme()
    {
        var s = Settings.Data;
        bool dark = s.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemControl.IsDarkMode,
        };

        // Light glass needs a slightly denser tint than dark glass to keep text readable.
        byte tint = (byte)(Math.Clamp(s.GlassOpacity + (dark ? 0 : 10), 10, 100) * 255 / 100);
        SetBrush("TintBrush", dark ? Color.FromArgb(tint, 0x1B, 0x1B, 0x20) : Color.FromArgb(tint, 0xF7, 0xF7, 0xFA));
        SetBrush("TextBrush", Hex(dark ? "#FFFFFFFF" : "#FF18181C"));
        SetBrush("SubtleBrush", Hex(dark ? "#A6FFFFFF" : "#99000000"));
        SetBrush("HoverBrush", Hex(dark ? "#14FFFFFF" : "#0D000000"));
        SetBrush("SepBrush", Hex(dark ? "#26FFFFFF" : "#1F000000"));

        if (TryHex(s.Accent, out var accent))
        {
            SetBrush("SelBrush", Color.FromArgb(dark ? (byte)0x66 : (byte)0x4D, accent.R, accent.G, accent.B));
            SetBrush("CaretBrush", accent);
            SetBrush("TextSelBrush", Color.FromArgb(0x73, accent.R, accent.G, accent.B));
        }
        else
        {
            SetBrush("SelBrush", Hex(dark ? "#2EFFFFFF" : "#1F000000"));
            SetBrush("CaretBrush", Hex(dark ? "#FFFFFFFF" : "#FF18181C"));
            SetBrush("TextSelBrush", Hex("#663B9DFF"));
        }
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static bool TryHex(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { color = Hex(hex); return true; }
        catch { return false; }
    }

    void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Panel.ActualWidth, h = Panel.ActualHeight;
        var shape = new RectangleGeometry(new Rect(0, 0, w, h), CornerRadius, CornerRadius);
        Panel.Clip = shape;
        ShadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(-150, -150, w + 300, h + 300)), shape);
        if (_live && _visible) PlaceGlass();
    }

    // ---------- live glass ----------

    /// <summary>Live glass needs the setting on, Windows transparency effects on, and a working compositor.</summary>
    bool PrepareLiveGlass()
    {
        if (Settings.Data.GlassMode != "Live" || _glassFailed) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if ((k?.GetValue("EnableTransparency") as int? ?? 1) == 0) return false; // Windows would show a flat colour
        }
        catch { }

        _glass ??= GlassBackdrop.TryCreate();
        if (_glass == null) _glassFailed = true;
        return _glass != null;
    }

    /// <summary>Keeps the glass window exactly under the panel, whatever height the results give it.</summary>
    void PlaceGlass()
    {
        if (_glass == null) return;
        _glass.Place(_hwnd,
            _winX + (int)Math.Round(MarginX * _scale), _panelTopPx,
            (int)Math.Round(Panel.ActualWidth * _scale), (int)Math.Round(Panel.ActualHeight * _scale),
            (float)(CornerRadius * _scale));
    }

    // ---------- searching ----------

    void OnQueryChanged(object sender, TextChangedEventArgs e) => UpdateResults();

    void UpdateResults()
    {
        int version = ++_version;
        _userMoved = false;
        _parked = null; // typing always returns to search results
        ActionsHeader.Visibility = Visibility.Collapsed;

        string q = Query.Text.Trim();
        string searchText = Commands.SearchText(q); // "open notepad" looks up "notepad"
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Everything in this first pass is in-memory, so it lands on the same frame as the keystroke.
        var items = new List<ResultItem>();
        int maxFiles = MaxFiles;
        if (q.Length > 0)
        {
            var command = Commands.TryParse(q);
            if (command != null) items.Add(command);
            else if (Answers.TryAnswer(q) is { } answer) items.Add(answer);

            var apps = AppIndex.Search(searchText, MaxApps);
            if (command == null)
            {
                // Not a full command yet: show what it could become ("set" -> "Set volume to …").
                // Suggestions go below apps only when an app name really starts with the query
                // ("set" -> Settings); otherwise they beat loose matches ("vol" -> DaVinci Resolve).
                var hints = Commands.Suggest(q, MaxHints);
                if (hints.Count > 0) maxFiles = 3;
                if (AppIndex.HasStrongMatch(searchText)) { items.AddRange(apps); items.AddRange(hints); }
                else { items.AddRange(hints); items.AddRange(apps); }
            }
            else items.AddRange(apps);

            items.AddRange(BuiltIns(q));
            items.Add(WebSearch(q));
        }

        SetRows(items, 0);

        // Files come from the Windows Search index off the UI thread, after a short pause in typing.
        if (searchText.Length >= 2)
        {
            Task.Run(async () =>
            {
                await Task.Delay(60);
                if (version != _version) return;
                var files = FileSearch.Search(searchText, maxFiles);
                if (files.Count == 0) return;
                await Dispatcher.BeginInvoke(() =>
                {
                    if (version != _version) return;
                    foreach (var f in files) IconLoader.Load(f);

                    // If an actions list is open, add the files to the parked results instead.
                    if (_parked != null)
                    {
                        _parked.InsertRange(Math.Max(0, _parked.Count - 1), files);
                        return;
                    }
                    int at = Math.Max(0, _results.Count - 1); // just above the web-search row
                    foreach (var f in files) _results.Insert(at++, f);
                    // Keep the top hit selected unless the user has already chosen a row.
                    if (!_userMoved) ResultsList.SelectedIndex = 0;
                });
            });
        }
    }

    void SetRows(IReadOnlyList<ResultItem> items, int selectedIndex)
    {
        _results.Clear();
        foreach (var item in items)
        {
            IconLoader.Load(item);
            _results.Add(item);
        }
        var visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = visibility;
        Separator.Visibility = visibility;
        Footer.Visibility = visibility;
        if (_results.Count > 0)
        {
            ResultsList.SelectedIndex = Math.Clamp(selectedIndex, 0, _results.Count - 1);
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }
        UpdateFooter();
    }

    static IEnumerable<ResultItem> BuiltIns(string q)
    {
        if (q.Length < 3) yield break;
        if ("glint settings".Contains(q, StringComparison.OrdinalIgnoreCase) ||
            "preferences".StartsWith(q, StringComparison.OrdinalIgnoreCase))
            yield return new ResultItem
            {
                Title = "Glint Settings", Kind = "Glint", Glyph = "",
                Action = () => ((App)Application.Current).OpenSettings(),
            };
        if ("quit glint".Contains(q, StringComparison.OrdinalIgnoreCase) ||
            "exit glint".Contains(q, StringComparison.OrdinalIgnoreCase))
            yield return new ResultItem
            {
                Title = "Quit Glint", Kind = "Glint", Glyph = "",
                Action = () => ((App)Application.Current).Quit(),
            };
    }

    static ResultItem WebSearch(string q) => new()
    {
        Title = $"Search the web for “{q}”",
        Kind = "Web",
        Glyph = "",
        Action = () => Process.Start(new ProcessStartInfo(
            "https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true }),
    };

    // ---------- actions / confirmation list ----------

    bool InActions => _parked != null;

    /// <summary>Swaps the results for the selected item's actions (or its confirmation step).</summary>
    void EnterActions(ResultItem item)
    {
        var actions = item.GetActions?.Invoke();
        if (actions == null || actions.Count == 0) return;

        _parked = _results.ToList();
        _parkedIndex = ResultsList.SelectedIndex;
        ActionsHeader.Text = item.ActionsTitle ?? item.Title;
        ActionsHeader.Visibility = Visibility.Visible;
        SetRows(actions, 0);
    }

    void ExitActions()
    {
        if (_parked == null) return;
        var rows = _parked;
        _parked = null;
        ActionsHeader.Visibility = Visibility.Collapsed;
        SetRows(rows, _parkedIndex);
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFooter();

    void UpdateFooter()
    {
        if (ResultsList.SelectedItem is not ResultItem item) { Footer.Text = ""; return; }

        if (InActions)
            Footer.Text = item.IsBack ? "Enter to go back" : "Enter to choose   ·   Esc to go back";
        else if (item.Completion != null)
            Footer.Text = "Enter to fill in";
        else if (item.GetActions != null && item.Action == null)
            Footer.Text = "Enter to continue";
        else if (item.GetActions != null)
            Footer.Text = "Enter to open   ·   Tab for more actions";
        else if (item.IsAnswer && item.CopyText != null)
            Footer.Text = "Enter to copy";
        else
            Footer.Text = "Enter to " + (item.Kind.StartsWith("Command") ? "run" : "open");
    }

    // ---------- input ----------

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var selected = ResultsList.SelectedItem as ResultItem;

        switch (e.Key)
        {
            case Key.Escape:
                if (InActions) ExitActions();
                else HideLauncher();
                break;
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Tab:
                if (!shift && !InActions && selected is { Completion: not null }) Execute(selected, false);
                else if (!shift && !InActions && selected is { GetActions: not null }) EnterActions(selected);
                else MoveSelection(shift ? -1 : 1);
                break;
            // → opens the actions only when the caret is at the end, so it still moves through the text.
            case Key.Right when !InActions && selected is { GetActions: not null } &&
                                Query.CaretIndex == Query.Text.Length && Query.SelectionLength == 0:
                EnterActions(selected);
                break;
            case Key.Left when InActions:
                ExitActions();
                break;
            case Key.Enter:
                Execute(selected, secondary: ctrl);
                break;
            case Key.C when ctrl && Query.SelectionLength == 0 && selected is { CopyText: { } text }:
                try { Clipboard.SetText(text); } catch { }
                HideLauncher();
                break;
            case Key.OemComma when ctrl:
                ((App)Application.Current).OpenSettings();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void MoveSelection(int delta)
    {
        int n = _results.Count;
        if (n == 0) return;
        _userMoved = true;
        ResultsList.SelectedIndex = ((ResultsList.SelectedIndex + delta) % n + n) % n;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ResultItem item })
            Execute(item, secondary: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    void Execute(ResultItem? item, bool secondary)
    {
        if (item == null) return;
        if (item.IsBack)
        {
            ExitActions();
            return;
        }
        if (item.Completion != null)
        {
            // A suggestion: type it into the box and stay open so the user can add the value.
            Query.Text = item.Completion;
            Query.CaretIndex = Query.Text.Length;
            Query.Focus();
            return;
        }
        if (item.Action == null && item.GetActions != null)
        {
            // A command that needs confirming (shut down, restart, ...): show the Cancel / Yes step.
            EnterActions(item);
            return;
        }

        var action = secondary && item.SecondaryAction != null ? item.SecondaryAction : item.Action;
        HideLauncher();
        try { action?.Invoke(); }
        catch { /* target moved or was deleted since it was indexed */ }
    }
}
