using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Runtime.InteropServices;

namespace RulesetUpdater;

public sealed class DesktopApp : Application
{
    public static int Run() => AppBuilder.Configure<DesktopApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);

    // Runs without creating a window, including on headless CI hosts. The release package
    // must carry its native UI dependencies inside the helper, not in unshipped loose files.
    internal static int VerifyNativeLibraries()
    {
        try
        {
            string extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
            string[] libraries = OperatingSystem.IsMacOS() ? ["libSkiaSharp", "libHarfBuzzSharp", "libAvaloniaNative"] : ["libSkiaSharp", "libHarfBuzzSharp"];
            foreach (string library in libraries)
            {
                nint handle = NativeLibrary.Load(library + extension, typeof(DesktopApp).Assembly, null);
                NativeLibrary.Free(handle);
            }
            Console.WriteLine("Desktop libraries verified");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            desktop.MainWindow = new UpdaterWindow(new DesktopUpdater(Program.DefaultRulesetsDirectory, http));
            desktop.Exit += (_, _) => http.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class UpdaterWindow : Window
{
    private readonly IDesktopUpdater updater;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Button check = new() { Name = "CheckUpdates", Content = "Check for updates" };
    private readonly Button openAdd = new() { Name = "OpenAdd", Content = "Add gamemode" };
    private readonly Button install = new() { Name = "InstallUpdates", Content = "Install updates", IsVisible = false };
    private readonly TextBox repository = new() { Name = "Repository", Watermark = "https://github.com/owner/repository", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button find = new() { Name = "FindSource", Content = "Find gamemode" };
    private readonly ComboBox choices = new() { Name = "GamemodeChoices", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock trust = text("");
    private readonly Button confirm = new() { Name = "ConfirmAdd", Content = "Trust and add", IsEnabled = false };
    private readonly Button cancel = new() { Name = "CancelAdd", Content = "Cancel" };
    private readonly StackPanel addPanel = new() { Name = "AddPanel", Spacing = 12, IsVisible = false };
    private readonly StackPanel confirmation = new() { Spacing = 12, IsVisible = false };
    private readonly TextBlock summary = new() { Name = "Summary", Text = "Updates are checked automatically when osu! starts", FontSize = 16, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock details = text("");
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    private bool busy;

    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;

    public UpdaterWindow(IDesktopUpdater updater)
    {
        this.updater = updater;
        Title = "Zankai Updater";
        Width = 690;
        Height = 570;
        MinWidth = 500;
        MinHeight = 380;
        Background = Brush.Parse("#202020");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var sourceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        sourceRow.Children.Add(repository);
        Grid.SetColumn(find, 1);
        sourceRow.Children.Add(find);
        choices.ItemTemplate = new FuncDataTemplate<ManagedRuleset>((r, _) => new TextBlock { Text = r?.Name });
        confirmation.Children.Add(choices);
        confirmation.Children.Add(trust);
        addPanel.Children.Add(new TextBlock { Text = "Add gamemode", FontSize = 18, FontWeight = FontWeight.SemiBold });
        addPanel.Children.Add(sourceRow);
        addPanel.Children.Add(confirmation);
        addPanel.Children.Add(row(confirm, cancel));
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 20,
                Children =
                {
                    new TextBlock { Text = "Zankai Updater", FontSize = 26, FontWeight = FontWeight.SemiBold },
                    row(check, openAdd, install), progress, addPanel,
                    new Separator(), summary, details,
                },
            },
        };
        openAdd.Click += (_, _) => { addPanel.IsVisible = true; repository.Focus(); };
        cancel.Click += (_, _) => { addPanel.IsVisible = false; clearSelection(); };
        repository.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) clearSelection(); };
        choices.SelectionChanged += (_, _) =>
        {
            confirm.IsEnabled = !busy && choices.SelectedItem is ManagedRuleset;
            if (choices.SelectedItem is ManagedRuleset rule)
                trust.Text = $"Allow {rule.Repository} to install and update {rule.Name}? Only add sources you trust."
                    + (rule.Channels.SequenceEqual(new[] { "lazer" }) ? "\nThis source supports stable lazer only." : "");
        };
        find.Click += (_, _) => CurrentOperation = runAsync("Finding gamemode…", async token =>
        {
            clearSelection();
            string input = repository.Text ?? "";
            var found = await Task.Run(() => updater.FindAsync(input, token), token);
            choices.ItemsSource = found;
            choices.SelectedIndex = found.Length > 0 ? 0 : -1;
            confirmation.IsVisible = found.Length > 0;
            summary.Text = found.Length > 0 ? "Confirm the source to add this gamemode" : "No supported gamemode found";
        });
        confirm.Click += (_, _) => CurrentOperation = runAsync("Adding gamemode…", async token =>
        {
            if (choices.SelectedItem is not ManagedRuleset rule) return;
            summary.Text = await Task.Run(() => updater.AddAsync(rule, token), token);
            install.IsVisible = false;
            addPanel.IsVisible = false;
            clearSelection();
        });
        check.Click += (_, _) => CurrentOperation = runAsync("Checking updates…", async token =>
        {
            install.IsVisible = false;
            var result = await Task.Run(() => updater.CheckAsync(token), token);
            summary.Text = result.Summary;
            details.Text = result.Details;
            install.IsVisible = result.ReadyCount > 0;
        });
        install.Click += (_, _) => CurrentOperation = runAsync("Installing updates…", async token =>
        {
            install.IsVisible = false;
            var result = await Task.Run(() => updater.InstallAsync(token), token);
            summary.Text = result.Message;
            if (result.ExitForSelfUpdate) Close();
        });
        // An in-progress transaction must finish or unwind before this process exits.
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            cancellation.Cancel();
            summary.Text = "Finishing the current operation before closing…";
        };
    }

    private async Task runAsync(string message, Func<CancellationToken, Task> action)
    {
        if (busy) return;
        setBusy(true);
        summary.Text = message;
        details.Text = "";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e) { summary.Text = e.Message; }
        finally
        {
            setBusy(false);
            if (cancellation.IsCancellationRequested) Close();
        }
    }

    private void clearSelection()
    {
        confirmation.IsVisible = false;
        choices.ItemsSource = null;
        choices.SelectedItem = null;
        confirm.IsEnabled = false;
    }

    private void setBusy(bool value)
    {
        busy = value;
        foreach (var control in new Control[] { check, openAdd, install, repository, find, choices, cancel }) control.IsEnabled = !value;
        confirm.IsEnabled = !value && choices.SelectedItem is ManagedRuleset;
        progress.IsVisible = value;
    }

    private static TextBlock text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
    private static WrapPanel row(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(control); }
        return panel;
    }
}
