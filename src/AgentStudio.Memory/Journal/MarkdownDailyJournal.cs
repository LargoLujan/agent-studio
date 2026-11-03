using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Memory.Journal;

/// <summary>
/// Append-only daily prompt journal in Markdown: <c>&lt;projectRoot&gt;/.agentstudio/bitacora/yyyy-MM-dd.md</c>.
/// Shares the file and the format of its title and its lines with the development hook <c>.claude/hooks/bitacora.ps1</c>,
/// so both can write the same file. Whoever creates the day writes the two lines under the title, each saying what it
/// writes: the app's (<see cref="Preamble"/> and <see cref="ResultPreamble"/>, V31) speak of the requests to the agents and
/// their results, not of the «Hecho» lines Claude adds in the repository of Agent Studio. The daily summary knows both.
/// The day is the local day (per <see cref="TimeProvider.LocalTimeZone"/>) of the instant being recorded.
/// Every day is opened through <paramref name="files"/>, the policy of the project's storage: <c>.agentstudio</c> and
/// <c>bitacora</c> must be real folders of the project and the day a file with a single name there, checked through the
/// handle that appends (ADR-0010, addenda of 2026-10-18). A link left there refuses the record, so the agent is never
/// launched (<see cref="Application.Providers.JournalingAgentProvider"/>), and nothing is written through it.
/// </summary>
public sealed partial class MarkdownDailyJournal(string projectRoot, IProjectStorageFiles files, TimeProvider timeProvider) : IPromptJournal
{
    /// <summary>The first line under the title of a day the app creates: what each entry says.</summary>
    internal const string Preamble =
        "Registro automático de Agent Studio: una línea por cada petición a un agente (hora · número · quién · proveedor/modelo · etiquetas · resumen).";

    /// <summary>The second line under the title of a day the app creates: what the result of a request says.</summary>
    internal const string ResultPreamble =
        "Cuando el agente termina, se añade una línea «Resultado» con el estado, la duración y los tokens.";

