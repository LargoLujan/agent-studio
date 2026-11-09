using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Orchestration.Analysis;

// Phase 13 (docs/17 D-18): what the analyzer of the assembly finds, while the person assembles. These are the types WP1
// (the rules and the analyzer) and WP6 (the canvas, the band and the undo) share. The analyzer never spends quota, never
// replaces the validator (which decides whether a plan may be approved) nor the auditor (D-21).

/// <summary>How serious a finding is (docs/17 D-18): errors keep the plan from being approved, as today; suggestions go folded.</summary>
public enum FindingSeverity
{
    Error,
    Warning,
    Suggestion,
}

/// <summary>
/// The codes of the analyzer that are not the validator's (docs/17 D-18). The codes of <see cref="PlanViolationCode"/> are
/// wrapped, never duplicated: a finding of the validator has the code <c>validator.</c> followed by the name of its
/// <see cref="PlanViolationCode"/> in lowercase words (<see cref="ForViolation"/>). The desktop says each code in one sentence
/// of its resources.
/// </summary>
public static class FindingCodes
{
    /// <summary>No Integration task: nothing would reach the person's branch (B10). Error; fix <see cref="AddIntegrationFix"/>.</summary>
    public const string NoIntegration = "delivery.no-integration";

    /// <summary>A test step without a build before it (B6). Error; fix <see cref="AddBuildStepFix"/>.</summary>
    public const string TestWithoutBuild = "verification.test-without-build";

    /// <summary>A judge of the vendor of the orchestrator or of an executor (<c>JudgeVendorNotIndependent</c>). Error; fix <see cref="SwitchJudgeFix"/>.</summary>
    public const string JudgeSameVendor = "team.judge-same-vendor";

    /// <summary>Two tasks that may run at once with scopes that overlap (<c>OverlappingWriteScopes</c>). Error; fix <see cref="AddDependencyFix"/>.</summary>
    public const string ParallelWriteOverlap = "plan.parallel-write-overlap";

    /// <summary>The scope of a task does not fit in the folders of its agent (D-11). Error; fixes <see cref="ReassignTaskFix"/> or <see cref="WidenAgentFoldersFix"/>.</summary>
    public const string WriteOutsideArea = "team.write-outside-area";

    /// <summary>More executors of a provider than can work at once. Warning; no fix (it informs).</summary>
    public const string ParallelCapacity = "team.parallel-capacity";

    /// <summary>The mandatory requirements of a task do not fit in its context budget: it would fail closed. Error; fixes <see cref="RaiseContextBudgetFix"/> or splitting the task.</summary>
    public const string PackTooSmall = "context.pack-too-small";

    /// <summary>What goes in before starting passes the warning threshold of the window of the model. Warning; fixes <see cref="ChangeModelFix"/>, <see cref="RemoveSkillsFix"/> or <see cref="SetMemoryFix"/>.</summary>
    public const string WindowTight = "context.window-tight";

    /// <summary>A task that writes with no memory, no brief and nothing from earlier tasks. Suggestion; fix <see cref="SetMemoryFix"/>.</summary>
    public const string ContextThin = "context.thin";

    /// <summary>Level «máximo» for tasks that only touch documentation or tests. Suggestion; fix <see cref="ReassignTaskFix"/>.</summary>
    public const string ModelOversized = "model.oversized";

    /// <summary>Level «ligero» with retries or rejections of the judge in this project. Suggestion; fix <see cref="ChangeModelFix"/>.</summary>
    public const string ModelUndersized = "model.undersized";

    /// <summary>The estimate of the plan passes the budget (<c>MaxAgentRuns</c>). Warning; fix <see cref="RaiseRunBudgetFix"/>.</summary>
    public const string CostOverBudget = "cost.over-budget";

    /// <summary>A window of quota over its warning. Warning; no fix (it informs: «puede esperar hasta las HH:MM»).</summary>
    public const string QuotaWindowNear = "quota.window-near";

    /// <summary>The plan does not keep an order note of the orchestrator (D-19). Warning; fix <see cref="AddDependencyFix"/>.</summary>
    public const string OrderNoteUnapplied = "order.note-unapplied";

