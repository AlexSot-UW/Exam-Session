using System.IO;

namespace ExamSession;

public sealed record ExamConfig(
    string PdfPath,
    TimeSpan Reading,
    TimeSpan Writing,
    string? ExitPassword,
    string LogPath)
{
    public string ExamName => Path.GetFileNameWithoutExtension(PdfPath);
    public TimeSpan Total => Reading + Writing;
}
