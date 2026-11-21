using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Glint;

/// <summary>
/// "Switch to" results: open windows whose title or app name matches what was typed, like Alt+Tab with search.
/// </summary>
internal static class OpenWindows
{
    sealed record Window(IntPtr Handle, string Title, string App, string? ExePath);

    public static List<ResultItem> Search(string query, int max)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || query.Length < 2) return new();

        var hits = new List<(Window w, int score)>();
        foreach (var w in List())
        {
            string title = w.Title.ToLowerInvariant(), app = w.App.ToLowerInvariant();
            int score = 0;
            foreach (string word in words)
            {
                if (app.StartsWith(word) || title.StartsWith(word)) score += 3;
                else if (app.Contains(" " + word) || title.Contains(" " + word)) score += 2;
                else if (app.Contains(word) || title.Contains(word)) score += 1;
                else { score = 0; break; }
            }
            if (score > 0) hits.Add((w, score));
        }

        // The window list is already in Alt+Tab order, so equal scores keep the most recently used first.
        return hits.OrderByDescending(h => h.score).Take(max).Select(h => new ResultItem
        {
            Title = h.w.Title,
            Subtitle = h.w.App,
            Kind = "Switch to window",
            IconKey = h.w.ExePath,
            IconIsFile = true,
            ProgramPath = h.w.ExePath,
            Glyph = "",
            Action = () => Activate(h.w.Handle),
        }).ToList();
    }

    /// <summary>Top-level windows a person would switch to, most recently used first.</summary>
    static List<Window> List()
    {
        var list = new List<Window>();
        int self = Environment.ProcessId;
        var names = new Dictionary<int, (string app, string? path)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            long ex = GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */).ToInt64();
            if ((ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title is "Program Manager") return true;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == self) return true;
            // Store apps live inside ApplicationFrameHost; the real app is a child window from another process.
            if (ClassName(hwnd) == "ApplicationFrameWindow")
            {
                int host = pid;
                EnumChildWindows(hwnd, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out int childPid);
                    if (childPid != host) { pid = childPid; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            if (!names.TryGetValue(pid, out var info)) names[pid] = info = AppInfo(pid);
            list.Add(new Window(hwnd, title, info.app, info.path));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Reading a program's description means opening its file, so it's remembered per path.
    static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A friendly app name ("Google Chrome") and the program's path, for the icon.</summary>
    public static (string app, string? path) AppInfo(int pid)
    {
        string? path = ExePath(pid);
        string app = "";
        bool known;
        lock (Descriptions) known = path != null && Descriptions.TryGetValue(path, out app!);
        if (path != null && !known)
        {
            app = "";
            try { app = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
            if (app.Length == 0) app = Path.GetFileNameWithoutExtension(path);
            lock (Descriptions) Descriptions[path] = app;
        }
        else if (path == null)
        {
            try { app = Process.GetProcessById(pid).ProcessName; } catch { }
        }
        return (app, path);
    }

    public static string? ExePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Brings a window to the front, restoring it first if it's minimised.</summary>
    static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        // Windows only lets the foreground app hand focus on; a tap of Alt counts as input and lifts that limit.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}
