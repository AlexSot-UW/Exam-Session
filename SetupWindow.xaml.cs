using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ExamSession;

public partial class SetupWindow : Window
{
    private readonly string _examsDir = FindExamsDir();
    private readonly List<FileInfo> _exams = new();

    public SetupWindow()
    {
        InitializeComponent();
        ExamsDirText.Text = $"PDFs from: {_examsDir}";
        RefreshList();
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

    private void RefreshList()
    {
        _exams.Clear();
        _exams.AddRange(Directory.EnumerateFiles(_examsDir, "*.pdf", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        ExamList.ItemsSource = null;
        ExamList.ItemsSource = _exams;
        if (_exams.Count > 0) ExamList.SelectedIndex = 0;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshList();

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_examsDir) { UseShellExecute = true });

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", InitialDirectory = _examsDir };
        if (dialog.ShowDialog(this) != true) return;

        var file = new FileInfo(dialog.FileName);
        _exams.Add(file);
        ExamList.ItemsSource = null;
        ExamList.ItemsSource = _exams;
        ExamList.SelectedItem = file;
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (ExamList.SelectedItem is not FileInfo pdf)
        {
            ShowError("Pick an exam PDF first.");
            return;
        }
        if (!int.TryParse(WritingBox.Text, out var writing) || writing <= 0)
        {
            ShowError("Writing time must be a whole number of minutes greater than 0.");
            return;
        }
        if (!int.TryParse(ReadingBox.Text, out var reading) || reading < 0)
        {
            ShowError("Reading time must be a whole number of minutes (0 for none).");
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        var password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;
        var logPath = Path.Combine(Directory.GetParent(_examsDir)!.FullName, "session-log.csv");
        var config = new ExamConfig(pdf.FullName, TimeSpan.FromMinutes(reading), TimeSpan.FromMinutes(writing),
            password, logPath);

        var exam = new ExamWindow(config);
        exam.Closed += (_, _) =>
        {
            Show();
            Activate();
        };
        Hide();
        exam.Show();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
