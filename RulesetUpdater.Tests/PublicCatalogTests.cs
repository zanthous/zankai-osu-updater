using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class PublicCatalogTests
{
    [Test, Explicit("Downloads public release DLLs for read-only metadata inspection; no DLL is loaded or installed.")]
    public async Task VerifyPublicCatalogAssets()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zankai-public-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            var source = new GitHubSource(http);
            foreach (ManagedRuleset rule in CatalogSource.Bundled().Rulesets.Where(r => r.Enabled))
            {
                var candidate = await source.FindAsync(rule, "2026.1005.0-lazer", default);
                Assert.That(candidate, Is.Not.Null, rule.Repository);
                string file = Path.Combine(directory, rule.InstalledFileName);
                await source.DownloadAsync(rule, candidate!, file, default);
                var info = AssemblyInfo.Read(file);
                Assert.That(info.Name, Is.EqualTo(rule.AssemblyName));
                TestContext.Progress.WriteLine($"{rule.Name}: {candidate!.ReleaseTag}; {info.TargetFramework}; API {info.RulesetApi ?? "unknown (skip automatic installation)"}; osu.Game {info.References.GetValueOrDefault("osu.Game")}");
            }
            var mosu = await source.LatestAsync("p-720/mosu", default);
            TestContext.Progress.WriteLine($"MOsu excluded: latest {mosu?.Tag}, assets {mosu?.Assets.Length}");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test, Explicit("Verifies the currently published native updater archive without installing or executing it.")]
    public async Task VerifyPublishedUpdaterArchive()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zankai-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storage = new UpdateStorage(directory);
            File.WriteAllText(Path.Combine(directory, SelfUpdate.HelperName), "old helper fixture");
            File.WriteAllText(Path.Combine(directory, SelfUpdate.BridgeName), "old bridge fixture");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            var pending = await new SelfUpdate(storage).PrepareAsync(new GitHubSource(http), "0.0.0", default);
            Assert.That(pending, Is.Not.Null);
            TestContext.Progress.WriteLine($"Verified {pending!.ReleaseTag} / {SelfUpdate.AssetName}: hash, archive layout, bridge identity and version");
            Assert.That(File.ReadAllText(Path.Combine(directory, SelfUpdate.HelperName)), Is.EqualTo("old helper fixture"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
