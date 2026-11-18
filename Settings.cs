using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Glint;

public sealed class SettingsData
{
    /// <summary>System.Windows.Input.ModifierKeys flags, e.g. "Control, Alt".</summary>
    public string HotkeyModifiers { get; set; } = "Control, Alt";
    /// <summary>System.Windows.Input.Key name, e.g. "Space".</summary>
    public string HotkeyKey { get; set; } = "Space";
    /// <summary>ISO code used when a currency query has no target. Empty = from Windows region.</summary>
    public string HomeCurrency { get; set; } = "";
    /// <summary>Folders whose contents never appear in file search results (subfolders included).</summary>
    public List<string> ExcludedFolders { get; set; } = new();

    // ----- appearance -----
    /// <summary>"System" follows Windows; otherwise "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Highlight colour as #RRGGBB, or empty for the neutral glass highlight.</summary>
    public string Accent { get; set; } = "";
    /// <summary>"Live" blurs what is behind the launcher in real time; "Snapshot" frosts a still image taken on open.</summary>
    public string GlassMode { get; set; } = "Live";
    // ----- Skyline (the menu bar docked to the top of the screen) -----
    public bool TopBarEnabled { get; set; } = true;
    /// <summary>"Blur" (clear live blur), "Acrylic" (tinted live blur with grain), "Mica" (tinted wallpaper), or "Solid".</summary>
    public string BarMaterial { get; set; } = "Blur";
    /// <summary>Spotify's current song, album art and a live waveform on the left of Skyline.</summary>
    public bool BarShowMedia { get; set; } = true;
    public bool BarShowSpeed { get; set; } = true;
    /// <summary>
    /// The Claude mascot: animates while Claude works, pulses orange when Claude is ready. Off by default,
    /// because turning it on adds hooks to Claude Code's settings, which the user has to agree to first.
    /// </summary>
    public bool BarShowClaude { get; set; } = false;
    public bool BarShowVolume { get; set; } = true;
    public bool BarShowWifi { get; set; } = true;
    public bool BarShowBluetooth { get; set; } = true;
    public bool BarShowBattery { get; set; } = true;
    /// <summary>A bell that opens the Windows notification centre.</summary>
    public bool BarShowNotifications { get; set; } = true;
    /// <summary>Backdrop blur, 1 (light) to 8 (heavy). Snapshot mode only; live blur strength is fixed by Windows.</summary>
    public int BlurStrength { get; set; } = 4;
    /// <summary>How opaque the tint over the blurred backdrop is, in percent. Lower = more see-through.</summary>
    public int GlassOpacity { get; set; } = 55;
    /// <summary>Panel width in device-independent pixels.</summary>
    public int PanelWidth { get; set; } = 680;
    /// <summary>Distance of the panel from the top of the screen, as a percentage of its height.</summary>
    public int TopPercent { get; set; } = 20;

    /// <summary>Launch counts per app, used to rank frequently used apps higher.</summary>
    public Dictionary<string, int> Usage { get; set; } = new();
}

internal static class Settings
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glint");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static SettingsData Data { get; private set; } = new();

    /// <summary>Set while Glint removes itself: nothing may write to the data folder any more.</summary>
    public static volatile bool Frozen;

    /// <summary>True when no settings file existed yet, i.e. the shortcut has never been chosen.</summary>
    public static bool FirstRun { get; private set; }

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            FirstRun = !File.Exists(FilePath);
            if (File.Exists(FilePath))
                Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Data = new(); }
    }

    public static void Save()
    {
        if (Frozen) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }

    public static string HomeCurrency
    {
        get
        {
            if (Data.HomeCurrency.Length == 3) return Data.HomeCurrency.ToUpperInvariant();
            try { return new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol; }
            catch { return "USD"; }
        }
    }

    public static void BumpUsage(string key)
    {
        Data.Usage[key] = Data.Usage.GetValueOrDefault(key) + 1;
        Save();
    }

    /// <summary>
    /// The "start when I sign in" entry. Only an entry Glint wrote counts as Glint's (an exe in quotes followed by
    /// --background, the exact form below), so another program that happened to use the name "Glint" is never
    /// reported as Glint's or deleted.
    /// </summary>
    public static bool LaunchAtStartup
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return IsGlintStartupEntry(k?.GetValue("Glint") as string);
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                string? existing = k.GetValue("Glint") as string;
                if (value)
                {
                    // Don't overwrite someone else's entry of the same name.
                    if (existing == null || IsGlintStartupEntry(existing))
                        k.SetValue("Glint", $"\"{Environment.ProcessPath}\" --background");
                }
                else if (IsGlintStartupEntry(existing))
                {
                    k.DeleteValue("Glint", false);
                }
            }
            catch { }
        }
    }

    static bool IsGlintStartupEntry(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^\"[^\"]+\\.exe\" --background$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
