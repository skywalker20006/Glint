using System.Windows;
using System.Windows.Input;

namespace Glint;

/// <summary>Drops down from Skyline's now-playing item: cover, song details and playback controls.</summary>
public partial class MediaFlyout : BarFlyout
{
    readonly MediaWatcher _media;

    internal MediaFlyout(MediaWatcher media)
    {
        InitializeComponent();
        _media = media;
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Space: _media.TogglePlayPause(); break;
                case Key.Right: _media.Next(); break;
                case Key.Left: _media.Previous(); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    protected override FrameworkElement Card => CardBorder;

    protected override void Refresh() => Update(_media.Current);

    internal void Update(MediaWatcher.Track? track)
    {
        if (track == null)
        {
            if (IsOpen) Close(); // Spotify closed
            return;
        }
        ArtBrush.ImageSource = track.Art;
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        AlbumText.Text = track.Album;
        AlbumText.Visibility = track.Album.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayIcon.Text = track.Playing ? "" : ""; // pause / play
        WashStop.Color = track.Accent;
    }

    void OnPlayPause(object sender, MouseButtonEventArgs e) => _media.TogglePlayPause();
    void OnNext(object sender, MouseButtonEventArgs e) => _media.Next();
    void OnPrevious(object sender, MouseButtonEventArgs e) => _media.Previous();
}
