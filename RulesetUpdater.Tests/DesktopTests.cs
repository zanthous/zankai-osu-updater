using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class DesktopTests
{
    private sealed class FakeUpdater : IDesktopUpdater
    {
        public int FindCalls, AddCalls, CheckCalls, InstallCalls;
        public bool FailFind, SelfUpdate;
        public ManagedRuleset[] Found = [new("test/example", "osu.Game.Rulesets.Example") { Channels = ["lazer"] }];
        public DesktopCheckResult Result = new("Everything is up to date", "Sticks: current", 0);
        public Task<ManagedRuleset[]> FindAsync(string repository, CancellationToken cancellation)
        {
            FindCalls++;
            if (FailFind) throw new IOException("Repository could not be reached");
            return Task.FromResult(Found);
        }
        public Task<string> AddAsync(ManagedRuleset ruleset, CancellationToken cancellation) { AddCalls++; return Task.FromResult("Added " + ruleset.Name); }
        public Task<DesktopCheckResult> CheckAsync(CancellationToken cancellation) { CheckCalls++; return Task.FromResult(Result); }
        public Task<DesktopInstallResult> InstallAsync(CancellationToken cancellation)
        {
            InstallCalls++;
            return Task.FromResult(new DesktopInstallResult(SelfUpdate ? "Finishing the updater update…" : "Installed updates", SelfUpdate));
        }
    }

    private static async Task<TerminalUi> run(FakeUpdater updater, ScriptedTerminal terminal)
    {
        var ui = new TerminalUi(updater, terminal, color: true);
        terminal.Busy = () => ui.Busy;
        Assert.That(await ui.RunAsync().WaitAsync(TimeSpan.FromSeconds(10)), Is.Zero);
        return ui;
    }

    [Test]
    public void OnlyManualLaunchOpensWindow()
    {
        Assert.That(Program.OpensWindow([]), Is.True);
        foreach (string mode in new[] { "--help", "--watch", "--finish-self-update", "--apply", "--add-source", "--verify-ui" })
            Assert.That(Program.OpensWindow([mode]), Is.False);
    }

    [Test]
    public void PackagedMenuVerificationPasses() => Assert.That(TerminalUiVerification.Run(), Is.Zero);

    [Test]
    public async Task MenuStartsWithAddAndNoInstallPrompt()
    {
        var updater = new FakeUpdater();
        await run(updater, new ScriptedTerminal().Expect(screen =>
        {
            Assert.That(screen, Does.Contain("❯ Check for updates"));
            Assert.That(screen, Does.Contain("Add gamemode"));
            Assert.That(screen, Does.Not.Contain("Install updates"));
        }));
        Assert.That(updater.CheckCalls, Is.Zero);
    }

    [Test]
    public async Task AddFlowRequiresExplicitConfirmation()
    {
        var updater = new FakeUpdater();
        var ui = await run(updater, new ScriptedTerminal()
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Type("https://github.com/test/example").Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                Assert.That(updater.FindCalls, Is.EqualTo(1));
                Assert.That(updater.AddCalls, Is.Zero);
                Assert.That(screen, Does.Contain("Allow test/example to install and update Example?"));
                Assert.That(screen, Does.Contain("This source supports stable lazer only."));
                Assert.That(screen, Does.Contain("❯ Trust and add"));
            })
            .Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                Assert.That(updater.AddCalls, Is.EqualTo(1));
                Assert.That(screen, Does.Contain("Added Example"));
                Assert.That(screen, Does.Contain("❯ Check for updates"));
            }));
        Assert.That(ui.Outcome, Is.EqualTo("Added Example"));
    }

    [Test]
    public async Task CancelAndChangedRepositoryDoNotAddSource()
    {
        var updater = new FakeUpdater();
        await run(updater, new ScriptedTerminal()
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Type("test/example").Press(ConsoleKey.Enter)
            .Press(ConsoleKey.Escape).Type("x")
            .Expect(screen => Assert.That(screen, Does.Contain("test/examplex").And.Not.Contain("Trust and add")))
            .Press(ConsoleKey.Escape)
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.DownArrow, ConsoleKey.Enter)
            .Expect(screen => Assert.That(screen, Does.Contain("❯ Check for updates"))));
        Assert.That(updater.FindCalls, Is.EqualTo(2));
        Assert.That(updater.AddCalls, Is.Zero);
    }

    [Test]
    public async Task SeveralGamemodesInOneRepositoryAreChosenBeforeConfirming()
    {
        var updater = new FakeUpdater { Found = [new("test/pack", "osu.Game.Rulesets.One"), new("test/pack", "osu.Game.Rulesets.Two")] };
        await run(updater, new ScriptedTerminal()
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Type("test/pack").Press(ConsoleKey.Enter)
            .Expect(screen => Assert.That(screen, Does.Contain("❯ One").And.Contain("Two").And.Not.Contain("Trust and add")))
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter)
            .Expect(screen => Assert.That(screen, Does.Contain("install and update Two?")))
            .Press(ConsoleKey.Enter));
        Assert.That(updater.AddCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task UpToDateCheckDoesNotOfferInstallation()
    {
        var updater = new FakeUpdater();
        var ui = await run(updater, new ScriptedTerminal()
            .Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                Assert.That(screen, Does.Contain("Everything is up to date"));
                Assert.That(screen, Does.Contain("Sticks: current"));
                Assert.That(screen, Does.Not.Contain("Install updates"));
                updater.Result = new DesktopCheckResult("1 update ready to install", "Example: ready", 1, [new("Example: ready", DesktopItemState.Ready)]);
            })
            .Press(ConsoleKey.Escape, ConsoleKey.Enter)
            .Expect(screen => Assert.That(screen, Does.Contain("● Example: ready").And.Contain("❯ Install updates")))
            .Press(ConsoleKey.Enter)
            .Expect(screen => Assert.That(screen, Does.Contain("Installed updates").And.Not.Contain("Install updates"))));
        Assert.That(updater.InstallCalls, Is.EqualTo(1));
        Assert.That(ui.Outcome, Is.EqualTo("Installed updates"));
    }

    [Test]
    public async Task SelfUpdateClosesTheMenuSoTheRunnerCanReplaceIt()
    {
        var updater = new FakeUpdater { SelfUpdate = true, Result = new("1 update ready to install", "Zankai Updater: v9 is ready.", 1) };
        var terminal = new ScriptedTerminal().Press(ConsoleKey.Enter, ConsoleKey.Enter);
        var ui = await run(updater, terminal);
        Assert.That(terminal.Finished, Is.False, "The menu closes on its own, without waiting for more keys.");
        Assert.That(ui.Outcome, Is.EqualTo("Finishing the updater update…"));
    }

    [Test]
    public async Task ErrorsRemainVisibleAndAllowRetry()
    {
        var updater = new FakeUpdater { FailFind = true };
        await run(updater, new ScriptedTerminal()
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Type("test/example").Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                Assert.That(screen, Does.Contain("could not be reached"));
                Assert.That(screen, Does.Contain("GitHub repository").And.Not.Contain("Trust and add"));
                updater.FailFind = false;
            })
            .Press(ConsoleKey.Enter)
            .Expect(screen => Assert.That(screen, Does.Contain("❯ Trust and add"))));
        Assert.That(updater.AddCalls, Is.Zero);
    }

    [Test]
    public async Task LongResultsWrapAndKeepTheActionsOnScreen()
    {
        var items = Enumerable.Range(1, 30).Select(i => new DesktopCheckItem($"Gamemode {i}: " + new string('x', 120), DesktopItemState.Attention)).ToArray();
        var updater = new FakeUpdater { Result = new("No updates ready to install — see details below", "", 0, items) };
        var terminal = new ScriptedTerminal(width: 50, height: 16);
        await run(updater, terminal.Press(ConsoleKey.Enter).Expect(screen =>
        {
            string[] lines = screen.TrimEnd('\n').Split('\n');
            Assert.That(lines, Has.Length.LessThan(16));
            Assert.That(lines.Max(l => l.Length), Is.LessThanOrEqualTo(50));
            Assert.That(screen, Does.Contain("❯ Back"));
            Assert.That(screen, Does.Contain("…"));
        }));
    }

    [Test]
    public async Task ReleaseTextCannotSendTerminalControlSequences()
    {
        var updater = new FakeUpdater { Result = new("Done", "", 0, [new("Example: v1\u001b]0;owned\u0007\u001b[2J ready", DesktopItemState.Info)]) };
        var terminal = new ScriptedTerminal();
        await run(updater, terminal.Press(ConsoleKey.Enter).Expect(_ =>
        {
            // Ordinal: a culture-aware comparison ignores the escape character itself.
            Assert.That(terminal.Frame.Contains("\u001b]", StringComparison.Ordinal), Is.False);
            Assert.That(terminal.Frame.Contains("\u001b[2J", StringComparison.Ordinal), Is.False);
            Assert.That(terminal.Screen, Does.Contain("Example: v1 ]0;owned [2J ready"));
        }));
    }
}
