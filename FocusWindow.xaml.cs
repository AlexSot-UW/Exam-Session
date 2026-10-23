using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ExamSession;

public partial class FocusWindow : Window
{
    private const string ConfirmPhrase = "END FOCUS";

    // Taskbar, Alt+Tab, Win+Tab and the desktop: passing through these is not a distraction.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
        "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "ForegroundStaging", "Progman", "WorkerW",
    };

    private readonly FocusConfig _config;
    private readonly string _pdfUri;
    private readonly HashSet<string> _allowed;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _bannerTimer = new();
    private readonly DispatcherTimer _graceTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Native.WinEventProc _hookProc; // kept in a field so the GC cannot collect the callback
    private readonly SortedSet<string> _blockedApps = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _hook;
    private IntPtr _hwnd;
    private KeyboardGuard? _keys;
    private List<IntPtr> _switchList = new();
    private int _switchIndex;
    private WindowState _restoreState = WindowState.Maximized;
    private DateTime _start;
    private bool _allowClose;
    private bool _finished;
    private bool _warned5;
    private int _blocked;
    private DateTime? _awaySince;
    private bool _awayCounted;
    private TimeSpan _timeAway;

    public FocusWindow(FocusConfig config)
    {
        InitializeComponent();
        _config = config;
        _pdfUri = new Uri(config.PdfPath).AbsoluteUri;
        _allowed = new HashSet<string>(config.AllowedApps, StringComparer.OrdinalIgnoreCase)
        {
            Process.GetCurrentProcess().ProcessName,
            "LockApp",
        };
        TitleText.Text = config.Name;
        AllowedText.Text = config.AllowedApps.Count == 0
            ? "Allowed: this PDF only"
            : "Allowed: " + string.Join(", ", config.AllowedApps);

        _hookProc = OnForegroundChanged;
        _clock.Tick += (_, _) => Tick();
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            Enforce();
        };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            Banner.Visibility = Visibility.Collapsed;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hook = Native.SetWinEventHook(Native.EventSystemForeground, Native.EventSystemForeground, IntPtr.Zero,
            _hookProc, 0, 0, Native.WinEventOutOfContext);
        _keys = new KeyboardGuard(blockWinKey: false);
        _keys.AltTab += reverse => Dispatcher.BeginInvoke(() => SwitchWindow(reverse));
        _keys.AltTabEnded += () => _switchList.Clear();

        _start = DateTime.Now;
        Tick();
        _clock.Start();

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamSession", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await Viewer.EnsureCoreWebView2Async(env);

        var core = Viewer.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Keep the viewer pinned to the assignment PDF: no links, popups or downloads.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.NavigationStarting += (_, args) =>
        {
            var target = args.Uri.Split('#')[0];
            if (!string.Equals(target, _pdfUri, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.Navigate(_pdfUri);
    }

    private void Tick()
    {
        var elapsed = DateTime.Now - _start;

        // Backstop for when pulling focus back failed and no new foreground event arrives.
        if (_awaySince is not null && !_graceTimer.IsEnabled) Enforce();

        if (!_config.HasLimit)
        {
            PhaseText.Text = "Elapsed";
            TimerText.Text = Format(elapsed);
            return;
        }

        var remaining = _config.Duration - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            TimerText.Text = "00:00:00";
            Finish("completed");
            return;
        }

        PhaseText.Text = "Remaining";
        TimerText.Text = Format(remaining);
        if (remaining <= TimeSpan.FromMinutes(5))
            TimerText.Foreground = Brushes.Orange;

        if (!_warned5 && remaining <= TimeSpan.FromMinutes(5) && _config.Duration > TimeSpan.FromMinutes(5))
        {
            _warned5 = true;
            ShowBanner("5 minutes remaining.", 10);
        }
    }

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private void ShowBanner(string message, int seconds)
    {
        BannerText.Text = message;
        Banner.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Interval = TimeSpan.FromSeconds(seconds);
        _bannerTimer.Start();
    }

    // --- Allow-list enforcement -------------------------------------------

    private bool IsAllowed(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || ShellClasses.Contains(Native.ClassName(hwnd))) return true;
        var name = Native.ProcessName(hwnd);
        return name is null || _allowed.Contains(name);
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_finished) return;
        if (IsAllowed(hwnd))
        {
            _graceTimer.Stop();
            EndAway();
            return;
        }

        // Let transient switches (e.g. through the taskbar to an allowed app) settle before acting.
        _awaySince ??= DateTime.Now;
        _graceTimer.Stop();
        _graceTimer.Start();
    }

    private void Enforce()
    {
        if (_finished) return;
        var hwnd = Native.GetForegroundWindow();
        if (IsAllowed(hwnd))
        {
            EndAway();
            return;
        }

        var name = Native.ProcessName(hwnd) ?? "unknown app";
        if (!_awayCounted)
        {
            _awayCounted = true;
            _blocked++;
            _blockedApps.Add(name);
            BlockedText.Text = $"Blocked {_blocked}x";
        }

        Native.ShowWindow(hwnd, Native.SwMinimize);
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Native.ForceForeground(_hwnd);
        ShowBanner($"{name} is not allowed during this session. Blocked {_blocked} time(s).", 6);
    }

    private void EndAway()
    {
        if (_awaySince is not { } since) return;
        if (_awayCounted) _timeAway += DateTime.Now - since;
        _awaySince = null;
        _awayCounted = false;
        if (_blocked > 0) BlockedText.Text = $"Blocked {_blocked}x ({(int)_timeAway.TotalSeconds}s away)";
    }

    // Stand-in for Windows' Alt+Tab, cycling only through this window and the allowed apps.
    private void SwitchWindow(bool reverse)
    {
        if (_finished) return;
        if (_switchList.Count == 0)
        {
            var allowed = Native.AppWindows()
                .Where(w => w.Hwnd == _hwnd || _allowed.Contains(w.Process))
                .Select(w => w.Hwnd)
                .ToList();
            if (!allowed.Contains(_hwnd)) allowed.Insert(0, _hwnd);
            _switchList = allowed;
            _switchIndex = Math.Max(0, _switchList.IndexOf(Native.GetForegroundWindow()));
        }
        if (_switchList.Count < 2) return;

        _switchIndex = (_switchIndex + (reverse ? -1 : 1) + _switchList.Count) % _switchList.Count;
        var target = _switchList[_switchIndex];
        if (target == _hwnd && WindowState == WindowState.Minimized) WindowState = _restoreState;
        Native.SwitchTo(target);
    }

    private void Unhook()
    {
        _keys?.Dispose();
        _keys = null;
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (!_finished) ShowEndPanel();
    }

    private void OnClosed(object? sender, EventArgs e) => Unhook();

    // --- Ending -----------------------------------------------------------

    private void OnEndClicked(object sender, RoutedEventArgs e) => ShowEndPanel();

    private void ShowEndPanel()
    {
        EndPrompt.Text = _config.ExitPassword is null
            ? $"Type {ConfirmPhrase} to confirm. The session will be logged as ended early."
            : "Enter your exit password. The session will be logged as ended early.";
        EndInput.Clear();
        EndError.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Hidden;
        EndPanel.Visibility = Visibility.Visible;
        EndInput.Focus();
    }

    private void OnEndCancel(object sender, RoutedEventArgs e)
    {
        EndPanel.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Visible;
    }

    private void OnEndConfirm(object sender, RoutedEventArgs e)
    {
        var expected = _config.ExitPassword ?? ConfirmPhrase;
        if (EndInput.Password != expected)
        {
            EndError.Text = _config.ExitPassword is null ? $"Type exactly: {ConfirmPhrase}" : "Wrong password.";
            EndError.Visibility = Visibility.Visible;
            EndInput.Clear();
            return;
        }
        Finish("ended early");
    }

    private void Finish(string outcome)
    {
        if (_finished) return;
        _finished = true;
        _clock.Stop();
        _graceTimer.Stop();
        Unhook();
        EndAway();

        var ended = DateTime.Now;
        var elapsed = ended - _start;
        var logNote = WriteLog(ended, elapsed, outcome);

        EndPanel.Visibility = Visibility.Collapsed;
        Banner.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Hidden;
        EndButton.IsEnabled = false;
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();

        DoneTitle.Text = outcome == "completed" ? "Focus session complete." : "Focus session ended early.";
        DoneDetails.Text =
            $"Assignment:    {_config.Name}\n" +
            $"Planned:       {(_config.HasLimit ? Format(_config.Duration) : "no limit")}\n" +
            $"Actual:        {Format(elapsed)}\n" +
            $"Allowed apps:  {(_config.AllowedApps.Count == 0 ? "none" : string.Join(", ", _config.AllowedApps))}\n" +
            $"Blocked:       {_blocked} time(s), {(int)_timeAway.TotalSeconds}s away\n" +
            (_blockedApps.Count > 0 ? $"Blocked apps:  {string.Join(", ", _blockedApps)}\n" : "") +
            "\n" + logNote;
        DonePanel.Visibility = Visibility.Visible;
    }

    private string WriteLog(DateTime ended, TimeSpan elapsed, string outcome)
    {
        try
        {
            var isNew = !File.Exists(_config.LogPath);
            using var writer = new StreamWriter(_config.LogPath, append: true);
            if (isNew)
                writer.WriteLine("started,assignment,planned_min,ended,elapsed_min,outcome,allowed_apps,blocked,blocked_apps,seconds_away");
            var inv = CultureInfo.InvariantCulture;
            writer.WriteLine(string.Join(",",
                _start.ToString("yyyy-MM-dd HH:mm:ss", inv),
                Quote(_config.Name),
                _config.Duration.TotalMinutes.ToString(inv),
                ended.ToString("yyyy-MM-dd HH:mm:ss", inv),
                elapsed.TotalMinutes.ToString("F1", inv),
                outcome,
                Quote(string.Join(";", _config.AllowedApps)),
                _blocked.ToString(inv),
                Quote(string.Join(";", _blockedApps)),
                ((int)_timeAway.TotalSeconds).ToString(inv)));
            return $"Logged to {_config.LogPath}";
        }
        catch (Exception ex)
        {
            return $"Could not write log: {ex.Message}";
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }
}
