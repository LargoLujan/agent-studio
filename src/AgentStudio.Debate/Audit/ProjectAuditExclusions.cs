using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Debate.Audit;

/// <summary>A selector of tests that a test step of the profile leaves out of the run, and why (Spanish: the judge and the person read it).</summary>
/// <param name="Filter">The option of the step, such as <c>--filter-not-trait</c>.</param>
/// <param name="Selector">What it names, such as <c>Category=LocalTools</c> or a class.</param>
/// <param name="Reason">Why those tests do not run in the audit, and what that leaves unchecked.</param>
public sealed record ExcludedTests(string Filter, string Selector, string Reason);

/// <summary>
/// What the tests of the profile leave out (docs/08, F5-5; ADR-0017, addendum of 2026-11-11). A whole-project audit only
/// exercises what its test steps run: a category excluded by <c>--filter-not-trait</c> (or a class, a method or a
/// namespace) is not checked by it, and the report must not promise more than it checks. This reads the arguments of the
/// isolated test steps and names every exclusion with the reason it is known by, so the request the person approves, the
/// dossier the judge reads and the report say it.
/// </summary>
public static class ProjectAuditExclusions
{
    private const string NotPrefix = "--filter-not-";

    private static readonly Dictionary<string, string> KnownTraits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Category=LocalTools"] = "necesitan herramientas reales del equipo (git, el propio aislamiento a baja integridad, el editor, el hook de la bitácora) y se ejercen a mano: esta auditoría no comprueba las garantías de seguridad de git ni del aislamiento que esas pruebas cubren",
        ["Category=ConPty"] = "necesitan la terminal ConPTY real del equipo del desarrollador",
        ["Category=Measurement"] = "son mediciones de rendimiento, sensibles a la carga de la máquina, y no se ejercen en una auditoría",
        ["Category=ProviderLive"] = "lanzan los CLIs reales de los proveedores y gastan cuota de suscripción",
        ["Category=NeedsMediumIntegrity"] = "necesitan integridad Medium (escriben en carpetas del usuario que Windows deniega a Low): se ejercen en el CI y a mano",
    };

    /// <summary>The selectors that the isolated test steps of <paramref name="profile"/> leave out, in the order they are written, without repeats.</summary>
    public static IReadOnlyList<ExcludedTests> Of(VerificationProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var found = new List<ExcludedTests>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in profile.Steps.Where(s => s.Kind == EvidenceKind.TestRun && s.Isolation != ProcessIsolation.None))
        {
            for (var i = 0; i + 1 < step.Arguments.Count; i++)
            {
                var option = step.Arguments[i];
                if (!option.StartsWith(NotPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var selector = step.Arguments[i + 1];
                if (seen.Add(option + " " + selector))
                {
                    found.Add(new ExcludedTests(option, selector, ReasonOf(option, selector)));
                }
            }
        }

        return found;
    }

    /// <summary>The lines of the request: nothing when the profile leaves nothing out, otherwise one line saying what the audit does not exercise.</summary>
    public static string Line(IReadOnlyList<ExcludedTests> excluded) =>
        excluded.Count == 0
            ? "Pruebas que la auditoría no ejerce: ninguna (el perfil no excluye ninguna)."
            : "Pruebas que la auditoría NO ejerce, por el filtro del perfil: " + string.Join("; ", excluded.Select(e => $"{e.Selector} ({e.Reason})")) + ".";

    private static string ReasonOf(string option, string selector) =>
        option.Equals(NotPrefix + "trait", StringComparison.OrdinalIgnoreCase) && KnownTraits.TryGetValue(selector, out var reason)
            ? reason
            : "excluido por el filtro del perfil del proyecto, sin motivo conocido por Agent Studio";
}
