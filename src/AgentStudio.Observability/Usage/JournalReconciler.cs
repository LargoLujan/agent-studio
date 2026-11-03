using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;

namespace AgentStudio.Observability.Usage;

/// <summary>
/// Compares the prompts of the journal with the attempts of the log (docs/14 D-08): the difference is shown, never
/// added to any figure. Only the product's entries count (<c>- **HH:mm:ss** · P-NNNN · …</c>, written by
/// <c>MarkdownDailyJournal</c> when a prompt is launched); the development hook's entries, the results, the «Hecho»
/// lines and anything else are no prompts. A project is reconciled from its first attempt of Phase 4 (the first
/// <c>AgentRunStarted</c> of version 2), because before it some prompts never reached the log. Every day is read
/// through <see cref="IProjectStorageReader"/>, which refuses a link planted as a day. The journal names its days and
/// times in the local zone of the machine that wrote it, which is the zone the panel queries in.
/// </summary>
internal static partial class JournalReconciler
{
    public const string NoJournal = "Sin bitácora del periodo.";

    public const string NoPhase4 = "No hay intentos de la Fase 4 en el periodo: la bitácora no se concilia con el registro.";

    public static UsageReconciliation Reconcile(
        IProjectStorageReader storage,
        IReadOnlyList<(string Root, UsageProjection Projection)> projects,
        UsageQuery query,
        IReadOnlyList<string> projectionWarnings,
        CancellationToken ct)
    {
        var warnings = new List<string>(projectionWarnings);
        var recorded = 0;
        var prompts = 0;
        var reconciled = false;
        var anyDay = false;
        var unreadable = false;
        foreach (var (root, projection) in projects)
        {
            if (projection.Phase4Since is not { } since)
            {
                continue;
            }

            var start = since > query.From ? since : query.From;
            if (start >= query.To)
            {
                continue;
            }

            reconciled = true;
            recorded += projection.Starts.Count(s => s >= start && s < query.To);
            var first = LocalDate(start, query.Zone);
            var last = LocalDate(query.To.AddTicks(-1), query.Zone);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                var path = "bitacora/" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md";
                try
                {
                    using var stream = storage.OpenForRead(root, path);
                    anyDay = true;
                    // The journal keeps whole seconds: the prompt of the first attempt is written in the same second
                    // it starts, before its tick, so the journal is counted from the start of that second.
                    prompts += CountPrompts(stream, day, WholeSecond(start), query.To, query.Zone);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // No prompt that day.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable = true;
                    warnings.Add($"No se pudo leer la bitácora {path} de {root}, así que no se concilia: {ex.Message}");
                }
            }
        }

        if (!reconciled)
        {
            warnings.Add(NoPhase4);
            return new UsageReconciliation(null, recorded, warnings);
        }

        if (unreadable)
        {
            return new UsageReconciliation(null, recorded, warnings);
        }

        if (!anyDay)
        {
            warnings.Add(NoJournal);
            return new UsageReconciliation(null, recorded, warnings);
        }

        var difference = prompts - recorded;
        if (difference != 0)
        {
            var count = Math.Abs(difference);
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Bitácora: {prompts} {(prompts == 1 ? "prompt" : "prompts")}; registro: {recorded} {(recorded == 1 ? "intento" : "intentos")}. Diferencia de {count} {(count == 1 ? "prompt" : "prompts")}, que no se suma (pruebas en vivo, versiones anteriores o intentos que no llegaron al proveedor)."));
        }

        return new UsageReconciliation(prompts, recorded, warnings);
    }

    /// <summary>The instant at the start of its second: the resolution of the times of the journal.</summary>
    private static DateTimeOffset WholeSecond(DateTimeOffset at) => at.AddTicks(-(at.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>The prompts of one day of the journal whose local time falls in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static int CountPrompts(Stream stream, DateOnly day, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            var match = PromptEntry().Match(line);
            if (!match.Success || !TimeOnly.TryParseExact(match.Groups["time"].ValueSpan, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                continue;
            }

            var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (at >= from && at < to)
            {
                count++;
            }
        }

        return count;
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    [GeneratedRegex(@"^- \*\*(?<time>\d{2}:\d{2}:\d{2})\*\* · P-\d+ · ", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PromptEntry();
}
