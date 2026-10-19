using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using NUnit.Framework;

[assembly: AvaloniaTestApplication(typeof(RulesetUpdater.Tests.DesktopTestAppBuilder))]

namespace RulesetUpdater.Tests;

public static class DesktopTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DesktopApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[TestFixture]
public sealed class DesktopTests
{
    private sealed class FakeUpdater : IDesktopUpdater
    {
        public int FindCalls, AddCalls, CheckCalls, InstallCalls;
        public bool FailFind;
        public DesktopCheckResult Result = new("Everything is up to date", "Sticks: current", 0);
        public Task<ManagedRuleset[]> FindAsync(string repository, CancellationToken cancellation)
        {
            FindCalls++;
            if (FailFind) throw new IOException("Repository could not be reached");
            return Task.FromResult(new[] { new ManagedRuleset("test/example", "osu.Game.Rulesets.Example") { Channels = ["lazer"] } });
        }
        public Task<string> AddAsync(ManagedRuleset ruleset, CancellationToken cancellation) { AddCalls++; return Task.FromResult("Added Example"); }
        public Task<DesktopCheckResult> CheckAsync(CancellationToken cancellation) { CheckCalls++; return Task.FromResult(Result); }
        public Task<DesktopInstallResult> InstallAsync(CancellationToken cancellation) { InstallCalls++; return Task.FromResult(new DesktopInstallResult("Installed updates")); }
    }

    private static T control<T>(UpdaterWindow window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static void click(UpdaterWindow window, string name)
    {
        var button = control<Button>(window, name);
        Assert.That(button.IsEnabled, Is.True);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void capture(UpdaterWindow window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("ZANKAI_UI_SCREENSHOTS");
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null);
        frame!.Save(Path.Combine(directory, name + ".png"));
    }

    [Test]
    public void OnlyManualLaunchOpensWindow()
    {
        Assert.That(Program.OpensWindow([]), Is.True);
        foreach (string mode in new[] { "--help", "--watch", "--finish-self-update", "--apply", "--add-source" })
            Assert.That(Program.OpensWindow([mode]), Is.False);
    }

    [AvaloniaTest]
    public void WindowStartsWithAddButtonAndNoInstallPrompt()
    {
        var updater = new FakeUpdater();
        var window = new UpdaterWindow(updater);
        window.Show();
        try
        {
            Assert.That(control<Button>(window, "OpenAdd").IsVisible, Is.True);
            Assert.That(control<Button>(window, "CheckUpdates").IsVisible, Is.True);
            Assert.That(control<Button>(window, "InstallUpdates").IsVisible, Is.False);
            Assert.That(updater.CheckCalls, Is.Zero);
            capture(window, "home");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task AddFlowRequiresExplicitConfirmation()
    {
        var updater = new FakeUpdater();
        var window = new UpdaterWindow(updater);
        window.Show();
        try
        {
            click(window, "OpenAdd");
            Assert.That(control<StackPanel>(window, "AddPanel").IsVisible, Is.True);
            control<TextBox>(window, "Repository").Text = "https://github.com/test/example";
            click(window, "FindSource");
            await window.CurrentOperation;
            Assert.That(updater.FindCalls, Is.EqualTo(1));
            Assert.That(updater.AddCalls, Is.Zero);
            Assert.That(control<Button>(window, "ConfirmAdd").IsEnabled, Is.True);
            capture(window, "add-gamemode");
            click(window, "ConfirmAdd");
            await window.CurrentOperation;
            Assert.That(updater.AddCalls, Is.EqualTo(1));
            Assert.That(control<StackPanel>(window, "AddPanel").IsVisible, Is.False);
            Assert.That(control<TextBlock>(window, "Summary").Text, Is.EqualTo("Added Example"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task CancelAndChangedRepositoryDoNotAddSource()
    {
        var updater = new FakeUpdater();
        var window = new UpdaterWindow(updater);
        window.Show();
        try
        {
            click(window, "OpenAdd");
            control<TextBox>(window, "Repository").Text = "test/example";
            click(window, "FindSource");
            await window.CurrentOperation;
            control<TextBox>(window, "Repository").Text = "different/repository";
            Assert.That(control<Button>(window, "ConfirmAdd").IsEnabled, Is.False);
            click(window, "FindSource");
            await window.CurrentOperation;
            click(window, "CancelAdd");
            Assert.That(updater.AddCalls, Is.Zero);
            Assert.That(control<StackPanel>(window, "AddPanel").IsVisible, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task UpToDateCheckDoesNotOfferInstallation()
    {
        var updater = new FakeUpdater();
        var window = new UpdaterWindow(updater);
        window.Show();
        try
        {
            click(window, "CheckUpdates");
            await window.CurrentOperation;
            Assert.That(control<TextBlock>(window, "Summary").Text, Is.EqualTo("Everything is up to date"));
            Assert.That(control<Button>(window, "InstallUpdates").IsVisible, Is.False);
            Assert.That(updater.InstallCalls, Is.Zero);
            capture(window, "up-to-date");
            updater.Result = new DesktopCheckResult("1 update ready to install", "Example: ready", 1);
            click(window, "CheckUpdates");
            await window.CurrentOperation;
            Assert.That(control<Button>(window, "InstallUpdates").IsVisible, Is.True);
            click(window, "InstallUpdates");
            await window.CurrentOperation;
            Assert.That(updater.InstallCalls, Is.EqualTo(1));
            Assert.That(control<Button>(window, "InstallUpdates").IsVisible, Is.False);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ErrorsRemainVisibleAndAllowRetry()
    {
        var updater = new FakeUpdater { FailFind = true };
        var window = new UpdaterWindow(updater);
        window.Show();
        try
        {
            click(window, "OpenAdd");
            click(window, "FindSource");
            await window.CurrentOperation;
            Assert.That(control<TextBlock>(window, "Summary").Text, Does.Contain("could not be reached"));
            Assert.That(control<Button>(window, "FindSource").IsEnabled, Is.True);
            Assert.That(control<Button>(window, "ConfirmAdd").IsEnabled, Is.False);
        }
        finally { window.Close(); }
    }
}
