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
            (string? appImage, string? appDir) = detectAppImage(entry.Location);
            var session = new ClientSession
            {
                ProcessId = process.Id,
                ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ProcessStartClockTicks = ProcessIdentity.TryReadLinuxStartClockTicks(process.Id),
                BootId = ProcessIdentity.TryReadLinuxBootId(),
                Version = version,
                ExecutablePath = Environment.ProcessPath ?? "",
                AssemblyPath = entry.Location,
                OfficialBuild = true,
                AppImagePath = appImage,
            };
            string sessionPath = Path.Combine(work, $"session-{session.ProcessId}-{session.ProcessStartUtcTicks}.json");
            writeAtomic(sessionPath, JsonSerializer.Serialize(session));
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            scrubAppImageEnvironment(start, appDir);
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

    private static (string? AppImage, string? AppDir) detectAppImage(string assemblyPath)
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        string? appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appDir) || !ProcessIdentity.IsPathUnderDirectory(assemblyPath, appDir))
            return (null, null);
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        string? verifiedAppImage = !string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage) ? Path.GetFullPath(appImage) : null;
        return (verifiedAppImage, appDir);
    }

    private static void scrubAppImageEnvironment(ProcessStartInfo start, string? appDir)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(appDir)) return;
        start.Environment.Remove("APPIMAGE");
        start.Environment.Remove("APPDIR");
        start.Environment.Remove("ARGV0");
        start.Environment.Remove("OWD");
        if (start.Environment.TryGetValue("LD_LIBRARY_PATH", out string? ldPath) && !string.IsNullOrEmpty(ldPath))
        {
            string? cleaned = ProcessIdentity.StripDirectoryEntries(ldPath, appDir);
            if (string.IsNullOrEmpty(cleaned)) start.Environment.Remove("LD_LIBRARY_PATH");
            else start.Environment["LD_LIBRARY_PATH"] = cleaned;
        }
    }

    private static void writeAtomic(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
