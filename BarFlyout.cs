using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Glint;

/// <summary>
/// Base for the small glass panels that drop down from Skyline (sound, network).
/// Opens under an icon, sits on live glass when available, and closes when you click elsewhere or press Esc.
/// The window must contain an element named "Card" (the rounded panel) inside a 12 px shadow margin.
/// </summary>
public abstract class BarFlyout : Window
{
    const double ShadowMargin = 12, CardRadius = 16;

    IntPtr _hwnd;
    GlassBackdrop? _glass;
    bool _disposing;
    double _scale = 1;
    int _x, _y;

    public bool IsOpen { get; private set; }

    /// <summary>The rounded panel; the live glass is fitted to it.</summary>
    protected abstract FrameworkElement Card { get; }

    /// <summary>Fill the controls with current values just before the panel appears.</summary>
    protected abstract void Refresh();

    protected BarFlyout()
    {
        Deactivated += (_, _) => Close();
        Closing += (_, e) =>
        {
            // Kept around and reused: hide instead of destroying it.
            if (_disposing) return;
            e.Cancel = true;
            HideFlyout();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Loaded += (_, _) => Card.SizeChanged += (_, _) => PlaceGlass();
    }

    /// <summary>Shows the panel centred under <paramref name="centerX"/> (physical pixels), kept on screen.</summary>
    public void Open(int centerX, int top, int screenRight)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
                Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
            _glass = TransparencyOn() ? GlassBackdrop.TryCreate() : null;
        }

        Refresh(); // first: a panel may change its height to fit what it shows
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = centerX, Y = top }, 2);
        _scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        int w = (int)Math.Round(Width * _scale), h = (int)Math.Round(Height * _scale);
        int margin = (int)Math.Round(ShadowMargin * _scale);
        _x = Math.Min(centerX - w / 2, screenRight - w + margin - (int)(6 * _scale));
        _y = top - margin;

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _x, _y, w, h, Native.SWP_NOACTIVATE);
        Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _x, _y, w, h, Native.SWP_NOACTIVATE);
        IsOpen = true;
        Activate();
        Native.SetForegroundWindow(_hwnd);
        UpdateLayout();
        PlaceGlass();
        _glass?.FadeIn(TimeSpan.FromMilliseconds(110));
    }

    void HideFlyout()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _glass?.Hide();
        Hide();
    }

    public void Dispose()
    {
        _disposing = true;
        _glass?.Hide();
        Close();
    }

    void PlaceGlass()
    {
        if (_glass == null || !IsOpen) return;
        int m = (int)Math.Round(ShadowMargin * _scale);
        _glass.Place(_hwnd, _x + m, _y + m,
            (int)Math.Round(Card.ActualWidth * _scale), (int)Math.Round(Card.ActualHeight * _scale), (float)(CardRadius * _scale));
    }

    static bool TransparencyOn()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (k?.GetValue("EnableTransparency") as int? ?? 1) != 0;
        }
        catch { return true; }
    }

    /// <summary>Sets the Fly* brushes the panels' XAML uses.</summary>
    public void ApplyTheme(bool dark)
    {
        bool glass = _glass != null || (_hwnd == IntPtr.Zero && TransparencyOn());
        byte alpha = glass ? (byte)0xA6 : (byte)0xF5;
        SetBrush("FlyTint", dark ? Color.FromArgb(alpha, 0x1E, 0x1E, 0x22) : Color.FromArgb(alpha, 0xF6, 0xF6, 0xF8));
        SetBrush("FlyText", dark ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0x10, 0x10, 0x14));
        SetBrush("FlySubtle", dark ? Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        SetBrush("FlyTrack", dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        SetBrush("FlyFill", dark ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD9, 0x30, 0x30, 0x36));
        SetBrush("FlyHover", dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        SetBrush("FlyLine", dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
    }

    void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }
}
