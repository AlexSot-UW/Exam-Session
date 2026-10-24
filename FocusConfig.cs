using System.IO;

namespace ExamSession;

public sealed record FocusConfig(
    IReadOnlyList<string> PdfPaths,
    TimeSpan Duration,
    IReadOnlyList<string> AllowedApps,
    string? ExitPassword,
    string LogPath)
{
    public IReadOnlyList<string> Names => PdfPaths.Select(Path.GetFileNameWithoutExtension).ToList()!;

    public string Name => string.Join("; ", Names);

    /// <summary>A zero duration means the session runs until ended by hand.</summary>
    public bool HasLimit => Duration > TimeSpan.Zero;
}
