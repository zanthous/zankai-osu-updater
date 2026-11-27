using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

// Runs the packaged helper the way the bridge does: one process records a client's identity
// and a different process has to recognise it, wait for it to close, and clean up.
[TestFixture]
public sealed class WatchTests
{
    private const string version = "2026.1115.0-lazer";

    [TestCase(true, true, false)]
    // An older bridge recorded only the UTC start time, which Linux estimates separately in every process.
    [TestCase(false, false, false)]
    // osu! can load the rulesets folder through a symlinked parent, while Linux reports the helper's resolved path.
    [TestCase(true, false, true)]
    public async Task HelperWatchesAClientRecordedByAnotherProcess(bool kernelIdentity, bool appImage, bool aliasedParent)
    {
        string? helper = Environment.GetEnvironmentVariable("ZANKAI_TEST_HELPER");
        if (string.IsNullOrWhiteSpace(helper)) { Assert.Ignore("Set the packaged helper path to test the startup watcher."); return; }
        if (aliasedParent && OperatingSystem.IsWindows())
        {
            Assert.Ignore("Creating directory symlinks on Windows requires privileges not needed by the updater.");
            return;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "zankai-watch-" + Guid.NewGuid().ToString("N"));
        string physical = Path.Combine(temporary, "physical");
        string work = Path.Combine(physical, "rulesets", ".ruleset-updater");
        Directory.CreateDirectory(work);
        Process? client = null, watcher = null;
        try
        {
            string rulesets = Path.Combine(physical, "rulesets");
            if (aliasedParent)
            {
                Directory.CreateSymbolicLink(Path.Combine(temporary, "alias"), physical);
                rulesets = Path.Combine(temporary, "alias", "rulesets");
            }
            string executable = Path.Combine(rulesets, SelfUpdate.HelperName);
            File.Copy(Path.GetFullPath(helper), executable);
            SelfUpdate.SetExecutable(executable);

            string mount = Path.Combine(temporary, ".mount_osu.fixture");
            string assemblyPath = ClientFixture.Write(Path.Combine(mount, "usr", "bin"), version);
            string? appImagePath = appImage ? Path.Combine(temporary, "osu.AppImage") : null;
            if (appImagePath != null) File.WriteAllText(appImagePath, "simulated-appimage");

            client = Process.Start(idleProgram())!;
            var session = new ClientSession
            {
                ProcessId = client.Id,
                ProcessStartUtcTicks = client.StartTime.ToUniversalTime().Ticks,
                ProcessStartClockTicks = kernelIdentity ? ProcessIdentity.TryReadLinuxStartClockTicks(client.Id) : null,
                BootId = kernelIdentity ? ProcessIdentity.TryReadLinuxBootId() : null,
                Version = version,
                ExecutablePath = Path.Combine(mount, "usr", "bin", "osu!"),
                AssemblyPath = assemblyPath,
                OfficialBuild = true,
                AppImagePath = appImagePath,
            };
            string sessionName = $"session-{session.ProcessId}-{session.ProcessStartUtcTicks}.json";
            string sessionPath = Path.Combine(rulesets, ".ruleset-updater", sessionName);
            // The bridge writes with the serializer's defaults, not the helper's own JSON contract.
            File.WriteAllText(sessionPath, JsonSerializer.Serialize(session));

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = rulesets,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "--watch", "--session", sessionPath }) start.ArgumentList.Add(argument);
            // Keep the test offline: every request is refused at once and the watcher uses its bundled catalog.
            string refusing = "http://127.0.0.1:" + unusedPort();
            foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                start.Environment[name] = refusing;
            start.Environment.Remove("NO_PROXY");
            start.Environment.Remove("no_proxy");
            Process running = watcher = Process.Start(start)!;
            Task<string> output = running.StandardOutput.ReadToEndAsync(), errors = running.StandardError.ReadToEndAsync();

            string statusPath = Path.Combine(work, "status.json");
            async Task<string> report() =>
                $"Status: {(File.Exists(statusPath) ? File.ReadAllText(statusPath) : "none")}\nOutput: {(running.HasExited ? await output : "")}\nErrors: {(running.HasExited ? await errors : "")}";

            // Written only once the watcher has recognised the running client and verified its assembly.
            string lastClientPath = Path.Combine(work, "last-client.json");
            var waiting = Stopwatch.StartNew();
            while (!File.Exists(lastClientPath))
            {
                if (running.HasExited) Assert.Fail("The watcher rejected a running client.\n" + await report());
                if (waiting.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail("The watcher never recorded the client.\n" + await report());
                await Task.Delay(100);
            }
            ClientSession recorded = UpdateStorage.ReadJson<ClientSession>(lastClientPath)!;
            Assert.Multiple(() =>
            {
                Assert.That(recorded.ProcessId, Is.EqualTo(session.ProcessId));
                Assert.That(recorded.ProcessStartClockTicks, Is.EqualTo(session.ProcessStartClockTicks));
                Assert.That(recorded.BootId, Is.EqualTo(session.BootId));
                Assert.That(recorded.Version, Is.EqualTo(version));
                Assert.That(recorded.AssemblyPath, Is.EqualTo(assemblyPath));
                if (appImagePath != null)
                {
                    Assert.That(recorded.AppImageSha256, Is.EqualTo(UpdateStorage.Hash(appImagePath)));
                    Assert.That(recorded.Compatibility?.RulesetApi, Is.EqualTo(ClientFixture.RulesetApi));
                    Assert.That(recorded.Compatibility?.Assemblies.Keys, Is.EqualTo(new[] { "osu.Game" }));
                }
                else Assert.That(recorded.AppImageSha256, Is.Null);
            });
            Assert.That(running.HasExited, Is.False, "The watcher must keep waiting while the client runs.");

            // An AppImage unmounts as its client closes, taking the assemblies with it.
            if (appImage) Directory.Delete(mount, true);
            client.Kill();
            await client.WaitForExitAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await running.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { Assert.Fail("The watcher did not notice the client closing.\n" + await report()); }
            Assert.That(running.ExitCode, Is.Zero, await report());
            Assert.That(File.Exists(Path.Combine(work, sessionName)), Is.False, "A finished session must be removed.");
            // The closed client must still verify as the recorded version, from its files or the AppImage record.
            string? state = UpdateStorage.ReadJson<UpdateStatus>(statusPath)?.State;
            Assert.That(state, Is.Not.EqualTo("waiting-for-client").And.Not.EqualTo("error"), await report());
        }
        finally
        {
            foreach (Process? process in new[] { watcher, client })
            {
                if (process == null) continue;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync();
                    }
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            Directory.Delete(temporary, true);
        }
    }

    // Any quiet, long-lived program can stand in for osu!: the watcher needs only its identity and its exit.
    private static ProcessStartInfo idleProgram()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping.exe" : "sleep")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        foreach (string argument in OperatingSystem.IsWindows() ? new[] { "-n", "300", "127.0.0.1" } : new[] { "300" }) start.ArgumentList.Add(argument);
        return start;
    }

    private static int unusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
