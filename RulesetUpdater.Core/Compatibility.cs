namespace RulesetUpdater;

public sealed record ClientCompatibility(AssemblyInfo Game, string? Directory, IReadOnlyDictionary<string, Version>? Assemblies = null)
{
    public static ClientCompatibility? Read(string? clientAssembly)
    {
        if (string.IsNullOrEmpty(clientAssembly)) return null;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(clientAssembly));
        if (directory == null) return null;
        string game = UpdateStorage.Child(directory, "osu.Game.dll");
        return File.Exists(game) ? new ClientCompatibility(AssemblyInfo.Read(game), directory) : null;
    }

    public static ClientCompatibility? FromSnapshot(ClientCompatibilitySnapshot? snapshot)
    {
        if (snapshot == null || string.IsNullOrEmpty(snapshot.TargetFramework) || string.IsNullOrEmpty(snapshot.RulesetApi))
            return null;
        var versions = new Dictionary<string, Version>(StringComparer.Ordinal);
        foreach (var (name, value) in snapshot.Assemblies)
            if (Version.TryParse(value, out Version? parsed)) versions[name] = parsed;
        var game = new AssemblyInfo("osu.Game", "", true)
        {
            RulesetApi = snapshot.RulesetApi,
            TargetFramework = snapshot.TargetFramework,
        };
        return new ClientCompatibility(game, null, versions);
    }

    public ClientCompatibilitySnapshot ToSnapshot()
    {
        var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Assemblies != null)
        {
            foreach (var pair in Assemblies.OrderBy(p => p.Key, StringComparer.Ordinal))
                assemblies[pair.Key] = pair.Value.ToString();
        }
        else if (!string.IsNullOrEmpty(Directory) && System.IO.Directory.Exists(Directory))
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith("osu.", StringComparison.Ordinal) && !name.StartsWith("ppy.", StringComparison.Ordinal))
                    continue;
                try { assemblies[name] = AssemblyInfo.Read(path).Version.ToString(); }
                catch (BadImageFormatException) { }
                catch (InvalidDataException) { }
                catch (IOException) { }
            }
        }
        return new ClientCompatibilitySnapshot
        {
            TargetFramework = Game.TargetFramework,
            RulesetApi = Game.RulesetApi,
            Assemblies = assemblies,
        };
    }

    public string? Rejection(AssemblyInfo ruleset)
    {
        if (Game.RulesetApi == null || ruleset.RulesetApi == null)
            return "Cannot verify the ruleset API without executing the DLL; leaving it untouched.";
        if (Game.RulesetApi != ruleset.RulesetApi)
            return $"Ruleset API {ruleset.RulesetApi} does not match this client's {Game.RulesetApi}.";
        const string frameworkPrefix = ".NETCoreApp,Version=v";
        static Version? framework(string value) => value.StartsWith(frameworkPrefix, StringComparison.Ordinal)
            && Version.TryParse(value[frameworkPrefix.Length..], out Version? version) ? version : null;
        Version? required = framework(ruleset.TargetFramework), available = framework(Game.TargetFramework);
        if (required == null || available == null || required > available)
            return "The DLL targets an unsupported or unknown .NET runtime.";
        foreach (var reference in ruleset.References.Where(r => r.Key.StartsWith("osu.", StringComparison.Ordinal) || r.Key.StartsWith("ppy.", StringComparison.Ordinal)))
        {
            if (Assemblies != null)
            {
                if (!Assemblies.TryGetValue(reference.Key, out Version? availableVersion) || availableVersion < reference.Value)
                    return $"This DLL needs a newer or missing {reference.Key} dependency.";
                continue;
            }
            if (string.IsNullOrEmpty(Directory))
                return $"This DLL needs a newer or missing {reference.Key} dependency.";
            string path = UpdateStorage.Child(Directory, reference.Key + ".dll");
            if (!File.Exists(path) || AssemblyInfo.Read(path).Version < reference.Value)
                return $"This DLL needs a newer or missing {reference.Key} dependency.";
        }
        return null;
    }
}
