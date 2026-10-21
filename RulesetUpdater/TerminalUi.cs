using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RulesetUpdater;

internal interface ITerminal
{
    int Width { get; }
    int Height { get; }
    bool KeyAvailable { get; }
    ConsoleKeyInfo ReadKey();
    void Write(string text);
}

// macOS and Linux have no Win32 window, and open the helper in a terminal anyway. The same actions
// run as a full-screen terminal menu; all updater operations still go through DesktopUpdater.
internal sealed class TerminalUi(IDesktopUpdater updater, ITerminal terminal, bool color)
{
    private enum Screen { Home, Results, Repository, Choose, Confirm }

    private const string csi = "\u001b[";
    private const string idle = "Updates are checked automatically when osu! starts";
    private static readonly string[] spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private Screen screen = Screen.Home;
    private string summary = idle, repository = "";
    private bool failed, quit, closing, dirty = true;
    private int selected, readyCount;
    private DesktopCheckItem[] items = [];
    private ManagedRuleset[] found = [];
    private ManagedRuleset? choice;
    private string? status;
    private Task<Action>? operation;
    private CancellationTokenSource? cancellation;

    internal bool Busy => operation != null;
    // Printed after leaving the full-screen menu, so the result stays visible in the terminal.
    internal string? Outcome { get; private set; }

    internal static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected
                                        && Environment.GetEnvironmentVariable("TERM") is not "dumb";

