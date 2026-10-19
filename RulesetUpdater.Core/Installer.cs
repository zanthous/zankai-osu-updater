namespace RulesetUpdater;

public sealed record InstallJournal(string Destination, string Hash, string BackupDirectory, InstalledFile[] Originals, bool Installing = false, bool Committed = false);

public sealed class Installer(UpdateStorage root, ManagedRuleset ruleset)
{
    private readonly UpdateStorage storage = root.ForRuleset(ruleset);
    public UpdateStatus? Status => UpdateStorage.ReadJson<UpdateStatus>(Path.Combine(storage.WorkDirectory, "status.json"));
    public string PendingPath => Path.Combine(storage.WorkDirectory, "pending.json");
    private string JournalPath => Path.Combine(storage.WorkDirectory, "transaction.json");
    public bool HasTransaction => File.Exists(JournalPath);
    public void RecordFailure(Exception error) => storage.Status("check-failed", $"{ruleset.Name}: {error.Message}");

    public async Task<StagedUpdate?> PrepareAsync(GitHubSource source, string clientVersion, CancellationToken cancellationToken, ClientCompatibility? compatibility = null)
    {
        // A failed/empty fresh check must not leave a stale pending update eligible for installation.
        if (File.Exists(PendingPath)) File.Delete(PendingPath);
        if (File.Exists(JournalPath)) throw new InvalidOperationException("Close osu! to recover an interrupted installation first.");
        InstalledFile[] installed = storage.FindInstalled(ruleset);
        if (!ruleset.Enabled)
        {
            storage.Status("disabled", ruleset.DisabledReason ?? "Updates disabled for this ruleset.");
            return null;
        }
        if (installed.Length == 0 && !ruleset.InstallIfMissing)
        {
            storage.Status("not-installed", $"{ruleset.Name} is not installed in this rulesets folder.");
            return null;
        }
        if (installed.Any(f => AssemblyInfo.Read(UpdateStorage.Child(storage.RulesetsDirectory, f.Name)).DevelopmentBuild))
        {
            storage.Status("development-build", "Leaving the installed development build untouched.");
            return null;
        }

        UpdateCandidate? candidate = await source.FindAsync(ruleset, clientVersion, cancellationToken);
        if (candidate == null)
        {
            storage.Status("waiting-for-build", $"No {ruleset.Name} release matches osu! {clientVersion} yet.");
            return null;
        }
        string hash = GitHubSource.ExpectedHash(candidate.Asset);
        if (installed.Any(f => f.Name.EndsWith(".broken", StringComparison.OrdinalIgnoreCase) && f.Sha256 == hash))
        {
            storage.Status("waiting-for-fix", "This release was already marked broken by lazer. Waiting for a different build.");
            return null;
        }
        if (installed.Length == 1 && installed[0].Sha256 == hash)
        {
            storage.Status("current", $"{ruleset.Name} is up to date for osu! {clientVersion}.");
            return null;
        }
        if (installed.Any(f => ReleaseSelection.IsNewerInstalledVersion(f.InformationalVersion, candidate.ReleaseTag)))
        {
            storage.Status("newer-installed", $"Leaving the newer installed {ruleset.Name} version untouched.");
            return null;
        }

        if (!ruleset.ExactClient && compatibility == null)
        {
            storage.Status("unverified-client", "Generic DLL updates need the client's osu.Game.dll for compatibility checks. Launch osu! once or pass --client-path.");
            return null;
        }

        storage.EnsureWorkDirectory();
        string stagedName = hash + ".download";
        string stagedPath = UpdateStorage.Child(storage.WorkDirectory, stagedName);
        if (File.Exists(stagedPath) && UpdateStorage.Hash(stagedPath) != hash) File.Delete(stagedPath);
        if (!File.Exists(stagedPath)) await source.DownloadAsync(ruleset, candidate, stagedPath, cancellationToken);
        AssemblyInfo info = AssemblyInfo.Read(stagedPath);
        if (info.Name != ruleset.AssemblyName) throw new InvalidDataException("Wrong staged assembly.");
        if (!ruleset.ExactClient && compatibility!.Rejection(info) is string rejection)
        {
            File.Delete(stagedPath);
            storage.Status("incompatible", rejection);
            return null;
        }
        var pending = new StagedUpdate(clientVersion, candidate.ReleaseTag, hash, stagedName, installed);
        UpdateStorage.WriteJson(PendingPath, pending);
        storage.Status("ready", $"{ruleset.Name} {candidate.ReleaseTag} is ready. Close osu! to apply it.");
        return pending;
    }

