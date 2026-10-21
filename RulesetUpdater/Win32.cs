using System.Runtime.InteropServices;
using System.Text;

namespace RulesetUpdater;

internal static class Win32
{
    internal const uint Child = 0x40000000, Visible = 0x10000000, TabStop = 0x10000;
    internal const uint WindowStyle = 0x00CF0000 | 0x02000000; // overlapped + clip children
    internal const uint Command = 0x111, Close = 0x10, Destroy = 2, Size = 5, DpiChanged = 0x2E0, Dispatch = 0x8001;
    internal const uint SetFont = 0x30, ButtonClick = 0xF5, ComboReset = 0x14B, ComboAdd = 0x143, ComboSelect = 0x14E, ComboSelection = 0x147;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo { internal Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint extended, string className, string text, uint style, int x, int y, int width, int height, nint parent, nint id, nint instance, nint parameter);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] internal static extern bool IsDialogMessageW(nint window, ref Message message);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] internal static extern nint SendText(nint window, uint message, nuint wParam, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowTextW(nint window, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] internal static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll")] internal static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, bool menu, uint extended, uint dpi);
    [DllImport("user32.dll")] internal static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] internal static extern nint LoadIconW(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBoxW(nint window, string text, string title, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);

    internal static string Text(nint window)
    {
        var text = new StringBuilder(GetWindowTextLengthW(window) + 1);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }
}
