using System.Windows;
using System.Windows.Input;

namespace Glint;

/// <summary>The small sound panel that drops down from Skyline's volume icon.</summary>
public partial class VolumeFlyout : BarFlyout
{
    readonly Action _onChanged;
    bool _syncing;

    public VolumeFlyout(Action onChanged)
    {
        InitializeComponent();
        _onChanged = onChanged;
    }

    protected override FrameworkElement Card => CardBorder;

    protected override void Refresh() => Sync();

    /// <summary>Reads the current level and mute state into the controls.</summary>
    public void Sync()
    {
        _syncing = true;
        int level = SystemControl.Volume ?? 0;
        bool muted = SystemControl.Muted == true;
        VolumeSlider.Value = level;
        VolumeSlider.Opacity = muted ? 0.45 : 1;
        PercentText.Text = level + "%";
        MuteIcon.Text = muted ? "" : level == 0 ? "" : level <= 33 ? "" : level <= 66 ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute" : "Mute";
        DeviceText.Text = SystemControl.OutputDeviceName ?? "";
        _syncing = false;
    }

    void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        SystemControl.SetVolume((int)Math.Round(e.NewValue)); // also unmutes, like dragging the system slider
        Sync();
        _onChanged();
    }

    void OnMuteClick(object sender, MouseButtonEventArgs e)
    {
        SystemControl.SetMute(SystemControl.Muted != true);
        Sync();
        _onChanged();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 2); break;
            case Key.Right or Key.Up: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 2); break;
            default: return;
        }
        e.Handled = true;
    }
}
