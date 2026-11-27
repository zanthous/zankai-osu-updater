using System.Globalization;

namespace RulesetUpdater;

// Shared as source so the bridge does not need another DLL beside it.
public sealed record ClientSession
{
    // Setters retain omitted-field defaults with .NET 8 source-generated JSON.
    public int Schema { get; set; } = 1;
    public int ProcessId { get; init; }
    public long ProcessStartUtcTicks { get; init; }
    public long? ProcessStartClockTicks { get; init; }
    public string? BootId { get; init; }
    public string Version { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public bool OfficialBuild { get; init; }
    public string? AppImagePath { get; init; }
    public long? AppImageSize { get; init; }
    public string? AppImageSha256 { get; init; }
    public ClientCompatibilitySnapshot? Compatibility { get; init; }
}

public sealed record ClientCompatibilitySnapshot
{
    public string TargetFramework { get; set; } = "";
    public string? RulesetApi { get; set; }
    public Dictionary<string, string> Assemblies { get; set; } = new(StringComparer.Ordinal);
}

public static class ProcessIdentity
{
    // Fallback tolerance when matching an older Linux bridge session that lacks kernel clock ticks.
    public static readonly long LinuxUtcTickTolerance = TimeSpan.FromSeconds(2).Ticks;

    public static long? TryReadLinuxStartClockTicks(int processId)
    {
        if (!OperatingSystem.IsLinux() || processId <= 0) return null;
        try
        {
            long? ticks = ParseLinuxProcStatStartClockTicks(File.ReadAllText($"/proc/{processId}/stat"), out bool exited);
            return exited ? null : ticks;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static long? ParseLinuxProcStatStartClockTicks(string stat, out bool exited)
    {
        exited = false;
        if (string.IsNullOrEmpty(stat)) return null;
        int close = stat.LastIndexOf(')');
        if (close < 0 || close + 2 >= stat.Length) return null;
        string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After ") ", fields[0] is field 3 (state) and fields[19] is field 22 (starttime in clock ticks since boot).
        if (fields.Length <= 19) return null;
        if (fields[0] is "Z" or "X" or "x") exited = true;
        return long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? ticks : null;
    }

    // Kernel clock ticks count from boot, so on their own they identify a process only within one boot.
    public static string? TryReadLinuxBootId()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string id = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return id.Length == 0 ? null : id;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // False only when both are known and differ: a process recorded during another boot cannot still be running.
    public static bool SameBoot(string? currentBootId, string? recordedBootId) =>
        string.IsNullOrEmpty(currentBootId) || string.IsNullOrEmpty(recordedBootId)
        || string.Equals(currentBootId, recordedBootId, StringComparison.Ordinal);

    // Null when either side lacks kernel clock ticks; the caller then compares UTC start times.
    public static bool? ClockTicksMatch(long? actualClockTicks, long? expectedClockTicks) =>
        actualClockTicks is { } actual && expectedClockTicks is { } expected ? actual == expected : null;

    public static bool StartTimeMatches(long actualUtcTicks, long expectedUtcTicks, bool linux) => linux
        ? Math.Abs(actualUtcTicks - expectedUtcTicks) <= LinuxUtcTickTolerance
        : actualUtcTicks == expectedUtcTicks;

    public static bool IsPathUnderDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedPath, normalizedDirectory, comparison)) return true;
        string prefix = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedDirectory : normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, comparison);
    }

    public static string? StripDirectoryEntries(string? pathList, string? directory)
    {
        if (string.IsNullOrEmpty(pathList) || string.IsNullOrWhiteSpace(directory)) return pathList;
        var kept = new List<string>();
        foreach (string entry in pathList.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.IsPathRooted(entry) && IsPathUnderDirectory(entry, directory)) continue;
            kept.Add(entry);
        }
        return kept.Count == 0 ? null : string.Join(':', kept);
    }
}
