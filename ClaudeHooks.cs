using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glint;

/// <summary>
/// Adds and removes the Claude Code hooks that tell Glint's mascot whether Claude is busy or ready.
///
/// Safety rules, all enforced here:
///  - Nothing is changed unless the user turned the mascot on and agreed (the Settings window asks).
///  - Only Glint's own hook entries are ever added or removed; everything else in the file stays as it is.
///  - A settings file that isn't valid JSON, or that contains comments rewriting would lose, is left untouched.
///  - The file is backed up before every change and replaced in one step, so it is never left half-written.
/// </summary>
internal static class ClaudeHooks
{
    /// <summary>
    /// The argument Glint's hooks pass to Glint.exe. Unique to Glint, so its hooks are recognised whatever the
    /// exe is called (people rename downloads, e.g. "Glint-v1.0.exe").
    /// </summary>
    public const string HookFlag = "--glint-claude-hook";
    /// <summary>The first version's flag; still accepted and recognised so older installs keep working and upgrade.</summary>
    public const string LegacyHookFlag = "--claude-hook";

    /// <summary>The events Glint listens to: prompt sent / tool about to run = busy, finished or needs you = ready.</summary>
    static readonly string[] Events = { "UserPromptSubmit", "PreToolUse", "Stop", "StopFailure", "Notification", "SessionEnd" };

    /// <summary>
    /// Only the notifications that mean Claude needs you: a permission prompt or a question. Claude Code's
    /// "still waiting for your input" reminder is left out, so a finished task doesn't pulse again a minute later.
    /// </summary>
    const string NotificationMatcher = "permission_prompt|elicitation_dialog";

    static string? MatcherFor(string ev) => ev == "Notification" ? NotificationMatcher : null;

    /// <summary>Claude Code's user settings file (honours CLAUDE_CONFIG_DIR, like Claude Code does).</summary>
    public static string SettingsPath
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static string BackupPath => SettingsPath + ".glint-backup";

    static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Glint.exe");

    public enum Status { NotInstalled, Installed, OutOfDate }

    /// <summary>Thrown when Glint decides not to touch the file; the message is shown to the user.</summary>
    public sealed class RefusedException(string message) : Exception(message);

    // ---------- the hook itself (runs inside the short-lived Glint.exe that Claude Code starts) ----------

