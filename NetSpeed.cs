using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Glint;

/// <summary>
/// Network speed (megabits per second) and data used since the PC started.
/// Windows keeps a byte counter per network adapter from when the adapter came up, normally at boot.
/// The counters can reset (an adapter restarted, a driver reloaded), so totals that would be lost are
/// carried over and saved, which also keeps them right when Glint itself is restarted.
/// </summary>
internal sealed class NetSpeed
{
    sealed class Counter { public long Rx, Tx, CarryRx, CarryTx; }

    sealed class SavedUsage
    {
        public DateTime Boot { get; set; }
        public Dictionary<string, long[]> Carry { get; set; } = new();
    }

    static readonly string UsagePath = Path.Combine(Settings.Dir, "netusage.json");

    /// <summary>Every adapter that has carried traffic this session, by adapter id.</summary>
    readonly Dictionary<string, Counter> _counters = new();
    readonly HashSet<string> _activeIds = new();
    long _rx, _tx;
    DateTime _at;
    bool _primed;
    DateTime _lastSave;

    public static DateTime BootTime { get; } = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public NetSpeed() => LoadCarry();

    /// <summary>Speed since the previous call, or null on the first call (there is nothing to compare with yet).</summary>
    public (double Down, double Up)? Sample()
    {
        if (!Refresh(out long rx, out long tx)) return null;

        var now = DateTime.UtcNow;
        (double, double)? result = null;
        double seconds = (now - _at).TotalSeconds;
        if (_primed && seconds > 0.2)
        {
            // A counter reset shows up as a drop; treat it as zero rather than a negative speed.
            double down = Math.Max(0, rx - _rx) * 8 / 1_000_000.0 / seconds;
            double up = Math.Max(0, tx - _tx) * 8 / 1_000_000.0 / seconds;
            result = (down, up);
        }
        _rx = rx;
        _tx = tx;
        _at = now;
        _primed = true;
        return result;
    }

    /// <summary>Bytes downloaded and uploaded since the PC started, across every connection used.</summary>
    public (long Down, long Up) UsageSinceBoot()
    {
        Refresh(out _, out _);
        long down = 0, up = 0;
        foreach (var c in _counters.Values)
        {
            down += c.CarryRx + c.Rx;
            up += c.CarryTx + c.Tx;
        }
        return (down, up);
    }

    /// <summary>Reads the adapter counters. rx/tx are the totals of connections that are up right now (for speed).</summary>
    bool Refresh(out long rx, out long tx)
    {
        rx = tx = 0;
        _activeIds.Clear();
        bool carried = false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                bool up = nic.OperationalStatus == OperationalStatus.Up;
                // Only adapters with a route out: skips virtual switches and VPN shadows that would double-count.
                bool routed = up && nic.GetIPProperties().GatewayAddresses.Count > 0;
                if (!routed && !_counters.ContainsKey(nic.Id)) continue;

                var stats = nic.GetIPStatistics();
                long r = stats.BytesReceived, t = stats.BytesSent;
                if (!_counters.TryGetValue(nic.Id, out var c)) _counters[nic.Id] = c = new Counter();
                if (r < c.Rx || t < c.Tx)
                {
                    // The adapter's counter started again from zero: keep what it had counted.
                    c.CarryRx += c.Rx;
                    c.CarryTx += c.Tx;
                    carried = true;
                }
                c.Rx = r;
                c.Tx = t;

                if (routed)
                {
                    _activeIds.Add(nic.Id);
                    rx += r;
                    tx += t;
                }
            }
        }
        catch { return false; }

        // An adapter that came or went changes the sum; restart the speed baseline rather than show a spike.
        if (_primed && (rx < _rx || tx < _tx)) _primed = false;
        if (carried || DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(5)) SaveCarry();
        return true;
    }

    void LoadCarry()
    {
        try
        {
            if (!File.Exists(UsagePath)) return;
            var saved = JsonSerializer.Deserialize<SavedUsage>(File.ReadAllText(UsagePath));
            // Only counts from this same boot apply.
            if (saved == null || Math.Abs((saved.Boot - BootTime).TotalMinutes) > 2) return;
            foreach (var (id, carry) in saved.Carry)
                if (carry.Length == 4)
                    _counters[id] = new Counter { CarryRx = carry[0], CarryTx = carry[1], Rx = carry[2], Tx = carry[3] };
        }
        catch { }
    }

    void SaveCarry()
    {
        if (Settings.Frozen) return;
        _lastSave = DateTime.UtcNow;
        try
        {
            var saved = new SavedUsage
            {
                Boot = BootTime,
                Carry = _counters.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.CarryRx, kv.Value.CarryTx, kv.Value.Rx, kv.Value.Tx }),
            };
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(UsagePath, JsonSerializer.Serialize(saved));
        }
        catch { }
    }

    public static string Format(double mbps) =>
        mbps >= 100 ? mbps.ToString("0") : mbps >= 0.05 ? mbps.ToString("0.0") : "0";

    /// <summary>"842 MB", "1.36 GB" (decimal units, like the Mbps speeds).</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => (bytes / 1e12).ToString("0.00") + " TB",
        >= 10_000_000_000 => (bytes / 1e9).ToString("0.0") + " GB",
        >= 1_000_000_000 => (bytes / 1e9).ToString("0.00") + " GB",
        >= 10_000_000 => (bytes / 1e6).ToString("0") + " MB",
        >= 1_000_000 => (bytes / 1e6).ToString("0.0") + " MB",
        >= 1_000 => (bytes / 1e3).ToString("0") + " KB",
        _ => bytes + " B",
    };
}