    private const string Folder = AgentStudioFiles.JournalFolder;
    private const int MaxSummaryLength = 200;
    private const int LockAttempts = 60;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    // Markdown syntax characters that get a backslash so free text cannot look like a heading, entry or tag.
    private static readonly SearchValues<char> MarkdownSpecials = SearchValues.Create("\\*_[]()#`<>|");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Spanish number style (1.200 / 12,3) independent of the machine culture.
    private static readonly NumberFormatInfo Spanish = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var local = ToLocal(entry.At);
        var day = DateOnly.FromDateTime(local.DateTime);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Sequence calculation and append happen under one exclusive file lock, so other processes cannot
            // take the same id in between.
            return await WithLockedFileAsync(
                day,
                stream =>
                {
                    var id = new JournalEntryId(day, NextSequence(stream));
                    return (id, FormatEntry(id, local, entry));
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var local = ToLocal(outcome.At);
        var day = DateOnly.FromDateTime(local.DateTime);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WithLockedFileAsync(day, _ => (0, FormatOutcome(id, local, outcome)), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string FormatEntry(JournalEntryId id, DateTimeOffset local, PromptJournalEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append("- **").Append(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("** · ")
            .Append(id).Append(" · ")
            .Append(Collapse(entry.Actor)).Append(" · ")
            .Append(ProviderModel(entry)).Append(" · ")
            .Append(string.Join(' ', NormalizeTags(entry.Tags))).Append(" · «")
            .Append(SanitizeSummary(entry.Summary)).Append('»');

        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.TaskId))
        {
            refs.Add("tarea " + Collapse(entry.TaskId));
        }

        if (!string.IsNullOrWhiteSpace(entry.DebateId))
        {
            refs.Add("debate " + Collapse(entry.DebateId));
        }

        if (refs.Count > 0)
        {
            sb.Append(" (").Append(string.Join(" · ", refs)).Append(')');
        }

        return sb.ToString();
    }

    private static string FormatOutcome(JournalEntryId id, DateTimeOffset local, PromptOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.Append("  - Resultado ").Append(id).Append(" · ")
            .Append(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(" · ")
            .Append(outcome.Status).Append(" · ")
            .Append(outcome.Duration.TotalSeconds.ToString("F1", Spanish)).Append(" s · ")
            .Append(FormatTokens(outcome.InputTokens, outcome.OutputTokens));

        if (!string.IsNullOrWhiteSpace(outcome.Note))
        {
            sb.Append(" · ").Append(SanitizeSummary(outcome.Note));
        }

        // Last, after the free note, whose backticks were replaced: the AgentRunStarted the launch was announced with
        // (docs/17 D-02), the contract the derivation of the journal reads.
        if (outcome.Anchor is { } anchor)
        {
            sb.Append(" · ").Append(JournalResultAnchor.Format(anchor));
        }

        return sb.ToString();
    }

    private static string FormatTokens(MetricValue<long> input, MetricValue<long> output)
    {
        if (!input.HasValue && !output.HasValue)
        {
            return "tokens no disponibles";
        }

        var provenance = MetricValue<long>.Combine(
            input.HasValue ? input.Provenance : MetricProvenance.Exact,
            output.HasValue ? output.Provenance : MetricProvenance.Exact);
        var label = provenance == MetricProvenance.Estimated ? "estimados" : "exactos";
        return $"tokens {Number(input)} → {Number(output)} ({label})";

        static string Number(MetricValue<long> value) => value.Value is { } v ? v.ToString("N0", Spanish) : "n/d";
    }

    private static string ProviderModel(PromptJournalEntry entry)
    {
        var provider = entry.Provider?.Value;
        var model = string.IsNullOrWhiteSpace(entry.Model) ? null : Collapse(entry.Model);
        return provider is null && model is null ? "-" : $"{provider ?? "-"}/{model ?? "-"}";
    }

    private static string SanitizeSummary(string? text)
    {
        var clean = Collapse(text).Replace('«', '‹').Replace('»', '›').Replace('`', '\'');
        if (clean.Length == 0)
        {
            return "(sin resumen)";
        }

        var limited = clean.Length > MaxSummaryLength ? clean[..(MaxSummaryLength - 3)].TrimEnd() + "..." : clean;
        return EscapeMarkdown(limited);
    }

    private static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (MarkdownSpecials.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Collapse(string? text) => WhitespaceRegex().Replace(text ?? string.Empty, " ").Trim();

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new List<string> { "#prompt" };
        foreach (var raw in tags ?? [])
        {
            var tag = NormalizeTag(raw);
            if (tag.Length > 0 && !result.Contains(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private static string NormalizeTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        var slug = InvalidTagCharsRegex().Replace(sb.ToString().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? string.Empty : "#" + slug;
    }

    private static int NextSequence(Stream stream)
    {
        var max = 0;
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            var match = EntryLineRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return max + 1;
    }

    /// <summary>
    /// Opens the day file with write sharing denied (other writers, the hook included, get a sharing violation and
    /// retry), lets <paramref name="build"/> compute the line from that same stream and appends it. The lock is held
    /// only while reading the sequence and writing. Only a sharing violation is retried: a refusal of the storage's
    /// policy is thrown at once.
    /// </summary>
    private async Task<T> WithLockedFileAsync<T>(DateOnly day, Func<Stream, (T Result, string Line)> build, CancellationToken ct)
    {
        var path = DayPath(day);
        for (var attempt = 1; ; attempt++)
        {
            Stream stream;
            try
            {
                stream = files.OpenForAppend(projectRoot, path);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < LockAttempts)
            {
                await Task.Delay(Math.Min(10 * attempt, 100) + Random.Shared.Next(10), ct).ConfigureAwait(false);
                continue;
            }

            using (stream)
            {
                var (result, line) = build(stream);
                var text = new StringBuilder();
                if (stream.Length == 0)
                {
                    text.Append("# Bitácora · ").Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(Environment.NewLine)
                        .Append(Environment.NewLine)
                        .Append(Preamble).Append(Environment.NewLine)
                        .Append(ResultPreamble).Append(Environment.NewLine)
                        .Append(Environment.NewLine);
                }
                else
                {
                    // A manual edit may have left the file without a final line break.
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n')
                    {
                        text.Append(Environment.NewLine);
                    }
                }

                text.Append(line).Append(Environment.NewLine);

                // One write of the whole block, so a concurrent writer cannot interleave inside a line.
                stream.Seek(0, SeekOrigin.End);
                stream.Write(Utf8.GetBytes(text.ToString()));
                return result;
            }
        }
    }

    /// <summary>The day file, relative to the project's <c>.agentstudio</c>.</summary>
    private static string DayPath(DateOnly day) => Folder + "/" + AgentStudioFiles.JournalDayFile(day);

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, timeProvider.LocalTimeZone);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex InvalidTagCharsRegex();

    [GeneratedRegex(@"^- \*\*[^*]+\*\* · P-(\d+) · ")]
    private static partial Regex EntryLineRegex();
}
