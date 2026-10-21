using System.Text;

namespace RulesetUpdater;

// Runs the published menu and its asynchronous actions without a terminal, network access,
// catalogs, DLL downloads or touching the user's installation.
internal static class TerminalUiVerification
{
    internal static int Run()
    {
        try
        {
            verify().GetAwaiter().GetResult();
            Console.WriteLine("Terminal menu verified: add, confirmation, cancel, check, install, errors and safe close");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Terminal menu verification: " + message);
    }

    private static async Task verify()
    {
        var updater = new VerificationUpdater();
        var terminal = new ScriptedTerminal();
        var ui = new TerminalUi(updater, terminal, color: true);
        terminal.Busy = () => ui.Busy;
        terminal
            .Expect(screen =>
            {
                require(screen.Contains("Add gamemode"), "Add gamemode must be visible on launch");
                require(!screen.Contains("Install updates"), "Launch must not prompt to install");
                require(updater.CheckCalls == 0 && updater.AddCalls == 0, "Launch must not start an update");
            })
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Type("test/example").Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                require(updater.AddCalls == 0, "Finding a source must not add it");
                require(screen.Contains("❯ Trust and add"), "A found source needs a confirmation");
                require(screen.Contains("Allow test/example to install"), "Show the repository being trusted");
            })
            .Press(ConsoleKey.Escape).Type("x")
            .Expect(screen => require(!screen.Contains("Trust and add"), "Editing a URL must invalidate confirmation"))
            .Press(ConsoleKey.Escape)
            .Expect(_ => require(updater.AddCalls == 0, "Cancel must not add a source"))
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Press(ScriptedTerminal.ClearLine).Type("test/example").Press(ConsoleKey.Enter, ConsoleKey.Enter)
            .Expect(screen =>
            {
                require(updater.AddCalls == 1, "Add only after explicit confirmation");
                require(!screen.Contains("GitHub repository") && screen.Contains("Added Example"), "Close the form after adding");
            })
            .Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                require(screen.Contains("Everything is up to date"), "Show the up-to-date result");
                require(!screen.Contains("Install updates"), "No install action when up to date");
                updater.ReadyCount = 1;
            })
            .Press(ConsoleKey.Escape, ConsoleKey.Enter)
            .Expect(screen => require(screen.Contains("❯ Install updates"), "Offer installation when updates are ready"))
            .Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                require(updater.InstallCalls == 1, "Install must call the updater");
                require(!screen.Contains("Install updates") && screen.Contains("Installed updates"), "Hide install after completion");
                updater.FailFind = true;
            })
            .Press(ConsoleKey.DownArrow, ConsoleKey.Enter).Press(ScriptedTerminal.ClearLine).Type("test/example").Press(ConsoleKey.Enter)
            .Expect(screen =>
            {
                require(screen.Contains("could not be reached"), "Errors must be visible");
                require(screen.Contains("GitHub repository") && !screen.Contains("Trust and add"), "An error must allow retry without a trusted choice");
                updater.WaitForClose = true;
            })
            .Press(ConsoleKey.Escape, ConsoleKey.Enter).PressDuringOperation(ScriptedTerminal.Interrupt);
        await ui.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
        require(updater.CancellationSeen && updater.Unwound, "Closing must wait for the operation to unwind");
        require(!terminal.Finished, "Ctrl+C during an operation must close the menu once it unwinds");
    }

    private sealed class VerificationUpdater : IDesktopUpdater
    {
        internal int AddCalls, CheckCalls, InstallCalls, ReadyCount;
        internal bool FailFind, WaitForClose, CancellationSeen, Unwound;

        public Task<ManagedRuleset[]> FindAsync(string repository, CancellationToken cancellation)
        {
            if (FailFind) throw new IOException("Repository could not be reached");
            return Task.FromResult(new[] { new ManagedRuleset(repository, "osu.Game.Rulesets.Example") { Channels = ["lazer"] } });
        }

        public Task<string> AddAsync(ManagedRuleset ruleset, CancellationToken cancellation)
        {
            AddCalls++;
            return Task.FromResult("Added Example");
        }

        public async Task<DesktopCheckResult> CheckAsync(CancellationToken cancellation)
        {
            CheckCalls++;
            if (WaitForClose)
            {
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { CancellationSeen = true; }
                await Task.Delay(200, CancellationToken.None); // a transaction still unwinding
                Unwound = true;
                cancellation.ThrowIfCancellationRequested();
            }
            return new DesktopCheckResult(ReadyCount == 0 ? "Everything is up to date" : "1 update ready to install", "Example: checked", ReadyCount,
                [new("Example: checked", ReadyCount == 0 ? DesktopItemState.Current : DesktopItemState.Ready)]);
        }

        public Task<DesktopInstallResult> InstallAsync(CancellationToken cancellation)
        {
            InstallCalls++;
            return Task.FromResult(new DesktopInstallResult("Installed updates"));
        }
    }
}

