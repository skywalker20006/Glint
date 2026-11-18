using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glint;

/// <summary>The Windows plumbing behind typed commands: theme, volume, brightness, power, media keys.</summary>
internal static class SystemControl
{
    // ---------- theme ----------

    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDarkMode
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Same two per-user values the Settings app writes, followed by the change broadcast.</summary>
    public static void SetDarkMode(bool dark)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
        {
            int light = dark ? 0 : 1;
            k.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        }
        SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */,
            IntPtr.Zero, "ImmersiveColorSet", 0x0002 /* SMTO_ABORTIFHUNG */, 300, out _);
    }

    // ---------- volume (Core Audio) ----------

    /// <summary>Master volume of the default playback device, 0-100, or null if there is none.</summary>
    public static int? Volume
    {
        get
        {
            try
            {
                var ep = Endpoint();
                return ep != null && ep.GetMasterVolumeLevelScalar(out float v) == 0 ? (int)Math.Round(v * 100) : null;
            }
            catch { return null; }
        }
    }

    public static void SetVolume(int percent)
    {
        var ep = Endpoint();
        if (ep == null) return;
        Guid ctx = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
        if (percent > 0) ep.SetMute(false, ref ctx);
    }

    public static bool? Muted
    {
        get
        {
            try
            {
                var ep = Endpoint();
                return ep != null && ep.GetMute(out bool m) == 0 ? m : null;
            }
            catch { return null; }
        }
    }

    public static void SetMute(bool mute)
    {
        Guid ctx = Guid.Empty;
        Endpoint()?.SetMute(mute, ref ctx);
    }

    /// <summary>Friendly name of the default playback device, e.g. "Speakers (Realtek(R) Audio)".</summary>
    public static string? OutputDeviceName
    {
        get
        {
            try
            {
                var device = DefaultDevice();
                if (device == null || device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0 || store == null) return null;
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                if (store.GetValue(ref key, out var value) != 0) return null;
                try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            catch { return null; }
        }
    }

    static IAudioMeterInformation? _meter;
    static DateTime _meterAt;

    /// <summary>
    /// Loudness of what's playing right now on the default speakers, 0..1. Cheap enough to read every frame.
    /// The device is looked up again every few seconds in case the default output changes.
    /// </summary>
    public static float OutputPeak
    {
        get
        {
            try
            {
                if (_meter == null || DateTime.UtcNow - _meterAt > TimeSpan.FromSeconds(5))
                {
                    _meterAt = DateTime.UtcNow;
                    var device = DefaultDevice();
                    Guid iid = typeof(IAudioMeterInformation).GUID;
                    _meter = device != null && device.Activate(ref iid, 23, IntPtr.Zero, out object o) == 0
                        ? (IAudioMeterInformation)o : null;
                }
                return _meter != null && _meter.GetPeakValue(out float peak) == 0 ? peak : 0;
            }
            catch
            {
                _meter = null;
                return 0;
            }
        }
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    static IMMDevice? DefaultDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) == 0 ? device : null;
    }

    static IAudioEndpointVolume? Endpoint()
    {
        var device = DefaultDevice();
        if (device == null) return null;
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        return device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) == 0
            ? (IAudioEndpointVolume)o
            : null;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore? store);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    // Method order must match the vtable in endpointvolume.h.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // ---------- brightness (WMI; built-in laptop panels only) ----------

    /// <summary>Sets brightness to an absolute level, or changes it by delta. Returns the new level, or null if unsupported.</summary>
    public static int? ChangeBrightness(int? absolute, int delta)
    {
        try
        {
            dynamic wmi = Marshal.BindToMoniker(@"winmgmts:\\.\root\wmi");
            int current = -1;
            // WMI objects must be read through Properties_ / ExecMethod_. The shorthand (b.CurrentBrightness,
            // m.WmiSetBrightness(...)) uses per-object dispatch ids that .NET caches, so it works once and
            // then fails on every later object.
            foreach (dynamic b in wmi.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            {
                current = Convert.ToInt32((object)b.Properties_.Item("CurrentBrightness").Value);
                break;
            }
            if (current < 0)
            {
                LastBrightnessError = "No display reports a brightness level.";
                return null;
            }

            int target = Math.Clamp(absolute ?? current + delta, 0, 100);
            foreach (dynamic m in wmi.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                dynamic input = m.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
                input.Properties_.Item("Timeout").Value = 1;
                input.Properties_.Item("Brightness").Value = (byte)target;
                m.ExecMethod_("WmiSetBrightness", input);
            }
            LastBrightnessError = null;
            return target;
        }
        catch (Exception ex)
        {
            LastBrightnessError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    public static string? LastBrightnessError { get; private set; }

    // ---------- power, media, misc ----------

    public static void Lock() => LockWorkStation();

    /// <summary>
    /// Runs Windows' own shutdown tool: "/s" shut down, "/r" restart, "/l" sign out, "/h" hibernate.
    /// It is never forced, so apps with unsaved work can still stop it and ask the user.
    /// </summary>
    public static void Power(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });

    public static void Sleep() => SetSuspendState(false, false, false);

    /// <summary>Windows shows its own "permanently delete?" confirmation before anything is removed.</summary>
    public static void EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0);

    public const byte VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static void PressKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2 /* key up */, UIntPtr.Zero);
    }

    public static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
