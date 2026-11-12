using AgentStudio.Application.Localization;

namespace AgentStudio.Orchestration;

/// <summary>
/// The texts of the orchestration the person reads and that are not anchored (docs/17 D-47, ADR-0054 addendum of 2026-11-12): a code
/// and its arguments, painted by the desktop in the language of the person (<c>Text_</c> + the code, in <c>OrchestratorStrings</c>).
/// What the engine anchors or sends (the first notice of a run, the approved warning, the summary of <c>RunFinished</c>, the
/// <c>Reason</c> of a budget verdict, the prompts) is not changed: it stays as it is (D-48), and a code is written next to it. The
/// messages of the exceptions are in English.
/// </summary>
public static class OrchestrationUserTexts
{
    // ---- an enumeration of names, as a piece of other texts ----
    /// <summary>«{0} y {1}»: the head of the list, joined with commas, and its last item.</summary>
    public const string ListAnd = "plan.list.and";

    /// <summary>«{0}, {1}»: the head of a list of pieces and the next one.</summary>
    public const string ListComma = "plan.list.comma";

    // ---- the delivery warnings of a plan (A) ----
    public const string DeliveryNotChecked = "plan.delivery.not-checked";
    public const string DeliveryNeverOne = "plan.delivery.never-one";
    public const string DeliveryNeverMany = "plan.delivery.never-many";
    public const string DeliveryMaybeOne = "plan.delivery.maybe-one";
    public const string DeliveryMaybeMany = "plan.delivery.maybe-many";
    public const string DeliveryReasonBranch = "plan.delivery.reason-branch";
    public const string DeliveryReasonTiming = "plan.delivery.reason-timing";
    public const string DeliveryBranchYes = "plan.delivery.branch-yes";
    public const string DeliveryBranchNo = "plan.delivery.branch-no";
    public const string DeliveryBranchOther = "plan.delivery.branch-other";
    public const string DeliveryHow = "plan.delivery.how";

    // ---- a step of the verification profile and the warning about writers at medium integrity (B) ----
    public const string StepRequires = "verification.step-requires";
    public const string StepWithoutIsolation = "verification.step-without-isolation";
    public const string MediumOpening = "verification.medium.opening";
    public const string MediumTask = "verification.medium.task";
    public const string MediumMeaning = "verification.medium.meaning";
    public const string MediumReasonUnknownAgent = "verification.medium.reason-unknown-agent";
    public const string MediumReasonNoAgent = "verification.medium.reason-no-agent";
    public const string MediumReasonShell = "verification.medium.reason-shell";
    public const string MediumReasonCodex = "verification.medium.reason-codex";
    public const string MediumReasonUnmeasuredCli = "verification.medium.reason-unmeasured-cli";
    public const string MediumReasonUnmeasuredClaude = "verification.medium.reason-unmeasured-claude";
    public const string MediumReasonToolsBeyond = "verification.medium.reason-tools-beyond";
    public const string MediumReasonPermissionsBeyond = "verification.medium.reason-permissions-beyond";
    public const string MediumReasonUnknownStep = "verification.medium.reason-unknown-step";
    public const string MediumReasonStepWithoutIsolation = "verification.medium.reason-step-without-isolation";

    /// <summary>Every code of this class: the desktop has a text for each of them in both languages (a test walks them).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ListAnd, ListComma,
        DeliveryNotChecked, DeliveryNeverOne, DeliveryNeverMany, DeliveryMaybeOne, DeliveryMaybeMany, DeliveryReasonBranch,
        DeliveryReasonTiming, DeliveryBranchYes, DeliveryBranchNo, DeliveryBranchOther, DeliveryHow,
        StepRequires, StepWithoutIsolation, MediumOpening, MediumTask, MediumMeaning, MediumReasonUnknownAgent, MediumReasonNoAgent,
        MediumReasonShell, MediumReasonCodex, MediumReasonUnmeasuredCli, MediumReasonUnmeasuredClaude, MediumReasonToolsBeyond,
        MediumReasonPermissionsBeyond, MediumReasonUnknownStep, MediumReasonStepWithoutIsolation,
    ];

    /// <summary>
    /// An enumeration as one argument: the item itself when there is one, and «a», «a y b», «a, b y c» as a piece (the desktop puts
    /// the «y» in its language) otherwise. Never empty.
    /// </summary>
    public static object List(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => UserText.Of(ListAnd, string.Join(", ", items.Take(items.Count - 1)), items[^1]),
        };
    }

    /// <summary>The same as <see cref="List(IReadOnlyList{string})"/> for pieces that are coded texts of their own.</summary>
    public static object List(IReadOnlyList<UserText> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => UserText.Of(ListAnd, items.Take(items.Count - 1).Skip(1).Aggregate(items[0], (head, next) => UserText.Of(ListComma, head, next)), items[^1]),
        };
    }
}
