using System.Text.Json;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Desktop;

/// Turns the exceptions a person can actually provoke into something worth reading. A bare
/// "Project validation failed." names neither the project nor what is wrong with it.
public static class FailureText
{
    private const int MaxIssues = 4;

    public static string Describe(Exception exception, string? path = null)
    {
        var name = path is null ? null : Path.GetFileName(path);
        var subject = name is null ? "This project" : $"\"{name}\"";
        return exception switch
        {
            ProjectValidationException validation =>
                $"{subject} is not a valid RoughCut project: {Issues(validation)}",
            JsonException json =>
                $"{subject} is not readable as JSON: {json.Message}",
            FileNotFoundException =>
                $"{subject} no longer exists at that location.",
            DirectoryNotFoundException =>
                $"The folder containing {subject} no longer exists.",
            UnauthorizedAccessException =>
                $"{subject} cannot be read; check the file permissions.",
            RevisionConflictException =>
                $"{subject} changed on disk since it was opened. Reload it and try again.",
            MediaToolException media =>
                $"FFmpeg could not process this media: {media.Message}",
            DesktopPlaybackUnavailableException playback => playback.Message,
            _ => exception.Message
        };
    }

    private static string Issues(ProjectValidationException validation)
    {
        var issues = validation.Issues
            .Take(MaxIssues)
            .Select(issue => $"{issue.Location}: {issue.Message}")
            .ToArray();
        var summary = string.Join("; ", issues);
        var remaining = validation.Issues.Length - issues.Length;
        return remaining > 0 ? $"{summary}; and {remaining} more" : summary;
    }
}
