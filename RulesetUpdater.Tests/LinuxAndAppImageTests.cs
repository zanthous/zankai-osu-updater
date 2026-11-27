using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class LinuxAndAppImageTests
{
    private string directory = null!;
    private UpdateStorage storage = null!;
    private const string version = "2026.1115.0-lazer";
    private static readonly ManagedRuleset genericFixture = new("test/ruleset", "osu.Game.Rulesets.UpdaterFixture")
    {
        AssetPattern = "{assembly}.dll",
        Channels = ["lazer"],
        InstallIfMissing = true,
    };

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "zankai-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        storage = new UpdateStorage(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [TestCase("12031 (osu!) S 12000 12000 12000 0 -1 4194304 100 0 0 0 10 5 0 0 20 0 42 0 9876543 100000 500", 9876543L, false)]
    [TestCase("12031 (osu (lazer) 2) R 12000 12000 12000 0 -1 4194304 100 0 0 0 10 5 0 0 20 0 42 0 123456 100000 500", 123456L, false)]
    [TestCase("12031 (osu!) Z 12000 12000 12000 0 -1 4194304 100 0 0 0 10 5 0 0 20 0 42 0 9876543 100000 500", 9876543L, true)]
    [TestCase("12031 (osu!) X 12000 12000 12000 0 -1 4194304 100 0 0 0 10 5 0 0 20 0 42 0 9876543 100000 500", 9876543L, true)]
    public void ParsesLinuxProcStatStartClockTicksAndState(string stat, long expectedTicks, bool expectedExited)
    {
        long? ticks = ProcessIdentity.ParseLinuxProcStatStartClockTicks(stat, out bool exited);
        Assert.That(ticks, Is.EqualTo(expectedTicks));
        Assert.That(exited, Is.EqualTo(expectedExited));
    }

    [TestCase("")]
    [TestCase("12031 missing-parens")]
    [TestCase("12031 (osu!) S 1 2 3")]
    [TestCase("12031 (osu!) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 notanumber")]
    public void RejectsMalformedLinuxProcStat(string stat)
    {
        Assert.That(ProcessIdentity.ParseLinuxProcStatStartClockTicks(stat, out _), Is.Null);
    }

    [Test]
    public void StartTimeMatchingHandlesLinuxBootTimeJitterAndKernelClockTicks()
    {
        const long recordedUtc = 639304383982834526L;
        const long jitteredUtc = recordedUtc + 3500; // 350 us wall-clock boot-time drift between processes

        // Kernel clock ticks decide on their own when both sides have them, whatever the wall clock did since.
        Assert.That(ProcessIdentity.ClockTicksMatch(9876543, 9876543), Is.True);
        Assert.That(ProcessIdentity.ClockTicksMatch(9876544, 9876543), Is.False);
        Assert.That(ProcessIdentity.ClockTicksMatch(9876543, null), Is.Null);
        Assert.That(ProcessIdentity.ClockTicksMatch(null, 9876543), Is.Null);

        // Older Linux bridge sessions without kernel clock ticks tolerate sub-second boot-time estimation drift.
        Assert.That(ProcessIdentity.StartTimeMatches(jitteredUtc, recordedUtc, linux: true), Is.True);
        Assert.That(ProcessIdentity.StartTimeMatches(recordedUtc + TimeSpan.TicksPerSecond * 5, recordedUtc, linux: true), Is.False);

        // Non-Linux platforms keep exact UTC tick matching.
        Assert.That(ProcessIdentity.StartTimeMatches(recordedUtc, recordedUtc, linux: false), Is.True);
        Assert.That(ProcessIdentity.StartTimeMatches(jitteredUtc, recordedUtc, linux: false), Is.False);
    }

    [Test]
    public void SessionsFromAnotherBootAreNotRunning()
    {
        const string boot = "5f0c8a1e-3d0b-4a57-9c55-0d6f5d1b2a10";
        Assert.That(ProcessIdentity.SameBoot(boot, boot), Is.True);
        Assert.That(ProcessIdentity.SameBoot(boot, "00000000-0000-0000-0000-000000000000"), Is.False);
        // Other systems and older bridges record no boot; the start time alone decides for them.
        Assert.That(ProcessIdentity.SameBoot(null, boot), Is.True);
        Assert.That(ProcessIdentity.SameBoot(boot, null), Is.True);
    }

    [Test]
    public void DetectsAppImagePathsAndScrubsLdLibraryPathEntries()
    {
        const string mount = "/tmp/.mount_osu.ApGCEoef";
        const string assembly = "/tmp/.mount_osu.ApGCEoef/usr/bin/osu!.dll";
        const string siblingAssembly = "/tmp/.mount_osu.ApGCEoef2/usr/bin/osu!.dll";

        Assert.That(ProcessIdentity.IsPathUnderDirectory(assembly, mount), Is.True);
        Assert.That(ProcessIdentity.IsPathUnderDirectory(assembly, mount + "/"), Is.True);
        Assert.That(ProcessIdentity.IsPathUnderDirectory(siblingAssembly, mount), Is.False);

        const string mountLib = "/tmp/.mount_osu.ApGCEoef/usr/lib";
        const string siblingLib = "/tmp/.mount_osu.ApGCEoef2/usr/lib";
        Assert.That(ProcessIdentity.StripDirectoryEntries(mountLib, mount), Is.Null);
        Assert.That(ProcessIdentity.StripDirectoryEntries($"{mountLib}:{siblingLib}:/usr/local/lib", mount), Is.EqualTo($"{siblingLib}:/usr/local/lib"));
    }

    [Test]
    public void CurrentProcessMatchesOwnIdentityAndPrunesDeadSessions()
    {
        using var current = Process.GetCurrentProcess();
        var live = new ClientSession
        {
            ProcessId = current.Id,
            ProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
            ProcessStartClockTicks = ProcessIdentity.TryReadLinuxStartClockTicks(current.Id),
            BootId = ProcessIdentity.TryReadLinuxBootId(),
            Version = version,
            OfficialBuild = true,
        };
        var wrongStart = live with
        {
            ProcessStartUtcTicks = live.ProcessStartUtcTicks - TimeSpan.TicksPerHour,
            ProcessStartClockTicks = live.ProcessStartClockTicks is { } c ? c + 1 : null,
        };
        var otherBoot = live with { BootId = "00000000-0000-0000-0000-000000000000" };
        var dead = new ClientSession
        {
            ProcessId = int.MaxValue,
            ProcessStartUtcTicks = 1,
            Version = version,
            OfficialBuild = true,
        };

        Assert.That(ClientProcesses.Running(live), Is.True);
        Assert.That(ClientProcesses.Running(wrongStart), Is.False);
        // Only Linux reports a boot to compare against.
        Assert.That(ClientProcesses.Running(otherBoot), Is.EqualTo(ProcessIdentity.TryReadLinuxBootId() == null));
        Assert.That(ClientProcesses.Running(dead), Is.False);

        string livePath = Path.Combine(storage.WorkDirectory, $"session-{live.ProcessId}-{live.ProcessStartUtcTicks}.json");
        string deadPath = Path.Combine(storage.WorkDirectory, "session-2147483647-1.json");
        UpdateStorage.WriteJson(livePath, live);
        UpdateStorage.WriteJson(deadPath, dead);

        ClientProcesses.PruneDeadSessions(storage);
        Assert.That(File.Exists(livePath), Is.True);
        Assert.That(File.Exists(deadPath), Is.False);
    }

    [Test]
    public void CompatibilitySnapshotEnforcesApiRuntimeAndDependenciesAfterUnmount()
    {
        AssemblyInfo ruleset = AssemblyInfo.Read(typeof(LinuxAndAppImageTests).Assembly.Location);
        var live = new ClientCompatibility(
            new AssemblyInfo("osu.Game", version, true) { RulesetApi = "2026.818.0", TargetFramework = ".NETCoreApp,Version=v10.0" },
            null,
            new Dictionary<string, Version>(StringComparer.Ordinal)
            {
                ["osu.Game"] = new(2026, 1115, 0, 0),
                ["osu.Framework"] = new(2026, 1110, 0, 0),
            });

        ClientCompatibilitySnapshot snapshot = live.ToSnapshot();
        ClientCompatibility restored = ClientCompatibility.FromSnapshot(snapshot)!;
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored.Rejection(ruleset), Is.Null);
        Assert.That(restored.Rejection(ruleset with { RulesetApi = "2022.822.0" }), Does.Contain("does not match"));
        Assert.That(restored.Rejection(ruleset with { TargetFramework = ".NETCoreApp,Version=v11.0" }), Does.Contain("runtime"));
        Assert.That(restored.Rejection(ruleset with { References = new Dictionary<string, Version> { ["osu.Game"] = new(2026, 1115, 0, 0) } }), Is.Null);
        Assert.That(restored.Rejection(ruleset with { References = new Dictionary<string, Version> { ["osu.Game"] = new(2027, 1, 0, 0) } }), Does.Contain("osu.Game"));
        Assert.That(restored.Rejection(ruleset with { References = new Dictionary<string, Version> { ["ppy.Localisation"] = new(1, 0, 0, 0) } }), Does.Contain("ppy.Localisation"));
    }

    [Test]
    public void SnapshotsCompatibilityFromTheMountedClientFiles()
    {
        string appImage = Path.Combine(directory, "osu.AppImage");
        File.WriteAllText(appImage, "simulated-appimage");
        string mount = Path.Combine(directory, ".mount_osu.ApGCEoef");
        string assemblyPath = ClientFixture.Write(Path.Combine(mount, "usr", "bin"), version);
        var session = new ClientSession
        {
            ProcessId = 12031,
            ProcessStartUtcTicks = 639304383982834526,
            Version = version,
            AssemblyPath = assemblyPath,
            OfficialBuild = true,
            AppImagePath = appImage,
        };

        // While mounted, the version and compatibility data come from the client's own files.
        Assert.That(ClientProcesses.ReadLiveAssemblyVersion(session), Is.EqualTo(version));
        ClientSession persisted = ClientProcesses.SnapshotForPersistence(session, ClientCompatibility.Read(assemblyPath));
        Assert.That(persisted.AppImageSha256, Is.EqualTo(UpdateStorage.Hash(appImage)));
        Assert.That(persisted.Compatibility, Is.Not.Null);
        Assert.That(persisted.Compatibility!.RulesetApi, Is.EqualTo(ClientFixture.RulesetApi));
        Assert.That(persisted.Compatibility.TargetFramework, Is.EqualTo(ClientFixture.TargetFramework));
        Assert.That(persisted.Compatibility.Assemblies, Is.EqualTo(new Dictionary<string, string> { ["osu.Game"] = ClientFixture.GameVersion.ToString() }));

        Directory.Delete(mount, true);
        var verified = ClientProcesses.ReadVerifiedClient(persisted);
        Assert.That(verified, Is.Not.Null);
        Assert.That(verified!.Value.Version, Is.EqualTo(version));
        AssemblyInfo ruleset = AssemblyInfo.Read(typeof(LinuxAndAppImageTests).Assembly.Location);
        ClientCompatibility compatibility = verified.Value.Compatibility!;
        Assert.That(compatibility.Rejection(ruleset), Is.Null);
        Assert.That(compatibility.Rejection(ruleset with { References = new Dictionary<string, Version> { ["osu.Game"] = ClientFixture.GameVersion } }), Is.Null);
        Assert.That(compatibility.Rejection(ruleset with { References = new Dictionary<string, Version> { ["osu.Framework"] = new(1, 0, 0, 0) } }), Does.Contain("osu.Framework"));
    }

    [Test]
    public async Task AppImageSnapshotSupportsClosedChecksAndRejectsModifiedAppImage()
    {
        string appImage = Path.Combine(directory, "osu.AppImage");
        File.WriteAllText(appImage, "simulated-appimage-v2026.1115.0");
        string mountDir = Path.Combine(directory, ".mount_osu.ApGCEoef", "usr", "bin");
        Directory.CreateDirectory(mountDir);
        string assemblyPath = Path.Combine(mountDir, "osu!.dll");
        File.WriteAllText(assemblyPath, "ephemeral");

        var session = new ClientSession
        {
            ProcessId = 12031,
            ProcessStartUtcTicks = 639304383982834526,
            Version = version,
            ExecutablePath = Path.Combine(mountDir, "osu!"),
            AssemblyPath = assemblyPath,
            OfficialBuild = true,
            AppImagePath = appImage,
        };
        var liveCompatibility = new ClientCompatibility(
            new AssemblyInfo("osu.Game", version, true) { RulesetApi = "2026.818.0", TargetFramework = ".NETCoreApp,Version=v10.0" },
            null,
            new Dictionary<string, Version>(StringComparer.Ordinal) { ["osu.Game"] = new(2026, 1115, 0, 0) });

        ClientSession persisted = ClientProcesses.SnapshotForPersistence(session, liveCompatibility);
        UpdateStorage.WriteJson(Path.Combine(storage.WorkDirectory, "last-client.json"), persisted);

        // Simulate AppImage unmounting as soon as osu! closes.
        Directory.Delete(Path.Combine(directory, ".mount_osu.ApGCEoef"), true);
        Assert.That(File.Exists(assemblyPath), Is.False);
        Assert.That(ClientProcesses.ReadLiveAssemblyVersion(persisted), Is.Null);

        var verified = ClientProcesses.ReadVerifiedClient(persisted);
        Assert.That(verified, Is.Not.Null);
        Assert.That(verified!.Value.Version, Is.EqualTo(version));
        Assert.That(verified.Value.Compatibility, Is.Not.Null);

        byte[] fixtureBytes = File.ReadAllBytes(typeof(LinuxAndAppImageTests).Assembly.Location);
        var catalog = new RulesetCatalog(1, [genericFixture]);
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return json(catalog);
            if (request.RequestUri.Host == "api.github.com")
            {
                var asset = new ReleaseAsset
                {
                    Name = genericFixture.InstalledFileName,
                    Size = fixtureBytes.Length,
                    Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant(),
                    DownloadUrl = $"https://github.com/{genericFixture.Repository}/releases/download/v1.1.0/{genericFixture.InstalledFileName}",
                };
                var release = new Release { Tag = "v1.1.0", Assets = [asset] };
                return request.RequestUri.AbsolutePath.EndsWith("/latest") ? json(release) : json(new[] { release });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixtureBytes) };
        }));

        // Real DesktopUpdater flow (no clientVersion override): checks and installs a generic ruleset using the verified AppImage snapshot.
        var app = new DesktopUpdater(directory, http, clientRunning: _ => false, checkSelf: false);
        var check = await app.CheckAsync(default);
        Assert.That(check.ReadyCount, Is.EqualTo(1), check.Details);
        await app.InstallAsync(default);
        Assert.That(File.ReadAllBytes(Path.Combine(directory, genericFixture.InstalledFileName)), Is.EqualTo(fixtureBytes));

        // Upgrading or replacing the AppImage invalidates the cached session so mismatched builds are never installed.
        File.WriteAllText(appImage, "simulated-appimage-v2026.1120.0");
        Assert.That(ClientProcesses.ReadVerifiedClient(persisted), Is.Null);
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => app.CheckAsync(default));
        Assert.That(error!.Message, Does.Contain("Launch osu! once"));

        // Removing the AppImage also invalidates the cached session.
        File.Delete(appImage);
        Assert.That(ClientProcesses.ReadVerifiedClient(persisted), Is.Null);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private static HttpResponseMessage json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, UpdateStorage.Json)) };
}
