using System.Text.RegularExpressions;
using System.Windows;
using Windows.Devices.Radios;

namespace Glint;

/// <summary>
/// Plain-English commands recognised by pattern: "turn off dark mode", "increase volume by 20",
/// "open notepad and spotify", "set a timer for 10 minutes", and so on.
/// Each match becomes a row that says exactly what will happen; nothing runs until Enter.
/// </summary>
internal static class Commands
{
    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    const string Pct = @"(?: ?%| percent)?";

    static readonly Regex Filler = new(@"^(?:hey glint,? |please |pls |can you |could you |would you |i want to |i wanna )+|\b(?:please|pls|the|my)\b", Opt);
    static readonly Regex OpenVerb = new(@"^(?:open|launch|start|run)\s+(?:up\s+)?(?<rest>.+)$", Opt);
    static readonly Regex ListSplit = new(@"\s*(?:,|&|\+|\band\b|\bthen\b)\s*", Opt);

    // theme
    static readonly Regex DarkOn = new(@"^(?:(?:turn on|enable|switch to|switch on|use|set|go|activate) )?dark (?:mode|theme)(?: on)?$|^(?:turn off|disable) light (?:mode|theme)$|^light (?:mode|theme) off$", Opt);
    static readonly Regex DarkOff = new(@"^(?:turn off|disable|switch off|exit|stop|deactivate) dark (?:mode|theme)$|^dark (?:mode|theme) off$|^(?:(?:turn on|enable|switch to|switch on|use|set|go|activate) )?light (?:mode|theme)(?: on)?$", Opt);
    static readonly Regex DarkToggle = new(@"^toggle (?:dark|light) (?:mode|theme)$|^(?:toggle|switch|change) theme$|^dark (?:mode|theme)$", Opt);

    // volume and brightness share their phrasing
    static Regex Up(string noun, string extra) => new(
        $@"^(?:(?:increase|raise|boost|bump|bump up|turn up|crank up|up) (?:{noun})|(?:{noun}) up|turn (?:{noun}) up|{extra})(?: by)?(?: (?<n>\d{{1,3}}))?{Pct}$", Opt);
    static Regex Down(string noun, string extra) => new(
        $@"^(?:(?:decrease|lower|reduce|drop|turn down|down) (?:{noun})|(?:{noun}) down|turn (?:{noun}) down|{extra})(?: by)?(?: (?<n>\d{{1,3}}))?{Pct}$", Opt);
    static Regex SetTo(string noun) => new(
        $@"^(?:set |change |turn |put |make )?(?:{noun})(?: level)?(?: to| at|=)? (?<n>\d{{1,3}}){Pct}$", Opt);

    const string VolNoun = "volume|sound|audio";
    const string BriNoun = "brightness|screen brightness|display brightness";
    static readonly Regex VolUp = Up(VolNoun, "louder|make it louder");
    static readonly Regex VolDown = Down(VolNoun, "quieter|make it quieter");
    static readonly Regex VolSet = SetTo(VolNoun);
    static readonly Regex VolMax = new(@"^(?:max(?:imum)? volume|full volume|volume (?:to )?(?:max(?:imum)?|full))$", Opt);
    static readonly Regex Mute = new(@"^(?:mute|silence)(?: volume| sound| audio| pc| computer)?$|^turn (?:off )?(?:sound|volume|audio)(?: off)?$", Opt);
    static readonly Regex Unmute = new(@"^unmute(?: volume| sound| audio| pc| computer)?$|^turn (?:on )?(?:sound|volume|audio) on$|^turn on (?:sound|volume|audio)$", Opt);
    static readonly Regex BriUp = Up(BriNoun, "brighter|make it brighter");
    static readonly Regex BriDown = Down(BriNoun, "dimmer|dim screen|dim display|make it dimmer");
    static readonly Regex BriSet = SetTo(BriNoun);

