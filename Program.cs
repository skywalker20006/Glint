namespace Glint;

/// <summary>
/// Entry point. Claude Code hooks start Glint.exe in a status-only mode that records what Claude is doing
/// and exits straight away, without loading the UI, so it is quick and never shows the launcher.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == ClaudeHooks.HookFlag || args[0] == ClaudeHooks.LegacyHookFlag))
            return ClaudeHooks.RunHook();

        // Command-line hook management, used by tests and handy for installers:
        //   Glint.exe --claude-hooks install | remove | status
        if (args.Length > 1 && args[0] == "--claude-hooks")
            return ClaudeHooks.RunCommand(args[1]);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
