using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RulesetUpdater;

public sealed record SelfUpdateFile(string Name, string Sha256, string OriginalHash);
public sealed record SelfUpdatePlan(string ReleaseTag, string Stage, SelfUpdateFile[] Files, bool Applying = false, bool Committed = false);

public sealed class SelfUpdate(UpdateStorage storage)
{
    public const string BridgeName = "osu.Game.Rulesets.ZankaiUpdater.dll";
    public static string HelperName => OperatingSystem.IsWindows() ? "ZankaiUpdater.exe" : "ZankaiUpdater";
    public string PlanPath => UpdateStorage.Child(storage.WorkDirectory, "self-update.json");
    public static string Runtime
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : throw new PlatformNotSupportedException();
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new PlatformNotSupportedException(),
            };
            return os + "-" + arch;
        }
    }
    public static string AssetName => $"ZankaiUpdater-{Runtime}" + (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");

    public async Task<SelfUpdatePlan?> PrepareAsync(GitHubSource source, string currentVersion, CancellationToken cancellationToken)
    {
        SelfUpdatePlan? previous = UpdateStorage.ReadJson<SelfUpdatePlan>(PlanPath);
        if (previous is { Applying: true } or { Committed: true }) throw new IOException("An interrupted updater replacement needs recovery while osu! is closed.");
        if (previous != null) Clean(previous);
        Release? release = await source.LatestAsync(CatalogSource.UpdaterRepository, cancellationToken);
        if (release == null || !ReleaseSelection.IsNewerInstalledVersion(release.Tag, currentVersion)) return null;
        ReleaseAsset[] assets = release.Assets.Where(a => a.Name == AssetName).ToArray();
        if (assets.Length == 0) return null;
        if (assets.Length != 1) throw new InvalidDataException("Ambiguous updater packages.");

        string stageName = "self-" + Guid.NewGuid().ToString("N");
        string stage = UpdateStorage.Child(storage.WorkDirectory, stageName);
        Directory.CreateDirectory(stage);
        string archive = UpdateStorage.Child(stage, "package");
        try
        {
            await source.DownloadAssetAsync(CatalogSource.UpdaterRepository, assets[0], archive, 100 * 1024 * 1024, cancellationToken);
            Extract(archive, stage, OperatingSystem.IsWindows());
            var bridge = AssemblyInfo.Read(UpdateStorage.Child(stage, BridgeName));
            if (bridge.Name + ".dll" != BridgeName || bridge.InformationalVersion.Split('+')[0] != release.Tag.TrimStart('v').Split('+')[0])
                throw new InvalidDataException("Updater package has an unexpected bridge identity or version.");
            SetExecutable(UpdateStorage.Child(stage, HelperName));
            SelfUpdateFile[] files = new[] { HelperName, BridgeName }.Select(name => new SelfUpdateFile(name,
                UpdateStorage.Hash(UpdateStorage.Child(stage, name)), UpdateStorage.Hash(UpdateStorage.Child(storage.RulesetsDirectory, name)))).ToArray();
            var plan = new SelfUpdatePlan(release.Tag, stageName, files);
            File.Delete(archive);
            UpdateStorage.WriteJson(PlanPath, plan);
            return plan;
        }
        catch
        {
            // This directory is ours; delete only the three known files, never an unexpected tree.
            foreach (string name in new[] { "package", HelperName, BridgeName }) File.Delete(UpdateStorage.Child(stage, name));
            Directory.Delete(stage);
            throw;
        }
    }

    public static void Extract(string archive, string destination, bool zip)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void write(string name, long size, Stream? input)
        {
            string helper = zip ? "ZankaiUpdater.exe" : "ZankaiUpdater";
            if ((name != BridgeName && name != helper) || !found.Add(name) || input == null || size <= 0 || size > 200 * 1024 * 1024 || (total += size) > 220 * 1024 * 1024)
                throw new InvalidDataException("Updater archive contains unexpected, duplicated, or oversized files.");
            using var output = new FileStream(UpdateStorage.Child(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[64 * 1024];
            long written = 0;
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                written += count;
                if (written > size) throw new InvalidDataException("Archive entry exceeded its declared size.");
                output.Write(buffer, 0, count);
            }
            if (written != size) throw new InvalidDataException("Truncated updater archive.");
            output.Flush(true);
        }
        using var file = File.OpenRead(archive);
        if (zip)
        {
            using var reader = new ZipArchive(file, ZipArchiveMode.Read);
            foreach (var entry in reader.Entries)
            {
                int type = (entry.ExternalAttributes >> 16) & 0xf000;
                if (type != 0 && type != 0x8000) throw new InvalidDataException("Links and directories are not allowed in updater packages.");
                using var input = entry.Open();
                write(entry.FullName, entry.Length, input);
            }
        }
        else
        {
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Links and directories are not allowed in updater packages.");
                write(entry.Name, entry.Length, entry.DataStream);
            }
        }
        if (found.Count != 2) throw new InvalidDataException("Updater package must contain the bridge and helper.");
    }

    public void Apply(Func<bool> clientRunning, Action<int>? afterReplaceForTest = null)
    {
        var plan = UpdateStorage.ReadJson<SelfUpdatePlan>(PlanPath) ?? throw new InvalidDataException("No updater replacement is staged.");
        Validate(plan);
        if (plan.Applying || plan.Committed) throw new IOException("Recover the previous updater transaction first.");
        if (clientRunning()) throw new IOException("osu! is running; updater replacement deferred.");
        string stage = UpdateStorage.Child(storage.WorkDirectory, plan.Stage);
        foreach (var item in plan.Files)
        {
            if (UpdateStorage.Hash(UpdateStorage.Child(storage.RulesetsDirectory, item.Name)) != item.OriginalHash
                || UpdateStorage.Hash(UpdateStorage.Child(stage, item.Name)) != item.Sha256)
                throw new IOException("Updater files changed since downloading.");
            CopyAtomic(UpdateStorage.Child(storage.RulesetsDirectory, item.Name), UpdateStorage.Child(stage, item.Name + ".old"), item.Name == HelperName);
        }
        plan = plan with { Applying = true };
        UpdateStorage.WriteJson(PlanPath, plan);
        try
        {
            int count = 0;
            foreach (var item in plan.Files)
            {
                if (clientRunning()) throw new IOException("osu! restarted; updater replacement cancelled.");
                CopyAtomic(UpdateStorage.Child(stage, item.Name), UpdateStorage.Child(storage.RulesetsDirectory, item.Name), item.Name == HelperName);
                afterReplaceForTest?.Invoke(++count);
            }
            plan = plan with { Committed = true };
            UpdateStorage.WriteJson(PlanPath, plan);
        }
        catch { Recover(); throw; }
        Clean(plan);
        storage.Status("self-updated", $"Installed Zankai Updater {plan.ReleaseTag}.");
    }

    // Caller holds the updater lock and has verified that osu! is closed.
    public void Recover()
    {
        var plan = UpdateStorage.ReadJson<SelfUpdatePlan>(PlanPath);
        if (plan == null || (!plan.Applying && !plan.Committed)) return;
        Validate(plan);
        string stage = UpdateStorage.Child(storage.WorkDirectory, plan.Stage);
        foreach (var item in plan.Files)
        {
            string target = UpdateStorage.Child(storage.RulesetsDirectory, item.Name);
            string hash = UpdateStorage.Hash(target);
            if (plan.Committed)
            {
                if (hash != item.Sha256) throw new IOException("Installed updater changed before cleanup.");
            }
            else
            {
                if (hash == item.OriginalHash) continue;
                if (hash != item.Sha256) throw new IOException("Updater was changed externally; recovery stopped.");
                string original = UpdateStorage.Child(stage, item.Name + ".old");
                if (UpdateStorage.Hash(original) != item.OriginalHash) throw new IOException("Updater recovery file failed verification.");
                CopyAtomic(original, target, item.Name == HelperName);
            }
        }
        Clean(plan);
    }

    private static void Validate(SelfUpdatePlan plan)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(plan.Stage, "^self-[0-9a-f]{32}$") || plan.Files.Length != 2
            || plan.Files[0].Name != HelperName || plan.Files[1].Name != BridgeName)
            throw new InvalidDataException("Invalid updater transaction.");
    }

    private void Clean(SelfUpdatePlan plan)
    {
        Validate(plan);
        string stage = UpdateStorage.Child(storage.WorkDirectory, plan.Stage);
        if (Directory.Exists(stage))
        {
            foreach (var item in plan.Files)
            {
                File.Delete(UpdateStorage.Child(stage, item.Name));
                File.Delete(UpdateStorage.Child(stage, item.Name + ".old"));
            }
            File.Delete(UpdateStorage.Child(stage, "package"));
            Directory.Delete(stage);
        }
        File.Delete(PlanPath);
    }

    public static void CopyAtomic(string source, string destination, bool executable)
    {
        UpdateStorage.RequireRegularPath(destination);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (executable) SetExecutable(temporary);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                                               | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
