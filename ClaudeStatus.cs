using System.IO;

namespace Glint;

/// <summary>
/// Whether Claude is working, waiting for you, or idle, as reported by Claude Code hooks.
/// The hooks (in ~/.claude/settings.json) keep one tiny file per Claude session in %APPDATA%\Glint\claude,
/// containing "busy" or "ready"; the file is removed when the session ends.
/// </summary>
internal sealed class ClaudeStatus : IDisposable
{
    public enum State { Idle, Busy, Ready }

    public static readonly string Folder = Path.Combine(Settings.Dir, "claude");

    /// <summary>How long "ready" pulses before the mascot settles back to idle on its own.</summary>
    static readonly TimeSpan ReadyCalmsAfter = TimeSpan.FromSeconds(10);
    /// <summary>A "busy" this old is a session that crashed without saying it stopped.</summary>
    static readonly TimeSpan BusyGoesStaleAfter = TimeSpan.FromHours(6);

    readonly FileSystemWatcher? _watcher;
    readonly System.Threading.Timer _debounce, _clock, _calm;
    DateTime _acknowledged = DateTime.MinValue;

    public State Current { get; private set; } = State.Idle;

    /// <summary>Raised on a background thread when the state changes.</summary>
    public event Action? Changed;

    public ClaudeStatus()
    {
        _debounce = new System.Threading.Timer(_ => Evaluate());
        // Fires the moment the newest "ready" is 10 seconds old, so the pulse ends right on time.
        _calm = new System.Threading.Timer(_ => Evaluate());
        // Time alone can change the state (a ready calming down), so look again now and then.
        _clock = new System.Threading.Timer(_ => Evaluate(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        try
        {
            Directory.CreateDirectory(Folder);
            _watcher = new FileSystemWatcher(Folder, "*.state")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler poke = (_, _) => _debounce.Change(80, Timeout.Infinite);
            _watcher.Created += poke;
            _watcher.Changed += poke;
            _watcher.Deleted += poke;
            _watcher.Renamed += (_, _) => _debounce.Change(80, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* folder unavailable: the mascot just stays idle */ }
        Evaluate();
    }

    /// <summary>You've seen that Claude is ready (clicked the mascot): stop pulsing until it next finishes.</summary>
    public void Acknowledge()
    {
        _acknowledged = DateTime.UtcNow;
        Evaluate();
    }

    void Evaluate()
    {
        bool busy = false, ready = false;
        var now = DateTime.UtcNow;
        TimeSpan? calmIn = null;
        try
        {
            foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.state"))
            {
                string text;
                try { text = File.ReadAllText(file.FullName).Trim(); }
                catch { continue; } // being written right now; the next change event re-reads it
                var age = now - file.LastWriteTimeUtc;
                if (text == "ready" && age < ReadyCalmsAfter && file.LastWriteTimeUtc > _acknowledged)
                {
                    ready = true;
                    var left = ReadyCalmsAfter - age;
                    if (calmIn == null || left > calmIn) calmIn = left; // the latest "ready" decides when to stop
                }
                else if (text == "busy" && age < BusyGoesStaleAfter) busy = true;
            }
        }
        catch { }

        if (calmIn is { } wait) _calm.Change(wait + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);

        // A session waiting for you matters more than one still working.
        var state = ready ? State.Ready : busy ? State.Busy : State.Idle;
        if (state == Current) return;
        Current = state;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
        _clock.Dispose();
        _calm.Dispose();
    }
}
