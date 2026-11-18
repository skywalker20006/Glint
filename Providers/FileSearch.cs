using System.Diagnostics;
using System.IO;

namespace Glint;

/// <summary>File and folder search backed by the Windows Search index (the same index Explorer uses).</summary>
internal static class FileSearch
{
    const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public static List<ResultItem> Search(string query, int max)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Clean).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return new();

        var excluded = Excluded();
        try
        {
            // Fast path: word-prefix match on the indexed file name. Fall back to a substring scan.
            var results = Run(BuildSql(words, max, prefix: true, excluded));
            if (results.Count == 0) results = Run(BuildSql(words, max, prefix: false, excluded));
            // The query already leaves excluded folders out; this is a safety net (e.g. differently cased paths).
            return results.Where(r => !IsExcluded(r.CopyText!, excluded)).ToList();
        }
        catch
        {
            return new(); // index unavailable (service stopped, etc.)
        }
    }

    /// <summary>Excluded folders from Settings, normalised to full paths without a trailing slash.</summary>
    static List<string> Excluded() => Settings.Data.ExcludedFolders
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .Select(f => { try { return Path.GetFullPath(f).TrimEnd('\\'); } catch { return f.TrimEnd('\\'); } })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    static bool IsExcluded(string path, List<string> excluded) => excluded.Any(f =>
        path.Equals(f, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));

    static string Clean(string w) =>
        new string(w.Where(c => c is not ('\'' or '"' or '%' or '[' or ']' or '*')).ToArray());

    static string BuildSql(string[] words, int max, bool prefix, List<string> excluded)
    {
        var conds = words.Select(w =>
            prefix && w.All(char.IsLetterOrDigit)
                ? $"CONTAINS(System.FileName, '\"{w}*\"')"
                : $"System.FileName LIKE '%{w}%'").ToList();

        // Leave out each excluded folder, everything under it, and the folder itself.
        foreach (string folder in excluded)
        {
            string f = folder.Replace("'", "''");
            conds.Add($"NOT SCOPE='file:{f}'");
            conds.Add($"System.ItemPathDisplay <> '{f}'");
        }

        return $"SELECT TOP {max} System.ItemPathDisplay, System.FileName, System.ItemTypeText, System.ItemType " +
               "FROM SystemIndex WHERE scope='file:' AND System.ItemType <> '.lnk' AND System.ItemType <> '.url' AND " +
               string.Join(" AND ", conds) +
               " ORDER BY System.Search.Rank DESC, System.DateModified DESC";
    }

    /// <summary>The Tab / → menu for a file or folder.</summary>
    static List<ResultItem> Actions(string path, bool isFolder)
    {
        string folder = isFolder ? path : Path.GetDirectoryName(path) ?? path;
        var list = new List<ResultItem>
        {
            new()
            {
                Title = "Open", Glyph = "",
                Action = () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }),
            },
        };
        if (!isFolder)
            list.Add(new()
            {
                Title = "Open with…", Glyph = "",
                Action = () =>
                {
                    var psi = new ProcessStartInfo("rundll32.exe") { UseShellExecute = false };
                    psi.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                },
            });
        list.Add(new()
        {
            Title = "Show in Explorer", Glyph = "",
            Action = () => Process.Start("explorer.exe", $"/select,\"{path}\""),
        });
        list.Add(new()
        {
            Title = "Copy path", Subtitle = path, Glyph = "",
            Action = () => { try { System.Windows.Clipboard.SetText(path); } catch { } },
        });
        list.Add(new()
        {
            Title = isFolder ? "Copy folder" : "Copy file", Subtitle = "Paste it into another folder with Ctrl+V", Glyph = "",
            Action = () =>
            {
                try { System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path }); }
                catch { }
            },
        });
        list.Add(new()
        {
            Title = "Open in Terminal", Subtitle = folder, Glyph = "",
            Action = () =>
            {
                var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(folder);
                Process.Start(psi);
            },
        });
        list.Add(new()
        {
            Title = "Move to Recycle Bin", Subtitle = "Can be restored from the Recycle Bin", Glyph = "",
            Action = () =>
            {
                const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
                const Microsoft.VisualBasic.FileIO.RecycleOption bin = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
                if (isFolder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, bin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, bin);
            },
        });
        return list;
    }

    static List<ResultItem> Run(string sql)
    {
        var list = new List<ResultItem>();
        dynamic conn = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection")!)!;
        dynamic rs = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Recordset")!)!;
        conn.Open(ConnectionString);
        try
        {
            rs.Open(sql, conn);
            while (!rs.EOF)
            {
                string? path = rs.Fields.Item("System.ItemPathDisplay").Value as string;
                string? name = rs.Fields.Item("System.FileName").Value as string;
                string? typeText = rs.Fields.Item("System.ItemTypeText").Value as string;
                string? type = rs.Fields.Item("System.ItemType").Value as string;
                rs.MoveNext();
                if (string.IsNullOrEmpty(path)) continue;

                bool isFolder = string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase);
                list.Add(new ResultItem
                {
                    Title = name ?? Path.GetFileName(path),
                    Subtitle = Path.GetDirectoryName(path) ?? "",
                    Kind = isFolder ? "Folder" : typeText ?? "File",
                    IconKey = path,
                    IconIsFile = true,
                    Action = () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }),
                    SecondaryAction = () => Process.Start("explorer.exe", $"/select,\"{path}\""),
                    CopyText = path,
                    GetActions = () => Actions(path, isFolder),
                });
            }
            rs.Close();
        }
        finally { conn.Close(); }
        return list;
    }
}
