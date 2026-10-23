using System.Runtime.InteropServices;

namespace ExamSession;

/// <summary>
/// Low-level mouse hook that keeps the cursor out of the taskbar, so nothing on it can be clicked or
/// hovered (Start, pinned apps, window previews, tray, clock). Windows removes the hook if the app
/// dies, so the taskbar can never be left disabled.
/// </summary>
internal sealed class TaskbarGuard : IDisposable
{
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmMouseLastButton = 0x020E; // WM_MOUSEHWHEEL

    // Popups the taskbar opens; clicks on them are swallowed in case one is already showing.
    private static readonly HashSet<string> TaskbarClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly Native.LowLevelMouseProc _proc; // kept in a field so the GC cannot collect the callback
    private readonly List<IntPtr> _taskbars = Native.Taskbars();
    private readonly HookThread _thread;
    private DateTime _lastNotice;
    private volatile bool _paused;

    /// <summary>While paused the cursor may enter the taskbar, e.g. to snip a screenshot that includes it.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Raised on the hook thread (at most every few seconds) when the cursor was stopped at the taskbar.</summary>
    public event Action? Blocked;

    public TaskbarGuard()
    {
        _proc = OnMouse;
        // The timer is a backstop that also frees a cursor already sitting on the taskbar at start.
        _thread = new HookThread("Taskbar guard",
            () => Native.SetWindowsHookEx(Native.WhMouseLl, _proc, Native.GetModuleHandle(null), 0),
            PushCursorOut, tickMs: 30);
    }

    private bool IsOverTaskbar(Native.Point pt) => TaskbarAt(pt) != IntPtr.Zero;

    private IntPtr TaskbarAt(Native.Point pt)
    {
        // Rects are read live: the taskbar can move, resize or auto-hide during a session.
        foreach (var hwnd in _taskbars)
            if (Native.GetWindowRect(hwnd, out var rect) && rect.Contains(pt))
                return hwnd;
        return IntPtr.Zero;
    }

    private void PushCursorOut()
    {
        if (_paused || !Native.GetCursorPos(out var pos)) return;
        var taskbar = TaskbarAt(pos);
        if (taskbar == IntPtr.Zero || !Native.GetWindowRect(taskbar, out var bar)) return;

        // Step off the side of the taskbar that faces the rest of the screen.
        var screen = Native.MonitorBounds(taskbar);
        if (bar.Right - bar.Left >= bar.Bottom - bar.Top)
            pos.Y = bar.Top > screen.Top ? bar.Top - 1 : bar.Bottom;
        else
            pos.X = bar.Left > screen.Left ? bar.Left - 1 : bar.Right;
        Native.SetCursorPos(pos.X, pos.Y);
        Notify();
    }

    private void Notify()
    {
        if (DateTime.Now - _lastNotice < TimeSpan.FromSeconds(4)) return;
        _lastNotice = DateTime.Now;
        Blocked?.Invoke();
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        var msg = (int)wParam;
        if (code >= 0 && !_paused && msg is >= WmMouseMove and <= WmMouseLastButton)
        {
            var mouse = Marshal.PtrToStructure<Native.MsLlHookStruct>(lParam);
            var blocked = msg == WmMouseMove
                ? IsOverTaskbar(mouse.Pt)
                : IsOverTaskbar(mouse.Pt) || TaskbarClasses.Contains(Native.RootClassAt(mouse.Pt));
            if (blocked)
            {
                if (msg is WmMouseMove or WmLButtonDown) Notify();
                return 1;
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => _thread.Dispose();
}
