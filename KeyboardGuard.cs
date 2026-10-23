using System.Runtime.InteropServices;

namespace ExamSession;

/// <summary>
/// Low-level keyboard hook that swallows the system window-switching shortcuts
/// (Alt+Tab, Alt+Esc, Win+Tab, Ctrl+Esc, Ctrl+Shift+Esc) and optionally the Windows key.
/// Ctrl+Alt+Del cannot be intercepted by any application.
/// </summary>
internal sealed class KeyboardGuard : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // kept in a field so the GC cannot collect the callback
    private readonly bool _blockWinKey;
    private IntPtr _hook;
    private bool _altTabbing;

    /// <summary>Raised on each swallowed Alt+Tab; the argument is true when Shift is held.</summary>
    public event Action<bool>? AltTab;

    /// <summary>Raised when Alt is released after one or more Alt+Tab presses.</summary>
    public event Action? AltTabEnded;

    public KeyboardGuard(bool blockWinKey)
    {
        _blockWinKey = blockWinKey;
        _proc = OnKey;
        _hook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _proc, Native.GetModuleHandle(null), 0);
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KbdLlHookStruct>(lParam);
            var msg = (int)wParam;
            var down = msg is Native.WmKeyDown or Native.WmSysKeyDown;
            if (key.ExtraInfo != Native.InjectedMarker && Handle((int)key.VkCode, down))
                return 1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool Handle(int vk, bool down)
    {
        if (vk is Native.VkLMenu or Native.VkRMenu && !down && _altTabbing)
        {
            _altTabbing = false;
            AltTabEnded?.Invoke();
            return false;
        }

        if (_blockWinKey && vk is Native.VkLWin or Native.VkRWin) return true;

        var alt = Native.IsKeyDown(Native.VkLMenu) || Native.IsKeyDown(Native.VkRMenu);
        var win = Native.IsKeyDown(Native.VkLWin) || Native.IsKeyDown(Native.VkRWin);
        var ctrl = Native.IsKeyDown(Native.VkControl);
        var blocked = vk == Native.VkTab ? alt || win : vk == Native.VkEscape && (alt || win || ctrl);
        if (!blocked) return false;

        if (down)
        {
            Native.TapDummyKey();
            if (vk == Native.VkTab && alt && !win)
            {
                _altTabbing = true;
                AltTab?.Invoke(Native.IsKeyDown(Native.VkShift));
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
