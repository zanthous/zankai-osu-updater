using System.Text.Json;

namespace RulesetUpdater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool interactive = args.Length == 0;
        UpdateStorage? storage = null;
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("Zankai Updater\n\nRun beside your ruleset DLLs to update Sticks. Close osu! before applying.\n\n  --check                  Check only; do not download or install\n  --apply                  Download and install while osu! is closed\n  --client-version TAG     Exact version, e.g. 2026.1005.0-lazer\n  --rulesets PATH          Use a specific rulesets directory\n  --help                   Show this help");
                return 0;
            }

            storage = new UpdateStorage(options.Rulesets ?? AppContext.BaseDirectory);
            string lockPath = Path.Combine(storage.WorkDirectory, "updater.lock");
            UpdateStorage.RequireRegularPath(lockPath);
            FileStream updaterLock;
            try { updaterLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                Console.WriteLine("An updater is already running for this folder. Close osu! and try again.");
                return 2;
            }
            using (updaterLock)
            using (var cancellation = new CancellationTokenSource())
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) })
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                var source = new GitHubSource(http);
                var installer = new Installer(storage, ManagedRuleset.Sticks);
                if (options.Watch)
                    return await Watch(options, storage, source, installer, cancellation.Token);

                ClientSession? previous = UpdateStorage.ReadJson<ClientSession>(Path.Combine(storage.WorkDirectory, "last-client.json"));
                string? version = options.Version ?? (previous != null ? ClientProcesses.ReadClientVersion(previous) : null);
                if (interactive && version == null)
                {
                    Console.Write("osu! version (including -lazer or -tachyon): ");
                    version = ClientVersion.Parse(Console.ReadLine());
                }
                if (version == null) throw new ArgumentException("Specify --client-version with your exact osu! version.");
                Console.WriteLine($"Sticks updates for osu! {version}");
                if (options.Check)
                {
                    UpdateCandidate? match = await source.FindAsync(ManagedRuleset.Sticks, version, cancellation.Token);
                    Console.WriteLine(match == null ? "No matching release is available." : $"Matching release: {match.ReleaseTag}\n{match.Asset.Name}");
                    return 0;
                }
                if (ClientProcesses.AnyOsuRunning(storage)) throw new InvalidOperationException("Close osu! before applying updates.");
                installer.Recover();
                StagedUpdate? pending = await installer.PrepareAsync(source, version, cancellation.Token);
                PrintStatus(storage);
                if (pending != null)
                {
                    if (interactive)
                    {
                        Console.Write("Install this update? [Y/n] ");
                        if (Console.ReadLine()?.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase) == true) return 0;
                    }
                    installer.Apply(pending, version, () => ClientProcesses.AnyOsuRunning(storage));
                    PrintStatus(storage);
                }
                return 0;
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("Update cancelled."); return 2; }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            try { storage?.Status("error", e.Message); } catch { }
            return 1;
        }
        finally
        {
            if (interactive && !Console.IsInputRedirected)
            {
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }
        }
    }

    private static async Task<int> Watch(Options options, UpdateStorage storage, GitHubSource source, Installer installer, CancellationToken cancellation)
    {
        string sessionPath = Path.GetFullPath(options.Session ?? throw new ArgumentException("Missing session."));
        if (Path.GetDirectoryName(sessionPath) != storage.WorkDirectory || !Path.GetFileName(sessionPath).StartsWith("session-", StringComparison.Ordinal))
            throw new ArgumentException("Invalid session path.");
        ClientSession session = UpdateStorage.ReadJson<ClientSession>(sessionPath) ?? throw new InvalidDataException("Missing client session.");
        if (session.Schema != 1 || !session.OfficialBuild || ClientVersion.Parse(session.Version) != session.Version || !ClientProcesses.Running(session))
            throw new InvalidDataException("The osu! session is no longer valid.");
        UpdateStorage.WriteJson(Path.Combine(storage.WorkDirectory, "last-client.json"), session);
        DateTimeOffset nextCheck = DateTimeOffset.MinValue;
        string lastCheckFile = Path.Combine(storage.WorkDirectory, "last-check.json");
        LastCheck? lastCheck = UpdateStorage.ReadJson<LastCheck>(lastCheckFile);
        if (lastCheck?.Version == session.Version && lastCheck.At < DateTimeOffset.UtcNow)
            nextCheck = lastCheck.At.AddMinutes(15);

        while (ClientProcesses.Running(session))
        {
            cancellation.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= nextCheck)
            {
                // Persist before making requests so rapid restarts/offline errors cannot flood the API.
                UpdateStorage.WriteJson(lastCheckFile, new LastCheck(session.Version, DateTimeOffset.UtcNow));
                nextCheck = DateTimeOffset.UtcNow.AddMinutes(15);
                try { await installer.PrepareAsync(source, session.Version, cancellation); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    storage.Status("check-failed", $"Update check failed; will retry: {e.Message}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
        }

        // Give the host's update/restart handoff time to start. Never force-close a game.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
        if (ClientProcesses.AnyOsuRunning(storage)) return 0;
        installer.Recover();
        StagedUpdate? pending = UpdateStorage.ReadJson<StagedUpdate>(installer.PendingPath);
        if (pending == null) return 0;
        string? currentVersion = ClientProcesses.ReadClientVersion(session);
        if (currentVersion == null || currentVersion != pending.ClientVersion)
        {
            storage.Status("waiting-for-client", "osu! changed installation or version. The next launch will check for the matching build.");
            return 0;
        }
        installer.Apply(pending, currentVersion, () => ClientProcesses.AnyOsuRunning(storage));
        File.Delete(sessionPath);
        return 0;
    }

    private static void PrintStatus(UpdateStorage storage) => Console.WriteLine(UpdateStorage.ReadJson<UpdateStatus>(Path.Combine(storage.WorkDirectory, "status.json"))?.Message);
    private sealed record LastCheck(string Version, DateTimeOffset At);

    private sealed record Options(bool Help, bool Check, bool Watch, string? Version, string? Rulesets, string? Session)
    {
        public static Options Parse(string[] args)
        {
            bool help = false, check = false, watch = false, apply = false;
            string? version = null, rulesets = null, session = null;
            for (int i = 0; i < args.Length; i++)
            {
                string value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "--help": case "-h": help = true; break;
                    case "--check": check = true; break;
                    case "--apply": apply = true; break;
                    case "--watch": watch = true; break;
                    case "--client-version": version = ClientVersion.Parse(value()) ?? throw new ArgumentException("Invalid client version."); break;
                    case "--rulesets": rulesets = value(); break;
                    case "--session": session = value(); break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            if ((check ? 1 : 0) + (apply ? 1 : 0) + (watch ? 1 : 0) > 1) throw new ArgumentException("Choose --check, --apply, or --watch.");
            if (args.Length > 0 && !help && !check && !apply && !watch) throw new ArgumentException("Specify --check or --apply.");
            return new Options(help, check, watch, version, rulesets, session);
        }
    }
}
