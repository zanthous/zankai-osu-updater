using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RulesetUpdater;

public sealed class GitHubSource(HttpClient http)
{
    private const long max_dll_size = 32 * 1024 * 1024;
    public async Task<Release?> LatestAsync(string repository, CancellationToken cancellationToken)
    {
        RulesetCatalog.Repository(repository);
        using var request = Request($"https://api.github.com/repos/{repository}/releases/latest");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        Release? release = await JsonSerializer.DeserializeAsync<Release>(body, UpdateStorage.Json, cancellationToken);
        return release is { Draft: false, Prerelease: false } ? release : null;
    }

    public async Task<UpdateCandidate?> FindAsync(ManagedRuleset ruleset, string version, CancellationToken cancellationToken)
    {
        new RulesetCatalog(1, [ruleset]).Validate();
        if (!ruleset.Enabled || !ruleset.Channels.Contains(version.Split('-').Last())) return null;
        if (!ruleset.ExactClient)
        {
            Release? latest = await LatestAsync(ruleset.Repository, cancellationToken);
            return ReleaseSelection.Select(latest == null ? [] : [latest], ruleset, version);
        }
        // GitHub lists by creation time, so inspect all pages in the bounded search before sorting by publication.
        var releases = new List<Release>();
        for (int page = 1; page <= 5; page++)
        {
            using var request = Request($"https://api.github.com/repos/{ruleset.Repository}/releases?per_page=100&page={page}");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            Release[] batch = await JsonSerializer.DeserializeAsync<Release[]>(body, UpdateStorage.Json, cancellationToken) ?? [];
            releases.AddRange(batch);
            if (batch.Length < 100) break;
        }
        return ReleaseSelection.Select(releases, ruleset, version);
    }

    public static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Zankai-Updater/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    public static string ExpectedHash(ReleaseAsset asset)
    {
        string digest = asset.Digest ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("The release has no valid GitHub SHA-256 digest; it will not be installed.");
        return digest[7..].ToLowerInvariant();
    }

    public async Task DownloadAsync(ManagedRuleset ruleset, UpdateCandidate candidate, string destination, CancellationToken cancellationToken)
    {
        ReleaseAsset asset = candidate.Asset;
        if (asset.Name != ruleset.AssetName(candidate.ClientVersion)) throw new InvalidDataException("Unexpected asset name.");
        await DownloadAssetAsync(ruleset.Repository, asset, destination, max_dll_size, cancellationToken);
        if (AssemblyInfo.Read(destination).Name != ruleset.AssemblyName)
        {
            File.Delete(destination);
            throw new InvalidDataException("Downloaded DLL has the wrong assembly identity.");
        }
    }

    public async Task DownloadAssetAsync(string repository, ReleaseAsset asset, string destination, long maxSize, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        cancellationToken = timeout.Token;
        RulesetCatalog.Repository(repository);
        string hash = ExpectedHash(asset);
        if (asset.Size <= 0 || asset.Size > maxSize) throw new InvalidDataException("Invalid download size.");
        var url = new Uri(asset.DownloadUrl, UriKind.Absolute);
        string prefix = $"/{repository}/releases/download/";
        if (url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 || !url.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download does not belong to the configured GitHub repository.");

        bool created = false;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                created = true;
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > asset.Size) throw new InvalidDataException("Download exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (total != asset.Size) throw new InvalidDataException("Incomplete download.");
                output.Flush(true);
            }
            if (UpdateStorage.Hash(destination) != hash) throw new InvalidDataException("Downloaded DLL failed SHA-256 verification.");
        }
        catch { if (created && File.Exists(destination)) File.Delete(destination); throw; }
    }
}
