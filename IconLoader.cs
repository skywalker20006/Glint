using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glint;

/// <summary>Loads shell icons on a dedicated STA thread so the UI never waits on them.</summary>
internal static class IconLoader
{
    const int SIIGBF_ICONONLY = 0x4;

    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();
    static readonly BlockingCollection<ResultItem> Queue = new();

    static IconLoader()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "Glint.Icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Load(ResultItem item)
    {
        if (item.IconKey == null || item.Icon != null) return;
        if (Cache.TryGetValue(item.IconKey, out var cached)) item.Icon = cached;
        else Queue.Add(item);
    }

    static void Worker()
    {
        foreach (var item in Queue.GetConsumingEnumerable())
        {
            string key = item.IconKey!;
            if (!Cache.TryGetValue(key, out var img))
            {
                if (Cache.Count > 3000) Cache.Clear();
                try { img = key.StartsWith("emoji:") ? EmojiImages.Render(key[6..]) : LoadIcon(key, item.IconIsFile); }
                catch { img = null; }
                Cache[key] = img;
            }
            item.Icon = img;
        }
    }

    static ImageSource? LoadIcon(string parsingName, bool iconOnly)
    {
        Guid iid = typeof(Native.IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        try
        {
            if (factory.GetImage(new Native.SIZE { cx = 64, cy = 64 }, iconOnly ? SIIGBF_ICONONLY : 0, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { Native.DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    static ImageSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var bits = new byte[w * h * 4];
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var dc = Native.CreateCompatibleDC(IntPtr.Zero);
        try { if (Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref bi, 0) == 0) return null; }
        finally { Native.DeleteDC(dc); }

        // Shell bitmaps are premultiplied BGRA; some legacy icons have no alpha channel at all.
        bool hasAlpha = false;
        for (int i = 3; i < bits.Length; i += 4)
            if (bits[i] != 0) { hasAlpha = true; break; }

        var src = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, bits, w * 4);
        src.Freeze();
        return src;
    }
}
