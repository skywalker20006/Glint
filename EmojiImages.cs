using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glint;

/// <summary>
/// Draws an emoji in colour into a bitmap. WPF's own text rendering only draws emoji in one colour, so this
/// goes through Direct2D and DirectWrite, which use the colour layers in Segoe UI Emoji.
/// Calls are made through the COM vtables directly; the slot numbers follow d2d1.h, dwrite.h and wincodec.h.
/// Use from one thread only (the icon loader's).
/// </summary>
internal static unsafe class EmojiImages
{
    static IntPtr _d2d, _wic, _format;
    static bool _failed;

    public static ImageSource? Render(string emoji, int size = 64)
    {
        if (_failed || !Init(size)) return null;
        IntPtr bitmap = IntPtr.Zero, target = IntPtr.Zero, brush = IntPtr.Zero;
        try
        {
            Guid pbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910"); // GUID_WICPixelFormat32bppPBGRA
            if (Call(_wic, 17, (uint)size, (uint)size, &pbgra, 1 /* cache on load */, &bitmap) != 0) return null;

            var props = new RenderTargetProperties { PixelFormat = 87 /* B8G8R8A8 */, AlphaMode = 1 /* premultiplied */ };
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RenderTargetProperties*, IntPtr*, int>)Slot(_d2d, 13))(
                    _d2d, bitmap, &props, &target) != 0) return null;

            var black = new ColorF { A = 1 };
            if (((delegate* unmanaged[Stdcall]<IntPtr, ColorF*, IntPtr, IntPtr*, int>)Slot(target, 8))(
                    target, &black, IntPtr.Zero, &brush) != 0) return null;

            var clear = new ColorF();
            var rect = new RectF { Right = size, Bottom = size };
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(target, 48))(target);                    // BeginDraw
            ((delegate* unmanaged[Stdcall]<IntPtr, ColorF*, void>)Slot(target, 47))(target, &clear);   // Clear
            fixed (char* text = emoji)
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, RectF*, IntPtr, int, int, void>)Slot(target, 27))(
                    target, text, (uint)emoji.Length, _format, &rect, brush, 4 /* ENABLE_COLOR_FONT */, 0);
            if (((delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)Slot(target, 49))(target, null, null) != 0) // EndDraw
                return null;

            var pixels = new byte[size * size * 4];
            fixed (byte* p = pixels)
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, byte*, int>)Slot(bitmap, 7))(
                        bitmap, IntPtr.Zero, (uint)size * 4, (uint)pixels.Length, p) != 0) return null;

            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            image.Freeze();
            return image;
        }
        catch { return null; }
        finally
        {
            Release(brush);
            Release(target);
            Release(bitmap);
        }
    }

    static bool Init(int size)
    {
        if (_format != IntPtr.Zero) return true;
        try
        {
            Guid d2dIid = new("06152247-6f50-465a-9245-118bfd3b6007");
            Guid dwriteIid = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            Guid wicClsid = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
            Guid wicIid = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            if (D2D1CreateFactory(0, ref d2dIid, IntPtr.Zero, out _d2d) != 0 ||
                CoCreateInstance(ref wicClsid, IntPtr.Zero, 1, ref wicIid, out _wic) != 0 ||
                DWriteCreateFactory(0, ref dwriteIid, out IntPtr dwrite) != 0)
            {
                _failed = true;
                return false;
            }

            IntPtr format;
            fixed (char* family = "Segoe UI Emoji")
            fixed (char* locale = "en-us")
                if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int, int, int, float, char*, IntPtr*, int>)Slot(dwrite, 15))(
                        dwrite, family, IntPtr.Zero, 400, 0, 5, size * 0.72f, locale, &format) != 0)
                {
                    _failed = true;
                    return false;
                }
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(format, 3))(format, 2); // centre horizontally
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(format, 4))(format, 2); // and vertically
            _format = format;
            return true;
        }
        catch
        {
            _failed = true;
            return false;
        }
    }

    static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    static int Call(IntPtr obj, int slot, uint a, uint b, Guid* c, int d, IntPtr* e) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, Guid*, int, IntPtr*, int>)Slot(obj, slot))(obj, a, b, c, d, e);

    static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RenderTargetProperties
    {
        public int Type;
        public uint PixelFormat;
        public int AlphaMode;
        public float DpiX, DpiY;
        public int Usage, MinLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ColorF { public float R, G, B, A; }

    [StructLayout(LayoutKind.Sequential)]
    struct RectF { public float Left, Top, Right, Bottom; }

    [DllImport("d2d1.dll")]
    static extern int D2D1CreateFactory(int type, ref Guid iid, IntPtr options, out IntPtr factory);

    [DllImport("dwrite.dll")]
    static extern int DWriteCreateFactory(int type, ref Guid iid, out IntPtr factory);

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr obj);
}
