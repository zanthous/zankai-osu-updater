namespace RulesetUpdater;

public sealed record ClientCompatibility(AssemblyInfo Game, string Directory)
{
    public static ClientCompatibility? Read(string? clientAssembly)
    {
        if (string.IsNullOrEmpty(clientAssembly)) return null;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(clientAssembly));
        if (directory == null) return null;
        string game = UpdateStorage.Child(directory, "osu.Game.dll");
        return File.Exists(game) ? new ClientCompatibility(AssemblyInfo.Read(game), directory) : null;
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
            string path = UpdateStorage.Child(Directory, reference.Key + ".dll");
            if (!File.Exists(path) || AssemblyInfo.Read(path).Version < reference.Value)
                return $"This DLL needs a newer or missing {reference.Key} dependency.";
        }
        return null;
    }
}
