using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class DesktopUpdaterTests
{
    private string directory = null!;
    private UpdateStorage storage = null!;
    private HttpClient http = null!;
    private bool running;
    private const string version = "2026.1005.0-lazer";
    private static readonly ManagedRuleset fixture = new("test/ruleset", "osu.Game.Rulesets.UpdaterFixture");
    private RulesetCatalog catalog = null!;
    private byte[] bytes = null!;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "zankai-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        storage = new UpdateStorage(directory);
        running = false;
        catalog = new RulesetCatalog(1, []);
        bytes = File.ReadAllBytes(typeof(DesktopUpdaterTests).Assembly.Location);
        http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return json(catalog);
            if (request.RequestUri.Host == "api.github.com")
            {
                var asset = new ReleaseAsset
                {
                    Name = request.RequestUri.AbsolutePath.EndsWith("/latest") ? fixture.InstalledFileName : fixture.AssetName(version),
                    Size = bytes.Length, Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    DownloadUrl = $"https://github.com/{fixture.Repository}/releases/download/v1.1.0/fixture.dll",
                };
                var release = new Release { Tag = "v1.1.0", Assets = [asset] };
                return request.RequestUri.AbsolutePath.EndsWith("/latest") ? json(release) : json(new[] { release });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
    }

    [TearDown]
    public void TearDown() { http.Dispose(); Directory.Delete(directory, true); }
    private static HttpResponseMessage json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, UpdateStorage.Json)) };
    private DesktopUpdater updater() => new(directory, http, _ => running, _ => version, checkSelf: false);

    [TestCase("https://github.com/test/ruleset/releases")]
    [TestCase("https://github.com/test/ruleset/releases/latest")]
    [TestCase("https://github.com/test/ruleset/releases/tag/v1.0")]
    public void AcceptsGitHubReleasePageLinks(string url) => Assert.That(DesktopUpdater.RepositoryInput(url), Is.EqualTo("test/ruleset"));

    [Test]
    public async Task FindingMissingModeDoesNotEnrollOrInstallUntilConfirmed()
    {
        var app = updater();
        var found = await app.FindAsync("https://github.com/test/ruleset", default);
        Assert.That(found.Single().AssemblyName, Is.EqualTo(fixture.AssemblyName));
        Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
        Assert.That(File.Exists(Path.Combine(storage.WorkDirectory, "external-sources.json")), Is.False);
        Assert.That(Directory.GetDirectories(storage.WorkDirectory), Is.Empty);
        await app.AddAsync(found.Single(), default);
        var loaded = await new CatalogSource(http, storage).LoadAsync(default);
        Assert.That(loaded.Rulesets.Single().InstallIfMissing, Is.True);
        Assert.That(loaded.Rulesets.Single().Channels, Is.EqualTo(new[] { "lazer" }));
        Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
    }

    [Test]
    public async Task ExplicitCatalogEnrollmentPreservesMaintainedPolicy()
    {
        catalog = new RulesetCatalog(1, [fixture]);
        await updater().AddAsync(fixture with { AssetPattern = "{assembly}.dll", Channels = ["lazer"] }, default);
        var loaded = (await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single();
        Assert.That(loaded.InstallIfMissing, Is.True);
        Assert.That(loaded.AssetPattern, Is.EqualTo(fixture.AssetPattern));
        Assert.That(loaded.Channels, Is.EqualTo(fixture.Channels));
    }

    [Test]
    public async Task ExternalOptInCannotOverrideDisabledOrDifferentCatalogSource()
    {
        new CatalogSource(http, storage).AddExternal([fixture with { InstallIfMissing = true }]);
        catalog = new RulesetCatalog(1, [fixture with { Enabled = false, DisabledReason = "Discontinued" }]);
        Assert.ThrowsAsync<InvalidOperationException>(() => updater().AddAsync(fixture, default));
        var disabled = (await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single();
        Assert.That(disabled.Enabled, Is.False);
        Assert.That(disabled.InstallIfMissing, Is.False);
        catalog = new RulesetCatalog(1, [fixture with { Repository = "different/repo" }]);
        Assert.ThrowsAsync<InvalidOperationException>(() => updater().AddAsync(fixture, default));
        var maintained = (await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single();
        Assert.That(maintained.InstallIfMissing, Is.False);
        Assert.That(maintained.Repository, Is.EqualTo("different/repo"));
    }

    [Test]
    public async Task UpToDateCheckReportsZeroReadyUpdates()
    {
        catalog = new RulesetCatalog(1, [fixture]);
        File.WriteAllBytes(Path.Combine(directory, fixture.InstalledFileName), bytes);
        var result = await updater().CheckAsync(default);
        Assert.That(result.ReadyCount, Is.Zero);
        Assert.That(result.Summary, Is.EqualTo("Everything is up to date"));
        Assert.That(File.Exists(new Installer(storage, fixture).PendingPath), Is.False);
    }

    [Test]
    public async Task ConfirmedMissingModeInstallsOnlyAfterSeparateInstallAction()
    {
        catalog = new RulesetCatalog(1, [fixture]);
        var app = updater();
        await app.AddAsync(fixture, default);
        var result = await app.CheckAsync(default);
        Assert.That(result.ReadyCount, Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(directory, fixture.InstalledFileName)), Is.False);
        running = true;
        Assert.ThrowsAsync<InvalidOperationException>(() => app.InstallAsync(default));
        Assert.That(File.Exists(Path.Combine(directory, fixture.InstalledFileName)), Is.False);
        running = false;
        await app.InstallAsync(default);
        Assert.That(File.ReadAllBytes(Path.Combine(directory, fixture.InstalledFileName)), Is.EqualTo(bytes));
    }

    [Test]
    public async Task UnverifiedGenericSourceIsNotOfferedForInstallation()
    {
        var app = updater();
        await app.AddAsync(new ManagedRuleset(fixture.Repository, fixture.AssemblyName) { AssetPattern = "{assembly}.dll", Channels = ["lazer"] }, default);
        var result = await app.CheckAsync(default);
        Assert.That(result.ReadyCount, Is.Zero);
        Assert.That(result.Summary, Is.Not.EqualTo("Everything is up to date"));
        Assert.That(result.Details, Does.Contain("compatibility"));
        Assert.That(Directory.GetFiles(directory, "*.dll"), Is.Empty);
    }

    [Test]
    public void BackgroundWorkerLockDoesNotGetBypassed()
    {
        using var guard = new FileStream(Path.Combine(storage.WorkDirectory, "updater.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var error = Assert.ThrowsAsync<IOException>(() => updater().AddAsync(fixture, default));
        Assert.That(error!.Message, Does.Contain("Close osu!"));
        Assert.That(File.Exists(Path.Combine(storage.WorkDirectory, "external-sources.json")), Is.False);
    }
}
