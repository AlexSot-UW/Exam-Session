using System.Runtime.InteropServices;

namespace ExamSession;

/// <summary>
/// Low-level keyboard hook that swallows the Windows key (Start menu, Win+R, Win+Tab, ...) and the
/// window-switching shortcuts (Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc).
/// Ctrl+Alt+Del and Win+L cannot be intercepted by any application.
/// </summary>
internal sealed class KeyboardGuard : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // kept in a field so the GC cannot collect the callback
    private readonly bool _allowSnapping;
    private readonly bool _allowScreenshots;
    private readonly HookThread _thread;
    private bool _winHeld;
    private bool _altTabbing;

    /// <summary>Raised on the hook thread for each swallowed Alt+Tab; the argument is true when Shift is held.</summary>
    public event Action<bool>? AltTab;

    /// <summary>Raised on the hook thread when Alt is released after one or more Alt+Tab presses.</summary>
    public event Action? AltTabEnded;

    /// <param name="allowSnapping">Let Win+arrow keys through so windows can still be snapped side by side.</param>
    /// <param name="allowScreenshots">Let Win+Shift+S and Win+PrtSc through.</param>
    public KeyboardGuard(bool allowSnapping, bool allowScreenshots)
    {
        _allowSnapping = allowSnapping;
        _allowScreenshots = allowScreenshots;
        _proc = OnKey;
        _thread = new HookThread("Keyboard guard",
            () => Native.SetWindowsHookEx(Native.WhKeyboardLl, _proc, Native.GetModuleHandle(null), 0));
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
        if (vk is Native.VkLWin or Native.VkRWin)
        {
            _winHeld = down;
            return true;
        }

        // The real Win key never reaches Windows, so permitted Win shortcuts are replayed with an injected one.
        var replay = _winHeld && (
            _allowSnapping && vk is >= Native.VkLeft and <= Native.VkDown ||
            _allowScreenshots && (vk == Native.VkSnapshot || vk == Native.VkS && Native.IsKeyDown(Native.VkShift)));
        if (replay)
        {
            if (down) Native.SendWinChord(vk);
            return true;
        }

        if (vk is Native.VkLMenu or Native.VkRMenu && !down && _altTabbing)
        {
            _altTabbing = false;
            AltTabEnded?.Invoke();
            return false;
        }

        var alt = Native.IsKeyDown(Native.VkLMenu) || Native.IsKeyDown(Native.VkRMenu);
        var ctrl = Native.IsKeyDown(Native.VkControl);
        var blocked = vk == Native.VkTab ? alt : vk == Native.VkEscape && (alt || ctrl);
        if (!blocked) return false;

        if (down)
        {
            Native.TapDummyKey();
            if (vk == Native.VkTab)
            {
                _altTabbing = true;
                AltTab?.Invoke(Native.IsKeyDown(Native.VkShift));
            }
        }
        return true;
    }

    public void Dispose() => _thread.Dispose();
}
