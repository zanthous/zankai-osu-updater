using System.Text.Json;
using System.Text.RegularExpressions;

namespace RulesetUpdater;

public sealed record RulesetCatalog(int Schema, ManagedRuleset[] Rulesets)
{
    public static string Repository(string value)
    {
        value = value.Trim();
        if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) value = value[19..].TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.Ordinal)) value = value[..^4];
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Use a GitHub owner/repository or repository URL.");
        return value;
    }

    public RulesetCatalog Validate()
    {
        if (Schema != 1 || Rulesets == null || Rulesets.Length > 100) throw new InvalidDataException("Unsupported ruleset catalog.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedRuleset r in Rulesets)
        {
            if (r == null || r.Repository == null || Repository(r.Repository) != r.Repository
                || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100
                || r.AssemblyName == null || !Regex.IsMatch(r.AssemblyName, @"^osu\.Game\.Rulesets\.[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)
                || r.AssemblyName.Equals("osu.Game.Rulesets.ZankaiUpdater", StringComparison.OrdinalIgnoreCase)
                || !names.Add(r.AssemblyName)
                || r.AssetPattern is not ("{assembly}.dll" or "{assembly}-{client}.dll")
                || r.Channels == null || r.Channels.Length == 0 || r.Channels.Any(c => c is not ("lazer" or "tachyon")))
                throw new InvalidDataException("Invalid or duplicate ruleset catalog entry.");
        }
        return this;
    }
}

public sealed class CatalogSource(HttpClient http, UpdateStorage storage)
{
    public const string UpdaterRepository = "zanthous/zankai-osu-updater";
    public const string Url = "https://raw.githubusercontent.com/" + UpdaterRepository + "/master/catalog/rulesets.json";
    public static RulesetCatalog Bundled()
    {
        using var resource = typeof(CatalogSource).Assembly.GetManifestResourceStream("rulesets.json")!;
        return (JsonSerializer.Deserialize(resource, UpdaterJsonContext.Default.RulesetCatalog) ?? throw new InvalidDataException("Missing catalog.")).Validate();
    }

    public async Task<RulesetCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        string cache = UpdateStorage.Child(storage.WorkDirectory, "catalog.json");
        RulesetCatalog catalog;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = GitHubSource.Request(Url);
            // Bound both the response size and the time spent reading the body.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + count > 256 * 1024) throw new InvalidDataException("Catalog is too large.");
                buffer.Write(chunk, 0, count);
            }
            catalog = (JsonSerializer.Deserialize(buffer.ToArray(), UpdaterJsonContext.Default.RulesetCatalog)
                       ?? throw new InvalidDataException("Empty catalog.")).Validate();
            UpdateStorage.WriteJson(cache, catalog);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            storage.Status("catalog-fallback", "Catalog refresh unavailable; using the last validated or bundled catalog.");
            try { catalog = UpdateStorage.ReadJson<RulesetCatalog>(cache)?.Validate() ?? Bundled(); }
            catch (Exception cached) when (cached is IOException or InvalidDataException or JsonException) { catalog = Bundled(); }
        }
        RulesetCatalog? external = UpdateStorage.ReadJson<RulesetCatalog>(UpdateStorage.Child(storage.WorkDirectory, "external-sources.json"))?.Validate();
        // A user may opt into installing a catalog mode. Its maintained source, compatibility
        // policy and disabled status still take precedence over all external-source data.
        var maintained = catalog.Rulesets.Select(c => c.Enabled && external?.Rulesets.Any(e => e.AssemblyName == c.AssemblyName
            && e.Repository.Equals(c.Repository, StringComparison.OrdinalIgnoreCase) && e.InstallIfMissing) == true
                ? c with { InstallIfMissing = true } : c);
        return new RulesetCatalog(1, maintained.Concat(external?.Rulesets.Where(e => !catalog.Rulesets.Any(c => c.AssemblyName == e.AssemblyName)) ?? []).ToArray()).Validate();
    }

    public static async Task<ManagedRuleset[]> DiscoverAsync(string repository, GitHubSource source, UpdateStorage storage, CancellationToken cancellationToken, bool includeMissing = false)
    {
        repository = RulesetCatalog.Repository(repository);
        Release? release = await source.LatestAsync(repository, cancellationToken);
        if (release == null) return [];
        var found = new List<ManagedRuleset>();
        foreach (ReleaseAsset asset in release.Assets.Where(a => Regex.IsMatch(a.Name, @"^osu\.Game\.Rulesets\.[A-Za-z][A-Za-z0-9_]*\.dll$")))
        {
            if (release.Assets.Count(a => a.Name == asset.Name) != 1) throw new InvalidDataException("Ambiguous DLL assets.");
            var ruleset = new ManagedRuleset(repository, Path.GetFileNameWithoutExtension(asset.Name)) { AssetPattern = "{assembly}.dll", Channels = ["lazer"] };
            new RulesetCatalog(1, [ruleset]).Validate();
            if (!includeMissing && storage.FindInstalled(ruleset).Length == 0) continue;
            GitHubSource.ExpectedHash(asset);
            found.Add(ruleset);
        }
        return found.ToArray();
    }

    public void AddExternal(IEnumerable<ManagedRuleset> rulesets)
    {
        string path = UpdateStorage.Child(storage.WorkDirectory, "external-sources.json");
        var existing = UpdateStorage.ReadJson<RulesetCatalog>(path)?.Validate().Rulesets ?? [];
        var added = rulesets.ToArray();
        var updated = new RulesetCatalog(1, existing.Where(e => !added.Any(a => a.AssemblyName == e.AssemblyName)).Concat(added).ToArray()).Validate();
        UpdateStorage.WriteJson(path, updated);
    }
}
