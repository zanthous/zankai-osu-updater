using System.Diagnostics;
using System.Reflection;

namespace RulesetUpdater;

internal static class SelfUpdateRunner
{
    public static string Version => typeof(SelfUpdateRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    public static bool Available(UpdateStorage storage) => ExecutablePath.IsExpected(Environment.ProcessPath, UpdateStorage.Child(storage.RulesetsDirectory, SelfUpdate.HelperName))
        && File.Exists(UpdateStorage.Child(storage.RulesetsDirectory, SelfUpdate.BridgeName));

    public static void Start(UpdateStorage storage)
    {
        if (!Available(storage)) throw new InvalidOperationException("Self-update requires the packaged standalone helper beside its ruleset DLL.");
        string directory = UpdateStorage.Child(storage.WorkDirectory, "install-runner");
        Directory.CreateDirectory(directory);
        string runner = UpdateStorage.Child(directory, SelfUpdate.HelperName);
        // Windows cannot replace the running executable. A temporary copy performs the swap after we exit.
        SelfUpdate.CopyAtomic(Environment.ProcessPath!, runner, true);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (string arg in new[] { "--finish-self-update", "--rulesets", storage.RulesetsDirectory,
                     "--wait-process", current.Id.ToString(), "--wait-start", current.StartTime.ToUniversalTime().Ticks.ToString() })
            start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
    }

    public static async Task WaitForParent(int pid, long ticks)
    {
        if (pid <= 0 || ticks <= 0) throw new ArgumentException("Missing updater process identity.");
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
    }

    public static void CleanRunner(UpdateStorage storage)
    {
        string directory = UpdateStorage.Child(storage.WorkDirectory, "install-runner");
        if (!Directory.Exists(directory)) return;
        try
        {
            File.Delete(UpdateStorage.Child(directory, SelfUpdate.HelperName));
            Directory.Delete(directory);
        }
        catch (IOException) { /* A previous runner may still be exiting on Windows. */ }
        catch (UnauthorizedAccessException) { }
    }
}
