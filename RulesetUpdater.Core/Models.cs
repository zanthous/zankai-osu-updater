using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RulesetUpdater;

public static partial class ClientVersion
{
    [GeneratedRegex(@"^\d{4}\.\d{1,4}\.\d+-(lazer|tachyon)$", RegexOptions.CultureInvariant)]
    private static partial Regex pattern();

    public static string? Parse(string? version)
    {
        string value = (version ?? "").Split('+')[0].Trim();
        return pattern().IsMatch(value) ? value : null;
    }
}

public sealed record ManagedRuleset(string Repository, string AssemblyName)
{
    public static readonly ManagedRuleset Sticks = new("zanthous/Sticks", "osu.Game.Rulesets.Sticks");
    // .NET 8 source-generated JSON overwrites missing init-only properties with
    // zero/null. Setters preserve these existing catalog defaults when omitted.
    public string Name { get; set; } = AssemblyName?.Replace("osu.Game.Rulesets.", "") ?? "";
    public string AssetPattern { get; set; } = "{assembly}-{client}.dll";
    public string[] Channels { get; set; } = ["lazer", "tachyon"];
    public bool Enabled { get; set; } = true;
    public bool InstallIfMissing { get; init; }
    public string? DisabledReason { get; init; }
    public bool ExactClient => AssetPattern.Contains("{client}", StringComparison.Ordinal);
    public string InstalledFileName => AssemblyName + ".dll";
    public string AssetName(string clientVersion) => AssetPattern.Replace("{assembly}", AssemblyName).Replace("{client}", clientVersion);
}

public sealed record ReleaseAsset
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public long Size { get; init; }
    public string? Digest { get; init; }
    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";
}

public sealed record Release
{
    public long Id { get; init; }
    [JsonPropertyName("tag_name")]
    public string Tag { get; set; } = "";
    [JsonPropertyName("published_at")]
    public DateTimeOffset PublishedAt { get; init; }
    public bool Draft { get; init; }
    public bool Prerelease { get; init; }
    public ReleaseAsset[] Assets { get; set; } = [];
}

public sealed record UpdateCandidate(string ReleaseTag, ReleaseAsset Asset, string ClientVersion);
public sealed record InstalledFile(string Name, string Sha256, string InformationalVersion);
public sealed record StagedUpdate(string ClientVersion, string ReleaseTag, string Sha256, string StagedFile, InstalledFile[] Originals);
public sealed record UpdateStatus(string State, string Message, DateTimeOffset UpdatedAt);

public static class ReleaseSelection
{
    public static UpdateCandidate? Select(IEnumerable<Release> releases, ManagedRuleset ruleset, string version)
    {
        if (ClientVersion.Parse(version) != version)
            throw new ArgumentException("An exact official lazer or Tachyon version is required.", nameof(version));

        if (!ruleset.Enabled || !ruleset.Channels.Contains(version.Split('-')[1])) return null;
        IEnumerable<Release> eligible = releases.Where(r => !r.Draft && !r.Prerelease).OrderByDescending(r => r.PublishedAt).ThenByDescending(r => r.Id);
        // An assetless latest release can be a discontinuation notice. Never resurrect an old generic build.
        if (!ruleset.ExactClient) eligible = eligible.Take(1);
        foreach (Release release in eligible)
        {
            ReleaseAsset[] assets = release.Assets.Where(a => a.Name == ruleset.AssetName(version)).ToArray();
            if (assets.Length > 1)
                throw new InvalidDataException("Release contains multiple matching DLLs.");
            if (assets.Length == 1)
                return new UpdateCandidate(release.Tag, assets[0], version);
        }
        return null;
    }

    public static bool IsNewerInstalledVersion(string installed, string release)
    {
        static (Version Version, string Suffix)? parse(string s)
        {
            var match = Regex.Match(s, @"^v?(\d+\.\d+(?:\.\d+)?)([a-z]*)(?:\+.*)?$", RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            Version parsed = Version.Parse(match.Groups[1].Value);
            return (new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)), match.Groups[2].Value);
        }
        var a = parse(installed);
        var b = parse(release);
        if (a == null || b == null) return false;
        int comparison = a.Value.Version.CompareTo(b.Value.Version);
        if (comparison != 0) return comparison > 0;
        comparison = a.Value.Suffix.Length.CompareTo(b.Value.Suffix.Length);
        return comparison != 0 ? comparison > 0 : string.CompareOrdinal(a.Value.Suffix, b.Value.Suffix) > 0;
    }
}
