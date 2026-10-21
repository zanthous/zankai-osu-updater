using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace RulesetUpdater.Tests;

[TestFixture]
public sealed class NativeHelperTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task StartupNeedsNoCompanionFilesAndDoesNotStartAnUpdate(bool explicitHelp)
    {
        string? helper = Environment.GetEnvironmentVariable("ZANKAI_TEST_HELPER");
        if (string.IsNullOrWhiteSpace(helper)) { Assert.Ignore("Set the packaged helper path to test its entry point."); return; }
        string directory = Path.Combine(Path.GetTempPath(), "zankai-aot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, SelfUpdate.HelperName);
            File.Copy(helper, executable);
            SelfUpdate.SetExecutable(executable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = directory,
            };
            // Without a terminal or desktop session there is nothing to open; a Linux desktop would reopen it in a terminal.
            start.Environment.Remove("DISPLAY");
            start.Environment.Remove("WAYLAND_DISPLAY");
            if (explicitHelp) start.ArgumentList.Add("--help");
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (!explicitHelp && OperatingSystem.IsWindows())
                {
                    while (!process.HasExited && process.MainWindowHandle == 0)
                    {
                        await Task.Delay(100, timeout.Token);
                        process.Refresh();
                    }
                    Assert.That(process.HasExited, Is.False, "Double-clicking the updater must open a window");
                    Assert.That(process.MainWindowTitle, Is.EqualTo("Zankai Updater"));
                    await Task.Delay(150, timeout.Token); // Allow the native control theme to finish its first paint.
                    CaptureIfRequested(process.MainWindowHandle, "home");
                    SendMessageW(GetDlgItem(process.MainWindowHandle, 101), 0xF5, 0, 0); // Add gamemode
                    await Task.Delay(150, timeout.Token);
                    CaptureIfRequested(process.MainWindowHandle, "add-gamemode");
                    SendMessageW(process.MainWindowHandle, 0x10, 0, 0); // WM_CLOSE
                }
                await process.WaitForExitAsync(timeout.Token);
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            Assert.That(process.ExitCode, Is.Zero, await process.StandardError.ReadToEndAsync());
            string output = await process.StandardOutput.ReadToEndAsync();
            if (explicitHelp || !OperatingSystem.IsWindows()) Assert.That(output, Does.Contain("Zankai Updater"));
            Assert.That(Directory.GetFileSystemEntries(directory), Is.EqualTo(new[] { executable }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task PublishedMenuActionsWorkWithoutNetworkOrInstallation()
    {
        string? helper = Environment.GetEnvironmentVariable("ZANKAI_TEST_HELPER");
        if (string.IsNullOrWhiteSpace(helper)) { Assert.Ignore("Set the packaged helper path to verify its window or terminal menu."); return; }
        using var process = Process.Start(new ProcessStartInfo(helper, "--verify-ui")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.That(process.ExitCode, Is.Zero, await process.StandardError.ReadToEndAsync());
        Assert.That(await process.StandardOutput.ReadToEndAsync(), Does.Contain(OperatingSystem.IsWindows() ? "Desktop window verified" : "Terminal menu verified"));
    }

    // Optional captures are of this test's own window, never the user's desktop.
    private static void CaptureIfRequested(nint window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("ZANKAI_NATIVE_SCREENSHOTS");
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        GetWindowRect(window, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint dc = CreateCompatibleDC(0);
        nint bitmap = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        nint previous = SelectObject(dc, bitmap);
        try
        {
            RedrawWindow(window, 0, 0, 0x185); // Invalidate/erase and repaint this window and its children now.
            Assert.That(PrintWindow(window, dc, 2), Is.True);
            byte[] pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            using var file = new BinaryWriter(File.Create(Path.Combine(directory, name + ".bmp")));
            file.Write((ushort)0x4D42); file.Write(54 + pixels.Length); file.Write(0); file.Write(54);
            file.Write(40); file.Write(width); file.Write(-height); file.Write((ushort)1); file.Write((ushort)32);
            file.Write(0); file.Write(pixels.Length); file.Write(0); file.Write(0); file.Write(0); file.Write(0);
            file.Write(pixels);
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixels, YPixels;
        public uint Used, Important;
    }
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
