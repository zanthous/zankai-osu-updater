namespace RulesetUpdater;

internal sealed record DesktopCheckResult(string Summary, string Details, int ReadyCount);
internal sealed record DesktopInstallResult(string Message, bool ExitForSelfUpdate = false);

internal interface IDesktopUpdater
{
    Task<ManagedRuleset[]> FindAsync(string repository, CancellationToken cancellation);
    Task<string> AddAsync(ManagedRuleset ruleset, CancellationToken cancellation);
    Task<DesktopCheckResult> CheckAsync(CancellationToken cancellation);
    Task<DesktopInstallResult> InstallAsync(CancellationToken cancellation);
}

// The window takes the same process lock as the startup worker, but only during an action.
// Leaving the window open must not prevent osu!'s automatic updater from running.
internal sealed class DesktopUpdater(string directory, HttpClient http,
                                     Func<UpdateStorage, bool>? clientRunning = null,
                                     Func<UpdateStorage, string?>? clientVersion = null,
                                     bool checkSelf = true) : IDesktopUpdater
{
    private readonly GitHubSource source = new(http);
    private readonly Func<UpdateStorage, bool> isRunning = clientRunning ?? ClientProcesses.AnyOsuRunning;

    private static FileStream acquire(UpdateStorage storage)
    {
        try { return new FileStream(UpdateStorage.Child(storage.WorkDirectory, "updater.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Another updater is running. Close osu!, wait a moment, then try again."); }
    }

    public async Task<ManagedRuleset[]> FindAsync(string repository, CancellationToken cancellation)
    {
        repository = RepositoryInput(repository);
        var storage = new UpdateStorage(directory);
        using var guard = acquire(storage);
        var catalog = await new CatalogSource(http, storage).LoadAsync(cancellation);
        var known = catalog.Rulesets.Where(r => r.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (known.Length > 0)
        {
            var enabled = known.Where(r => r.Enabled).ToArray();
            if (enabled.Length == 0) throw new InvalidOperationException(known[0].DisabledReason ?? "This gamemode is disabled in the catalog.");
            return enabled;
        }
        var discovered = await CatalogSource.DiscoverAsync(repository, source, storage, cancellation, includeMissing: true);
        var allowed = discovered.Where(d => !catalog.Rulesets.Any(c => c.AssemblyName == d.AssemblyName)).ToArray();
        if (allowed.Length == 0)
            throw new InvalidOperationException("No supported gamemode DLL was found. The latest release must contain an osu.Game.Rulesets.Name.dll file, and must not replace another catalog source.");
        return allowed;
    }

    internal static string RepositoryInput(string value)
    {
        // Users commonly copy a repository's Releases page, rather than its root URL.
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.Host == "github.com" && uri.UserInfo.Length == 0 && uri.IsDefaultPort)
        {
            string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[2] == "releases") return RulesetCatalog.Repository(parts[0] + "/" + parts[1]);
        }
        return RulesetCatalog.Repository(value);
    }

    public async Task<string> AddAsync(ManagedRuleset ruleset, CancellationToken cancellation)
    {
        new RulesetCatalog(1, [ruleset]).Validate();
        var storage = new UpdateStorage(directory);
        using var guard = acquire(storage);
        var catalogs = new CatalogSource(http, storage);
        var catalog = await catalogs.LoadAsync(cancellation);
        var existing = catalog.Rulesets.SingleOrDefault(c => c.AssemblyName == ruleset.AssemblyName);
        if (existing != null)
        {
            if (!existing.Enabled) throw new InvalidOperationException(existing.DisabledReason ?? "This gamemode is disabled in the catalog.");
            if (!existing.Repository.Equals(ruleset.Repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This gamemode is managed by a different catalog source and cannot be overridden.");
            ruleset = existing;
        }
        cancellation.ThrowIfCancellationRequested();
        catalogs.AddExternal([ruleset with { InstallIfMissing = true }]);
        return $"Added {ruleset.Name}. Use Check for updates to install it, or it will be checked the next time osu! starts.";
    }

    private (string Version, ClientCompatibility? Compatibility) client(UpdateStorage storage)
    {
        var previous = UpdateStorage.ReadJson<ClientSession>(UpdateStorage.Child(storage.WorkDirectory, "last-client.json"));
        string? version = clientVersion != null ? clientVersion(storage) : previous != null ? ClientProcesses.ReadClientVersion(previous) : null;
        if (version == null) throw new InvalidOperationException("Launch osu! once with Zankai Updater installed, then close osu! and try again. This lets the updater identify the correct client version.");
        return (version, previous != null ? ClientCompatibility.Read(previous.AssemblyPath) : null);
    }

    private static (ManagedRuleset Rule, Installer Installer)[] installers(UpdateStorage storage, RulesetCatalog catalog) =>
        catalog.Rulesets.Select(rule => (Rule: rule, Installer: new Installer(storage, rule)))
            .Where(x => x.Rule.InstallIfMissing || storage.FindInstalled(x.Rule).Length > 0 || x.Installer.HasTransaction).ToArray();

    public async Task<DesktopCheckResult> CheckAsync(CancellationToken cancellation)
    {
        var storage = new UpdateStorage(directory);
        using var guard = acquire(storage);
        if (isRunning(storage)) throw new InvalidOperationException("Close osu! before checking updates here. Automatic updates are already checked when osu! starts.");
        var (version, compatibility) = client(storage);
        var catalog = await new CatalogSource(http, storage).LoadAsync(cancellation);
        storage.RemoveEmptyRulesetDirectories(catalog.Rulesets);
        var messages = new List<string>();
        int ready = 0;
        bool current = true;
        foreach (var (rule, installer) in installers(storage, catalog))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                installer.Recover();
                if (await installer.PrepareAsync(source, version, cancellation, compatibility) != null) ready++;
                var status = installer.Status;
                current &= status?.State is "current" or "newer-installed" or "development-build" or "disabled";
                messages.Add($"{rule.Name}: {status?.Message}");
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested)
            {
                current = false;
                installer.RecordFailure(e);
                messages.Add($"{rule.Name}: {e.Message}");
            }
        }
        if (checkSelf && SelfUpdateRunner.Available(storage))
        {
            try
            {
                var self = new SelfUpdate(storage);
                var previous = UpdateStorage.ReadJson<SelfUpdatePlan>(self.PlanPath);
                if (previous is { Applying: true } or { Committed: true })
                {
                    ready++;
                    messages.Add("Zankai Updater: an interrupted update is ready to finish.");
                }
                else if (await self.PrepareAsync(source, SelfUpdateRunner.Version, cancellation) is { } update)
                {
                    ready++;
                    messages.Add($"Zankai Updater: {update.ReleaseTag} is ready.");
                }
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested) { current = false; messages.Add($"Zankai Updater: {e.Message}"); }
        }
        string summary = ready > 0 ? $"{ready} update{(ready == 1 ? "" : "s")} ready to install"
            : current ? "Everything is up to date" : "No updates ready to install — see details below";
        return new DesktopCheckResult(summary, string.Join(Environment.NewLine + Environment.NewLine, messages), ready);
    }

    public async Task<DesktopInstallResult> InstallAsync(CancellationToken cancellation)
    {
        var storage = new UpdateStorage(directory);
        using var guard = acquire(storage);
        if (isRunning(storage)) throw new InvalidOperationException("Close osu! before installing updates.");
        var (version, _) = client(storage);
        var catalog = await new CatalogSource(http, storage).LoadAsync(cancellation);
        int installed = 0;
        foreach (var (_, installer) in installers(storage, catalog))
        {
            cancellation.ThrowIfCancellationRequested();
            if (isRunning(storage)) throw new InvalidOperationException("osu! started. Close it before continuing.");
            installer.Recover();
            var pending = UpdateStorage.ReadJson<StagedUpdate>(installer.PendingPath);
            if (pending == null) continue;
            installer.Apply(pending, version, () => isRunning(storage));
            installed++;
        }
        var self = new SelfUpdate(storage);
        if (File.Exists(self.PlanPath))
        {
            if (isRunning(storage)) throw new InvalidOperationException("Close osu! before updating Zankai Updater.");
            SelfUpdateRunner.Start(storage);
            return new DesktopInstallResult("Finishing the updater update…", true);
        }
        return new DesktopInstallResult(installed == 0 ? "Everything is up to date" : $"Installed {installed} update{(installed == 1 ? "" : "s")}. You can open osu! now.");
    }
}
