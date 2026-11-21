using System.Diagnostics;
using System.IO;

namespace Glint;

/// <summary>
/// Installed apps, read from the shell's AppsFolder (covers both classic and Store apps).
/// The list is cached in memory so searching it is instant.
/// </summary>
internal static class AppIndex
{
    /// <param name="Target">The .exe a classic app points at; null for Store apps.</param>
    sealed record AppEntry(string Name, string Path, string Lower, string Initials, string? Target);

    static volatile List<AppEntry> _apps = new();
    static DateTime _lastRefresh = DateTime.MinValue;
    static int _refreshing;

    /// <summary>Raised (on a background thread) once the first scan completes after startup.</summary>
    public static event Action? Loaded;

    static readonly string[] JunkExtensions = { ".url", ".chm", ".txt", ".htm", ".html", ".pdf", ".rtf" };

    public static void RefreshIfStale()
    {
        if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromMinutes(5)) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        var t = new Thread(() =>
        {
            try
            {
                var list = new List<AppEntry>();
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string? name = item.Name, path = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (JunkExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                    string? target = null;
                    try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                    list.Add(Make(name, path, target));
                }
                if (list.Count > 0)
                {
                    bool first = _apps.Count == 0;
                    _apps = list;
                    _lastRefresh = DateTime.UtcNow;
                    if (first) Loaded?.Invoke();
                }
            }
            catch { /* keep the previous list */ }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        })
        { IsBackground = true, Name = "Glint.Apps" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static AppEntry Make(string name, string path, string? target)
    {
        if (target != null && !(target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)))
            target = null;
        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        string initials = new string(words.Select(w => char.ToLowerInvariant(w[0])).ToArray());
        return new AppEntry(name, path, name.ToLowerInvariant(), initials, target);
    }

    /// <param name="strict">Skip loose letter-sequence matches; used when a command launches the top hit unseen.</param>
    public static List<ResultItem> Search(string query, int max, bool strict = false)
    {
        string q = query.ToLowerInvariant();
        var usage = Settings.Data.Usage;
        int minScore = strict ? 600 : 1;

        return _apps
            .Select(a => (app: a, score: Score(a, q)))
            .Where(x => x.score >= minScore)
            .Select(x => (x.app, score: x.score + Math.Min(usage.GetValueOrDefault(x.app.Path), 20) * 8))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.app.Name.Length)
            .Take(max)
            .Select(x => ToResult(x.app))
            .ToList();
    }

    /// <summary>Whether some app name (or one of its words, or its initials) starts with the query.</summary>
    public static bool HasStrongMatch(string query)
    {
        string q = query.ToLowerInvariant();
        return _apps.Any(a => Score(a, q) >= 750);
    }

    static int Score(AppEntry a, string q)
    {
        string n = a.Lower;
        if (n == q) return 1000;
        if (n.StartsWith(q)) return 900;
        int idx = n.IndexOf(q, StringComparison.Ordinal);
        if (idx > 0 && !char.IsLetterOrDigit(n[idx - 1])) return 800;   // start of a later word
        if (q.Length >= 2 && a.Initials.StartsWith(q)) return 750;      // "vsc" -> Visual Studio Code
        if (idx > 0) return 600;
        if (q.Length >= 3 && !q.Contains(' ') && IsSubsequence(q, n)) return 300;
        return 0;
    }

    static bool IsSubsequence(string q, string n)
    {
        int i = 0;
        foreach (char c in n)
            if (i < q.Length && c == q[i]) i++;
        return i == q.Length;
    }

    static ResultItem ToResult(AppEntry a)
    {
        string parsing = @"shell:AppsFolder\" + a.Path;
        Action open = () =>
        {
            Settings.BumpUsage(a.Path);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(parsing);
            Process.Start(psi);
        };
        string? target = a.Target;

        return new ResultItem
        {
            Title = a.Name,
            Kind = "Application",
            IconKey = parsing,
            ProgramPath = target,
            Action = open,
            // Store apps have no .exe to elevate or reveal, so they get no extra actions.
            GetActions = target == null ? null : () => new List<ResultItem>
            {
                new() { Title = "Open", Glyph = "", Action = open },
                new()
                {
                    Title = "Run as administrator", Glyph = "",
                    Action = () =>
                    {
                        Settings.BumpUsage(a.Path);
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas" });
                    },
                },
                new()
                {
                    Title = "Open file location", Glyph = "",
                    Action = () => Process.Start("explorer.exe", $"/select,\"{target}\""),
                },
                new()
                {
                    Title = "Copy path", Subtitle = target, Glyph = "",
                    Action = () => { try { System.Windows.Clipboard.SetText(target); } catch { } },
                },
            },
        };
    }
}
