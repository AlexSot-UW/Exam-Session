using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExamSession;

internal static class Native
{
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    public const uint EventSystemForeground = 0x0003;
    public const uint WinEventOutOfContext = 0x0000;
    public const int WhKeyboardLl = 13;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const int VkTab = 0x09;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkEscape = 0x1B;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;
    public const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const byte VkDummy = 0xFF;

    /// <summary>Tags keystrokes this app injects so its own keyboard hook lets them through.</summary>
    public static readonly UIntPtr InjectedMarker = new(0x45584D53);
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;
    private const byte VkMenu = 0x12;
    private const uint KeyEventExtended = 0x1;
    private const uint KeyEventUp = 0x2;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hmod, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(GetWindowTextLength(hwnd) + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static uint ProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string? NameOfProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Process name that owns a top-level window, looking through the UWP frame host.</summary>
    public static string? ProcessName(IntPtr hwnd)
    {
        var framePid = ProcessId(hwnd);
        var name = NameOfProcess(framePid);
        if (!string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return name;

        // Store apps are drawn inside a frame window; the real app owns one of its children.
        string? inner = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            var pid = ProcessId(child);
            if (pid == framePid) return true;
            inner = NameOfProcess(pid);
            return false;
        }, IntPtr.Zero);
        return inner ?? name;
    }

    /// <summary>Windows that would show up on the taskbar or in Alt+Tab, topmost first.</summary>
    public static List<(IntPtr Hwnd, string Process, string Title)> AppWindows()
    {
        var result = new List<(IntPtr, string, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero
                && (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) == 0
                && GetWindowTextLength(hwnd) > 0 && !IsCloaked(hwnd)
                && ProcessName(hwnd) is { } name)
                result.Add((hwnd, name, Title(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Presses a key that does nothing. Sent after swallowing Tab/Esc so the held Alt or Win key is
    /// not seen as a lone press (which would open a menu bar or the Start menu on release).
    /// </summary>
    public static void TapDummyKey()
    {
        keybd_event(VkDummy, 0, 0, InjectedMarker);
        keybd_event(VkDummy, 0, KeyEventUp, InjectedMarker);
    }

    /// <summary>Brings a window forward while the user holds Alt, which Windows permits.</summary>
    public static void SwitchTo(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static void ForceForeground(IntPtr hwnd)
    {
        // Windows only lets the foreground process hand over focus. A synthetic Alt tap counts as
        // user input, which lifts that restriction for the following SetForegroundWindow.
        keybd_event(VkMenu, 0, KeyEventExtended, InjectedMarker);
        keybd_event(VkMenu, 0, KeyEventExtended | KeyEventUp, InjectedMarker);
        SetForegroundWindow(hwnd);
    }
}
