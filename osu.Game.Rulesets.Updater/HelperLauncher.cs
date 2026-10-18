using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using osu.Framework.Logging;
using RulesetUpdater;

namespace osu.Game.Rulesets.Updater;

internal static class HelperLauncher
{
    private static int started;

    public static void StartOnce()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        try
        {
            Assembly? entry = Assembly.GetEntryAssembly();
            // Local client builds must not silently replace development rulesets with public releases.
            if (entry == null || !entry.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "osu.Game.Utils.OfficialBuildAttribute")) return;
            string version = (entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{4}\.\d{1,4}\.\d+-(lazer|tachyon)$")) return;
            string folder = Path.GetDirectoryName(typeof(UpdaterRuleset).Assembly.Location)!;
            string helper = Path.Combine(folder, OperatingSystem.IsWindows() ? "ZankaiUpdater.exe" : "ZankaiUpdater");
            if (!File.Exists(helper))
            {
                Logger.Log("Zankai Updater: place the updater program beside the DLL to enable automatic updates.");
                return;
            }
            string work = Path.Combine(folder, ".ruleset-updater");
            Directory.CreateDirectory(work);
            using var process = Process.GetCurrentProcess();
            var session = new ClientSession
            {
                ProcessId = process.Id,
                ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                Version = version,
                ExecutablePath = Environment.ProcessPath ?? "",
                AssemblyPath = entry.Location,
                OfficialBuild = true,
            };
            string sessionPath = Path.Combine(work, $"session-{session.ProcessId}-{session.ProcessStartUtcTicks}.json");
            File.WriteAllText(sessionPath, JsonSerializer.Serialize(session));
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            start.ArgumentList.Add("--watch");
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add(sessionPath);
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            // A missing helper, offline connection, or failed update must never crash osu!.
            Logger.Log($"Zankai Updater could not start: {e.Message}");
        }
    }
}
