using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace Glint;

/// <summary>
/// Follows what Spotify is playing through Windows' media controls (the same information the volume overlay
/// shows), so no Spotify account or API key is needed. Raises <see cref="Changed"/> on a background thread.
/// </summary>
internal sealed class MediaWatcher
{
    public sealed record Track(string Title, string Artist, string Album, BitmapSource? Art, Color Accent, bool Playing);

    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _artKey;
    BitmapSource? _art;
    Color _accent = Color.FromRgb(0x1E, 0xD7, 0x60); // Spotify green until the album art says otherwise
    int _refreshing;
    volatile bool _pending;

    /// <summary>The Spotify track, or null when Spotify isn't running or has nothing loaded.</summary>
    public Track? Current { get; private set; }

    public event Action? Changed;

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => PickSession();
            PickSession();
        }
        catch { /* media controls unavailable: Skyline just never shows a track */ }
    }

    void PickSession()
    {
        GlobalSystemMediaTransportControlsSession? spotify = null;
        try
        {
            spotify = _manager?.GetSessions()
                .FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
        }
        catch { }

        if (!ReferenceEquals(spotify, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
            }
            _session = spotify;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
            }
        }
        Refresh();
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Refresh();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Refresh();

    async void Refresh()
    {
        // Spotify fires several events per track change; collapse them into one read,
        // and read once more if another event arrived meanwhile so the final state is never missed.
        _pending = true;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
          while (_pending)
          {
            _pending = false;
            await Task.Delay(120);
            var session = _session;
            Track? track = null;
            if (session != null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                bool playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                               GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (props != null && !string.IsNullOrWhiteSpace(props.Title))
                {
                    string key = props.Title + "|" + props.AlbumTitle + "|" + props.Artist;
                    if (key != _artKey)
                    {
                        _artKey = key;
                        _art = await LoadArt(props.Thumbnail);
                        _accent = _art != null ? AccentFrom(_art) : Color.FromRgb(0x1E, 0xD7, 0x60);
                    }
                    track = new Track(props.Title, props.Artist ?? "", props.AlbumTitle ?? "", _art, _accent, playing);
                }
            }
            Current = track;
            Changed?.Invoke();
          }
        }
        catch { }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    static async Task<BitmapSource?> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            using var source = stream.AsStreamForRead();
            var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// <summary>
    /// The colour the waveform glows in: the cover's main colour, averaged over the largest group of
    /// similar hues, then lifted so it reads on dark glass the way the Dynamic Island tints its waveform.
    /// </summary>
    internal static Color AccentFrom(BitmapSource art)
    {
        const int Size = 32;
        var small = new FormatConvertedBitmap(new TransformedBitmap(art,
            new ScaleTransform((double)Size / art.PixelWidth, (double)Size / art.PixelHeight)), PixelFormats.Bgra32, null, 0);
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);

        var (r, g, b, ok) = MainColour(px, w * h);
        if (!ok) return Color.FromRgb(0xE8, 0xE8, 0xEE); // greyscale cover: soft white bars

        double peak = Math.Max(r, Math.Max(g, b));
        double lift = 0.95 / Math.Max(peak, 0.01);
        return Color.FromRgb(Lift(r * lift), Lift(g * lift), Lift(b * lift));
    }

    /// <summary>
    /// Groups the coloured pixels by hue (24 slices of 15 degrees), picks the slice that covers the most of
    /// the cover (counting its two neighbours, so a colour split across a boundary isn't penalised), and
    /// averages the actual pixels in it. Area wins, so a small bright logo can't take over.
    /// </summary>
    static (double R, double G, double B, bool Ok) MainColour(byte[] px, int count)
    {
        const int Bins = 24;
        var weight = new double[Bins];
        var sumR = new double[Bins];
        var sumG = new double[Bins];
        var sumB = new double[Bins];

        for (int i = 0; i < px.Length; i += 4)
        {
            double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
            double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
            double sat = max == 0 ? 0 : (max - min) / max;
            // Black, white and grey carry no hue; leave them out of the vote.
            if (max < 0.15 || sat < 0.18) continue;

            double hue;
            if (max == pr) hue = 60 * (((pg - pb) / (max - min)) % 6);
            else if (max == pg) hue = 60 * ((pb - pr) / (max - min) + 2);
            else hue = 60 * ((pr - pg) / (max - min) + 4);
            if (hue < 0) hue += 360;

            int bin = (int)(hue / (360.0 / Bins)) % Bins;
            // Mostly area; a little extra for richer colour so a faint tint doesn't beat a real one.
            double wgt = 0.6 + 0.4 * sat * max;
            weight[bin] += wgt;
            sumR[bin] += pr * wgt;
            sumG[bin] += pg * wgt;
            sumB[bin] += pb * wgt;
        }

        int best = -1;
        double bestScore = 0, total = weight.Sum();
        for (int bin = 0; bin < Bins; bin++)
        {
            double score = weight[bin] + 0.5 * (weight[(bin + 1) % Bins] + weight[(bin + Bins - 1) % Bins]);
            if (score > bestScore) { bestScore = score; best = bin; }
        }
        // Too little colour overall (under ~6% of the cover) counts as a greyscale cover.
        if (best < 0 || total < count * 0.06) return (0, 0, 0, false);

        double wr = 0, wg = 0, wb = 0, ws = 0;
        foreach (int bin in new[] { (best + Bins - 1) % Bins, best, (best + 1) % Bins })
        {
            wr += sumR[bin]; wg += sumG[bin]; wb += sumB[bin]; ws += weight[bin];
        }
        return (wr / ws, wg / ws, wb / ws, true);
    }

    static byte Lift(double v) => (byte)Math.Clamp(v * 255, 60, 255);

    public async void TogglePlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
}
