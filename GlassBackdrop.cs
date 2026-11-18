using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Glint;

/// <summary>
/// Live glass: a second, click-through window that sits directly beneath the launcher and shows the
/// desktop compositor's real-time blurred backdrop, clipped to an anti-aliased rounded rectangle.
/// The launcher draws its tint, rim and content on top of it.
/// </summary>
internal sealed class GlassBackdrop
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_USE_HOSTBACKDROPBRUSH = 17, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    static IntPtr _dispatcherQueue; // kept alive for the life of the process

    readonly HwndSource _source;
    readonly Compositor _compositor;
    readonly DesktopWindowTarget _target;
    readonly SpriteVisual _visual;
    readonly CompositionRoundedRectangleGeometry _shape;

    public IntPtr Handle => _source.Handle;

    /// <summary>Returns null when this Windows build can't provide a live backdrop; the caller falls back to the snapshot.</summary>
    public static GlassBackdrop? TryCreate()
    {
        try { return new GlassBackdrop(); }
        catch { return null; }
    }

    GlassBackdrop()
    {
        // The compositor needs a dispatcher queue on its thread (the WPF UI thread).
        if (_dispatcherQueue == IntPtr.Zero)
        {
            var options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current thread */, apartmentType = 2 /* STA */,
            };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _dispatcherQueue));
        }

        // A bare window with no drawing surface of its own: everything in it comes from the compositor.
        _source = new HwndSource(new HwndSourceParameters("GlintGlass")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT,
            PositionX = 0, PositionY = 0, Width = 1, Height = 1,
        });

        int on = 1, doNotRound = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_HOSTBACKDROPBRUSH, ref on, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));

        _compositor = new Compositor();
        _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle, false, out IntPtr rawTarget);
        _target = DesktopWindowTarget.FromAbi(rawTarget);
        Marshal.Release(rawTarget);

        _shape = _compositor.CreateRoundedRectangleGeometry();
        _visual = _compositor.CreateSpriteVisual();
        _visual.Brush = _compositor.CreateHostBackdropBrush();
        _visual.Clip = _compositor.CreateGeometricClip(_shape);
        _target.Root = _visual;
    }

    /// <summary>Positions the glass (physical pixels) directly below <paramref name="above"/> in the z-order and shows it.</summary>
    public void Place(IntPtr above, int x, int y, int w, int h, float radius)
    {
        var size = new Vector2(w, h);
        _visual.Size = size;
        _shape.Size = size;
        _shape.CornerRadius = new Vector2(radius);
        SetWindowPos(Handle, above, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void FadeIn(TimeSpan duration)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = duration;
        _visual.StartAnimation("Opacity", fade);
    }

    public void Hide() => ShowWindow(Handle, 0 /* SW_HIDE */);

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwndTarget, bool isTopmost, out IntPtr target);
    }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
