using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Windows.Networking.Connectivity;

namespace Glint;

/// <summary>
/// Looks on GitHub for a newer Glint release: once a day, shortly after Glint starts, and again when the internet
/// comes back after a failed try. It only reads the latest release's version and page link; nothing is
/// downloaded or installed. The person goes to the release page themselves.
/// </summary>
internal static class Updater
{
    const string Repo = "skywalker20006/Glint";
    const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    const string ReleasesPage = "https://github.com/" + Repo + "/releases/";
    static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(30);

    static System.Threading.Timer? _timer;
    static int _checking;

    /// <summary>This build's version, e.g. 1.1.1.</summary>
    public static Version Current { get; } = ThisVersion();

    public static bool Checking => _checking == 1;
    /// <summary>Why the last check failed, or null if it worked.</summary>
    public static string? Error { get; private set; }

    /// <summary>The newest release found, if it is newer than this build.</summary>
    public static Version? Available =>
        Version.TryParse(Settings.Data.LatestVersion, out var v) && v > Current ? v : null;

    /// <summary>Page of the newer release, always on this repository's GitHub releases.</summary>
    public static string? AvailableUrl => Available != null ? Settings.Data.LatestUrl : null;

    /// <summary>Raised on a background thread whenever a check starts or ends.</summary>
    public static event Action? Changed;

    /// <summary>Raised once per newer version found, so the tray can mention it a single time.</summary>
    public static event Action<Version, string>? NewVersionFound;

    public static string Short(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>Starts the daily checks, if turned on in Settings.</summary>
    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => CheckIfDue(), null, FirstCheckAfter, TimeSpan.FromHours(1));
        NetworkInformation.NetworkStatusChanged += _ =>
        {
            // The last try failed (no internet): try again now that the connection changed.
            if (Error != null) CheckIfDue(ignoreAge: true);
        };
    }

    static void CheckIfDue(bool ignoreAge = false)
    {
        if (!Settings.Data.AutoCheckUpdates || Settings.Frozen) return;
        if (!ignoreAge && DateTime.UtcNow - Settings.Data.LastUpdateCheck < CheckEvery) return;
        _ = CheckAsync();
    }

    /// <summary>Asks GitHub for the latest release now. Safe to call from any thread; never throws.</summary>
    public static async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        Changed?.Invoke();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub's API needs a User-Agent; this one names the app only.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Glint/" + Short(Current));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestApi);

            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new FormatException("Unrecognised version " + tag);
            // Only ever send people to this repository's releases.
            if (!url.StartsWith(ReleasesPage, StringComparison.OrdinalIgnoreCase)) url = ReleasesPage + "latest";

            Settings.Data.LatestVersion = Short(latest);
            Settings.Data.LatestUrl = url;
            Settings.Data.LastUpdateCheck = DateTime.UtcNow;
            Error = null;

            if (latest > Current && Settings.Data.NotifiedVersion != Short(latest))
            {
                Settings.Data.NotifiedVersion = Short(latest);
                NewVersionFound?.Invoke(latest, url);
            }
            Settings.Save();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Error = "No releases found on GitHub.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = "Couldn't reach GitHub. Glint will try again when you're online.";
        }
        catch
        {
            Error = "GitHub's answer couldn't be read.";
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
            Changed?.Invoke();
        }
    }

    static Version ThisVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}
