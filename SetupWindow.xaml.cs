using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ExamSession;

public sealed class AppChoice
{
    public required string ProcessName { get; init; }
    public required string Label { get; init; }
    public bool IsChecked { get; set; }
}

public partial class SetupWindow : Window
{
    private static readonly string AllowedAppsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamSession", "focus-apps.txt");

    private readonly string _examsDir = FindExamsDir();
    private readonly string _assignmentsDir;
    private readonly List<FileInfo> _exams = new();
    private readonly List<FileInfo> _assignments = new();
    private List<AppChoice> _apps = new();

    public SetupWindow()
    {
        InitializeComponent();
        _assignmentsDir = Path.Combine(Directory.GetParent(_examsDir)!.FullName, "Assignments");
        Directory.CreateDirectory(_assignmentsDir);

        ExamsDirText.Text = $"PDFs from: {_examsDir}";
        AssignmentsDirText.Text = $"PDFs from: {_assignmentsDir}";
        LoadPdfs(_examsDir, _exams, ExamList);
        LoadPdfs(_assignmentsDir, _assignments, AssignmentList);
        RefreshApps(LoadSavedApps());
    }

    // Walk up from the exe so the project-level Exams folder is found when running from bin\.
    private static string FindExamsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Exams");
            if (Directory.Exists(candidate)) return candidate;
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "Exams");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static void LoadPdfs(string dir, List<FileInfo> files, ListBox list)
    {
        files.Clear();
        files.AddRange(Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        list.ItemsSource = null;
        list.ItemsSource = files;
        if (files.Count > 0) list.SelectedIndex = 0;
    }

    private void BrowsePdf(string dir, List<FileInfo> files, ListBox list)
    {
        var multi = list.SelectionMode != SelectionMode.Single;
        var dialog = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", InitialDirectory = dir, Multiselect = multi };
        if (dialog.ShowDialog(this) != true) return;

        var added = dialog.FileNames.Select(f => new FileInfo(f)).ToList();
        files.AddRange(added);
        list.ItemsSource = null;
        list.ItemsSource = files;
        if (!multi)
        {
            list.SelectedItem = added[0];
            return;
        }
        list.SelectedItems.Clear();
        foreach (var file in added) list.SelectedItems.Add(file);
    }

    private static void OpenFolder(string dir) =>
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });

    private void LaunchSession(Window session)
    {
        session.Closed += (_, _) =>
        {
            Show();
            Activate();
        };
        Hide();
        session.Show();
    }

    // --- Mock exam --------------------------------------------------------

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => LoadPdfs(_examsDir, _exams, ExamList);

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) => OpenFolder(_examsDir);

    private void OnBrowseClicked(object sender, RoutedEventArgs e) => BrowsePdf(_examsDir, _exams, ExamList);

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (ExamList.SelectedItem is not FileInfo pdf)
        {
            ShowError(ErrorText, "Pick an exam PDF first.");
            return;
        }
        if (!int.TryParse(WritingBox.Text, out var writing) || writing <= 0)
        {
            ShowError(ErrorText, "Writing time must be a whole number of minutes greater than 0.");
            return;
        }
        if (!int.TryParse(ReadingBox.Text, out var reading) || reading < 0)
        {
            ShowError(ErrorText, "Reading time must be a whole number of minutes (0 for none).");
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        var password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;
        var logPath = Path.Combine(Directory.GetParent(_examsDir)!.FullName, "session-log.csv");
        var config = new ExamConfig(pdf.FullName, TimeSpan.FromMinutes(reading), TimeSpan.FromMinutes(writing),
            password, logPath);

        LaunchSession(new ExamWindow(config));
    }

    // --- Focus ------------------------------------------------------------

    private void OnRefreshAssignmentsClicked(object sender, RoutedEventArgs e) =>
        LoadPdfs(_assignmentsDir, _assignments, AssignmentList);

    private void OnOpenAssignmentsFolderClicked(object sender, RoutedEventArgs e) => OpenFolder(_assignmentsDir);

    private void OnBrowseAssignmentClicked(object sender, RoutedEventArgs e) =>
        BrowsePdf(_assignmentsDir, _assignments, AssignmentList);

    private IEnumerable<string> CheckedApps() => _apps.Where(a => a.IsChecked).Select(a => a.ProcessName);

    private void OnRefreshAppsClicked(object sender, RoutedEventArgs e) => RefreshApps(CheckedApps().ToList());

    private void OnAddAppClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        RefreshApps(CheckedApps().Append(Path.GetFileNameWithoutExtension(dialog.FileName)).ToList());
    }

    private void RefreshApps(IEnumerable<string> checkedNames)
    {
        var selected = new HashSet<string>(checkedNames, StringComparer.OrdinalIgnoreCase);
        var own = Process.GetCurrentProcess().ProcessName;

        var running = Native.AppWindows()
            .Where(w => !string.Equals(w.Process, own, StringComparison.OrdinalIgnoreCase))
            .GroupBy(w => w.Process, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppChoice
            {
                ProcessName = g.Key,
                Label = $"{g.Key}  —  {g.First().Title}",
                IsChecked = selected.Contains(g.Key),
            })
            .ToList();
        var notRunning = selected
            .Where(name => !running.Any(a => string.Equals(a.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => new AppChoice { ProcessName = name, Label = $"{name}  —  (not running)", IsChecked = true });

        _apps = running.Concat(notRunning)
            .OrderByDescending(a => a.IsChecked)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppList.ItemsSource = _apps;
    }

    private static IEnumerable<string> LoadSavedApps()
    {
        try
        {
            return File.ReadAllLines(AllowedAppsFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void SaveApps(IEnumerable<string> apps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AllowedAppsFile)!);
            File.WriteAllLines(AllowedAppsFile, apps);
        }
        catch (IOException)
        {
            // Remembering the selection is a convenience only.
        }
    }

    private void OnStartFocusClicked(object sender, RoutedEventArgs e)
    {
        // Keep the list order rather than click order, so the session tabs match the setup list.
        var pdfs = _assignments.Where(AssignmentList.SelectedItems.Contains).Select(f => f.FullName).ToList();
        if (pdfs.Count == 0)
        {
            ShowError(FocusErrorText, "Pick at least one assignment PDF first.");
            return;
        }
        if (!int.TryParse(DurationBox.Text, out var duration) || duration < 0)
        {
            ShowError(FocusErrorText, "Duration must be a whole number of minutes (0 for no limit).");
            return;
        }

        FocusErrorText.Visibility = Visibility.Collapsed;
        var allowed = CheckedApps().ToList();
        SaveApps(allowed);
        var password = string.IsNullOrEmpty(FocusPasswordBox.Password) ? null : FocusPasswordBox.Password;
        var logPath = Path.Combine(Directory.GetParent(_examsDir)!.FullName, "focus-log.csv");
        var config = new FocusConfig(pdfs,TimeSpan.FromMinutes(duration), allowed, password, logPath);

        LaunchSession(new FocusWindow(config));
    }

    private static void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }
}
