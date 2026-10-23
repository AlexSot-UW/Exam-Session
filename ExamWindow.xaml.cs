using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ExamSession;

public partial class ExamWindow : Window
{
    private const string ConfirmPhrase = "END EXAM";

    private readonly ExamConfig _config;
    private readonly string _pdfUri;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _bannerTimer = new();

    private KeyboardGuard? _keys;
    private DateTime _start;
    private bool _allowClose;
    private bool _finished;
    private bool _warned15;
    private bool _readingOver;
    private int _focusLosses;
    private DateTime? _lostAt;
    private TimeSpan _timeAway;

    public ExamWindow(ExamConfig config)
    {
        InitializeComponent();
        _config = config;
        _pdfUri = new Uri(config.PdfPath).AbsoluteUri;
        TitleText.Text = config.ExamName;

        _clock.Tick += (_, _) => Tick();
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            Banner.Visibility = Visibility.Collapsed;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _keys = new KeyboardGuard(allowSnapping: false);

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamSession", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await Viewer.EnsureCoreWebView2Async(env);

        var core = Viewer.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Keep the viewer pinned to the exam PDF: no links, popups or downloads.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.NavigationStarting += (_, args) =>
        {
            var target = args.Uri.Split('#')[0];
            if (!string.Equals(target, _pdfUri, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.Navigate(_pdfUri);

        _start = DateTime.Now;
        Tick();
        _clock.Start();
    }

    private void Tick()
    {
        var elapsed = DateTime.Now - _start;
        var remaining = _config.Total - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            TimerText.Text = "00:00:00";
            Finish("completed");
            return;
        }

        if (elapsed < _config.Reading)
        {
            PhaseText.Text = "Reading time";
            TimerText.Text = Format(_config.Reading - elapsed);
            return;
        }

        if (!_readingOver && _config.Reading > TimeSpan.Zero)
        {
            _readingOver = true;
            ShowBanner("Reading time is over. You may start writing.", 8);
        }

        PhaseText.Text = "Writing";
        TimerText.Text = Format(remaining);

        if (remaining <= TimeSpan.FromMinutes(5))
            TimerText.Foreground = Brushes.IndianRed;
        else if (remaining <= TimeSpan.FromMinutes(15))
            TimerText.Foreground = Brushes.Orange;

        if (!_warned15 && remaining <= TimeSpan.FromMinutes(15) && _config.Writing > TimeSpan.FromMinutes(15))
        {
            _warned15 = true;
            ShowBanner("15 minutes remaining.", 10);
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

    // --- Staying in front -------------------------------------------------

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_finished) return;
        _focusLosses++;
        _lostAt ??= DateTime.Now;
        Dispatcher.BeginInvoke(() =>
        {
            Topmost = true;
            Activate();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (_lostAt is not { } lostAt) return;
        _timeAway += DateTime.Now - lostAt;
        _lostAt = null;
        AwayText.Text = $"Left window {_focusLosses}x ({(int)_timeAway.TotalSeconds}s)";
        ShowBanner($"You left the exam window. This has been recorded ({_focusLosses} time(s)).", 6);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!_finished && WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (!_finished) ShowEndPanel();
    }

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
        _keys?.Dispose();
        _keys = null;
        if (_lostAt is { } lostAt)
        {
            _timeAway += DateTime.Now - lostAt;
            _lostAt = null;
        }

        var ended = DateTime.Now;
        var elapsed = ended - _start;
        var logNote = WriteLog(ended, elapsed, outcome);

        EndPanel.Visibility = Visibility.Collapsed;
        Banner.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Hidden;
        EndButton.IsEnabled = false;
        Topmost = false;

        DoneTitle.Text = outcome == "completed" ? "Time's up. Pens down." : "Exam ended early.";
        DoneDetails.Text =
            $"Exam:          {_config.ExamName}\n" +
            $"Planned:       {Format(_config.Total)}\n" +
            $"Actual:        {Format(elapsed)}\n" +
            $"Left window:   {_focusLosses} time(s), {(int)_timeAway.TotalSeconds}s total\n\n" +
            logNote;
        DonePanel.Visibility = Visibility.Visible;
    }

    private string WriteLog(DateTime ended, TimeSpan elapsed, string outcome)
    {
        try
        {
            var isNew = !File.Exists(_config.LogPath);
            using var writer = new StreamWriter(_config.LogPath, append: true);
            if (isNew)
                writer.WriteLine("started,exam,reading_min,writing_min,ended,elapsed_min,outcome,focus_losses,seconds_away");
            var inv = CultureInfo.InvariantCulture;
            writer.WriteLine(string.Join(",",
                _start.ToString("yyyy-MM-dd HH:mm:ss", inv),
                $"\"{_config.ExamName.Replace("\"", "\"\"")}\"",
                _config.Reading.TotalMinutes.ToString(inv),
                _config.Writing.TotalMinutes.ToString(inv),
                ended.ToString("yyyy-MM-dd HH:mm:ss", inv),
                elapsed.TotalMinutes.ToString("F1", inv),
                outcome,
                _focusLosses.ToString(inv),
                ((int)_timeAway.TotalSeconds).ToString(inv)));
            return $"Logged to {_config.LogPath}";
        }
        catch (Exception ex)
        {
            return $"Could not write log: {ex.Message}";
        }
    }

    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }
}
