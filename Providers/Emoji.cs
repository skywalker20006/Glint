using System.IO;
using System.Windows;

namespace Glint;

/// <summary>
/// Emoji search: ":fire" or "emoji party" lists matching emoji, and Enter copies one.
/// The list (Assets/emoji.txt) is made by tools/make_emoji_list.py.
/// </summary>
internal static class Emoji
{
    sealed record Entry(string Text, string Name, string[] Words, string[] Keywords, int Order);

    static Entry[]? _all;

    /// <summary>True for ":…" and "emoji …"; <paramref name="rest"/> is what to look for.</summary>
    public static bool IsEmojiQuery(string q, out string rest)
    {
        rest = "";
        if (q.StartsWith(':') && !q.StartsWith("::")) rest = q[1..].Trim();
        else if (q.Equals("emoji", StringComparison.OrdinalIgnoreCase) || q.Equals("emojis", StringComparison.OrdinalIgnoreCase)) { }
        else if (q.StartsWith("emoji ", StringComparison.OrdinalIgnoreCase)) rest = q[6..].Trim();
        else return false;
        return true;
    }

    public static List<ResultItem> Search(string query, int max)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entries = All();
        IEnumerable<Entry> hits = words.Length == 0
            ? entries
            : entries.Select(e => (e, score: Score(e, words)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.e.Order)
                .Select(x => x.e);

        return hits.Take(max).Select(e => new ResultItem
        {
            Title = char.ToUpperInvariant(e.Name[0]) + e.Name[1..],
            Subtitle = e.Keywords.Length > 0 ? string.Join(", ", e.Keywords.Take(4)) : "",
            Kind = "Emoji",
            IconKey = "emoji:" + e.Text,
            CopyText = e.Text,
            Action = () => { try { Clipboard.SetText(e.Text); } catch { } },
        }).ToList();
    }

    /// <summary>Every typed word must start a word of the name or a keyword. Name matches count for more.</summary>
    static int Score(Entry e, string[] typed)
    {
        int score = 0;
        foreach (string t in typed)
        {
            if (e.Words.Contains(t) || e.Keywords.Contains(t)) score += 3;
            else if (e.Words.Any(w => w.StartsWith(t, StringComparison.Ordinal))) score += 2;
            else if (e.Keywords.Any(k => k.StartsWith(t, StringComparison.Ordinal))) score += 1;
            else return 0;
        }
        // Prefer short, exact names: "fire" before "fire engine".
        if (string.Join(' ', typed) == e.Name) score += 3;
        return score;
    }

    static Entry[] All()
    {
        if (_all != null) return _all;
        var list = new List<Entry>();
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/emoji.txt"));
            using var reader = new StreamReader(info.Stream);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                list.Add(new Entry(parts[0], parts[1], parts[1].Split(' '),
                    parts.Length > 2 ? parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>(),
                    list.Count));
            }
        }
        catch { }
        return _all = list.ToArray();
    }
}
