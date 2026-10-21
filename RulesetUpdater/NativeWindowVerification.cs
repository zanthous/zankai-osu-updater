using static RulesetUpdater.Win32;

namespace RulesetUpdater;

// Runs the actual published controls and asynchronous event handlers without network access,
// catalogs, DLL downloads or touching the user's installation.
internal static class NativeWindowVerification
{
    internal static int Run()
    {
        var updater = new VerificationUpdater();
        return NativeWindow.Run(updater, window =>
        {
            _ = Task.Run(async () =>
            {
                try { await verify(window, updater); }
                catch (Exception e) { await window.InvokeAsync(() => window.VerificationFailed(e)); }
            });
        });
    }

    private static void require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native UI verification: " + message);
    }

    private static void click(NativeWindow window, int id)
    {
        require(IsWindowEnabled(window[id]) && IsWindowVisible(window[id]), $"Control {id} must be available");
        SendMessageW(window[id], ButtonClick, 0, 0);
    }

    private static async Task perform(NativeWindow window, int button)
    {
        Task operation = Task.CompletedTask;
        await window.InvokeAsync(() => { click(window, button); operation = window.CurrentOperation; });
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task verify(NativeWindow window, VerificationUpdater updater)
    {
        await window.InvokeAsync(() =>
        {
            require(IsWindowVisible(window[NativeWindow.OpenAdd]), "Add gamemode must be visible on launch");
            require(!IsWindowVisible(window[NativeWindow.Install]), "Launch must not prompt to install");
            require(updater.CheckCalls == 0 && updater.AddCalls == 0, "Launch must not start an update");
            click(window, NativeWindow.OpenAdd);
            SetWindowTextW(window[NativeWindow.Repository], "test/example");
        });
        await perform(window, NativeWindow.Find);
        await window.InvokeAsync(() =>
        {
            require(updater.AddCalls == 0, "Finding a source must not add it");
            require(IsWindowEnabled(window[NativeWindow.Confirm]), "A found source needs a confirmation button");
            require(Text(window[NativeWindow.Trust]).Contains("test/example"), "Show the repository being trusted");
            SetWindowTextW(window[NativeWindow.Repository], "test/different");
            require(!IsWindowEnabled(window[NativeWindow.Confirm]), "Editing a URL must invalidate confirmation");
        });
        await perform(window, NativeWindow.Find);
        await window.InvokeAsync(() =>
        {
            click(window, NativeWindow.Cancel);
            require(updater.AddCalls == 0, "Cancel must not add a source");
            click(window, NativeWindow.OpenAdd);
        });
        await perform(window, NativeWindow.Find);
        await perform(window, NativeWindow.Confirm);
        await window.InvokeAsync(() =>
        {
            require(updater.AddCalls == 1, "Add only after explicit confirmation");
            require(!IsWindowVisible(window[NativeWindow.Repository]), "Close the form after adding");
        });
        await perform(window, NativeWindow.Check);
        await window.InvokeAsync(() =>
        {
            require(Text(window[NativeWindow.Summary]) == "Everything is up to date", "Show the up-to-date result");
            require(!IsWindowVisible(window[NativeWindow.Install]), "No install button when up to date");
        });
        updater.ReadyCount = 1;
        await perform(window, NativeWindow.Check);
        await perform(window, NativeWindow.Install);
        await window.InvokeAsync(() =>
        {
            require(updater.InstallCalls == 1, "Install button must call the updater");
            require(!IsWindowVisible(window[NativeWindow.Install]), "Hide install after completion");
            click(window, NativeWindow.OpenAdd);
        });
        updater.FailFind = true;
        await perform(window, NativeWindow.Find);
        await window.InvokeAsync(() =>
        {
            require(Text(window[NativeWindow.Summary]).Contains("could not be reached"), "Errors must be visible");
            require(IsWindowEnabled(window[NativeWindow.Find]), "An error must allow retry");
            require(!IsWindowEnabled(window[NativeWindow.Confirm]), "Failed discovery must not keep a trusted choice");
        });
        updater.WaitForClose = true;
        Task finishing = Task.CompletedTask;
        await window.InvokeAsync(() =>
        {
            click(window, NativeWindow.Check);
            finishing = window.CurrentOperation;
            require(window.Busy && !IsWindowEnabled(window[NativeWindow.OpenAdd]), "Disable actions during an operation");
            SendMessageW(window.Handle, Close, 0, 0);
            require(IsWindow(window.Handle), "Close must wait for the operation to unwind");
        });
        await updater.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await window.InvokeAsync(() =>
        {
            require(IsWindow(window.Handle), "Window must stay alive until the operation finishes");
            Console.WriteLine("Desktop window verified: add, confirmation, cancel, check, install, errors and safe close");
            updater.FinishClose.TrySetResult();
        });
        await finishing.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class VerificationUpdater : IDesktopUpdater
    {
        internal int AddCalls, CheckCalls, InstallCalls, ReadyCount;
        internal bool FailFind, WaitForClose;
        internal readonly TaskCompletionSource CancellationSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinishClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                using var registration = cancellation.Register(() => CancellationSeen.TrySetResult());
                await FinishClose.Task;
                cancellation.ThrowIfCancellationRequested();
            }
            return new DesktopCheckResult(ReadyCount == 0 ? "Everything is up to date" : "1 update ready to install", "Example: checked", ReadyCount);
        }
        public Task<DesktopInstallResult> InstallAsync(CancellationToken cancellation)
        {
            InstallCalls++;
            return Task.FromResult(new DesktopInstallResult("Installed updates"));
        }
    }
}
