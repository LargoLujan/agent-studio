using System.Globalization;
using System.Text;
using AgentStudio.Application.Projects;

namespace AgentStudio.Memory.Derivation;

/// <summary>A day of the journal that could not be read safely: why, in Spanish for the person, and in English for its alert.</summary>
internal sealed record JournalProblem(string Problem, string Alert);

/// <summary>The days of the journal of a project as read now: each readable day with its text, and what could not be read.</summary>
internal sealed record JournalRead(IReadOnlyList<(DateOnly Day, string Text)> Days, IReadOnlyList<JournalProblem> Problems);

/// <summary>
/// Reads the days of the journal of a project (<c>.agentstudio/bitacora/AAAA-MM-DD.md</c>) for the derivation (docs/17 D-02).
/// <list type="bullet">
/// <item>The folder is listed, and only the names its writers give a day (<see cref="AgentStudioFiles.IsJournalDay"/>) are
/// taken. A folder that is a link is not read at all.</item>
/// <item>Each day is read through <see cref="IProjectStorageReader"/>, with the policy of the storage of the project: a day
/// that is a link, or has a second name, or a folder on the way that is a link, is refused, so nothing from outside the
/// project becomes memory. A day that vanished in between is skipped.</item>
/// <item>A day larger than <see cref="MaxDayBytes"/> is not read: the journal is content of the repository.</item>
/// </list>
/// What cannot be read leaves a <see cref="JournalProblem"/> and nothing of it is anchored; the rest is read as UTF-8.
/// </summary>
internal static class JournalFiles
{
    /// <summary>The largest day of the journal read, in bytes.</summary>
    public const int MaxDayBytes = 8 * 1024 * 1024;

    private const string Folder = AgentStudioFiles.DataFolder + "/" + AgentStudioFiles.JournalFolder;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static JournalRead Read(IProjectStorageReader reader, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var folder = Path.Combine(projectRoot, AgentStudioFiles.DataFolder, AgentStudioFiles.JournalFolder);
        List<string> names;
        try
        {
            if (!Directory.Exists(folder))
            {
                return new JournalRead([], []);
            }

            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                return new JournalRead([], [FolderProblem("it is a link")]);
            }

            names = [.. Directory.EnumerateFiles(folder, "*.md", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false })
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(AgentStudioFiles.IsJournalDay)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JournalRead([], [FolderProblem(ex.Message)]);
        }

        var days = new List<(DateOnly Day, string Text)>();
        var problems = new List<JournalProblem>();
        foreach (var name in names)
        {
            var day = DateOnly.ParseExact(name.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                if (ReadDay(reader, projectRoot, name) is { } text)
                {
                    days.Add((day, text));
                }
                else
                {
                    problems.Add(DayProblem(day, name, string.Create(CultureInfo.InvariantCulture, $"it is larger than {MaxDayBytes} bytes")));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The day went away since the folder was listed: nothing to anchor.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(DayProblem(day, name, ex.Message));
            }
        }

        return new JournalRead(days, problems);
    }

    /// <summary>The text of a day, or null when it is larger than <see cref="MaxDayBytes"/>.</summary>
    private static string? ReadDay(IProjectStorageReader reader, string projectRoot, string name)
    {
        using var stream = reader.OpenForRead(projectRoot, AgentStudioFiles.JournalFolder + "/" + name);
        using var copy = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (copy.Length + read > MaxDayBytes)
            {
                return null;
            }

            copy.Write(buffer, 0, read);
        }

        return Utf8.GetString(copy.GetBuffer(), 0, (int)copy.Length);
    }

    private static JournalProblem FolderProblem(string why) => new(
        $"La carpeta de la bitácora ({Folder}) no se pudo leer de forma segura: no se ancla nada de ella en la memoria.",
        $"The folder of the journal ('{Folder}') could not be read safely ({why}): nothing of it is anchored as memory.");

    private static JournalProblem DayProblem(DateOnly day, string name, string why)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new JournalProblem(
            $"El día {date} de la bitácora ({Folder}/{name}) no se pudo leer de forma segura: no se ancla nada de él en la memoria.",
            $"Day {date} of the journal ('{Folder}/{name}') could not be read safely ({why}): nothing of it is anchored as memory.");
    }
}