    /// <summary>Records the event Claude Code reports on stdin, then exits. Never shows any UI.</summary>
    public static int RunHook()
    {
        try
        {
            if (!Console.IsInputRedirected) return 0;
            // Tool events can carry large inputs (a whole file being written); only the start matters here.
            string json = ReadCapped(Console.OpenStandardInput(), 256 * 1024);
            string? eventName = null, session = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                eventName = doc.RootElement.TryGetProperty("hook_event_name", out var e) ? e.GetString() : null;
                session = doc.RootElement.TryGetProperty("session_id", out var s) ? s.GetString() : null;
            }
            catch (JsonException)
            {
                // Truncated by the cap: pull the two fields out of the text instead.
                eventName = Field(json, "hook_event_name");
                session = Field(json, "session_id");
            }

            string file = Path.Combine(ClaudeStatus.Folder, SafeName(session) + ".state");
            Directory.CreateDirectory(ClaudeStatus.Folder);
            switch (eventName)
            {
                case "UserPromptSubmit" or "PreToolUse":
                    File.WriteAllText(file, "busy");
                    break;
                case "Stop" or "StopFailure" or "Notification":
                    File.WriteAllText(file, "ready");
                    break;
                case "SessionEnd":
                    File.Delete(file);
                    break;
            }
        }
        catch { /* a hook must never make Claude Code report an error */ }
        return 0;
    }

    /// <summary>
    /// Keeps the first <paramref name="max"/> bytes but reads the input to the end, so Claude Code never sees
    /// its write to the hook fail (exiting early would break the pipe while it is still sending).
    /// </summary>
    static string ReadCapped(Stream input, int max)
    {
        var buffer = new byte[max];
        int total = 0, n;
        while (total < max && (n = input.Read(buffer, total, max - total)) > 0) total += n;
        var discard = new byte[64 * 1024];
        while (input.Read(discard, 0, discard.Length) > 0) { }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    static string? Field(string json, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Session ids become file names, so only letters, digits, '-' and '_' are kept.</summary>
    static string SafeName(string? session)
    {
        var name = new string((session ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(80).ToArray());
        return name.Length > 0 ? name : "default";
    }

    // ---------- installing and removing ----------

    static JsonObject GlintHook() => new()
    {
        ["type"] = "command",
        ["command"] = ExePath,
        ["args"] = new JsonArray(HookFlag),
        ["async"] = true,
        ["timeout"] = 10,
    };

    /// <summary>
    /// Glint's own entries: the current hook (Glint.exe with the hook flag, at any install path), and the
    /// earlier shell version that wrote into %APPDATA%\Glint\claude.
    /// </summary>
    static bool IsGlintHook(JsonNode? hook)
    {
        if (hook is not JsonObject h) return false;
        string command = h["command"]?.GetValueKind() == JsonValueKind.String ? h["command"]!.GetValue<string>() : "";
        string firstArg = h["args"] is JsonArray a && a.Count > 0 && a[0]?.GetValueKind() == JsonValueKind.String
            ? a[0]!.GetValue<string>() : "";
        if (firstArg == HookFlag) return true;
        if (firstArg == LegacyHookFlag && Path.GetFileName(command).Equals("Glint.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return command.Contains("$APPDATA/Glint/claude", StringComparison.Ordinal);
    }

    static bool IsCurrentGlintHook(JsonNode? hook) =>
        IsGlintHook(hook) && hook!["command"]!.GetValue<string>().Equals(ExePath, StringComparison.OrdinalIgnoreCase)
        && hook["args"] is JsonArray a && a.Count > 0 && a[0]!.GetValue<string>() == HookFlag
        && hook["async"]?.GetValueKind() == JsonValueKind.True;

    public static Status Check()
    {
        try
        {
            var root = Load(out _);
            if (root["hooks"] is not JsonObject hooks) return Status.NotInstalled;
            bool any = false, allCurrent = true;
            foreach (string ev in Events)
            {
                var found = Entries(hooks, ev).ToList();
                any |= found.Count > 0;
                allCurrent &= found.Count == 1 && IsCurrentGlintHook(found[0].Hook)
                              && MatcherOf(found[0].Group) == MatcherFor(ev);
            }
            return !any ? Status.NotInstalled : allCurrent ? Status.Installed : Status.OutOfDate;
        }
        catch { return Status.NotInstalled; }
    }

    /// <summary>Glint's hook objects under one event, with the group (and so the matcher) each sits in.</summary>
    static IEnumerable<(JsonNode Group, JsonNode Hook)> Entries(JsonObject hooks, string ev)
    {
        if (hooks[ev] is not JsonArray groups) yield break;
        foreach (var group in groups)
            if (group?["hooks"] is JsonArray list)
                foreach (var hook in list)
                    if (IsGlintHook(hook)) yield return (group, hook!);
    }

    static string? MatcherOf(JsonNode group) =>
        group["matcher"]?.GetValueKind() == JsonValueKind.String ? group["matcher"]!.GetValue<string>() : null;

    /// <summary>Adds Glint's hooks (replacing any older Glint ones). Throws RefusedException if the file shouldn't be touched.</summary>
    public static void Install()
    {
        var known = CreatedSections();
        HashSet<string> created = new();
        Modify(root =>
        {
            created = new HashSet<string>(known);
            RemoveGlintEntries(root, created);
            var hooks = root["hooks"] as JsonObject;
            if (hooks == null)
            {
                if (root["hooks"] != null) throw new RefusedException("Its \"hooks\" section isn't in the expected format.");
                root["hooks"] = hooks = new JsonObject();
                created.Add(HooksSection);
            }
            foreach (string ev in Events)
            {
                if (hooks[ev] == null)
                {
                    hooks[ev] = new JsonArray();
                    created.Add(ev);
                }
                if (hooks[ev] is not JsonArray groups) throw new RefusedException($"Its \"{ev}\" hooks aren't in the expected format.");
                var group = new JsonObject();
                if (MatcherFor(ev) is { } matcher) group["matcher"] = matcher;
                group["hooks"] = new JsonArray(GlintHook());
                groups.Add(group);
            }
        });
        SaveCreatedSections(created);
    }

    /// <summary>Removes Glint's hooks, and the sections Glint itself created for them; nothing else.</summary>
    public static void Remove()
    {
        var created = CreatedSections();
        Modify(root => RemoveGlintEntries(root, new HashSet<string>(created)));
        SaveCreatedSections(null);
    }

    // ---------- remembering which sections Glint created ----------
    //
    // An event list such as "Stop": [] may have been in the file before Glint, or Glint may have added it for its
    // own hook. Once Glint's entry is in it, the file alone can't say which. So when Glint creates a section it
    // notes it here, and on removal only those notes allow an emptied section to be tidied away.

    const string HooksSection = "(hooks)";
    static string RecordPath => Path.Combine(Settings.Dir, "claude-hooks.json");

    static HashSet<string> CreatedSections()
    {
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(RecordPath));
            if (all != null && all.TryGetValue(SettingsPath.ToLowerInvariant(), out var list)) return new HashSet<string>(list);
        }
        catch { }
        return new HashSet<string>();
    }

    static void SaveCreatedSections(HashSet<string>? created)
    {
        if (Settings.Frozen) return;
        try
        {
            Dictionary<string, string[]> all;
            try { all = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(RecordPath)) ?? new(); }
            catch { all = new(); }
            string key = SettingsPath.ToLowerInvariant();
            if (created == null || created.Count == 0) all.Remove(key);
            else all[key] = created.OrderBy(s => s).ToArray();
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(RecordPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Removes Glint's hook entries. A hook group is tidied away only if taking Glint's entry out emptied it; an
    /// event list or the "hooks" section only if, on top of that, Glint created it (see <see cref="CreatedSections"/>).
    /// Anything that was there before Glint stays, even if it is empty. Sections Glint tidies away are dropped from
    /// <paramref name="created"/>.
    /// </summary>
    static void RemoveGlintEntries(JsonObject root, HashSet<string> created)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        bool removedAnEvent = false;
        foreach (var (ev, node) in hooks.ToList())
        {
            if (node is not JsonArray groups) continue;
            bool removedAGroup = false;
            for (int g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g]?["hooks"] is not JsonArray list) continue;
                bool removedHere = false;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!IsGlintHook(list[i])) continue;
                    list.RemoveAt(i);
                    removedHere = true;
                }
                if (removedHere && list.Count == 0)
                {
                    groups.RemoveAt(g);
                    removedAGroup = true;
                }
            }
            if (removedAGroup && groups.Count == 0 && created.Contains(ev))
            {
                hooks.Remove(ev);
                created.Remove(ev);
                removedAnEvent = true;
            }
        }
        if (removedAnEvent && hooks.Count == 0 && created.Contains(HooksSection))
        {
            root.Remove("hooks");
            created.Remove(HooksSection);
        }
    }

    /// <summary>
    /// Writes characters as they are ("&amp;&amp;", "&lt;", accented letters) instead of .NET's default \u escapes,
    /// so a rewritten file reads the same as the original apart from Glint's own entries.
    /// </summary>
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Reads, changes and safely writes the settings file. Does nothing if the change has no effect.</summary>
    static void Modify(Action<JsonObject> change)
    {
        string path = SettingsPath;
        for (int attempt = 0; ; attempt++)
        {
            var root = Load(out string? original);
            string before = root.ToJsonString();
            change(root);
            if (root.ToJsonString() == before) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string updated = root.ToJsonString(WriteOptions) + Environment.NewLine;

            // Make sure it reads back as the same JSON before going anywhere near the real file.
            if (JsonNode.Parse(updated)!.ToJsonString() != root.ToJsonString())
                throw new RefusedException("Glint couldn't produce a valid settings file, so nothing was changed.");

            string temp = path + ".glint-tmp";
            File.WriteAllText(temp, updated);

            // If Claude Code (or you) saved the file while Glint was working on it, start again from the new
            // version rather than overwrite that change.
            string? now = File.Exists(path) ? File.ReadAllText(path) : null;
            if (now != original)
            {
                File.Delete(temp);
                if (attempt < 3) { Thread.Sleep(150); continue; }
                throw new RefusedException("The file kept changing while Glint was editing it. Try again in a moment.");
            }

            if (original != null) File.WriteAllText(BackupPath, original);
            File.Move(temp, path, overwrite: true); // a single replace: never a half-written file
            return;
        }
    }

    /// <summary>
    /// Parses the settings file. A missing file is an empty object; anything Glint can't round-trip exactly
    /// (invalid JSON, comments, a non-object at the top) is refused.
    /// </summary>
    static JsonObject Load(out string? original)
    {
        string path = SettingsPath;
        original = File.Exists(path) ? File.ReadAllText(path) : null;
        if (original == null || original.Trim().Length == 0) return new JsonObject();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            bool hasComments;
            try
            {
                JsonNode.Parse(original, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                });
                hasComments = true;
            }
            catch (JsonException) { hasComments = false; }
            throw new RefusedException(hasComments
                ? "It contains comments, which Glint would lose by rewriting it."
                : "It isn't valid JSON right now, so Glint won't risk editing it.");
        }
        return node as JsonObject ?? throw new RefusedException("It isn't in the expected format.");
    }

    // ---------- keeping an opted-in install working ----------

    /// <summary>
    /// For someone who already opted in: if Glint moved (or the hooks are the older shell version), point them
    /// at this Glint.exe. Only Glint's own entries are touched. Failures are silent; Settings shows the status.
    /// </summary>
    public static void RepairIfOutOfDate()
    {
        try
        {
            if (Check() == Status.OutOfDate) Install();
        }
        catch { }
    }

    // ---------- command line (tests, installers) ----------

    public static int RunCommand(string command)
    {
        try
        {
            switch (command)
            {
                case "install": Install(); Console.WriteLine("installed"); break;
                case "remove": Remove(); Console.WriteLine("removed"); break;
                case "status": Console.WriteLine(Check()); break;
                default: Console.WriteLine("usage: --claude-hooks install|remove|status"); return 2;
            }
            return 0;
        }
        catch (RefusedException ex)
        {
            Console.WriteLine("refused: " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}
