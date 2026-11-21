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
                if (ep != null && ep.GetMasterVolumeLevelScalar(out float v) == 0) return (int)Math.Round(v * 100);
                ResetAudio(); // stale or missing device: look it up afresh next time
                return null;
            }
            catch { ResetAudio(); return null; }
        }
    }

    public static void SetVolume(int percent)
    {
        Guid ctx = Guid.Empty;
        WithEndpoint(ep =>
        {
            int hr = ep.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
            if (hr == 0 && percent > 0) ep.SetMute(false, ref ctx);
            return hr;
        });
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
        WithEndpoint(ep => ep.SetMute(mute, ref ctx));
    }

    /// <summary>
    /// Runs a volume change on the default device. If the cached endpoint has gone stale (the default output
    /// changed, or an HDMI/USB device dropped out and came back) it is looked up again and tried once more.
    /// Never throws: a failed volume change must not take the app down.
    /// </summary>
    static void WithEndpoint(Func<IAudioEndpointVolume, int> action)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var ep = Endpoint();
                if (ep == null || action(ep) == 0) return;
            }
            catch { }
            ResetAudio();
        }
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

    // One enumerator per thread, made once. Creating a fresh one on every call (dozens a second while the
    // slider is dragged) can fail with InvalidCastException while Windows is rebuilding its audio endpoints,
    // e.g. right after an HDMI monitor with speakers is plugged in or wakes up.
    [ThreadStatic] static IMMDeviceEnumerator? _enumerator;
    [ThreadStatic] static IAudioEndpointVolume? _endpoint;
    [ThreadStatic] static DateTime _endpointAt;

    static void ResetAudio()
    {
        _enumerator = null;
        _endpoint = null;
    }

    static IMMDevice? DefaultDevice()
    {
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();
            return _enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) == 0 ? device : null;
        }
        catch
        {
            _enumerator = null;
            return null;
        }
    }

    /// <summary>Volume control of the default device, reused for a second so a slider drag doesn't re-resolve it per tick.</summary>
    static IAudioEndpointVolume? Endpoint()
    {
        if (_endpoint != null && DateTime.UtcNow - _endpointAt < TimeSpan.FromSeconds(1)) return _endpoint;
        _endpoint = null;
        var device = DefaultDevice();
        if (device == null) return null;
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0 || o is not IAudioEndpointVolume ep)
            return null;
        _endpointAt = DateTime.UtcNow;
        return _endpoint = ep;
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

    // ---------- per-app volume (audio sessions) ----------

    /// <summary>One app's own volume, as in Windows' volume mixer. An app can have several sessions; they move together.</summary>
    public sealed class AppVolume
    {
        readonly List<ISimpleAudioVolume> _controls;

        internal AppVolume(string name, string? exePath, bool isSystem, bool active, ISimpleAudioVolume control)
        {
            Name = name;
            ExePath = exePath;
            IsSystem = isSystem;
            Active = active;
            _controls = new() { control };
        }

        public string Name { get; }
        public string? ExePath { get; }
        /// <summary>Windows' own sounds (notifications, errors).</summary>
        public bool IsSystem { get; }
        /// <summary>Playing sound right now, rather than just open.</summary>
        public bool Active { get; internal set; }

        internal void Add(ISimpleAudioVolume control) => _controls.Add(control);

        public int Volume
        {
            get
            {
                try { return _controls[0].GetMasterVolume(out float v) == 0 ? (int)Math.Round(v * 100) : 0; }
                catch { return 0; }
            }
        }

        public bool Muted
        {
            get
            {
                try { return _controls[0].GetMute(out bool m) == 0 && m; }
                catch { return false; }
            }
        }

        public void SetVolume(int percent)
        {
            Guid ctx = Guid.Empty;
            foreach (var c in _controls)
            {
                try
                {
                    c.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
                    if (percent > 0) c.SetMute(false, ref ctx);
                }
                catch { /* the app closed */ }
            }
        }

        public void SetMute(bool mute)
        {
            Guid ctx = Guid.Empty;
            foreach (var c in _controls)
            {
                try { c.SetMute(mute, ref ctx); } catch { }
            }
        }
    }

    /// <summary>Apps with sound open on the default output, those playing first. Empty if Windows can't say.</summary>
    public static List<AppVolume> AppVolumes()
    {
        var byApp = new Dictionary<string, AppVolume>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var device = DefaultDevice();
            if (device == null) return new();
            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out object o) != 0 || o is not IAudioSessionManager2 manager) return new();
            if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return new();
            sessions.GetCount(out int count);
            int self = Environment.ProcessId;
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0 || session is not IAudioSessionControl2 control) continue;
                if (control.GetState(out int state) != 0 || state == 2 /* expired */) continue;
                if (session is not ISimpleAudioVolume volume) continue;

                bool isSystem = control.IsSystemSoundsSession() == 0;
                control.GetProcessId(out int pid);
                if (!isSystem && (pid == 0 || pid == self)) continue;

                string name, key;
                string? path = null;
                if (isSystem)
                {
                    name = "System sounds";
                    key = "system:";
                }
                else
                {
                    (name, path) = OpenWindows.AppInfo(pid);
                    if (control.GetDisplayName(out string? display) == 0 && !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@'))
                        name = display;
                    key = path ?? "pid:" + pid;
                }

                bool active = state == 1;
                if (byApp.TryGetValue(key, out var app))
                {
                    app.Add(volume);
                    app.Active |= active;
                }
                else byApp[key] = new AppVolume(name, path, isSystem, active, volume);
            }
        }
        catch { }
        return byApp.Values
            .OrderByDescending(a => a.Active).ThenBy(a => a.IsSystem).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
    }

    // Method order must match audiopolicy.h: IAudioSessionControl's nine methods, then IAudioSessionControl2's.
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string? name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string? path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
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