    // misc
    static readonly Regex LockPc = new(@"^lock(?: pc| computer| screen| windows| it)?$", Opt);
    static readonly Regex SleepPc = new(@"^(?:sleep|go to sleep|put (?:pc|computer|it) to sleep|sleep (?:pc|computer))$", Opt);
    static readonly Regex RecycleBin = new(@"^(?:empty|clear) (?:recycle bin|recycling bin|trash|bin)$", Opt);
    static readonly Regex Screenshot = new(@"^(?:take (?:a )?)?(?:screenshot|screen shot|snip|screen snip|screen capture)$", Opt);
    static readonly Regex PlayPause = new(@"^(?:pause|resume|play pause|toggle playback)(?: music| song| media| video)?$|^play (?:music|song|media|video)$", Opt);
    static readonly Regex NextTrack = new(@"^(?:next|skip)(?: song| track)?$|^skip this(?: song| track)?$", Opt);
    static readonly Regex PrevTrack = new(@"^(?:previous|prev|last)(?: song| track)$|^go back a (?:song|track)$", Opt);
    static readonly Regex Timer = new(
        @"^(?:(?:set |start )?(?:a )?timer (?:for |of )?|remind me in |wake me in )(?<n>\d+(?:\.\d+)?) ?(?<u>s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?)$" +
        @"|^(?<n>\d+(?:\.\d+)?) ?(?<u>s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?) timer$", Opt);
    // radios
    const string RadioNoun = "(?<r>wifi|wi-fi|wi fi|wireless|bluetooth)";
    static readonly Regex RadioOn = new($@"^(?:turn on|enable|switch on|activate) {RadioNoun}$|^(?:turn |switch )?{RadioNoun} on$", Opt);
    static readonly Regex RadioOff = new($@"^(?:turn off|disable|switch off|deactivate) {RadioNoun}$|^(?:turn |switch )?{RadioNoun} off$", Opt);
    static readonly Regex RadioToggle = new($@"^(?:toggle )?{RadioNoun}$", Opt);

    // power (each of these asks for confirmation before doing anything)
    const string Pc = "(?: (?:pc|computer|laptop|windows|it|this pc|down pc|down computer))?";
    static readonly Regex ShutDown = new($@"^(?:shut ?down|power off|power down){Pc}$|^turn off (?:pc|computer|laptop|this pc)$", Opt);
    static readonly Regex Restart = new($@"^(?:restart|reboot){Pc}$", Opt);
    static readonly Regex SignOut = new(@"^(?:sign ?out|log ?out|log ?off)$", Opt);
    static readonly Regex Hibernate = new($@"^hibernate{Pc}$", Opt);

    static readonly Regex SettingsPage =new(@"^(?:(?:turn|switch) (?:on|off) |toggle |enable |disable )?(?<p>.+?)(?: settings| (?:on|off))?$", Opt);

