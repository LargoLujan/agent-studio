using System.Globalization;
using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;

namespace AgentStudio.Memory.Summary;

/// <summary>A day of the journal as read: its text (null when there is no file) and the SHA-256 of its bytes.</summary>
internal sealed record JournalDay(DateOnly Day, string? Text, string Sha256);

/// <summary>
/// Reads a day of the prompt journal, <c>.agentstudio/bitacora/AAAA-MM-DD.md</c>, through the checked read of the storage
/// (<see cref="IProjectStorageReader"/>): a link planted as the day, or as a folder on the way, is refused, so the summary
/// never copies a file from outside the project into the memory and the versioned Markdown (docs/14 D-31).
/// </summary>
internal static class JournalReader
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static JournalDay Read(IProjectStorageReader reader, string projectRoot, DateOnly day)
    {
        byte[] bytes;
        try
        {
            using var stream = reader.OpenForRead(projectRoot, "bitacora/" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }
        catch (FileNotFoundException)
        {
            return new JournalDay(day, null, ContentHash.Sha256Hex(ReadOnlySpan<byte>.Empty));
        }

        var text = Utf8.GetString(bytes);
        return new JournalDay(day, text.Length > 0 && text[0] == '﻿' ? text[1..] : text, ContentHash.Sha256Hex(bytes));
    }
}
