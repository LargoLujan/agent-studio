using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// A step as the user should read it before approving a plan that runs it on the machine: id, title, the exact command
/// line, the steps it requires (which run before it whenever it runs) and, when the profile turns isolation off for it
/// (ADR-0017), that the agents' code runs with the user's own rights. The review of the plan shows one such line per step,
/// and <see cref="PlanWarning(PlanDefinition, IEnumerable{AgentDefinition}, VerificationProfile, bool)"/> next to its approval.
/// </summary>
public static class VerificationStepText
{
    /// <summary>What a step without isolation adds to its line.</summary>
    public const string WithoutIsolation = "SIN AISLAMIENTO: el código que escriben los agentes se ejecuta con tus permisos en el worktree";

    /// <summary>What a step that requires others adds to its line, before their ids.</summary>
    public const string RequiresPrefix = "requiere ";

    private const string Opening = "Atención: este plan tiene escritores con integridad Medium.";

    /// <summary>What a writer at medium integrity means for the run (ADR-0018, decision 3).</summary>
    private const string Meaning = "Pueden escribir en cualquier sitio donde puedas escribir tú, y lo que hagan queda fuera del modelo de amenaza (ADR-0018). Por eso la comprobación de tu repositorio será estricta: una carpeta ignorada que no se pueda verificar bloquea la ejecución.";

    public static string Line(VerificationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var line = $"{step.Id} · {step.Title} · {string.Join(' ', [step.Executable, .. step.Arguments.Select(Quote)])}";
        if (step.Requires is { Count: > 0 } requires)
        {
            line += " · " + RequiresPrefix + Enumeration([.. requires]);
        }

        return step.Isolation == ProcessIsolation.LowIntegrity ? line : line + " · " + WithoutIsolation;
    }

    /// <summary>
    /// The warning the review of a plan shows next to «Aprobar plan», and the approval request of the plan carries, when
    /// the plan creates writers at medium integrity (ADR-0018, decision 3), read by <see cref="MediumIntegrityWriters.Find"/>
    /// exactly as the check of the user's repository reads them. It names each task that brings one, in the order of the
    /// plan, and why: an agent with Bash, an executor that writes with Codex or with a CLI or a version whose confinement
    /// was not measured, tools beyond the measured profile, an agent not in the roster, a step the plan runs without
    /// isolation or one the profile does not have. Then it says what that means for the run. Null when there is none.
    /// </summary>
    /// <param name="claudeWritesConfined">What the confinement probe says of the installed Claude Code (<see cref="Application.Providers.IWriteConfinementProbe"/>); false when nobody could say.</param>
    public static string? PlanWarning(PlanDefinition plan, IEnumerable<AgentDefinition> roster, VerificationProfile profile, bool claudeWritesConfined) =>
        PlanWarning(MediumIntegrityWriters.Find(plan, roster, profile, claudeWritesConfined));

    /// <summary>The warning that names <paramref name="writers"/> (<see cref="MediumIntegrityWriters.Find"/>), task by task; null when there is none.</summary>
    public static string? PlanWarning(IReadOnlyList<MediumIntegrityWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(writers);
        if (writers.Count == 0)
        {
            return null;
        }

        var tasks = writers
            .GroupBy(w => w.Task)
            .Select(task => $"{task.Key}: {Enumeration([.. task.Select(Reason).Distinct(StringComparer.Ordinal)])}.");
        return string.Join(' ', [Opening, .. tasks, Meaning]);
    }

    /// <summary>
    /// The line of a step as pieces for the screen (docs/17 D-47): what is not language (<see cref="StepLineParts.Plain"/>: id, title and
    /// exact command line) and, when they apply, the coded texts of the steps it requires and of its lack of isolation. The desktop
    /// joins them with « · » in the language of the person. <see cref="Line"/> stays as it is, for what is not the screen.
    /// </summary>
    public static StepLineParts LineParts(VerificationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var plain = $"{step.Id} · {step.Title} · {string.Join(' ', [step.Executable, .. step.Arguments.Select(Quote)])}";
        var requires = step.Requires is { Count: > 0 } ids ? UserText.Of(OrchestrationUserTexts.StepRequires, OrchestrationUserTexts.List([.. ids])) : null;
        var withoutIsolation = step.Isolation == ProcessIsolation.LowIntegrity ? null : UserText.Of(OrchestrationUserTexts.StepWithoutIsolation);
        return new StepLineParts(plain, requires, withoutIsolation);
    }