    public void Apply(StagedUpdate pending, string currentClientVersion, Func<bool> clientRunning, Action<int>? afterMoveForTest = null)
    {
        if (pending.ClientVersion != currentClientVersion) throw new InvalidOperationException("osu! changed version; the pending update needs to be checked again.");
        if (clientRunning()) throw new InvalidOperationException("Close osu! before installing updates.");
        if (File.Exists(JournalPath)) throw new InvalidOperationException("An interrupted installation must be recovered first.");
        InstalledFile[] current = storage.FindInstalled(ruleset);
        if (!current.SequenceEqual(pending.Originals)) throw new InvalidOperationException("The installed DLLs changed after downloading. Check for updates again.");

        string staged = UpdateStorage.Child(storage.WorkDirectory, pending.StagedFile);
        if (UpdateStorage.Hash(staged) != pending.Sha256 || AssemblyInfo.Read(staged).Name != ruleset.AssemblyName)
            throw new InvalidDataException("Staged DLL verification failed.");
        string destination = UpdateStorage.Child(storage.RulesetsDirectory, ruleset.InstalledFileName);
        if (File.Exists(destination) && !current.Any(f => f.Name == ruleset.InstalledFileName))
            throw new IOException("Another file occupies the install destination.");

        string backupName = "replacement-" + Guid.NewGuid().ToString("N");
        string backup = UpdateStorage.Child(storage.WorkDirectory, backupName);
        Directory.CreateDirectory(backup);
        var journal = new InstallJournal(ruleset.InstalledFileName, pending.Sha256, backupName, current);
        UpdateStorage.WriteJson(JournalPath, journal);
        try
        {
            int count = 0;
            foreach (InstalledFile original in current)
            {
                if (clientRunning()) throw new InvalidOperationException("osu! restarted before installation completed.");
                File.Move(UpdateStorage.Child(storage.RulesetsDirectory, original.Name), UpdateStorage.Child(backup, original.Name));
                afterMoveForTest?.Invoke(++count);
            }
            if (clientRunning()) throw new InvalidOperationException("osu! restarted before installation completed.");
            journal = journal with { Installing = true };
            UpdateStorage.WriteJson(JournalPath, journal);
            File.Move(staged, destination);
            journal = journal with { Committed = true };
            UpdateStorage.WriteJson(JournalPath, journal);
        }
        catch
        {
            Recover();
            throw;
        }
        // The replacement succeeded. Delete the temporary originals, not retain a rollback archive.
        // Keep the committed journal until cleanup finishes so an interruption cannot strand old DLLs.
        cleanUp(journal);
        storage.Status("installed", $"Installed {ruleset.Name} {pending.ReleaseTag} for osu! {currentClientVersion}.");
    }

    // Run only while osu! is closed. Temporary originals exist only until replacement completes.
    public bool Recover()
    {
        InstallJournal? journal = UpdateStorage.ReadJson<InstallJournal>(JournalPath);
        if (journal == null) return false;
        if (journal.Destination != ruleset.InstalledFileName) throw new InvalidDataException("Unexpected transaction destination.");
        string backup = UpdateStorage.Child(storage.WorkDirectory, journal.BackupDirectory);
        string destination = UpdateStorage.Child(storage.RulesetsDirectory, journal.Destination);
        if (journal.Committed)
        {
            if (!File.Exists(destination) || UpdateStorage.Hash(destination) != journal.Hash)
                throw new IOException("The installed DLL changed before replacement cleanup completed.");
            cleanUp(journal);
            storage.Status("installed", "Finished cleaning up a completed installation.");
            return true;
        }
        if (journal.Installing && File.Exists(destination))
        {
            if (UpdateStorage.Hash(destination) != journal.Hash) throw new IOException("Installation destination was modified; backups have been retained.");
            File.Delete(destination);
        }
        foreach (InstalledFile original in journal.Originals)
        {
            string saved = UpdateStorage.Child(backup, original.Name);
            string target = UpdateStorage.Child(storage.RulesetsDirectory, original.Name);
            if (File.Exists(target))
            {
                if (UpdateStorage.Hash(target) != original.Sha256) throw new IOException("Original DLL was modified; backups have been retained.");
                continue;
            }
            if (!File.Exists(saved) || UpdateStorage.Hash(saved) != original.Sha256) throw new IOException("Cannot recover the original DLL from its backup.");
            File.Move(saved, target);
        }
        cleanUp(journal);
        storage.Status("recovered", "Recovered the previous DLLs after an interrupted installation.");
        return true;
    }

    private void cleanUp(InstallJournal journal)
    {
        string temporary = UpdateStorage.Child(storage.WorkDirectory, journal.BackupDirectory);
        if (Directory.Exists(temporary))
        {
            foreach (InstalledFile original in journal.Originals)
            {
                string saved = UpdateStorage.Child(temporary, original.Name);
                if (!File.Exists(saved)) continue;
                if (UpdateStorage.Hash(saved) != original.Sha256)
                    throw new IOException("A temporary original DLL changed; cleanup was stopped.");
                File.Delete(saved);
            }
            // Do not recursively delete anything unexpected that appeared in this folder.
            Directory.Delete(temporary);
        }
        if (journal.Committed && UpdateStorage.ReadJson<StagedUpdate>(PendingPath)?.Sha256 == journal.Hash)
            File.Delete(PendingPath);
        File.Delete(JournalPath);
    }
}
