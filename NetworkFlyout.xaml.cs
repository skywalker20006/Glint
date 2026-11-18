using System.Windows;
using Windows.Networking.Connectivity;

namespace Glint;

/// <summary>Drops down from Skyline's speed readout: data downloaded and uploaded since the PC started.</summary>
public partial class NetworkFlyout : BarFlyout
{
    readonly Func<(long Down, long Up)> _usage;

    internal NetworkFlyout(Func<(long Down, long Up)> usage)
    {
        InitializeComponent();
        _usage = usage;
    }

    protected override FrameworkElement Card => CardBorder;

    protected override void Refresh() => Sync();

    /// <summary>Updates the numbers; Skyline calls this every second while the panel is open.</summary>
    public void Sync()
    {
        var (down, up) = _usage();
        DownText.Text = NetSpeed.FormatBytes(down);
        UpText.Text = NetSpeed.FormatBytes(up);
        TotalText.Text = "Total " + NetSpeed.FormatBytes(down + up);

        var on = DateTime.Now - NetSpeed.BootTime;
        string uptime = on.TotalDays >= 1 ? $"{(int)on.TotalDays} d {on.Hours} h"
            : on.TotalHours >= 1 ? $"{(int)on.TotalHours} h {on.Minutes} m" : $"{Math.Max(1, on.Minutes)} m";
        string started = NetSpeed.BootTime.Date == DateTime.Today ? NetSpeed.BootTime.ToString("t")
            : NetSpeed.BootTime.ToString("ddd t");
        SinceText.Text = $"Since {started}  ·  on for {uptime}";

        ConnectionText.Text = ConnectionName();
    }

    static string ConnectionName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return "Not connected";
            return profile.IsWlanConnectionProfile ? "Wi-Fi: " + profile.ProfileName : profile.ProfileName;
        }
        catch { return ""; }
    }
}