    static readonly Dictionary<string, (string Uri, string Name)> SettingsPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wifi"] = ("ms-settings:network-wifi", "Wi-Fi"), ["wi-fi"] = ("ms-settings:network-wifi", "Wi-Fi"),
        ["bluetooth"] = ("ms-settings:bluetooth", "Bluetooth"),
        ["airplane mode"] = ("ms-settings:network-airplanemode", "Airplane mode"),
        ["flight mode"] = ("ms-settings:network-airplanemode", "Airplane mode"),
        ["hotspot"] = ("ms-settings:network-mobilehotspot", "Mobile hotspot"),
        ["mobile hotspot"] = ("ms-settings:network-mobilehotspot", "Mobile hotspot"),
        ["vpn"] = ("ms-settings:network-vpn", "VPN"),
        ["night light"] = ("ms-settings:nightlight", "Night light"),
        ["display"] = ("ms-settings:display", "Display"),
        ["sound"] = ("ms-settings:sound", "Sound"), ["audio"] = ("ms-settings:sound", "Sound"),
        ["network"] = ("ms-settings:network", "Network"), ["internet"] = ("ms-settings:network", "Network"),
        ["notifications"] = ("ms-settings:notifications", "Notifications"),
        ["do not disturb"] = ("ms-settings:notifications", "Notifications"),
        ["focus"] = ("ms-settings:quiethours", "Focus"),
        ["battery"] = ("ms-settings:powersleep", "Power & battery"), ["power"] = ("ms-settings:powersleep", "Power & battery"),
        ["storage"] = ("ms-settings:storagesense", "Storage"),
        ["windows update"] = ("ms-settings:windowsupdate", "Windows Update"), ["update"] = ("ms-settings:windowsupdate", "Windows Update"),
        ["personalization"] = ("ms-settings:personalization", "Personalization"),
        ["wallpaper"] = ("ms-settings:personalization-background", "Background"),
        ["background"] = ("ms-settings:personalization-background", "Background"),
        ["mouse"] = ("ms-settings:mousetouchpad", "Mouse"), ["touchpad"] = ("ms-settings:devices-touchpad", "Touchpad"),
        ["privacy"] = ("ms-settings:privacy", "Privacy"),
        ["date and time"] = ("ms-settings:dateandtime", "Date & time"), ["time"] = ("ms-settings:dateandtime", "Date & time"),
        ["language"] = ("ms-settings:regionlanguage", "Language"),
        ["default apps"] = ("ms-settings:defaultapps", "Default apps"),
        ["startup apps"] = ("ms-settings:startupapps", "Startup apps"),
        ["installed apps"] = ("ms-settings:appsfeatures", "Installed apps"),
        ["about"] = ("ms-settings:about", "About this PC"),
    };

    /// <summary>These only count as a settings request when "settings" (or on/off) is actually typed.</summary>
    static readonly HashSet<string> NeedsSuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        "display", "sound", "audio", "network", "internet", "notifications", "focus", "battery", "power", "storage",
        "update", "personalization", "background", "mouse", "privacy", "time", "language", "about",
    };

    static readonly Dictionary<string, Environment.SpecialFolder> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["documents"] = Environment.SpecialFolder.MyDocuments, ["desktop"] = Environment.SpecialFolder.DesktopDirectory,
        ["pictures"] = Environment.SpecialFolder.MyPictures, ["music"] = Environment.SpecialFolder.MyMusic,
        ["videos"] = Environment.SpecialFolder.MyVideos,
    };

    /// <summary>"open notepad" searches for "notepad": the verb is not part of the name.</summary>
    public static string SearchText(string query)
    {
        var m = OpenVerb.Match(query.Trim());
        return m.Success ? m.Groups["rest"].Value.Trim() : query;
    }

    public static string Normalize(string query)
    {
        string t = query.Trim().TrimEnd('?', '!', '.').ToLowerInvariant();
        t = Filler.Replace(t, " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    public static ResultItem? TryParse(string query)
    {
        string t = Normalize(query);
        if (t.Length < 3) return null;
        Match m;

        // ----- theme -----
        if (DarkToggle.IsMatch(t)) return Theme(!SystemControl.IsDarkMode);
        if (DarkOn.IsMatch(t)) return Theme(true);
        if (DarkOff.IsMatch(t)) return Theme(false);

        // ----- volume -----
        if ((m = VolUp.Match(t)).Success) return VolumeBy(+Number(m, 10));
        if ((m = VolDown.Match(t)).Success) return VolumeBy(-Number(m, 10));
        if ((m = VolSet.Match(t)).Success) return VolumeTo(Number(m, 50));
        if (VolMax.IsMatch(t)) return VolumeTo(100);
        if (Mute.IsMatch(t))
            return Row("Mute", "Silence the default playback device", "", () => SystemControl.SetMute(true));
        if (Unmute.IsMatch(t))
            return Row("Unmute", "Turn sound back on", "", () => SystemControl.SetMute(false));

        // ----- brightness -----
        if ((m = BriUp.Match(t)).Success) return Brightness(null, +Number(m, 10));
        if ((m = BriDown.Match(t)).Success) return Brightness(null, -Number(m, 10));
        if ((m = BriSet.Match(t)).Success) return Brightness(Math.Clamp(Number(m, 50), 0, 100), 0);

        // ----- Wi-Fi and Bluetooth -----
        if ((m = RadioOn.Match(t)).Success) return RadioRow(Kind(m), true);
        if ((m = RadioOff.Match(t)).Success) return RadioRow(Kind(m), false);
        if ((m = RadioToggle.Match(t)).Success && RadioControl.IsOn(Kind(m)) is bool isOn) return RadioRow(Kind(m), !isOn);

        // ----- shut down, restart, sign out, hibernate -----
        if (ShutDown.IsMatch(t))
            return Confirm("Shut down this PC", "Shut down this PC? Unsaved work in open apps could be lost.",
                "Yes, shut down", "", () => SystemControl.Power("/s /t 0"));
        if (Restart.IsMatch(t))
            return Confirm("Restart this PC", "Restart this PC? Unsaved work in open apps could be lost.",
                "Yes, restart", "", () => SystemControl.Power("/r /t 0"));
        if (SignOut.IsMatch(t))
            return Confirm("Sign out of Windows", "Sign out? This closes all your open apps.",
                "Yes, sign out", "", () => SystemControl.Power("/l"));
        if (Hibernate.IsMatch(t))
            return Confirm("Hibernate this PC", "Hibernate this PC? Open apps are saved and restored when you turn it back on.",
                "Yes, hibernate", "", () => SystemControl.Power("/h"));

        // ----- power, media, misc -----
        if (LockPc.IsMatch(t)) return Row("Lock this PC", "Go to the lock screen", "", SystemControl.Lock);
        if (SleepPc.IsMatch(t)) return Row("Put this PC to sleep", "", "", SystemControl.Sleep);
        if (RecycleBin.IsMatch(t))
            return Row("Empty the Recycle Bin", "Windows will ask you to confirm first", "", SystemControl.EmptyRecycleBin);
        if (Screenshot.IsMatch(t))
            return Row("Take a screenshot", "Opens the Windows snipping overlay", "", () => SystemControl.OpenUri("ms-screenclip:"));
        if (PlayPause.IsMatch(t))
            return Row("Play / pause media", "", "", () => SystemControl.PressKey(SystemControl.VK_MEDIA_PLAY_PAUSE));
        if (NextTrack.IsMatch(t))
            return Row("Next track", "", "", () => SystemControl.PressKey(SystemControl.VK_MEDIA_NEXT));
        if (PrevTrack.IsMatch(t))
            return Row("Previous track", "", "", () => SystemControl.PressKey(SystemControl.VK_MEDIA_PREV));
        if ((m = Timer.Match(t)).Success) return MakeTimer(m);

        // ----- open several things at once -----
        if ((m = OpenVerb.Match(t)).Success && OpenMany(m.Groups["rest"].Value) is { } many) return many;

        // ----- settings pages -----
        string target = OpenVerb.Match(t) is { Success: true } o ? o.Groups["rest"].Value : t;
        if ((m = SettingsPage.Match(target)).Success && SettingsPages.TryGetValue(m.Groups["p"].Value.Trim(), out var page))
        {
            bool hasSuffix = m.Value.Length != m.Groups["p"].Value.Length;
            if (hasSuffix || !NeedsSuffix.Contains(m.Groups["p"].Value.Trim()))
                return Row($"Open {page.Name} settings", "Windows Settings", "", () => SystemControl.OpenUri(page.Uri));
        }

        return null;
    }

    // ---------- suggestions while typing ----------

    /// <param name="Phrase">What gets typed for the user. A trailing space means a value still has to follow.</param>
    sealed record Hint(string Phrase, string Title, string Example, string Glyph);

    static readonly Hint[] Hints =
    {
        new("files ", "Search only files …", "files budget   (or “> budget”)", ""),
        new("emoji ", "Search emoji …", "emoji party   (or “:party”)", ""),
        new("set volume to ", "Set volume to …", "set volume to 50", ""),
        new("set brightness to ", "Set brightness to …", "set brightness to 60", ""),
        new("set a timer for ", "Set a timer for …", "set a timer for 10 minutes", ""),
        new("increase volume by ", "Increase volume by …", "increase volume by 20", ""),
        new("increase brightness by ", "Increase brightness by …", "increase brightness by 20", ""),
        new("decrease volume by ", "Decrease volume by …", "decrease volume by 20", ""),
        new("decrease brightness by ", "Decrease brightness by …", "decrease brightness by 20", ""),
        new("turn on dark mode", "", "", ""),
        new("turn off dark mode", "", "", ""),
        new("toggle wifi", "", "", ""),
        new("toggle bluetooth", "", "", ""),
        new("shut down", "", "", ""),
        new("restart", "", "", ""),
        new("sign out", "", "", ""),
        new("mute", "", "", ""),
        new("unmute", "", "", ""),
        new("take a screenshot", "", "", ""),
        new("lock", "", "", ""),
        new("sleep", "", "", ""),
        new("pause music", "", "", ""),
        new("next song", "", "", ""),
        new("previous song", "", "", ""),
        new("empty recycle bin", "", "", ""),
    };

    /// <summary>
    /// Commands the partial query could become, so "set" already shows "Set volume to …".
    /// Value-taking commands complete the text; the rest are ready to run.
    /// </summary>
    public static List<ResultItem> Suggest(string query, int max)
    {
        var list = new List<ResultItem>();
        string t = Normalize(query);
        if (t.Length < 2) return list;

        var ranked = Hints
            .Select((h, i) => (h, i, rank: MatchRank(h.Phrase.TrimEnd(), t)))
            .Where(x => x.rank > 0)
            .OrderByDescending(x => x.rank).ThenBy(x => x.i)
            .Take(max);

        foreach (var (h, _, _) in ranked)
        {
            if (h.Phrase.EndsWith(' '))
            {
                list.Add(new ResultItem
                {
                    Title = h.Title,
                    Subtitle = "For example: " + h.Example,
                    Kind = "Command  ·  Enter to fill in",
                    Glyph = h.Glyph,
                    Completion = h.Phrase,
                });
            }
            else if (TryParse(h.Phrase) is { } cmd)
            {
                list.Add(new ResultItem
                {
                    Title = cmd.Title, Subtitle = cmd.Subtitle, Kind = "Command", Glyph = cmd.Glyph, Action = cmd.Action,
                    GetActions = cmd.GetActions, ActionsTitle = cmd.ActionsTitle,
                });
            }
        }
        return list;
    }

    /// <summary>2 = the phrase starts with what was typed; 1 = a later word in it does ("vol", "bright", "timer").</summary>
    static int MatchRank(string phrase, string typed)
    {
        if (phrase.StartsWith(typed, StringComparison.Ordinal)) return 2;
        if (typed.Length >= 3 && phrase.Contains(" " + typed, StringComparison.Ordinal)) return 1;
        return 0;
    }

    // ---------- row builders ----------

    static int Number(Match m, int fallback) =>
        m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out int n) ? n : fallback;

    static ResultItem Row(string title, string subtitle, string glyph, Action action) => new()
    {
        Title = title,
        Subtitle = subtitle,
        Kind = "Command  ·  Enter to run",
        Glyph = glyph,
        IsAnswer = true,
        Action = action,
    };

    static RadioKind Kind(Match m) =>
        m.Groups["r"].Value.StartsWith("blue", StringComparison.OrdinalIgnoreCase) ? RadioKind.Bluetooth : RadioKind.WiFi;

    static ResultItem RadioRow(RadioKind kind, bool on)
    {
        string name = RadioControl.Name(kind);
        bool? current = RadioControl.IsOn(kind);
        string state = current == null ? "" : current == on ? $"{name} is already {(on ? "on" : "off")}" : $"Currently {(current.Value ? "on" : "off")}";
        if (kind == RadioKind.WiFi && !on && current != false)
            state += (state.Length > 0 ? "  ·  " : "") + "this PC will go offline";

        return Row($"Turn {(on ? "on" : "off")} {name}", state, kind == RadioKind.WiFi ? "" : "", () => Task.Run(async () =>
        {
            if (await RadioControl.SetAsync(kind, on) is { } error)
                App.Notify($"Couldn't turn {(on ? "on" : "off")} {name}", error);
        }));
    }

    /// <summary>A command that never runs on the first Enter: it opens a Cancel / Yes step, with Cancel selected.</summary>
    static ResultItem Confirm(string title, string question, string yesLabel, string glyph, Action action) => new()
    {
        Title = title,
        Subtitle = "Asks you to confirm first",
        Kind = "Command  ·  Enter to continue",
        Glyph = glyph,
        IsAnswer = true,
        ActionsTitle = question,
        GetActions = () => new List<ResultItem>
        {
            new() { Title = "Cancel", Glyph = "", IsBack = true },
            new() { Title = yesLabel, Glyph = glyph, Action = action },
        },
    };

    static ResultItem Theme(bool toDark)
    {
        bool already = SystemControl.IsDarkMode == toDark;
        string mode = toDark ? "dark" : "light";
        return Row(
            toDark ? "Turn on dark mode" : "Turn off dark mode",
            already ? $"Windows is already in {mode} mode" : $"Switch Windows and apps to the {mode} theme",
            toDark ? "" : "",
            () => SystemControl.SetDarkMode(toDark));
    }

    static ResultItem? VolumeBy(int delta)
    {
        if (SystemControl.Volume is not int current) return null;
        return VolumeRow(current, Math.Clamp(current + delta, 0, 100));
    }

    static ResultItem? VolumeTo(int percent)
    {
        if (SystemControl.Volume is not int current) return null;
        return VolumeRow(current, Math.Clamp(percent, 0, 100));
    }

    static ResultItem VolumeRow(int current, int target) => Row(
        $"Set volume to {target}%",
        target == current ? $"Volume is already {current}%" : $"Currently {current}%",
        target == 0 ? "" : "",
        () => SystemControl.SetVolume(target));

    static ResultItem Brightness(int? absolute, int delta) => Row(
        absolute is int a ? $"Set brightness to {a}%" : $"{(delta > 0 ? "Increase" : "Decrease")} brightness by {Math.Abs(delta)}%",
        "Built-in display",
        "",
        () => Task.Run(() =>
        {
            if (SystemControl.ChangeBrightness(absolute, delta) == null)
                App.Notify("Couldn't change brightness", SystemControl.LastBrightnessError ?? "Unknown error.");
        }));

    static ResultItem MakeTimer(Match m)
    {
        double n = double.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        char u = char.ToLowerInvariant(m.Groups["u"].Value[0]);
        var span = u == 's' ? TimeSpan.FromSeconds(n) : u == 'm' ? TimeSpan.FromMinutes(n) : TimeSpan.FromHours(n);
        string unit = u == 's' ? "second" : u == 'm' ? "minute" : "hour";
        string label = $"{n:0.##} {unit}{(n == 1 ? "" : "s")}";

        return Row($"Start a {n:0.##}-{unit} timer",$"Notifies you at {DateTime.Now + span:t}", "", () =>
        {
            _ = Task.Delay(span).ContinueWith(_ => App.Notify("Timer finished", $"Your {label} timer is up."));
            App.Notify("Timer started", $"{label}, ends at {DateTime.Now + span:t}.");
        });
    }

    /// <summary>"notepad and spotify" → one row that launches every item it could resolve.</summary>
    static ResultItem? OpenMany(string rest)
    {
        var names = ListSplit.Split(rest).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (names.Length < 2) return null;

        var found = new List<(string Name, Action Run)>();
        var missing = new List<string>();
        foreach (string name in names)
        {
            if (Resolve(name) is { } hit) found.Add(hit);
            else missing.Add(name);
        }
        if (found.Count == 0) return null;

        string list = found.Count == 1 ? found[0].Name
            : string.Join(", ", found.Take(found.Count - 1).Select(f => f.Name)) + " and " + found[^1].Name;
        return Row(
            "Open " + list,
            missing.Count > 0 ? "Nothing found for: " + string.Join(", ", missing) : $"{found.Count} items",
            "",
            () =>
            {
                foreach (var (_, run) in found)
                    try { run(); } catch { }
            });
    }

    static (string Name, Action Run)? Resolve(string name)
    {
        if (name.EndsWith(" folder", StringComparison.OrdinalIgnoreCase)) name = name[..^7];

        if (name.Equals("downloads", StringComparison.OrdinalIgnoreCase))
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return ("Downloads", () => SystemControl.OpenUri(path));
        }
        if (Folders.TryGetValue(name, out var folder))
        {
            string path = Environment.GetFolderPath(folder);
            return (char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant(), () => SystemControl.OpenUri(path));
        }
        string settingsKey = name.EndsWith(" settings", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
        if (settingsKey.Length != name.Length && SettingsPages.TryGetValue(settingsKey, out var page))
            return (page.Name + " settings", () => SystemControl.OpenUri(page.Uri));

        var app = AppIndex.Search(name, 1, strict: true).FirstOrDefault();
        return app?.Action != null ? (app.Title, app.Action) : null;
    }
}
