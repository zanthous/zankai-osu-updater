using System.Diagnostics;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class StandaloneTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task NativeRunnerReplacesBothFilesAfterParentExits(int directoryAliasDepth)
    {
        if (directoryAliasDepth > 0 && OperatingSystem.IsWindows())
        {
            Assert.Ignore("Creating directory symlinks on Windows requires privileges not needed by the updater.");
            return;
        }
        string? helper = Environment.GetEnvironmentVariable("ZANKAI_TEST_HELPER");
        string? bridge = Environment.GetEnvironmentVariable("ZANKAI_TEST_BRIDGE");
        if (helper == null || bridge == null) { Assert.Ignore("Set packaged helper and bridge paths to test a native self-update."); return; }
        helper = Path.GetFullPath(helper);
        bridge = Path.GetFullPath(bridge);
        string temporary = Path.Combine(Path.GetTempPath(), "zankai-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string directory = temporary;
            if (directoryAliasDepth > 0)
            {
                string physical = Path.Combine(temporary, "physical");
                Directory.CreateDirectory(Path.Combine(physical, "nested"));
                string target = physical;
                if (directoryAliasDepth == 2)
                {
                    // Model macOS /var -> /private/var even on Linux: resolving the
                    // install alias returns a target whose parent is itself an alias.
                    string parentAlias = Path.Combine(temporary, "parent-alias");
                    Directory.CreateSymbolicLink(parentAlias, physical);
                    target = Path.Combine(parentAlias, "nested");
                }
                directory = Path.Combine(temporary, "alias");
                Directory.CreateSymbolicLink(directory, target);
            }
            var storage = new UpdateStorage(directory);
            string stageName = "self-" + Guid.NewGuid().ToString("N");
            string stage = Path.Combine(storage.WorkDirectory, stageName);
            string runnerDirectory = Path.Combine(storage.WorkDirectory, "install-runner");
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(runnerDirectory);
            string runnerPath = Path.Combine(runnerDirectory, SelfUpdate.HelperName);
            File.Copy(helper, runnerPath);
            SelfUpdate.SetExecutable(runnerPath);
            File.Copy(helper, Path.Combine(stage, SelfUpdate.HelperName));
            File.Copy(bridge, Path.Combine(stage, SelfUpdate.BridgeName));
            File.WriteAllText(Path.Combine(directory, SelfUpdate.HelperName), "previous helper");
            File.WriteAllText(Path.Combine(directory, SelfUpdate.BridgeName), "previous bridge");
            var files = new[] { SelfUpdate.HelperName, SelfUpdate.BridgeName }.Select(name => new SelfUpdateFile(name,
                UpdateStorage.Hash(Path.Combine(stage, name)), UpdateStorage.Hash(Path.Combine(directory, name)))).ToArray();
            var self = new SelfUpdate(storage);
            UpdateStorage.WriteJson(self.PlanPath, new SelfUpdatePlan("vtest", stageName, files));
            // The runner must also handle a parent which exited before it obtained the process handle.
            var start = new ProcessStartInfo(runnerPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "--finish-self-update", "--rulesets", directory, "--wait-process", int.MaxValue.ToString(), "--wait-start", "1" }) start.ArgumentList.Add(arg);
            using (var runner = Process.Start(start)!)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await runner.WaitForExitAsync(timeout.Token);
                Assert.That(runner.ExitCode, Is.Zero, await runner.StandardError.ReadToEndAsync());
            }
            Assert.That(File.Exists(self.PlanPath), Is.False);
            foreach (var file in files) Assert.That(UpdateStorage.Hash(Path.Combine(directory, file.Name)), Is.EqualTo(file.Sha256));
            using var installed = Process.Start(new ProcessStartInfo(Path.Combine(directory, SelfUpdate.HelperName), "--help") { UseShellExecute = false, RedirectStandardOutput = true })!;
            string output = await installed.StandardOutput.ReadToEndAsync();
            await installed.WaitForExitAsync();
            Assert.That(installed.ExitCode, Is.Zero);
            Assert.That(output, Does.StartWith("Zankai Updater"));
        }
        finally { Directory.Delete(temporary, true); }
    }
}
