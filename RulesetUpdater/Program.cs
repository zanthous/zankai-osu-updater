namespace RulesetUpdater;

internal static partial class Program
{
    // Full-extraction bundles may place managed assemblies in a runtime cache.
    // User files always belong next to the actual standalone executable.
    internal static string DefaultRulesetsDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static async Task<int> RunCommandAsync(string[] args)
    {
        UpdateStorage? storage = null;
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("Zankai Updater\n\nChecks the catalog and itself once at osu! startup. Applies after osu! closes.\n\n  --check                  Check only; no DLL or program downloads\n  --apply                  Download and install while osu! is closed\n  --list                   List catalog sources\n  --add-source OWNER/REPO   Add a trusted external source for an installed DLL\n  --yes                    Confirm trusting the specified external repository\n  --client-version TAG     Exact version, e.g. 2026.1005.0-lazer\n  --client-path PATH       Client assembly or osu.Game.dll for compatibility checks\n  --rulesets PATH          Use a specific rulesets directory\n  --help                   Show this help");
                return 0;
            }
            storage = new UpdateStorage(options.Rulesets ?? DefaultRulesetsDirectory);
            if (options.FinishSelfUpdate) await SelfUpdateRunner.WaitForParent(options.WaitProcess, options.WaitStart);
            string lockPath = UpdateStorage.Child(storage.WorkDirectory, "updater.lock");
            FileStream updaterLock;
            try { updaterLock = await AcquireLock(lockPath, options.Watch || options.FinishSelfUpdate); }
            catch (IOException)
            {
                Console.WriteLine("An updater is already running for this folder.");
                return 2;
            }
            using (updaterLock)
            using (var cancellation = new CancellationTokenSource())
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) })
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                var token = cancellation.Token;
                var self = new SelfUpdate(storage);
                if (options.FinishSelfUpdate)
                {
                    string runner = Path.Combine(storage.WorkDirectory, "install-runner", SelfUpdate.HelperName);
                    if (!ExecutablePath.IsExpected(Environment.ProcessPath, runner))
                        throw new InvalidOperationException("Self-replacement must run from the temporary install runner.");
                    if (ClientProcesses.AnyOsuRunning(storage)) return 0;
                    self.Recover();
                    if (File.Exists(self.PlanPath)) self.Apply(() => ClientProcesses.AnyOsuRunning(storage));
                    return 0;
                }
                SelfUpdateRunner.CleanRunner(storage);
                var oldSelf = UpdateStorage.ReadJson<SelfUpdatePlan>(self.PlanPath);
                if (!options.Check && !options.List && options.AddSource == null && oldSelf is ({ Applying: true } or { Committed: true }))
                {
                    if (ClientProcesses.AnyOsuRunning(storage))
                        throw new IOException("Close osu! and run ZankaiUpdater once to finish an interrupted updater replacement.");
                    SelfUpdateRunner.Start(storage);
                    return 0;
                }

                var source = new GitHubSource(http);
                var catalogs = new CatalogSource(http, storage);
                RulesetCatalog catalog = await catalogs.LoadAsync(token);
                if (options.List)
                {
                    foreach (var rule in catalog.Rulesets) Console.WriteLine($"{rule.Name}: {rule.Repository} [{string.Join(", ", rule.Channels)}]{(rule.Enabled ? rule.InstallIfMissing ? " — install automatically" : " — update if installed" : " — " + rule.DisabledReason)}");
                    return 0;
                }
                if (options.AddSource != null)
                {
                    var discovered = await CatalogSource.DiscoverAsync(options.AddSource, source, storage, token);
                    var added = discovered.Where(d => !catalog.Rulesets.Any(c => c.AssemblyName == d.AssemblyName)).ToArray();
                    if (added.Length == 0) throw new InvalidOperationException("No unmanaged installed ruleset matches a plain DLL in this repository's latest release. Catalog entries cannot be overridden.");
                    Console.WriteLine($"Trust {RulesetCatalog.Repository(options.AddSource)} to provide executable updates for {string.Join(", ", added.Select(a => a.Name))}?");
                    if (!options.Yes)
                    {
                        if (Console.IsInputRedirected) throw new ArgumentException("Pass --yes to confirm trusting this repository.");
                        Console.Write("[y/N] ");
                        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 0;
                    }
                    catalogs.AddExternal(added);
                    Console.WriteLine("Source added. It will be checked at the next launch (stable lazer only).");
                    return 0;
                }

                storage.RemoveEmptyRulesetDirectories(catalog.Rulesets);
                var installers = catalog.Rulesets.Select(rule => (Rule: rule, Installer: new Installer(storage, rule)))
                    .Where(x => x.Rule.InstallIfMissing || storage.FindInstalled(x.Rule).Length > 0 || x.Installer.HasTransaction).ToArray();
                if (options.Watch)
                {
                    await Watch(options, storage, source, installers, self, token);
                    return 0;
                }

                var previous = UpdateStorage.ReadJson<ClientSession>(UpdateStorage.Child(storage.WorkDirectory, "last-client.json"));
                string? version = options.Version ?? (previous != null ? ClientProcesses.ReadClientVersion(previous) : null);
                if (version == null) throw new ArgumentException("Specify --client-version with your exact osu! version.");
                if (options.Check)
                {
                    foreach (var (rule, _) in installers)
                    {
                        UpdateCandidate? match = await source.FindAsync(rule, version, token);
                        Console.WriteLine($"{rule.Name}: {(match == null ? rule.DisabledReason ?? "no matching release" : match.ReleaseTag + " / " + match.Asset.Name + " (compatibility checked before installation)")}");
                    }
                    Release? latest = await source.LatestAsync(CatalogSource.UpdaterRepository, token);
                    Console.WriteLine($"Zankai Updater: latest {latest?.Tag ?? "unavailable"}; this build {SelfUpdateRunner.Version}");
                    return 0;
                }
                if (ClientProcesses.AnyOsuRunning(storage)) throw new InvalidOperationException("Close osu! before applying updates.");
                string? clientPath = options.ClientPath ?? (previous != null && ClientProcesses.ReadClientVersion(previous) == version ? previous.AssemblyPath : null);
                var compatibility = ClientCompatibility.Read(clientPath);
                foreach (var (_, installer) in installers) installer.Recover();
                int failures = await Prepare(source, installers, version, compatibility, token);
                await PrepareSelf(storage, source, self, token);
                failures += Apply(storage, installers, version);
                if (File.Exists(self.PlanPath)) SelfUpdateRunner.Start(storage);
                return failures == 0 ? 0 : 1;
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("Update cancelled."); return 2; }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            try { storage?.Status("error", e.Message); } catch { }
            return 1;
        }
    }

    private static async Task<FileStream> AcquireLock(string path, bool wait)
    {
        // Let a previous launch finish its short close/restart handoff before acquiring the lock.
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (wait && attempt < 15) { await Task.Delay(TimeSpan.FromSeconds(1)); }
        }
    }

    private static async Task<int> Prepare(GitHubSource source, (ManagedRuleset Rule, Installer Installer)[] installers,
                                          string version, ClientCompatibility? compatibility, CancellationToken cancellation)
    {
        int failures = 0;
        foreach (var (rule, installer) in installers)
        {
            try
            {
                await installer.PrepareAsync(source, version, cancellation, compatibility);
                Console.WriteLine($"{rule.Name}: {installer.Status?.Message}");
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                failures++;
                installer.RecordFailure(e);
                Console.Error.WriteLine($"{rule.Name}: {e.Message}");
            }
        }
        return failures;
    }

    private static int Apply(UpdateStorage storage, (ManagedRuleset Rule, Installer Installer)[] installers, string version)
    {
        int failures = 0;
        foreach (var (rule, installer) in installers)
        {
            try
            {
                installer.Recover();
                var pending = UpdateStorage.ReadJson<StagedUpdate>(installer.PendingPath);
                if (pending == null) continue;
                installer.Apply(pending, version, () => ClientProcesses.AnyOsuRunning(storage));
                Console.WriteLine(installer.Status?.Message);
            }
            catch (Exception e) { failures++; storage.Status("install-failed", $"{rule.Name}: {e.Message}"); }
        }
        return failures;
    }

    private static async Task PrepareSelf(UpdateStorage storage, GitHubSource source, SelfUpdate self, CancellationToken cancellation)
    {
        if (!SelfUpdateRunner.Available(storage)) return;
        try
        {
            var pending = await self.PrepareAsync(source, SelfUpdateRunner.Version, cancellation);
            if (pending != null) storage.Status("self-update-ready", $"Zankai Updater {pending.ReleaseTag} will install after osu! closes.");
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested) { storage.Status("self-check-failed", e.Message); }
    }

    private static async Task<bool> Watch(Options options, UpdateStorage storage, GitHubSource source,
                                         (ManagedRuleset Rule, Installer Installer)[] installers, SelfUpdate self, CancellationToken cancellation)
    {
        string sessionPath = Path.GetFullPath(options.Session ?? throw new ArgumentException("Missing session."));
        if (Path.GetDirectoryName(sessionPath) != storage.WorkDirectory || !Path.GetFileName(sessionPath).StartsWith("session-", StringComparison.Ordinal))
            throw new ArgumentException("Invalid session path.");
        ClientSession session = UpdateStorage.ReadJson<ClientSession>(sessionPath) ?? throw new InvalidDataException("Missing client session.");
        if (session.Schema != 1 || !session.OfficialBuild || ClientVersion.Parse(session.Version) != session.Version || !ClientProcesses.Running(session))
            throw new InvalidDataException("The osu! session is no longer valid.");
        UpdateStorage.WriteJson(UpdateStorage.Child(storage.WorkDirectory, "last-client.json"), session);
        try
        {
            // One network pass for this launch. This loop only waits for osu! to close.
            await Prepare(source, installers, session.Version, ClientCompatibility.Read(session.AssemblyPath), cancellation);
            await PrepareSelf(storage, source, self, cancellation);
            while (ClientProcesses.Running(session)) await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
            if (ClientProcesses.AnyOsuRunning(storage)) return false;
            string? version = ClientProcesses.ReadClientVersion(session);
            if (version == session.Version) Apply(storage, installers, version);
            else storage.Status("waiting-for-client", "osu! changed installation or version. The next launch will check for matching builds.");
            if (!File.Exists(self.PlanPath)) return false;
            SelfUpdateRunner.Start(storage);
            return true;
        }
        finally { if (!ClientProcesses.Running(session)) File.Delete(sessionPath); }
    }

    private sealed record Options
    {
        public bool Help, Check, Watch, List, Yes, FinishSelfUpdate;
        public string? Version, Rulesets, Session, AddSource, ClientPath;
        public int WaitProcess;
        public long WaitStart;
        public static Options Parse(string[] args)
        {
            var o = new Options();
            int modes = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "--help": case "-h": o.Help = true; break;
                    case "--check": o.Check = true; modes++; break;
                    case "--apply": modes++; break;
                    case "--watch": o.Watch = true; modes++; break;
                    case "--list": o.List = true; modes++; break;
                    case "--add-source": o.AddSource = RulesetCatalog.Repository(value()); modes++; break;
                    case "--yes": o.Yes = true; break;
                    case "--finish-self-update": o.FinishSelfUpdate = true; modes++; break;
                    case "--wait-process": o.WaitProcess = int.Parse(value()); break;
                    case "--wait-start": o.WaitStart = long.Parse(value()); break;
                    case "--client-version": o.Version = ClientVersion.Parse(value()) ?? throw new ArgumentException("Invalid client version."); break;
                    case "--client-path": o.ClientPath = value(); break;
                    case "--rulesets": o.Rulesets = value(); break;
                    case "--session": o.Session = value(); break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            if (modes > 1 || (args.Length > 0 && !o.Help && modes == 0)) throw new ArgumentException("Choose one action: --check, --apply, --list, or --add-source.");
            return o;
        }
    }
}