    /// <summary>
    /// The warning of <see cref="PlanWarning(IReadOnlyList{MediumIntegrityWriter})"/> as coded sentences for the screen, in the same
    /// order (the opening, one per task and what it means). The desktop paints them in the language of the person and joins them with
    /// a space; <see cref="PlanWarning(IReadOnlyList{MediumIntegrityWriter})"/> stays as it is, because the approval of the plan
    /// carries it and the run compares it byte by byte. Empty when there is no writer.
    /// </summary>
    public static IReadOnlyList<UserText> PlanWarningTexts(IReadOnlyList<MediumIntegrityWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(writers);
        if (writers.Count == 0)
        {
            return [];
        }

        var tasks = writers
            .GroupBy(w => w.Task)
            .Select(task => UserText.Of(
                OrchestrationUserTexts.MediumTask,
                task.Key.Value,
                OrchestrationUserTexts.List([.. task.Select(ReasonText).Distinct()])));
        return [UserText.Of(OrchestrationUserTexts.MediumOpening), .. tasks, UserText.Of(OrchestrationUserTexts.MediumMeaning)];
    }

    private static UserText ReasonText(MediumIntegrityWriter writer) => writer.Reason switch
    {
        MediumIntegrityReason.UnknownAgent => writer.Agent is { } agent
            ? UserText.Of(OrchestrationUserTexts.MediumReasonUnknownAgent, agent.ToString())
            : UserText.Of(OrchestrationUserTexts.MediumReasonNoAgent),
        MediumIntegrityReason.Shell => UserText.Of(OrchestrationUserTexts.MediumReasonShell, Name(writer.Agent)),
        MediumIntegrityReason.CodexWrites => UserText.Of(OrchestrationUserTexts.MediumReasonCodex, Name(writer.Agent)),
        MediumIntegrityReason.UnmeasuredCli => UserText.Of(OrchestrationUserTexts.MediumReasonUnmeasuredCli, Name(writer.Agent), writer.Provider?.ToString() ?? string.Empty),
        MediumIntegrityReason.UnmeasuredClaudeVersion => UserText.Of(OrchestrationUserTexts.MediumReasonUnmeasuredClaude, Name(writer.Agent)),
        MediumIntegrityReason.ToolsBeyondMeasuredProfile => writer.Tools.Count > 0
            ? UserText.Of(OrchestrationUserTexts.MediumReasonToolsBeyond, Name(writer.Agent), string.Join(", ", writer.Tools))
            : UserText.Of(OrchestrationUserTexts.MediumReasonPermissionsBeyond, Name(writer.Agent)),
        MediumIntegrityReason.UnknownStep => UserText.Of(OrchestrationUserTexts.MediumReasonUnknownStep, writer.Step ?? string.Empty),
        _ => UserText.Of(OrchestrationUserTexts.MediumReasonStepWithoutIsolation, writer.Step ?? string.Empty),
    };

    private static string Name(AgentId? agent) => agent?.ToString() ?? string.Empty;

    private static string Reason(MediumIntegrityWriter writer) => writer.Reason switch
    {
        MediumIntegrityReason.UnknownAgent => writer.Agent is { } agent ? $"el agente «{agent}» no está en la plantilla" : "no tiene agente asignado",
        MediumIntegrityReason.Shell => $"el agente «{writer.Agent}» tiene Bash",
        MediumIntegrityReason.CodexWrites => $"el agente «{writer.Agent}» escribe con Codex, cuyo sandbox siempre tiene shell",
        MediumIntegrityReason.UnmeasuredCli => $"el agente «{writer.Agent}» escribe con {writer.Provider}, cuyo confinamiento no se ha medido",
        MediumIntegrityReason.UnmeasuredClaudeVersion => $"el agente «{writer.Agent}» escribe con una versión de Claude Code cuyo confinamiento no se ha medido",
        MediumIntegrityReason.ToolsBeyondMeasuredProfile => writer.Tools.Count > 0
            ? $"el agente «{writer.Agent}» tiene herramientas fuera del perfil medido ({Enumeration(writer.Tools)})"
            : $"el agente «{writer.Agent}» tiene permisos fuera del perfil medido",
        MediumIntegrityReason.UnknownStep => $"el paso «{writer.Step}» no está en el perfil",
        _ => $"el paso «{writer.Step}» se ejecuta sin aislamiento",
    };

    /// <summary>«a», «a y b», «a, b y c».</summary>
    private static string Enumeration(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} y {items[^1]}";

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;
}

/// <summary>
/// A step of the verification profile as the screen paints it: <see cref="Plain"/> is id, title and command line (not language);
/// <see cref="Requires"/> and <see cref="WithoutIsolation"/> are coded texts, null when they do not apply.
/// </summary>
public sealed record StepLineParts(string Plain, UserText? Requires, UserText? WithoutIsolation);
