using System.Diagnostics;

namespace RulesetUpdater;

internal static class ClientProcesses
{
    public static bool Running(ClientSession session)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == session.ProcessStartUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // If access is denied, assume it is still running.
        catch { return true; }
    }

    public static bool AnyOsuRunning(UpdateStorage storage)
    {
        foreach (string path in Directory.EnumerateFiles(storage.WorkDirectory, "session-*.json"))
        {
            ClientSession? session = UpdateStorage.ReadJson<ClientSession>(path);
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

    public static string? ReadClientVersion(ClientSession session)
    {
        if (string.IsNullOrEmpty(session.AssemblyPath) || !File.Exists(session.AssemblyPath)) return null;
        AssemblyInfo info = AssemblyInfo.Read(session.AssemblyPath);
        return info.OfficialBuild ? ClientVersion.Parse(info.InformationalVersion) : null;
    }
}