    internal static int Run(IDesktopUpdater updater)
    {
        var ui = new TerminalUi(updater, new ConsoleTerminal(), string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")));
        string? error = null;
        Console.TreatControlCAsInput = true;
        Console.Out.Write(csi + "?1049h"); // alternate screen
        try { return ui.RunAsync().GetAwaiter().GetResult(); }
        catch (Exception e) { error = e.Message; return 1; }
        finally
        {
            Console.Out.Write(csi + "?25h" + csi + "?1049l");
            Console.TreatControlCAsInput = false;
            if (error != null) Console.Error.WriteLine(error);
            else if (ui.Outcome != null) Console.WriteLine(ui.Outcome);
        }
    }

    // File managers start programs without a terminal. Reopen the helper in one so the menu is visible.
    internal static bool OpenInTerminal()
    {
        if (!OperatingSystem.IsLinux() || !Console.IsInputRedirected || Environment.ProcessPath is not { } self
            || (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))))
            return false;
        string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (name, prefix) in terminals)
        {
            string? program = path.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
            if (program == null) continue;
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (string arg in prefix) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(self);
            try
            {
                Process.Start(start)?.Dispose();
                return true;
            }
            catch (Win32Exception) { }
        }
        return false;
    }

    private static readonly (string Name, string[] Prefix)[] terminals =
    [
        ("xdg-terminal-exec", []), // freedesktop default terminal
        ("x-terminal-emulator", ["-e"]), // Debian and Ubuntu default terminal
        ("gnome-terminal", ["--"]),
        ("ptyxis", ["--"]),
        ("konsole", ["-e"]),
        ("xfce4-terminal", ["-x"]),
        ("mate-terminal", ["-x"]),
        ("kitty", []),
        ("alacritty", ["-e"]),
        ("foot", []),
        ("xterm", ["-e"]),
    ];

    internal async Task<int> RunAsync()
    {
        (int Width, int Height) size = default;
        long shown = -1;
        while (true)
        {
            if (operation is { IsCompleted: true }) finish();
            if (quit) return 0;
            var current = (terminal.Width, terminal.Height);
            long frame = Busy ? Environment.TickCount64 / 100 : 0;
            if (dirty || current != size || frame != shown)
            {
                render(current.Width, current.Height, frame);
                (dirty, size, shown) = (false, current, frame);
            }
            if (terminal.KeyAvailable)
            {
                key(terminal.ReadKey());
                dirty = true;
            }
            else if (operation != null) await Task.WhenAny(operation, Task.Delay(50));
            else await Task.Delay(50);
        }
    }

    private (string Label, Action Run)[] menu() => screen switch
    {
        Screen.Home => [("Check for updates", check), ("Add gamemode", openAdd), ("Quit", () => quit = true)],
        Screen.Results when readyCount > 0 => [("Install updates", install), ("Back", home)],
        Screen.Results => [("Back", home)],
        Screen.Choose => [.. found.Select(rule => (rule.Name, (Action)(() => confirm(rule)))), ("Cancel", home)],
        Screen.Confirm => [("Trust and add", add), ("Cancel", home)],
        _ => [],
    };

    private void key(ConsoleKeyInfo key)
    {
        bool interrupt = key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (operation != null)
        {
            // Let the current file transaction finish or unwind; never abandon it halfway.
            if (!interrupt && key.Key != ConsoleKey.Escape) return;
            closing |= interrupt;
            cancellation!.Cancel();
            status = closing ? "Finishing the current operation before closing…" : "Cancelling…";
            return;
        }
        if (interrupt) { quit = true; return; }
        if (screen == Screen.Repository) { edit(key); return; }
        var actions = menu();
        selected = Math.Clamp(selected, 0, actions.Length - 1);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: selected = (selected + actions.Length - 1) % actions.Length; break;
            case ConsoleKey.DownArrow or ConsoleKey.Tab: selected = (selected + 1) % actions.Length; break;
            case ConsoleKey.Enter: actions[selected].Run(); break;
            case ConsoleKey.Escape: back(); break;
            case ConsoleKey.Q when screen == Screen.Home: quit = true; break;
        }
    }

    private void edit(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter) { if (repository.Trim().Length > 0) find(); }
        else if (key.Key == ConsoleKey.Escape) home();
        else if (key.Key == ConsoleKey.Backspace) repository = repository.Length > 0 ? repository[..^1] : "";
        else if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control)) repository = "";
        else if (!char.IsControl(key.KeyChar) && repository.Length < 2048) repository += key.KeyChar;
    }

    private void back()
    {
        if (screen == Screen.Home) quit = true;
        // Escape from a found source returns to editing it, which invalidates the confirmation.
        else if (screen is Screen.Choose or Screen.Confirm) open(Screen.Repository, "");
        else home();
    }

    private void open(Screen next, string message, bool error = false)
    {
        (screen, selected, summary, failed) = (next, 0, message, error);
    }

    private void home() => open(Screen.Home, idle);
    private void openAdd() => open(Screen.Repository, "");

    private void confirm(ManagedRuleset rule)
    {
        choice = rule;
        open(Screen.Confirm, "Confirm the source to add this gamemode");
    }

    private void run(string message, Func<CancellationToken, Task<Action>> action)
    {
        status = message;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        operation = Task.Run(() => action(token), CancellationToken.None);
    }

    private void finish()
    {
        var completed = operation!;
        bool cancelled = cancellation!.IsCancellationRequested;
        cancellation.Dispose();
        (operation, cancellation, status, dirty) = (null, null, null, true);
        try { completed.GetAwaiter().GetResult()(); }
        catch (OperationCanceledException) when (cancelled) { (summary, failed) = ("Cancelled", false); }
        catch (Exception e) { (summary, failed) = (e.Message, true); }
        if (closing) quit = true;
    }

    private void check() => run("Checking updates…", async token =>
    {
        var result = await updater.CheckAsync(token);
        return () =>
        {
            open(Screen.Results, result.Summary);
            readyCount = result.ReadyCount;
            items = result.Items ?? result.Details.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                  .Select(line => new DesktopCheckItem(line, DesktopItemState.Info)).ToArray();
        };
    });

    private void install()
    {
        // Like the window: installation is offered again only after another check.
        readyCount = 0;
        run("Installing updates…", async token =>
        {
            var result = await updater.InstallAsync(token);
            return () =>
            {
                open(Screen.Home, result.Message);
                Outcome = result.Message;
                // The updater replaces itself once this process exits.
                closing |= result.ExitForSelfUpdate;
            };
        });
    }

    private void find()
    {
        string input = repository;
        (found, choice) = ([], null);
        run("Finding gamemode…", async token =>
        {
            var rules = await updater.FindAsync(input, token);
            return () =>
            {
                found = rules;
                if (rules.Length == 1) confirm(rules[0]);
                else if (rules.Length > 1) open(Screen.Choose, "Choose the gamemode to add");
                else open(Screen.Repository, "No supported gamemode found", true);
            };
        });
    }

    private void add()
    {
        if (choice is not { } rule) return;
        run("Adding gamemode…", async token =>
        {
            string result = await updater.AddAsync(rule, token);
            return () =>
            {
                (found, choice) = ([], null);
                open(Screen.Home, result);
                Outcome = result;
            };
        });
    }

    private void render(int width, int height, long frame)
    {
        width = Math.Max(width, 40);
        height = Math.Max(height, 12);
        int inner = width - 4;
        var body = new List<string>();
        int? inputLine = null;
        string title = screen is Screen.Home or Screen.Results ? "Zankai Updater" : "Add gamemode";
        string version = "v" + SelfUpdateRunner.Version.Split('+')[0];
        var head = new List<string>
        {
            "  " + paint("1", title) + new string(' ', Math.Max(1, inner - title.Length - version.Length)) + paint("2", version),
            "  " + paint("2", new string('─', inner)),
        };

        void text(string value, string style = "")
        {
            foreach (string line in wrap(value, inner)) body.Add("  " + paint(style, line));
        }

        if (screen == Screen.Repository)
        {
            body.Add("  GitHub repository");
            int field = inner - 4;
            string visible = repository.Length < field ? repository : repository[^(field - 1)..];
            body.Add("  " + paint("2", "┌" + new string('─', inner - 2) + "┐"));
            inputLine = body.Count;
            body.Add("  " + paint("2", "│ ") + (repository.Length == 0 ? paint("2", "https://github.com/owner/repository".PadRight(field)) : visible.PadRight(field)) + paint("2", " │"));
            body.Add("  " + paint("2", "└" + new string('─', inner - 2) + "┘"));
            if (summary.Length > 0) text(summary, failed ? "31" : "");
        }
        else
        {
            text(summary, failed ? "31" : screen == Screen.Home && summary == idle ? "" : "1");
            if (screen == Screen.Results && items.Length > 0)
            {
                body.Add("");
                foreach (var item in items)
                {
                    var (symbol, style) = item.State switch
                    {
                        DesktopItemState.Ready => ("●", "36"),
                        DesktopItemState.Current => ("✓", "32"),
                        DesktopItemState.Attention => ("!", "33"),
                        _ => ("•", ""),
                    };
                    bool first = true;
                    foreach (string line in wrap(item.Message, inner - 2))
                    {
                        body.Add("  " + (first ? paint(style, symbol) : " ") + " " + line);
                        first = false;
                    }
                }
            }
            if (screen == Screen.Confirm && choice is { } rule)
            {
                body.Add("");
                text($"Allow {rule.Repository} to install and update {rule.Name}? Only add sources you trust.");
                if (rule.Channels.SequenceEqual(new[] { "lazer" })) text("This source supports stable lazer only.", "2");
            }
        }
        body.Add("");

        var actions = new List<string>();
        if (status != null) actions.Add("  " + paint("36", spinner[frame % spinner.Length]) + " " + status);
        else
        {
            var entries = menu();
            selected = Math.Clamp(selected, 0, Math.Max(0, entries.Length - 1));
            for (int i = 0; i < entries.Length; i++)
                actions.Add(i == selected ? "  " + paint("1;36", "❯ " + entries[i].Label) : "    " + entries[i].Label);
        }
        string hint = status != null ? "Esc cancel"
            : screen == Screen.Repository ? "Enter find   Esc back"
            : screen == Screen.Home ? "↑↓ move   Enter select   Esc quit" : "↑↓ move   Enter select   Esc back";
        var foot = new List<string> { "", "  " + paint("2", hint) };

        // Keep the actions visible on short terminals; long details are cut instead.
        int room = height - 1 - head.Count - actions.Count - foot.Count;
        if (body.Count > room)
        {
            body.RemoveRange(Math.Max(0, room - 1), body.Count - Math.Max(0, room - 1));
            if (room > 0) body.Add("  " + paint("2", "…"));
            if (inputLine >= body.Count - 1) inputLine = null;
        }

        var output = new StringBuilder(csi + "H");
        foreach (string line in head.Concat(body).Concat(actions).Concat(foot)) output.Append(line).Append(csi).Append("K\r\n");
        output.Append(csi).Append('J');
        if (inputLine is { } row && status == null)
        {
            int column = Math.Min(repository.Length, inner - 5);
            output.Append(csi).Append(head.Count + row + 1).Append(';').Append(5 + column).Append('H').Append(csi).Append("?25h");
        }
        else output.Append(csi).Append("?25l");
        terminal.Write(output.ToString());
    }

    private string paint(string style, string text) => color && style.Length > 0 ? $"{csi}{style}m{text}{csi}0m" : text;

    // Messages can include text from GitHub releases; drop control characters so they cannot drive the terminal.
    internal static IEnumerable<string> wrap(string text, int width)
    {
        foreach (string paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string clean = new(paragraph.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            string line = "";
            foreach (string word in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string rest = word;
                while (rest.Length > width) // unbroken text such as a long link
                {
                    if (line.Length > 0) { yield return line; line = ""; }
                    yield return rest[..width];
                    rest = rest[width..];
                }
                if (line.Length == 0) line = rest;
                else if (line.Length + 1 + rest.Length <= width) line += " " + rest;
                else { yield return line; line = rest; }
            }
            yield return line;
        }
    }

    private sealed class ConsoleTerminal : ITerminal
    {
        public int Width => size(() => Console.WindowWidth, 80);
        public int Height => size(() => Console.WindowHeight, 24);
        public bool KeyAvailable => Console.KeyAvailable;
        public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);
        public void Write(string text) => Console.Out.Write(text);

        private static int size(Func<int> read, int fallback)
        {
            try { int value = read(); return value > 0 ? value : fallback; }
            catch (IOException) { return fallback; }
        }
    }
}
