using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static RulesetUpdater.Win32;

namespace RulesetUpdater;

// Standard OS controls supply text editing, accessibility, keyboard navigation and rendering.
// All updater operations still go through DesktopUpdater, shared with the other platforms.
internal sealed class NativeWindow
{
    internal const int Check = 100, OpenAdd = 101, Install = 102, Repository = 103, Find = 104,
                       Choices = 105, Confirm = 106, Cancel = 107, Summary = 108, Details = 109,
                       Title = 110, AddTitle = 111, Trust = 112;
    private const string className = "ZankaiUpdater.NativeWindow";
    private static readonly WindowProcedure procedure = windowProcedure;
    private static NativeWindow? current;
    private readonly IDesktopUpdater updater;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentQueue<Action> pending = new();
    private readonly Dictionary<int, nint> controls = new();
    private ManagedRuleset[] found = [];
    private nint window, font, titleFont;
    private uint dpi = 96;
    private bool addVisible, installVisible, busy, closing;
    private int exitCode;
    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;
    internal nint Handle => window;
    internal nint this[int id] => controls[id];
    internal bool Busy => busy;

    private NativeWindow(IDesktopUpdater updater) => this.updater = updater;

    internal static int Run(IDesktopUpdater updater, Action<NativeWindow>? ready = null)
    {
        var app = new NativeWindow(updater);
        current = app;
        nint instance = GetModuleHandleW(null);
        var type = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), ClassName = className,
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance,
            Cursor = LoadCursorW(0, 32512), Icon = LoadIconW(0, 32512), Background = 16, // COLOR_BTNFACE + 1
        };
        bool registered = false;
        try
        {
            if (RegisterClassExW(ref type) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            app.window = CreateWindowExW(0, className, "Zankai Updater", WindowStyle,
                int.MinValue, int.MinValue, 700, 600, 0, 0, instance, 0);
            if (app.window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            app.dpi = GetDpiForWindow(app.window);
            app.createControls();
            app.updateFonts();
            var size = new Rect { Right = app.px(680), Bottom = app.px(560) };
            AdjustWindowRectExForDpi(ref size, WindowStyle, false, 0, app.dpi);
            SetWindowPos(app.window, 0, 0, 0, size.Right - size.Left, size.Bottom - size.Top, 0x6); // no move/z-order
            app.refresh();
            ShowWindow(app.window, 5);
            UpdateWindow(app.window);
            SetFocus(app[Check]);
            if (ready != null) app.post(() => ready(app));
            int result;
            while ((result = GetMessageW(out var message, 0, 0, 0)) > 0)
            {
                if (IsDialogMessageW(app.window, ref message)) continue;
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return app.exitCode;
        }
        catch (Exception e)
        {
            if (ready == null) MessageBoxW(app.window, e.Message, "Zankai Updater", 0x10);
            Console.Error.WriteLine(e);
            return 1;
        }
        finally
        {
            if (IsWindow(app.window)) DestroyWindow(app.window);
            if (app.font != 0) DeleteObject(app.font);
            if (app.titleFont != 0) DeleteObject(app.titleFont);
            if (registered) UnregisterClassW(className, instance);
            app.cancellation.Dispose();
            current = null;
        }
    }

    private void createControls()
    {
        add(Title, "STATIC", "Zankai Updater");
        add(Check, "BUTTON", "Check for updates", TabStop);
        add(OpenAdd, "BUTTON", "Add gamemode", TabStop);
        add(Install, "BUTTON", "Install updates", TabStop);
        add(Summary, "STATIC", "Updates are checked automatically when osu! starts");
        add(AddTitle, "STATIC", "GitHub repository");
        add(Repository, "EDIT", "", TabStop | 0x80, 0x200); // auto horizontal scroll, client edge
        SendMessageW(this[Repository], 0xC5, 2048, 0); // EM_SETLIMITTEXT
        SendText(this[Repository], 0x1501, 1, "https://github.com/owner/repository"); // EM_SETCUEBANNER
        add(Find, "BUTTON", "Find gamemode", TabStop);
        add(Choices, "COMBOBOX", "", TabStop | 0x3 | 0x200000); // dropdown list + vertical scroll
        add(Trust, "STATIC", "");
        add(Confirm, "BUTTON", "Trust and add", TabStop);
        add(Cancel, "BUTTON", "Cancel", TabStop);
        add(Details, "EDIT", "", TabStop | 0x4 | 0x40 | 0x800 | 0x200000, 0x200); // multiline, read-only, scroll
    }

    private void add(int id, string type, string text, uint style = 0, uint extended = 0)
    {
        nint control = CreateWindowExW(extended, type, text, Child | Visible | style,
            0, 0, 0, 0, window, id, GetModuleHandleW(null), 0);
        if (control == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        controls.Add(id, control);
    }

    private int px(double value) => (int)Math.Round(value * dpi / 96);

    private void updateFonts()
    {
        nint oldFont = font, oldTitle = titleFont;
        font = CreateFontW(-px(15), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        titleFont = CreateFontW(-px(25), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        foreach (var (id, handle) in controls) SendMessageW(handle, SetFont, (nuint)(id == Title ? titleFont : font), 1);
        if (oldFont != 0) DeleteObject(oldFont);
        if (oldTitle != 0) DeleteObject(oldTitle);
    }

    private void layout()
    {
        if (controls.Count < 13) return;
        GetClientRect(window, out var area);
        double width = area.Right * 96.0 / dpi, height = area.Bottom * 96.0 / dpi;
        void place(int id, double x, double y, double w, double h) => MoveWindow(this[id], px(x), px(y), px(w), px(h), true);
        place(Title, 24, 20, width - 48, 34);
        place(Check, 24, 66, 160, 34);
        place(OpenAdd, 196, 66, 144, 34);
        place(Install, 352, 66, 144, 34);
        place(Summary, 24, 116, width - 48, 56);
        place(AddTitle, 24, 184, width - 48, 24);
        place(Repository, 24, 212, width - 192, 32);
        place(Find, width - 156, 212, 132, 32);
        place(Choices, 24, 256, width - 48, 200); // includes the expanded dropdown
        place(Trust, 24, 300, width - 48, 58);
        place(Confirm, 24, 370, 144, 34);
        place(Cancel, found.Length > 0 ? 180 : 24, found.Length > 0 ? 370 : 256, 100, 34);
        double detailsY = addVisible ? found.Length > 0 ? 424 : 310 : 184;
        place(Details, 24, detailsY, width - 48, Math.Max(30, height - detailsY - 24));
    }

    private void refresh()
    {
        foreach (int id in new[] { Check, OpenAdd, Install, Repository, Find, Choices, Confirm, Cancel })
            EnableWindow(this[id], !busy && (id != Confirm || selected() != null));
        ShowWindow(this[Install], installVisible ? 5 : 0);
        ShowWindow(this[Details], Text(this[Details]).Length > 0 ? 5 : 0);
        foreach (int id in new[] { AddTitle, Repository, Find, Cancel }) ShowWindow(this[id], addVisible ? 5 : 0);
        foreach (int id in new[] { Choices, Trust, Confirm }) ShowWindow(this[id], addVisible && found.Length > 0 ? 5 : 0);
        layout();
    }

    private ManagedRuleset? selected()
    {
        int index = (int)SendMessageW(this[Choices], ComboSelection, 0, 0);
        return index >= 0 && index < found.Length ? found[index] : null;
    }

    private void clearSelection()
    {
        found = [];
        SendMessageW(this[Choices], ComboReset, 0, 0);
        SetWindowTextW(this[Trust], "");
    }

    private void selectionChanged()
    {
        var rule = selected();
        SetWindowTextW(this[Trust], rule == null ? "" : $"Allow {rule.Repository} to install and update {rule.Name}? Only add sources you trust."
            + (rule.Channels.SequenceEqual(new[] { "lazer" }) ? "\r\nThis source supports stable lazer only." : ""));
        refresh();
    }

    private void command(int id, int notification)
    {
        if (busy) return;
        if (id == 1) // IDOK from IsDialogMessage: Enter activates the focused action.
        {
            nint focus = GetFocus();
            id = focus == this[Repository] ? Find : controls.FirstOrDefault(c => c.Value == focus).Key;
        }
        if (id == 2) // IDCANCEL from IsDialogMessage: Escape dismisses the form, then the window.
        {
            if (!addVisible) { close(); return; }
            id = Cancel;
        }
        if (id == Repository && notification == 0x300) { clearSelection(); refresh(); return; } // EN_CHANGE
        if (id == Choices && notification == 1) { selectionChanged(); return; } // CBN_SELCHANGE
        if (notification != 0) return;
        switch (id)
        {
            case OpenAdd:
                addVisible = true;
                refresh();
                SetFocus(this[Repository]);
                break;
            case Cancel:
                addVisible = false;
                clearSelection();
                refresh();
                SetFocus(this[OpenAdd]);
                break;
            case Find:
                string input = Text(this[Repository]);
                clearSelection();
                runAsync("Finding gamemode…", async token =>
                {
                    var rules = await updater.FindAsync(input, token);
                    return () =>
                    {
                        found = rules;
                        foreach (var rule in rules) SendText(this[Choices], ComboAdd, 0, rule.Name);
                        SendMessageW(this[Choices], ComboSelect, 0, 0);
                        selectionChanged();
                        SetWindowTextW(this[Summary], rules.Length > 0 ? "Confirm the source to add this gamemode" : "No supported gamemode found");
                    };
                });
                break;
            case Confirm:
                if (selected() is not { } choice) break;
                runAsync("Adding gamemode…", async token =>
                {
                    string result = await updater.AddAsync(choice, token);
                    return () =>
                    {
                        SetWindowTextW(this[Summary], result);
                        addVisible = installVisible = false;
                        clearSelection();
                        SetFocus(this[OpenAdd]);
                    };
                });
                break;
            case Check:
                installVisible = false;
                runAsync("Checking updates…", async token =>
                {
                    var result = await updater.CheckAsync(token);
                    return () =>
                    {
                        SetWindowTextW(this[Summary], result.Summary);
                        SetWindowTextW(this[Details], result.Details.ReplaceLineEndings("\r\n"));
                        installVisible = result.ReadyCount > 0;
                    };
                });
                break;
            case Install:
                if (!installVisible) break;
                installVisible = false;
                runAsync("Installing updates…", async token =>
                {
                    var result = await updater.InstallAsync(token);
                    return () =>
                    {
                        SetWindowTextW(this[Summary], result.Message);
                        closing |= result.ExitForSelfUpdate;
                    };
                });
                break;
        }
    }

    private void runAsync(string status, Func<CancellationToken, Task<Action>> action)
    {
        busy = true;
        SetWindowTextW(this[Summary], status);
        SetWindowTextW(this[Details], "");
        refresh();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CurrentOperation = finished.Task;
        _ = Task.Run(async () =>
        {
            Action update;
            try { update = await action(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { update = () => { }; }
            catch (Exception e)
            {
                update = () =>
                {
                    SetWindowTextW(this[Summary], e.Message);
                    SetWindowTextW(this[Details], e.Message.ReplaceLineEndings("\r\n"));
                };
            }
            post(() =>
            {
                try { update(); }
                finally
                {
                    busy = false;
                    refresh();
                    // Do not tear down the process while a file transaction is in progress.
                    if (closing) DestroyWindow(window);
                    finished.TrySetResult();
                }
            });
        });
    }

    private void close()
    {
        if (!busy) { DestroyWindow(window); return; }
        closing = true;
        cancellation.Cancel();
        SetWindowTextW(this[Summary], "Finishing the current operation before closing…");
    }

    private void post(Action action)
    {
        pending.Enqueue(action);
        PostMessageW(window, Dispatch, 0, 0);
    }

    internal Task InvokeAsync(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() =>
        {
            try { action(); finished.TrySetResult(); }
            catch (Exception e) { finished.TrySetException(e); }
        });
        return finished.Task;
    }

    internal void VerificationFailed(Exception error)
    {
        Console.Error.WriteLine(error);
        exitCode = 1;
        close();
    }

    private static nint windowProcedure(nint handle, uint message, nuint wParam, nint lParam)
    {
        var app = current;
        if (app == null) return DefWindowProcW(handle, message, wParam, lParam);
        // No managed exception may cross the native window procedure boundary.
        try
        {
            switch (message)
            {
                case Command:
                    app.command((int)(wParam & 0xffff), (int)((wParam >> 16) & 0xffff));
                    return 0;
                case Dispatch:
                    while (app.pending.TryDequeue(out var action)) action();
                    return 0;
                case Size: app.layout(); return 0;
                case Close: app.close(); return 0;
                case Destroy: PostQuitMessage(app.exitCode); return 0;
                case 0x24: // WM_GETMINMAXINFO
                    var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    limits.MinTrackSize = new Point { X = app.px(640), Y = app.px(540) };
                    Marshal.StructureToPtr(limits, lParam, false);
                    return 0;
                case DpiChanged:
                    app.dpi = (uint)(wParam & 0xffff);
                    app.updateFonts();
                    var bounds = Marshal.PtrToStructure<Rect>(lParam);
                    SetWindowPos(handle, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x14);
                    app.layout();
                    return 0;
            }
        }
        catch (Exception e)
        {
            app.exitCode = 1;
            Console.Error.WriteLine(e);
            if (app.controls.TryGetValue(Summary, out nint summary)) SetWindowTextW(summary, e.Message);
            else PostQuitMessage(1);
            return 0;
        }
        return DefWindowProcW(handle, message, wParam, lParam);
    }
}