// Plays keys to the menu and runs checks against the rendered screen between them.
internal sealed class ScriptedTerminal(int width = 80, int height = 24) : ITerminal
{
    internal static readonly ConsoleKeyInfo Interrupt = new('\u0003', ConsoleKey.C, false, false, true);
    internal static readonly ConsoleKeyInfo ClearLine = new('\u0015', ConsoleKey.U, false, false, true);
    private readonly Queue<(ConsoleKeyInfo Key, Action<string>? Check, bool DuringOperation)> steps = new();

    internal Func<bool> Busy { get; set; } = () => false;
    internal string Frame { get; private set; } = "";
    // Set once the script has run out; the menu is then closed with Ctrl+C.
    internal bool Finished { get; private set; }

    public int Width => width;
    public int Height => height;
    public bool KeyAvailable => !Busy() || (steps.TryPeek(out var next) && next.DuringOperation);

    public ConsoleKeyInfo ReadKey()
    {
        while (steps.TryDequeue(out var step))
        {
            if (step.DuringOperation != Busy())
                throw new InvalidOperationException(step.DuringOperation ? "Expected an operation to be running." : "The script continued during an operation.");
            if (step.Check == null) return step.Key;
            step.Check(Screen);
        }
        Finished = true;
        return Interrupt;
    }

    public void Write(string text) => Frame = text;

    // The last frame as plain text, without escape sequences.
    internal string Screen
    {
        get
        {
            var text = new StringBuilder();
            for (int i = 0; i < Frame.Length; i++)
            {
                if (Frame[i] == '\u001b')
                {
                    while (++i < Frame.Length && !char.IsAsciiLetter(Frame[i])) { }
                    continue;
                }
                if (Frame[i] != '\r') text.Append(Frame[i]);
            }
            return text.ToString();
        }
    }

    internal ScriptedTerminal Press(params ConsoleKey[] keys)
    {
        foreach (var key in keys) Press(Key(key));
        return this;
    }

    internal ScriptedTerminal Press(ConsoleKeyInfo key)
    {
        steps.Enqueue((key, null, false));
        return this;
    }

    internal ScriptedTerminal PressDuringOperation(ConsoleKeyInfo key)
    {
        steps.Enqueue((key, null, true));
        return this;
    }

    internal ScriptedTerminal Type(string text)
    {
        foreach (char c in text) Press(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        return this;
    }

    internal ScriptedTerminal Expect(Action<string> check)
    {
        steps.Enqueue((default, check, false));
        return this;
    }

    internal static ConsoleKeyInfo Key(ConsoleKey key) => new(key switch
    {
        ConsoleKey.Enter => '\r',
        ConsoleKey.Escape => '\u001b',
        ConsoleKey.Backspace => '\b',
        ConsoleKey.Tab => '\t',
        _ => '\0',
    }, key, false, false, false);
}
