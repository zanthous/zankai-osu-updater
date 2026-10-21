namespace RulesetUpdater;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--verify-ui" })) return OperatingSystem.IsWindows() ? NativeWindowVerification.Run() : TerminalUiVerification.Run();
        if (!OpensWindow(args)) return RunCommandAsync(args).GetAwaiter().GetResult();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var updater = new DesktopUpdater(DefaultRulesetsDirectory, http);
        if (OperatingSystem.IsWindows()) return NativeWindow.Run(updater);
        if (TerminalUi.Interactive) return TerminalUi.Run(updater);
        if (TerminalUi.OpenInTerminal()) return 0;
        Console.WriteLine("Run ZankaiUpdater in a terminal to open the menu.\n");
        return RunCommandAsync(["--help"]).GetAwaiter().GetResult();
    }

    internal static bool OpensWindow(string[] args) => args.Length == 0;
}
