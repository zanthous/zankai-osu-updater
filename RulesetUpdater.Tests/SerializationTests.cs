using System.Text.Json;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class SerializationTests
{
    [Test]
    public void OmittedFieldsKeepExistingDefaults()
    {
        const string minimalCatalog = """
            {"schema":1,"rulesets":[{"repository":"test/example","assemblyName":"osu.Game.Rulesets.Example"}]}
            """;
        var catalog = JsonSerializer.Deserialize<RulesetCatalog>(minimalCatalog, UpdateStorage.Json)!.Validate();
        var rule = catalog.Rulesets.Single();
        var release = JsonSerializer.Deserialize<Release>("{}", UpdateStorage.Json)!;
        var session = JsonSerializer.Deserialize<ClientSession>("{}", UpdateStorage.Json)!;
        Assert.Multiple(() =>
        {
            Assert.That(rule.Name, Is.EqualTo("Example"));
            Assert.That(rule.Enabled, Is.True);
            Assert.That(rule.InstallIfMissing, Is.False);
            Assert.That(rule.AssetPattern, Is.EqualTo("{assembly}-{client}.dll"));
            Assert.That(rule.Channels, Is.EqualTo(new[] { "lazer", "tachyon" }));
            Assert.That(release.Assets, Is.Not.Null.And.Empty);
            Assert.That(release.Tag, Is.Empty);
            Assert.That(session.Schema, Is.EqualTo(1));
            Assert.That(session.AssemblyPath, Is.Empty);
        });
    }

    [Test]
    public void ReadsExistingPascalCaseBridgeSessions()
    {
        const string json = """
            {"Schema":1,"ProcessId":123,"ProcessStartUtcTicks":638900000000000000,
             "Version":"2026.1005.0-lazer","ExecutablePath":"/osu/osu!",
             "AssemblyPath":"/osu/osu!.dll","OfficialBuild":true}
            """;
        var session = JsonSerializer.Deserialize<ClientSession>(json, UpdateStorage.Json)!;
        Assert.Multiple(() =>
        {
            Assert.That(session.Schema, Is.EqualTo(1));
            Assert.That(session.ProcessId, Is.EqualTo(123));
            Assert.That(session.ProcessStartUtcTicks, Is.EqualTo(638900000000000000));
            Assert.That(session.Version, Is.EqualTo("2026.1005.0-lazer"));
            Assert.That(session.ExecutablePath, Is.EqualTo("/osu/osu!"));
            Assert.That(session.AssemblyPath, Is.EqualTo("/osu/osu!.dll"));
            Assert.That(session.OfficialBuild, Is.True);
        });
    }

    [Test]
    public void ReadsGitHubNamesAndNestedAssets()
    {
        const string json = """
            [{"id":321,"tag_name":"v0.2.0","published_at":"2026-10-20T12:00:00Z",
              "draft":false,"prerelease":true,"assets":[{"id":987,"name":"ZankaiUpdater-win-x64.zip",
              "size":123456,"digest":"sha256:abc","browser_download_url":"https://github.com/test/updater/releases/download/v0.2.0/update.zip"}]}]
            """;
        var release = JsonSerializer.Deserialize<Release[]>(json, UpdateStorage.Json)!.Single();
        var asset = release.Assets.Single();
        Assert.Multiple(() =>
        {
            Assert.That(release.Id, Is.EqualTo(321));
            Assert.That(release.Tag, Is.EqualTo("v0.2.0"));
            Assert.That(release.PublishedAt, Is.EqualTo(DateTimeOffset.Parse("2026-10-20T12:00:00Z")));
            Assert.That(release.Draft, Is.False);
            Assert.That(release.Prerelease, Is.True);
            Assert.That(asset.Id, Is.EqualTo(987));
            Assert.That(asset.Name, Is.EqualTo("ZankaiUpdater-win-x64.zip"));
            Assert.That(asset.Size, Is.EqualTo(123456));
            Assert.That(asset.Digest, Is.EqualTo("sha256:abc"));
            Assert.That(asset.DownloadUrl, Does.EndWith("/v0.2.0/update.zip"));
        });
    }

    [Test]
    public void AllPersistedContractsHaveGeneratedMetadata()
    {
        roundTrip(CatalogSource.Bundled());
        roundTrip(new ClientSession { ProcessId = 123, OfficialBuild = true });
        roundTrip(new StagedUpdate("2026.1005.0-lazer", "v1.2.0", "hash", "payload",
            [new InstalledFile("old.dll", "previous", "1.1.0")]));
        roundTrip(new InstallJournal("mode.dll", "hash", "backup",
            [new InstalledFile("old.dll", "previous", "1.1.0")], Installing: true, Committed: true));
        roundTrip(new SelfUpdatePlan("v0.2.0", "stage",
            [new SelfUpdateFile("ZankaiUpdater", "hash", "previous")], Applying: true, Committed: true));
        roundTrip(new UpdateStatus("ready", "Update ready", DateTimeOffset.UtcNow));
    }

    private static void roundTrip<T>(T value)
    {
        string path = Path.Combine(Path.GetTempPath(), "zankai-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            UpdateStorage.WriteJson(path, value);
            var restored = UpdateStorage.ReadJson<T>(path);
            Assert.That(restored, Is.Not.Null);
            Assert.That(JsonSerializer.Serialize(restored, UpdateStorage.Json), Is.EqualTo(File.ReadAllText(path)));
        }
        finally { File.Delete(path); }
    }
}
