using System.Diagnostics;
using System.IO;

namespace Glint;

/// <summary>
/// "Remove Glint from this PC": undoes everything Glint set up outside its own exe, so deleting Glint.exe
/// afterwards leaves nothing behind. What it removes:
///  - the "start when I sign in" entry,
///  - Glint's Claude Code hooks (only Glint's; the rest of Claude Code's settings stay) and Glint's backup file,
///  - Glint's data folder (%APPDATA%\Glint: settings, cached exchange rates, data-usage totals, logs),
///  - Skyline's reserved strip at the top of the screen (released when Glint quits).
/// Glint.exe itself is left for the user to delete: a program can't delete itself while it runs, and
/// silently deleting a file from wherever it was downloaded to would be surprising.
/// </summary>
internal static class Uninstaller
{
    /// <summary>Does the clean-up. Returns one line per step, for the summary shown before Glint quits.</summary>
    public static List<string> Run()
    {
        var report = new List<string>();

        try
        {
            if (Settings.LaunchAtStartup)
            {
                Settings.LaunchAtStartup = false;
                report.Add(Settings.LaunchAtStartup
                    ? "✗ Couldn't remove the \"start when I sign in\" entry."
                    : "✓ Glint no longer starts when you sign in.");
            }
        }
        catch (Exception ex) { report.Add("✗ Couldn't remove the \"start when I sign in\" entry: " + ex.Message); }

        try
        {
            if (ClaudeHooks.Check() != ClaudeHooks.Status.NotInstalled)
            {
                ClaudeHooks.Remove();
                report.Add("✓ Removed Glint's hooks from Claude Code's settings.");
            }
            foreach (string leftover in new[] { ClaudeHooks.BackupPath, ClaudeHooks.SettingsPath + ".glint-tmp" })
                if (File.Exists(leftover)) File.Delete(leftover);
        }
        catch (ClaudeHooks.RefusedException ex)
        {
            report.Add("✗ Glint's Claude Code hooks are still in " + ClaudeHooks.SettingsPath + " (" + ex.Message +
                       ") Remove the entries that run Glint.exe by hand.");
        }
        catch (Exception ex) { report.Add("✗ Couldn't remove Glint's Claude Code hooks: " + ex.Message); }

        // Nothing may write to the data folder from here on, or it would come back after being deleted.
        Settings.Frozen = true;
        try
        {
            // The folder delete must only ever hit a folder named exactly "Glint", several levels deep
            // (normally C:\Users\<you>\AppData\Roaming\Glint), never a drive or a top-level folder.
            string dir = Path.GetFullPath(Settings.Dir).TrimEnd('\\');
            int depth = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
            if (!Path.GetFileName(dir).Equals("Glint", StringComparison.OrdinalIgnoreCase) || depth < 4)
                throw new InvalidOperationException("its location looked wrong, so it was left alone");
            // Deleted a few seconds after Glint has fully exited, by a hidden command prompt.
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            };
            // Passed as one raw string: cmd.exe has its own quoting rules and mis-reads .NET's escaped quotes.
            psi.Arguments = $"/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{dir}\"";
            Process.Start(psi);
            report.Add("✓ Glint's settings and data (" + Settings.Dir + ") will be deleted once Glint closes.");
        }
        catch (Exception ex) { report.Add("✗ Couldn't delete " + Settings.Dir + ": " + ex.Message); }

        report.Add("✓ Skyline's space at the top of the screen is released when Glint closes.");
        return report;
    }

    /// <summary>After quitting, shows Glint.exe in Explorer so the user can delete it.</summary>
    public static void RevealExe()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{exe}\""); } catch { }
    }
}
