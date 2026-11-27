using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RulesetUpdater;

internal static class ClientProcesses
{
    public static bool Running(ClientSession session)
    {
        if (session.ProcessId <= 0) return false;
        if (!ProcessIdentity.SameBoot(ProcessIdentity.TryReadLinuxBootId(), session.BootId)) return false;
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return MatchesProcess(process, session.ProcessStartUtcTicks, session.ProcessStartClockTicks);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // If access is denied, assume it is still running.
        catch { return true; }
    }

    internal static bool MatchesProcess(Process process, long startUtcTicks, long? startClockTicks = null)
    {
        if (process.HasExited) return false;
        if (OperatingSystem.IsLinux())
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{process.Id}/stat"); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return false; }
            long? actualClock = ProcessIdentity.ParseLinuxProcStatStartClockTicks(stat, out bool exited);
            if (exited) return false;
            // Kernel ticks settle it without Process.StartTime, whose boot-time estimate differs between processes.
            if (ProcessIdentity.ClockTicksMatch(actualClock, startClockTicks) is { } sameClockTicks) return sameClockTicks;
        }
        return ProcessIdentity.StartTimeMatches(process.StartTime.ToUniversalTime().Ticks, startUtcTicks, OperatingSystem.IsLinux());
    }

    public static bool AnyOsuRunning(UpdateStorage storage)
    {
        foreach (string path in Directory.EnumerateFiles(storage.WorkDirectory, "session-*.json"))
        {
            ClientSession? session;
            try { session = UpdateStorage.ReadJson<ClientSession>(path); }
            catch (JsonException) { continue; }
            catch (FileNotFoundException) { continue; }
            if (session != null && Running(session)) return true;
        }
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName;
                    if (name.Equals("osu!", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("osu", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("osu.Desktop", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return false;
    }

    public static void PruneDeadSessions(UpdateStorage storage)
    {
        foreach (string path in Directory.EnumerateFiles(storage.WorkDirectory, "session-*.json"))
        {
            try
            {
                ClientSession? session = UpdateStorage.ReadJson<ClientSession>(path);
                if (session != null && !Running(session)) File.Delete(path);
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string? ReadLiveAssemblyVersion(ClientSession session)
    {
        if (string.IsNullOrEmpty(session.AssemblyPath) || !File.Exists(session.AssemblyPath)) return null;
        AssemblyInfo info = AssemblyInfo.Read(session.AssemblyPath);
        return info.OfficialBuild ? ClientVersion.Parse(info.InformationalVersion) : null;
    }

    public static ClientSession SnapshotForPersistence(ClientSession session, ClientCompatibility? compatibility)
    {
        if (string.IsNullOrEmpty(session.AppImagePath) || !File.Exists(session.AppImagePath)) return session;
        (long size, string sha256) = FingerprintFile(session.AppImagePath);
        return session with
        {
            AppImageSize = size,
            AppImageSha256 = sha256,
            Compatibility = compatibility?.ToSnapshot(),
        };
    }

    public static (string Version, ClientCompatibility? Compatibility)? ReadVerifiedClient(ClientSession session)
    {
        if (ReadLiveAssemblyVersion(session) is { } liveVersion)
            return (liveVersion, ClientCompatibility.Read(session.AssemblyPath));
        if (!HasVerifiedAppImage(session)) return null;
        string? version = session.OfficialBuild ? ClientVersion.Parse(session.Version) : null;
        if (version == null) return null;
        ClientCompatibility? compatibility = ClientCompatibility.FromSnapshot(session.Compatibility);
        return (version, compatibility);
    }

    public static string? ReadClientVersion(ClientSession session) => ReadVerifiedClient(session)?.Version;

    internal static bool HasVerifiedAppImage(ClientSession session)
    {
        if (!session.OfficialBuild || ClientVersion.Parse(session.Version) != session.Version) return false;
        if (string.IsNullOrEmpty(session.AppImagePath) || session.AppImageSize is not > 0 || string.IsNullOrEmpty(session.AppImageSha256))
            return false;
        if (!File.Exists(session.AppImagePath)) return false;
        try
        {
            using var file = File.OpenRead(session.AppImagePath);
            if (file.Length != session.AppImageSize.Value) return false;
            string actual = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            return string.Equals(actual, session.AppImageSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static (long Size, string Sha256) FingerprintFile(string path)
    {
        using var file = File.OpenRead(path);
        return (file.Length, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
    }
}
