using System.IO;

namespace ExamSession;

public sealed record FocusConfig(
    string PdfPath,
    TimeSpan Duration,
    IReadOnlyList<string> AllowedApps,
    string? ExitPassword,
    string LogPath)
{
    public string Name => Path.GetFileNameWithoutExtension(PdfPath);

    /// <summary>A zero duration means the session runs until ended by hand.</summary>
    public bool HasLimit => Duration > TimeSpan.Zero;
}
