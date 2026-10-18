using System.Security.Cryptography;
using System.Text.Json;

namespace RulesetUpdater;

public sealed class UpdateStorage
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public string RulesetsDirectory { get; }
    public string WorkDirectory { get; }

    public UpdateStorage(string rulesetsDirectory)
    {
        RulesetsDirectory = Path.GetFullPath(rulesetsDirectory);
        if (!Directory.Exists(RulesetsDirectory)) throw new DirectoryNotFoundException(RulesetsDirectory);
        WorkDirectory = Path.Combine(RulesetsDirectory, ".ruleset-updater");
        Directory.CreateDirectory(WorkDirectory);
        RequireRegularPath(WorkDirectory);
    }

    public static void RequireRegularPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to update through a symbolic link: {Path.GetFileName(path)}");
    }

    public static string Child(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || Path.GetFileName(name) != name)
            throw new InvalidDataException("Invalid update filename.");
        string path = Path.Combine(directory, name);
        RequireRegularPath(path);
        return path;
    }

    public static T? ReadJson<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;

    public static void WriteJson<T>(string path, T value)
    {
        RequireRegularPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Json);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Status(string state, string message) => WriteJson(Path.Combine(WorkDirectory, "status.json"), new UpdateStatus(state, message, DateTimeOffset.UtcNow));
    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public InstalledFile[] FindInstalled(ManagedRuleset ruleset)
    {
        var result = new List<InstalledFile>();
        foreach (string path in Directory.EnumerateFiles(RulesetsDirectory).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dll.broken", StringComparison.OrdinalIgnoreCase)))
        {
            RequireRegularPath(path);
            AssemblyInfo info;
            try { info = AssemblyInfo.Read(path); }
            catch (BadImageFormatException) { continue; }
            catch (InvalidDataException) { continue; }
            if (info.Name == ruleset.AssemblyName)
                result.Add(new InstalledFile(Path.GetFileName(path), Hash(path), info.InformationalVersion));
        }
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
}
