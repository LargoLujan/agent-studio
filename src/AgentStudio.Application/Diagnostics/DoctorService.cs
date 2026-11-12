using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Application.Diagnostics;

/// <summary>
/// What a source that reads a computer shares across the checks of one run (docs/20 D-03): the tools are asked once per run, however many checks
/// need the answer, and an answer is never kept for the next run. The service opens the scope before the sources run and closes it after.
/// </summary>
public interface IDoctorRunScope
{
    /// <summary>A run starts: what was kept is dropped, and what is read from now on is kept until <see cref="End"/>.</summary>
    void Begin();

    /// <summary>The run ended: nothing is kept.</summary>
    void End();
}

/// <summary>
/// Runs every source of Auto Doctor and joins their checks (docs/20 D-01). It never fails whole: a source that throws becomes one check in error
/// whose code names the source and whose facts hold no text of the exception (it may quote a path or a line of a file of the repository), and a
/// code that two sources repeat is told apart, so the report is always valid.
/// </summary>
public sealed class DoctorService(IEnumerable<IDoctorCheckSource> sources, IEnumerable<IDoctorRunScope>? scopes = null) : IDoctorService
{
    private readonly IReadOnlyList<IDoctorCheckSource> _sources = [.. sources ?? throw new ArgumentNullException(nameof(sources))];
    private readonly IReadOnlyList<IDoctorRunScope> _scopes = [.. scopes ?? []];

    /// <summary>The sections that need an open project: without one they are not run (docs/20 D-03).</summary>
    public static bool NeedsProject(DoctorSection section) => section is DoctorSection.Project or DoctorSection.DeclaredEnvironment;

    public async Task<DoctorReport> RunAsync(ProjectContext? project, CancellationToken ct)
    {
        foreach (var scope in _scopes)
        {
            scope.Begin();
        }

        try
        {
            var runnable = _sources.Where(s => project is not null || !NeedsProject(s.Section)).ToList();
            var results = await Task.WhenAll(runnable.Select((s, i) => RunSourceAsync(s, i, project, ct))).ConfigureAwait(false);
            var ordered = runnable.Select((s, i) => (s.Section, Index: i, Checks: results[i])).OrderBy(r => r.Section).ThenBy(r => r.Index);
            return new DoctorReport(Distinct(ordered.SelectMany(r => r.Checks)));
        }
        finally
        {
            foreach (var scope in _scopes)
            {
                scope.End();
            }
        }
    }

    private static async Task<IReadOnlyList<DoctorCheck>> RunSourceAsync(IDoctorCheckSource source, int index, ProjectContext? project, CancellationToken ct)
    {
        try
        {
            return await source.CheckAsync(project, ct).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One faulty source must not hide the checks of the others.
        catch (Exception)
#pragma warning restore CA1031
        {
            var name = source.GetType().Name;
            return
            [
                new DoctorCheck(
                    "doctor.source-failed." + Kebab(name) + "-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    source.Section,
                    DoctorStatus.Error,
                    [DoctorFact.Of("source", name)]),
            ];
        }
    }

    /// <summary>A report holds a code once: the second check that repeats one gets <c>-2</c>, then <c>-3</c>… (a bug of a source, shown and not thrown).</summary>
    private static List<DoctorCheck> Distinct(IEnumerable<DoctorCheck> checks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DoctorCheck>();
        foreach (var check in checks)
        {
            var code = check.Code;
            for (var n = 2; !seen.Add(code); n++)
            {
                code = check.Code + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            result.Add(code == check.Code ? check : new DoctorCheck(code, check.Section, check.Status, check.Facts, check.FixCode));
        }

        return result;
    }

    /// <summary>A type name as a piece of a code: <c>ProjectCheckSource</c> is <c>project-check-source</c>.</summary>
    private static string Kebab(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsAsciiLetterUpper(c))
            {
                if (text.Length > 0 && text[^1] != '-')
                {
                    text.Append('-');
                }

                text.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                text.Append(c);
            }
        }

        var kebab = text.Length == 0 ? "source" : text.ToString();
        return kebab.Length > 40 ? kebab[..40] : kebab;
    }
}
