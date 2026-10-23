namespace ExamSession;

/// <summary>
/// Runs a low-level input hook on its own thread. Windows lets input through unhandled when a hook's
/// thread is slow to respond, so the hook must not share a thread with the UI (WebView2 rendering,
/// layout) or fast mouse movement and key presses slip past it.
/// </summary>
internal sealed class HookThread : IDisposable
{
    private const uint WmQuit = 0x0012;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private readonly Func<IntPtr> _install;
    private readonly Native.TimerProc? _tick; // kept in a field so the GC cannot collect the callback
    private readonly uint _tickMs;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    /// <param name="install">Installs the hook and returns its handle; called on the hook thread.</param>
    /// <param name="tick">Optional callback run on the hook thread every <paramref name="tickMs"/>.</param>
    public HookThread(string name, Func<IntPtr> install, Action? tick = null, uint tickMs = 0)
    {
        _install = install;
        if (tick is not null) _tick = (_, _, _, _) => tick();
        _tickMs = tickMs;
        new Thread(Run) { IsBackground = true, Name = name }.Start();
        _ready.Wait();
    }

    private void Run()
    {
        // Physical pixels everywhere, matching the coordinates low-level hooks report.
        Native.SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        _threadId = Native.GetCurrentThreadId();
        var hook = _install();
        if (_tick is not null) Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, _tickMs, _tick);
        _ready.Set();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Native.UnhookWindowsHookEx(hook);
    }

    public void Dispose()
    {
        if (_threadId == 0) return;
        Native.PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }
}
