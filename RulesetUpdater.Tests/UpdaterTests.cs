using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

// Metadata fixture: the updater reads the constant without loading or executing it.
public sealed class FixtureRuleset
{
    public string RulesetAPIVersionSupported => "2026.818.0";
}

[TestFixture]
public sealed class UpdaterTests
{
    private string directory = null!;
    private UpdateStorage storage = null!;
    private const string version = "2026.1005.0-lazer";
    private static readonly ManagedRuleset fixture = new("test/ruleset", "osu.Game.Rulesets.UpdaterFixture");
    private static byte[] Fixture => File.ReadAllBytes(typeof(UpdaterTests).Assembly.Location);

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "zankai-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        storage = new UpdateStorage(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ReleaseAsset Asset(string name, byte[] bytes, string repository = "test/ruleset") => new()
    {
        Name = name, Size = bytes.Length, Digest = "sha256:" + Hash(bytes), DownloadUrl = $"https://github.com/{repository}/releases/download/v1.1.0/{name}",
    };
    private static Release Release(params ReleaseAsset[] assets) => new() { Tag = "v1.1.0", Id = 1, PublishedAt = DateTimeOffset.UtcNow, Assets = assets };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, UpdateStorage.Json)) };
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpClient Client(Release release, byte[] bytes) => new(new Handler(r => r.RequestUri!.Host == "api.github.com"
        ? r.RequestUri.AbsolutePath.EndsWith("/latest") ? Json(release) : Json(new[] { release }) : Bytes(bytes)));

    [Test]
    public void CatalogHasPersonalInstallAndExternalUpdatePolicies()
    {
        var catalog = CatalogSource.Bundled();
        Assert.That(catalog.Rulesets.Single(r => r.Name == "Sticks").InstallIfMissing, Is.True);
        Assert.That(catalog.Rulesets.Where(r => r.Name != "Sticks").All(r => !r.InstallIfMissing), Is.True);
        Assert.That(catalog.Rulesets.Single(r => r.Name == "MOsu").Enabled, Is.False);
        Assert.That(catalog.Rulesets.Count(r => r.Enabled), Is.EqualTo(9));
    }

    [TestCase("../repo")]
    [TestCase("https://evil.example/owner/repo")]
    [TestCase("owner/repo?token=oops")]
    [TestCase("owner/repo/releases")]
    public void RejectsUntrustedRepositorySyntax(string repository) => Assert.Throws<InvalidDataException>(() => RulesetCatalog.Repository(repository));

    [Test]
    public void NormalizesRepositoryUrl() => Assert.That(RulesetCatalog.Repository("https://github.com/owner/repo.git"), Is.EqualTo("owner/repo"));

    [Test]
    public void RejectsDuplicateUnsafeAndSelfCatalogEntries()
    {
        Assert.Throws<InvalidDataException>(() => new RulesetCatalog(1, [fixture, fixture]).Validate());
        Assert.Throws<InvalidDataException>(() => new RulesetCatalog(1, [fixture with { AssemblyName = "../osu.Game" }]).Validate());
        Assert.Throws<InvalidDataException>(() => new RulesetCatalog(1, [fixture with { AssemblyName = "osu.Game.Rulesets.ZankaiUpdater" }]).Validate());
        Assert.Throws<InvalidDataException>(() => new RulesetCatalog(1, [fixture with { AssetPattern = "../../client.dll" }]).Validate());
    }

    [Test]
    public async Task RemoteCatalogRefreshesAndSurvivesOffline()
    {
        var remote = new RulesetCatalog(1, [fixture with { InstallIfMissing = true }]);
        using (var http = new HttpClient(new Handler(_ => Json(remote))))
            Assert.That((await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single().InstallIfMissing, Is.True);
        using (var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
            Assert.That((await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single().AssemblyName, Is.EqualTo(fixture.AssemblyName));
    }

    [Test]
    public async Task InvalidRemoteDoesNotReplaceValidCache()
    {
        UpdateStorage.WriteJson(Path.Combine(storage.WorkDirectory, "catalog.json"), new RulesetCatalog(1, [fixture]));
        using var http = new HttpClient(new Handler(_ => Json(new RulesetCatalog(99, []))));
        Assert.That((await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single().AssemblyName, Is.EqualTo(fixture.AssemblyName));
    }

    [Test]
    public async Task ExternalDiscoveryOnlyMatchesInstalledAssembly()
    {
        byte[] bytes = Fixture;
        var release = Release(Asset(fixture.InstalledFileName, bytes), Asset("osu.Game.Rulesets.Uninstalled.dll", bytes));
        using var http = Client(release, bytes);
        var source = new GitHubSource(http);
        Assert.That(await CatalogSource.DiscoverAsync(fixture.Repository, source, storage, default), Is.Empty);
        File.WriteAllBytes(Path.Combine(directory, "renamed.dll"), bytes);
        var found = await CatalogSource.DiscoverAsync(fixture.Repository, source, storage, default);
        Assert.That(found.Single().AssemblyName, Is.EqualTo(fixture.AssemblyName));
        Assert.That(found.Single().Channels, Is.EqualTo(new[] { "lazer" }));
        Assert.That(found.Single().InstallIfMissing, Is.False);
    }

    [Test]
    public async Task MaintainedDisabledEntryOverridesExternal()
    {
        var local = new CatalogSource(new HttpClient(), storage);
        local.AddExternal([fixture]);
        using var http = new HttpClient(new Handler(_ => Json(new RulesetCatalog(1, [fixture with { Enabled = false }]))));
        Assert.That((await new CatalogSource(http, storage).LoadAsync(default)).Rulesets.Single().Enabled, Is.False);
    }

    [Test]
    public void ExactClientBuildCanUseEarlierReleaseButPlainDllCannot()
    {
        var older = Release(Asset(fixture.AssetName(version), Fixture)) with { PublishedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var newer = Release() with { Id = 2 };
        Assert.That(ReleaseSelection.Select([older, newer], fixture, version), Is.Not.Null);
        var plain = fixture with { AssetPattern = "{assembly}.dll" };
        older = older with { Assets = [Asset(plain.InstalledFileName, Fixture)] };
        Assert.That(ReleaseSelection.Select([older, newer], plain, version), Is.Null);
    }

    [Test]
    public void PrereleaseDoesNotMeanTachyonAndStableIsNotAssumedCompatible()
    {
        var plain = fixture with { AssetPattern = "{assembly}.dll", Channels = ["lazer"] };
        var release = Release(Asset(plain.InstalledFileName, Fixture));
        Assert.That(ReleaseSelection.Select([release], plain, "2026.1013.0-tachyon"), Is.Null);
        Assert.That(ReleaseSelection.Select([release with { Prerelease = true }], plain, version), Is.Null);
        Assert.That(ReleaseSelection.Select([release], plain with { Enabled = false }, version), Is.Null);
    }

    [TestCase("v1.0.2c", "1.0.2b", true)]
    [TestCase("v0.4", "0.3", true)]
    [TestCase("1.0.0", "v1.0.0", false)]
    [TestCase("0.4.0", "v0.4", false)]
    [TestCase("1.0.2", "1.0.2-dev", false)]
    public void VersionsCompareConservatively(string newer, string older, bool expected)
        => Assert.That(ReleaseSelection.IsNewerInstalledVersion(newer, older), Is.EqualTo(expected));

    [Test]
    public void MetadataAndCompatibilityChecks()
    {
        AssemblyInfo info = AssemblyInfo.Read(typeof(UpdaterTests).Assembly.Location);
        Assert.That(info.RulesetApi, Is.EqualTo("2026.818.0"));
        Assert.That(info.TargetFramework, Is.EqualTo(".NETCoreApp,Version=v8.0"));
        var game = new AssemblyInfo("osu.Game", "2026.1005.0", true) { RulesetApi = "2026.818.0", TargetFramework = ".NETCoreApp,Version=v8.0" };
        var compatible = new ClientCompatibility(game, directory);
        Assert.That(compatible.Rejection(info), Is.Null);
        Assert.That(compatible.Rejection(info with { RulesetApi = "2022.822.0" }), Does.Contain("does not match"));
        Assert.That(compatible.Rejection(info with { RulesetApi = null }), Does.Contain("Cannot verify"));
        Assert.That(compatible.Rejection(info with { TargetFramework = ".NETCoreApp,Version=v10.0" }), Does.Contain("runtime"));
        Assert.That(compatible.Rejection(info with { References = new Dictionary<string, Version> { ["osu.Game"] = new Version(9999, 1) } }), Does.Contain("dependency"));
    }

    [Test]
    public async Task DownloadDoesNotDeleteExistingDestination()
    {
        byte[] bytes = Fixture;
        var asset = Asset(fixture.AssetName(version), bytes);
        using var http = Client(Release(asset), bytes);
        string file = Path.Combine(directory, "existing");
        File.WriteAllText(file, "preserve me");
        Assert.ThrowsAsync<IOException>(() => new GitHubSource(http).DownloadAsync(fixture, new UpdateCandidate("v1.1.0", asset, version), file, default));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("preserve me"));
    }

    [Test]
    public void CorruptHashAndWrongRepositoryFailClosed()
    {
        byte[] bytes = Fixture;
        using var http = Client(Release(), bytes);
        var source = new GitHubSource(http);
        var asset = Asset(fixture.AssetName(version), bytes);
        var candidate = new UpdateCandidate("v1.1.0", asset with { Digest = "sha256:" + new string('0', 64) }, version);
        string destination = Path.Combine(directory, "download");
        Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAsync(fixture, candidate, destination, default));
        Assert.That(File.Exists(destination), Is.False);
        candidate = candidate with { Asset = asset with { DownloadUrl = "https://github.com/other/repo/releases/download/v1/file.dll" } };
        Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAsync(fixture, candidate, destination, default));
    }

    [Test]
    public async Task PersonalCatalogInstallsMissingGameButExternalDoesNot()
    {
        byte[] bytes = Fixture;
        using var http = Client(Release(Asset(fixture.AssetName(version), bytes)), bytes);
        var source = new GitHubSource(http);
        var external = new Installer(storage, fixture);
        Assert.That(await external.PrepareAsync(source, version, default), Is.Null);
        var personal = new Installer(storage, fixture with { InstallIfMissing = true });
        var pending = await personal.PrepareAsync(source, version, default);
        Assert.That(pending, Is.Not.Null);
        Assert.That(pending!.Originals, Is.Empty);
        personal.Apply(pending, version, () => false);
        Assert.That(storage.FindInstalled(fixture).Single().Sha256, Is.EqualTo(Hash(bytes)));
    }

    [Test]
    public async Task MissingGenericCompatibilityBlocksUpdateAndClearsStalePending()
    {
        var generic = fixture with { AssetPattern = "{assembly}.dll" };
        byte[] bytes = Fixture;
        File.WriteAllBytes(Path.Combine(directory, fixture.InstalledFileName), bytes.Concat(new byte[] { 1 }).ToArray());
        using var http = Client(Release(Asset(generic.AssetName(version), bytes)), bytes);
        var installer = new Installer(storage, generic);
        UpdateStorage.WriteJson(installer.PendingPath, new StagedUpdate(version, "old", "old", "old", []));
        Assert.That(await installer.PrepareAsync(new GitHubSource(http), version, default), Is.Null);
        Assert.That(File.Exists(installer.PendingPath), Is.False);
        Assert.That(installer.Status!.State, Is.EqualTo("unverified-client"));
    }

    [Test]
    public async Task InstallationRollsBackAndDoesNotTouchOtherRulesets()
    {
        byte[] bytes = Fixture;
        string original = Path.Combine(directory, "renamed.dll");
        File.WriteAllBytes(original, bytes.Concat(new byte[] { 1 }).ToArray());
        File.WriteAllText(Path.Combine(directory, "unrelated.txt"), "untouched");
        using var http = Client(Release(Asset(fixture.AssetName(version), bytes)), bytes);
        var installer = new Installer(storage, fixture);
        var pending = await installer.PrepareAsync(new GitHubSource(http), version, default);
        Assert.Throws<IOException>(() => installer.Apply(pending!, version, () => false, _ => throw new IOException("simulated interruption")));
        Assert.That(UpdateStorage.Hash(original), Is.EqualTo(pending!.Originals.Single().Sha256));
        Assert.That(File.ReadAllText(Path.Combine(directory, "unrelated.txt")), Is.EqualTo("untouched"));
        Assert.That(installer.HasTransaction, Is.False);
        Assert.That(new Installer(storage, ManagedRuleset.Sticks).PendingPath, Is.Not.EqualTo(installer.PendingPath));
    }

    [Test]
    public void ArchiveRejectsTraversalDuplicatesAndLinks()
    {
        foreach (string name in new[] { "../evil", SelfUpdate.BridgeName })
        {
            string archive = Path.Combine(directory, Guid.NewGuid() + ".zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var entry = zip.CreateEntry(name).Open()) entry.WriteByte(1);
                using (var entry = zip.CreateEntry(name).Open()) entry.WriteByte(2);
            }
            string output = Path.Combine(directory, Guid.NewGuid().ToString());
            Directory.CreateDirectory(output);
            Assert.Throws<InvalidDataException>(() => SelfUpdate.Extract(archive, output, true));
        }
        string tarFile = Path.Combine(directory, "link.tar.gz");
        using (var stream = File.Create(tarFile))
        using (var gzip = new GZipStream(stream, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip)) writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "ZankaiUpdater") { LinkName = "/tmp/elsewhere" });
        Assert.Throws<InvalidDataException>(() => SelfUpdate.Extract(tarFile, directory, false));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExtractsBothPlatformPackageFormats(bool zip)
    {
        string archive = Path.Combine(directory, "archive"), output = Path.Combine(directory, "extracted");
        Directory.CreateDirectory(output);
        string[] names = [SelfUpdate.BridgeName, zip ? "ZankaiUpdater.exe" : "ZankaiUpdater"];
        using (var stream = File.Create(archive))
        {
            if (zip)
            {
                using var writer = new ZipArchive(stream, ZipArchiveMode.Create);
                foreach (string name in names) { using var entry = writer.CreateEntry(name).Open(); entry.WriteByte(42); }
            }
            else
            {
                using var gzip = new GZipStream(stream, CompressionMode.Compress);
                using var writer = new TarWriter(gzip);
                foreach (string name in names) writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream([42]) });
            }
        }
        SelfUpdate.Extract(archive, output, zip);
        Assert.That(Directory.GetFiles(output).Length, Is.EqualTo(2));
    }

    private (SelfUpdate Self, SelfUpdatePlan Plan) StageSelf()
    {
        string stageName = "self-" + Guid.NewGuid().ToString("N");
        string stage = Path.Combine(storage.WorkDirectory, stageName);
        Directory.CreateDirectory(stage);
        var files = new List<SelfUpdateFile>();
        foreach (string name in new[] { SelfUpdate.HelperName, SelfUpdate.BridgeName })
        {
            File.WriteAllText(Path.Combine(directory, name), "old-" + name);
            File.WriteAllText(Path.Combine(stage, name), "new-" + name);
            files.Add(new SelfUpdateFile(name, UpdateStorage.Hash(Path.Combine(stage, name)), UpdateStorage.Hash(Path.Combine(directory, name))));
        }
        var plan = new SelfUpdatePlan("v1.2.0", stageName, files.ToArray());
        var self = new SelfUpdate(storage);
        UpdateStorage.WriteJson(self.PlanPath, plan);
        return (self, plan);
    }

    [Test]
    public void SelfReplacementUpdatesBothFilesAndDeletesOldCopies()
    {
        var (self, _) = StageSelf();
        self.Apply(() => false);
        foreach (string name in new[] { SelfUpdate.HelperName, SelfUpdate.BridgeName }) Assert.That(File.ReadAllText(Path.Combine(directory, name)), Is.EqualTo("new-" + name));
        Assert.That(File.Exists(self.PlanPath), Is.False);
        Assert.That(Directory.GetDirectories(storage.WorkDirectory, "self-*"), Is.Empty);
        if (!OperatingSystem.IsWindows()) Assert.That(File.GetUnixFileMode(Path.Combine(directory, SelfUpdate.HelperName)).HasFlag(UnixFileMode.UserExecute), Is.True);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void SelfReplacementFailureRestoresBothFiles(int failAt)
    {
        var (self, _) = StageSelf();
        Assert.Throws<IOException>(() => self.Apply(() => false, n => { if (n == failAt) throw new IOException("interruption"); }));
        foreach (string name in new[] { SelfUpdate.HelperName, SelfUpdate.BridgeName }) Assert.That(File.ReadAllText(Path.Combine(directory, name)), Is.EqualTo("old-" + name));
        Assert.That(File.Exists(self.PlanPath), Is.False);
    }

    [Test]
    public void RecoversSelfUpdateAfterProcessCrashBetweenFiles()
    {
        var (self, plan) = StageSelf();
        string stage = Path.Combine(storage.WorkDirectory, plan.Stage);
        foreach (var f in plan.Files) File.Copy(Path.Combine(directory, f.Name), Path.Combine(stage, f.Name + ".old"));
        File.Copy(Path.Combine(stage, SelfUpdate.HelperName), Path.Combine(directory, SelfUpdate.HelperName), true);
        UpdateStorage.WriteJson(self.PlanPath, plan with { Applying = true });
        self.Recover();
        foreach (string name in new[] { SelfUpdate.HelperName, SelfUpdate.BridgeName }) Assert.That(File.ReadAllText(Path.Combine(directory, name)), Is.EqualTo("old-" + name));
    }

    [Test]
    public void SelfReplacementRefusesRunningClientOrChangedFiles()
    {
        var (self, _) = StageSelf();
        Assert.Throws<IOException>(() => self.Apply(() => true));
        File.WriteAllText(Path.Combine(directory, SelfUpdate.HelperName), "changed outside updater");
        Assert.Throws<IOException>(() => self.Apply(() => false));
        Assert.That(File.ReadAllText(Path.Combine(directory, SelfUpdate.HelperName)), Is.EqualTo("changed outside updater"));
    }

    [Test]
    public void CommittedSelfUpdateCanFinishCleanupAfterStageAlreadyRemoved()
    {
        var (self, plan) = StageSelf();
        string stage = Path.Combine(storage.WorkDirectory, plan.Stage);
        foreach (var f in plan.Files) File.Copy(Path.Combine(stage, f.Name), Path.Combine(directory, f.Name), true);
        Directory.Delete(stage, true);
        UpdateStorage.WriteJson(self.PlanPath, plan with { Applying = true, Committed = true });
        self.Recover();
        Assert.That(File.Exists(self.PlanPath), Is.False);
    }
}