    /// <summary>The plan allows an order the orchestrator said would be harmful (D-19). Warning; fix <see cref="AddDependencyFix"/>.</summary>
    public const string OrderHarmful = "order.harmful";

    /// <summary>An agent with a shell, the network or Codex writing (ADR-0018, point 3). Warning; fix <see cref="RemoveCapabilityFix"/>; if it stays, approving asks Windows Hello (D-37).</summary>
    public const string HighRiskCapability = "capability.high-risk";

    /// <summary>The prefix of the findings that wrap a rule of the validator.</summary>
    public const string PlanViolationPrefix = "validator.";

    /// <summary>
    /// The code of the finding that wraps a violation of the validator: <c>validator.</c> and the name of its code in lowercase
    /// words joined by <c>-</c> (<c>OverlappingWriteScopes</c> is <c>validator.overlapping-write-scopes</c>), the form of every
    /// coded text (<c>UserText</c>). The codes of D-18 already use <c>plan.</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not one of the validator.</exception>
    public static string ForViolation(PlanViolationCode code)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown code of the validator.");
        }

        var name = code.ToString();
        var words = new System.Text.StringBuilder(PlanViolationPrefix, PlanViolationPrefix.Length + name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                words.Append('-');
            }

            words.Append(char.ToLowerInvariant(name[i]));
        }

        return words.ToString();
    }

    /// <summary>Every code of the analyzer of its own (not the wrapped ones), in the order of D-18.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NoIntegration, TestWithoutBuild, JudgeSameVendor, ParallelWriteOverlap, WriteOutsideArea, ParallelCapacity, PackTooSmall,
        WindowTight, ContextThin, ModelOversized, ModelUndersized, CostOverBudget, QuotaWindowNear, OrderNoteUnapplied, OrderHarmful,
        HighRiskCapability,
    ];
}

/// <summary>
/// One finding of the analyzer (docs/17 D-18): its code, how serious it is, the nodes it is about (task ids and agent ids, as
/// they are), the arguments of its sentence and, when there is one, a typed fix of one click. Its text comes from the resources
/// of the desktop by its code; nothing here is a sentence.
/// </summary>
/// <param name="Code">One of <see cref="FindingCodes"/>, or a wrapped rule of the validator (<see cref="FindingCodes.ForViolation"/>).</param>
/// <param name="Severity">How serious.</param>
/// <param name="Nodes">The ids of the nodes it is about (tasks of the Plan layer, agents of the Team layer), shown next to each.</param>
/// <param name="Arguments">The arguments of its sentence (<see cref="TextArguments"/>).</param>
/// <param name="Fix">The fix of one click, or null when it only informs.</param>
public sealed record Finding(string Code, FindingSeverity Severity, IReadOnlyList<string> Nodes, IReadOnlyList<object> Arguments, Fix? Fix = null)
{
    /// <summary>Builds a finding with its arguments checked.</summary>
    /// <exception cref="ArgumentException">A blank code or node, an unknown severity, or an argument a coded text does not carry.</exception>
    public static Finding Of(string code, FindingSeverity severity, IEnumerable<string> nodes, Fix? fix = null, params object[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity.");
        }

        var ids = nodes?.ToList() ?? [];
        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A finding names its nodes by their ids; none is blank.", nameof(nodes));
        }

        return new Finding(code, severity, ids.AsReadOnly(), TextArguments.Checked(arguments, nameof(arguments)), fix);
    }

    /// <summary>Whether it keeps the plan from being approved: an error does, as today.</summary>
    public bool BlocksApproval => Severity == FindingSeverity.Error;
}

/// <summary>
/// The result of one analysis of the draft (docs/17 D-16 and D-18): its findings, in a stable order, and the generation of the
/// draft it was computed on. The analysis runs off the UI thread on an immutable copy, 200 ms after the last edit, and its
/// result is applied only if the generation is still the same.
/// </summary>
public sealed record MontageAnalysis(long Generation, IReadOnlyList<Finding> Findings)
{
    /// <summary>Whether any finding keeps the plan from being approved.</summary>
    public bool BlocksApproval => Findings.Any(f => f.BlocksApproval);
}
