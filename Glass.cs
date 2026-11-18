using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace Glint;

/// <summary>
/// Produces the frosted backdrop: grabs the pixels that will sit behind the panel,
/// then blurs and saturates them on a downscaled copy (a few milliseconds on the CPU).
/// </summary>
internal static class Glass
{
    const int Downscale = 6;
    const int Radius = 4;
    const double Saturation = 1.75;

    /// <param name="radius">Blur radius on the downscaled image, 1 (light) to 8 (heavy).</param>
    public static BitmapSource? Capture(int x, int y, int w, int h, int radius = Radius)
    {
        radius = Math.Clamp(radius, 1, 8);
        try
        {
            if (w <= 0 || h <= 0) return null;
            int sw = Math.Max(8, w / Downscale), sh = Math.Max(8, h / Downscale);

            using var full = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(small))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, sw, sh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            return Frost(small, radius, Saturation);
        }
        catch
        {
            return null; // e.g. secure desktop; the panel falls back to its plain tint
        }
    }

    /// <summary>Blurs and saturates an already-downscaled bitmap in place of a GPU effect.</summary>
    public static BitmapSource Frost(Bitmap small, int radius, double saturation)
    {
        int sw = small.Width, sh = small.Height;
        var data = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        int stride = data.Stride;
        var buf = new byte[stride * sh];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        small.UnlockBits(data);

        // Three box-blur passes approximate a Gaussian.
        var tmp = new byte[buf.Length];
        for (int i = 0; i < 3; i++)
        {
            BoxH(buf, tmp, sw, sh, stride, radius);
            BoxV(tmp, buf, sw, sh, stride, radius);
        }
        Saturate(buf, saturation);

        var src = BitmapSource.Create(sw, sh, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, buf, stride);
        src.Freeze();
        return src;
    }

    /// <summary>
    /// Mica, the way Windows defines it: the desktop wallpaper, very heavily blurred, sampled where the
    /// window sits. It deliberately ignores other windows, so it never changes as you work.
    /// Returns a frosted image of the whole primary screen's wallpaper, or null if there is none.
    /// </summary>
    public static BitmapSource? MicaWallpaper(int screenW, int screenH)
    {
        try
        {
            string? path = WallpaperPath();
            if (path == null) return null;

            using var stream = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(path)); // don't lock the file
            using var wallpaper = Image.FromStream(stream);

            const int Down = 16;
            int sw = Math.Max(8, screenW / Down), sh = Math.Max(8, screenH / Down);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                // Same as the "Fill" wallpaper fit, Windows' default: cover the screen, centred.
                double scale = Math.Max((double)sw / wallpaper.Width, (double)sh / wallpaper.Height);
                int dw = (int)Math.Ceiling(wallpaper.Width * scale), dh = (int)Math.Ceiling(wallpaper.Height * scale);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(wallpaper, (sw - dw) / 2, (sh - dh) / 2, dw, dh);
            }
            return Frost(small, 6, 1.3);
        }
        catch { return null; }
    }

    static string? WallpaperPath()
    {
        var sb = new System.Text.StringBuilder(520);
        if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, sb.Capacity, sb, 0) && System.IO.File.Exists(sb.ToString()))
            return sb.ToString();
        string transcoded = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return System.IO.File.Exists(transcoded) ? transcoded : null;
    }

    /// <summary>A small tile of faint grain, the subtle noise that gives acrylic its texture.</summary>
    public static BitmapSource Noise()
    {
        const int Size = 128;
        var rng = new Random(7);
        var px = new byte[Size * Size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            byte v = (byte)rng.Next(256);
            byte a = (byte)rng.Next(0, 40);
            // premultiplied
            px[i] = px[i + 1] = px[i + 2] = (byte)(v * a / 255);
            px[i + 3] = a;
        }
        var src = BitmapSource.Create(Size, Size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, px, Size * 4);
        src.Freeze();
        return src;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(int action, int param, System.Text.StringBuilder value, int flags);

    static void BoxH(byte[] s, byte[] d, int w, int h, int stride, int r)
    {
        int win = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += s[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    d[row + x * 4 + c] = (byte)(sum / win);
                    int add = Math.Min(x + r + 1, w - 1), rem = Math.Max(x - r, 0);
                    sum += s[row + add * 4 + c] - s[row + rem * 4 + c];
                }
            }
        }
    }

    static void BoxV(byte[] s, byte[] d, int w, int h, int stride, int r)
    {
        int win = 2 * r + 1;
        for (int x = 0; x < w; x++)
        {
            int col = x * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += s[Math.Clamp(k, 0, h - 1) * stride + col + c];
                for (int y = 0; y < h; y++)
                {
                    d[y * stride + col + c] = (byte)(sum / win);
                    int add = Math.Min(y + r + 1, h - 1), rem = Math.Max(y - r, 0);
                    sum += s[add * stride + col + c] - s[rem * stride + col + c];
                }
            }
        }
    }

    static void Saturate(byte[] buf, double saturation)
    {
        for (int i = 0; i + 3 < buf.Length; i += 4)
        {
            double b = buf[i], g = buf[i + 1], r = buf[i + 2];
            double l = 0.114 * b + 0.587 * g + 0.299 * r;
            buf[i] = Clamp(l + (b - l) * saturation);
            buf[i + 1] = Clamp(l + (g - l) * saturation);
            buf[i + 2] = Clamp(l + (r - l) * saturation);
        }
    }

    static byte Clamp(double v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
}
