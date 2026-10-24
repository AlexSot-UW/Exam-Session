using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

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

    // The Win+Shift+S / PrtSc snipping overlay and the Snipping Tool editor (old and new versions).
    private static readonly HashSet<string> ScreenshotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool", "ScreenClippingHost", "ScreenSketch",
    };

    private readonly FocusConfig _config;
    private readonly string[] _pdfUris;
    private readonly WebView2?[] _viewers;
    private readonly List<Button> _tabs = new();
    private readonly HashSet<string> _allowed;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _bannerTimer = new();
    private readonly DispatcherTimer _graceTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Native.WinEventProc _hookProc; // kept in a field so the GC cannot collect the callback
    private readonly SortedSet<string> _blockedApps = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _hook;
    private IntPtr _hwnd;
    private KeyboardGuard? _keys;
    private TaskbarGuard? _taskbar;
    private Task<CoreWebView2Environment>? _webEnv;
    private int _current;
    private List<IntPtr> _switchList = new();
    private int _switchIndex;
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
        _pdfUris = config.PdfPaths.Select(p => new Uri(p).AbsoluteUri).ToArray();
        _viewers = new WebView2?[_pdfUris.Length];
        _allowed = new HashSet<string>(config.AllowedApps, StringComparer.OrdinalIgnoreCase)
        {
            Process.GetCurrentProcess().ProcessName,
            "LockApp",
        };
        _allowed.UnionWith(ScreenshotApps);
        TitleText.Text = config.Names[0];
        AllowedText.Text = config.AllowedApps.Count == 0
            ? (_pdfUris.Length == 1 ? "Allowed: this PDF only" : "Allowed: these PDFs only")
            : "Allowed: " + string.Join(", ", config.AllowedApps);

        for (var i = 0; i < _pdfUris.Length; i++)
        {
            var index = i;
            var tab = new Button
            {
                Content = config.Names[i], Margin = new Thickness(8, 4, 0, 4), Padding = new Thickness(12, 4, 12, 4),
                Focusable = false, ToolTip = config.PdfPaths[i],
            };
            tab.Click += (_, _) => ShowPdf(index);
            _tabs.Add(tab);
            PdfTabs.Children.Add(tab);
        }
        if (_pdfUris.Length > 1) PdfTabs.Visibility = Visibility.Visible;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).AddHook(BlockWindowCommands);
        _hook = Native.SetWinEventHook(Native.EventSystemForeground, Native.EventSystemForeground, IntPtr.Zero,
            _hookProc, 0, 0, Native.WinEventOutOfContext);
        _keys = new KeyboardGuard(allowSnapping: true, allowScreenshots: true);
        _keys.AltTab += reverse => Dispatcher.BeginInvoke(() => SwitchWindow(reverse));
        _keys.AltTabEnded += () => Dispatcher.BeginInvoke(() => _switchList.Clear());
        _taskbar = new TaskbarGuard();
        _taskbar.Blocked += () => Dispatcher.BeginInvoke(() =>
            ShowBanner("The taskbar is disabled during focus sessions. Use Alt+Tab to switch to allowed apps.", 5));

        _start = DateTime.Now;
        Tick();
        _clock.Start();

        ShowPdf(0);
    }

    // --- PDF viewers --------------------------------------------------------

    private void ShowPdf(int index)
    {
        if (_finished) return;
        _current = index;
        _viewers[index] ??= CreateViewer(_pdfUris[index]);
        for (var i = 0; i < _viewers.Length; i++)
        {
            if (_viewers[i] is { } viewer) viewer.Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
            _tabs[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            _tabs[i].Background = i == index ? new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)) : null;
        }
        TitleText.Text = _config.Names[index];
        if (Viewers.Visibility == Visibility.Visible) _viewers[index]!.Focus();
    }

    private WebView2 CreateViewer(string pdfUri)
    {
        var viewer = new WebView2();
        Viewers.Children.Add(viewer);
        _ = InitViewer(viewer, pdfUri);
        return viewer;
    }

    private async Task InitViewer(WebView2 viewer, string pdfUri)
    {
        _webEnv ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamSession", "WebView2"));
        await viewer.EnsureCoreWebView2Async(await _webEnv);

        var core = viewer.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Keep each viewer pinned to its assignment PDF: no links, popups or downloads.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.NavigationStarting += (_, args) =>
        {
            var target = args.Uri.Split('#')[0];
            if (!string.Equals(target, pdfUri, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.Navigate(pdfUri);
    }

    // Ctrl+Tab / Ctrl+PageDown and their reverses cycle the PDFs. WebView2 forwards these as accelerator keys.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pdfUris.Length < 2 || _finished || Viewers.Visibility != Visibility.Visible) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        int step;
        if (e.Key == Key.Tab) step = shift ? -1 : 1;
        else if (e.Key == Key.PageDown) step = 1;
        else if (e.Key == Key.PageUp) step = -1;
        else return;

        e.Handled = true;
        ShowPdf((_current + step + _pdfUris.Length) % _pdfUris.Length);
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
        // Let a snip reach the bottom of the screen.
        if (_taskbar is not null)
            _taskbar.Paused = hwnd != IntPtr.Zero && Native.ProcessName(hwnd) is { } name && ScreenshotApps.Contains(name);
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
        if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
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
        if (target == _hwnd && WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
        Native.SwitchTo(target);
    }

    private void Unhook()
    {
        _keys?.Dispose();
        _keys = null;
        _taskbar?.Dispose();
        _taskbar = null;
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    // Refuses move, resize, minimize and restore however they are requested (Alt+Space menu, Win+arrows, ...).
    private IntPtr BlockWindowCommands(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSysCommand = 0x0112;
        const int ScSize = 0xF000, ScMove = 0xF010, ScMinimize = 0xF020, ScRestore = 0xF120;
        if (msg != WmSysCommand || _finished) return IntPtr.Zero;

        var command = wParam.ToInt32() & 0xFFF0; // low 4 bits are used internally by Windows
        if (command is ScSize or ScMove or ScMinimize or ScRestore) handled = true;
        return IntPtr.Zero;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // The window stays fullscreen for the whole session: no minimizing, restoring or snapping.
        if (!_finished && WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
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
        Viewers.Visibility = Visibility.Hidden;
        EndPanel.Visibility = Visibility.Visible;
        EndInput.Focus();
    }

    private void OnEndCancel(object sender, RoutedEventArgs e)
    {
        EndPanel.Visibility = Visibility.Collapsed;
        Viewers.Visibility = Visibility.Visible;
        _viewers[_current]?.Focus();
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
        Viewers.Visibility = Visibility.Hidden;
        EndButton.IsEnabled = false;
        PdfTabs.IsEnabled = false;
        if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
        Activate();

        DoneTitle.Text = outcome == "completed" ? "Focus session complete." : "Focus session ended early.";
        DoneDetails.Text =
            $"{(_pdfUris.Length == 1 ? "Assignment:  " : "Assignments: ")}  {_config.Name}\n" +
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
