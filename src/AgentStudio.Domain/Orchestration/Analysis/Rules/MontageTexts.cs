using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

/// <summary>
/// A sentence of the system as a code and its arguments (docs/17 D-47): what the desktop paints with the resource
/// <c>Text_&lt;code&gt;</c> (<c>UserText</c>), in the language of the person. Nothing here is a sentence.
/// </summary>
public sealed record CodedSentence(string Code, IReadOnlyList<object> Arguments);

/// <summary>
/// The sentences of the analyzer (WP1): which one paints each finding and the button of each fix. The resources live in
/// <c>Desktop/Resources/MontageStrings.resx</c> and its English satellite, one per code.
/// </summary>
public static class MontageTexts
{
    /// <summary>The sentence of a window of quota whose reset the CLI did not say: the same finding with three arguments.</summary>
    public const string QuotaWindowNearWithoutReset = "quota.window-near-no-reset";

    /// <summary>The codes of the buttons of the fixes, one per kind of fix and variant.</summary>
    public static IReadOnlyList<string> FixCodes { get; } =
    [
        "fix.add-integration", "fix.add-build-step", "fix.add-dependency", "fix.reassign-task", "fix.switch-judge", "fix.add-judge",
        "fix.widen-agent-folders", "fix.raise-context-budget", "fix.raise-run-budget", "fix.change-model", "fix.change-model-effort",
        "fix.memory-on", "fix.memory-off", "fix.remove-skills", "fix.remove-capability",
    ];

    /// <summary>
    /// The sentence that paints <paramref name="finding"/>: its code and its arguments, but for a window of quota without its
    /// reset, which has a sentence of its own (<see cref="QuotaWindowNearWithoutReset"/>).
    /// </summary>
    public static CodedSentence SentenceOf(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Code == FindingCodes.QuotaWindowNear && finding.Arguments.Count == 3
            ? new CodedSentence(QuotaWindowNearWithoutReset, finding.Arguments)
            : new CodedSentence(finding.Code, finding.Arguments);
    }

    /// <summary>The text of the button of <paramref name="fix"/>: «Añadir Integración al final», «T4 después de T2»…</summary>
    /// <exception cref="ArgumentException">A kind of fix this list does not know.</exception>
    public static CodedSentence ButtonOf(Fix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        return fix switch
        {
            AddIntegrationFix => Of("fix.add-integration"),
            AddBuildStepFix f => Of("fix.add-build-step", f.BuildStep, f.Task.Value),
            AddDependencyFix f => Of("fix.add-dependency", f.Task.Value, f.DependsOn.Value),
            ReassignTaskFix f => Of("fix.reassign-task", f.Task.Value, f.Agent.Value),
            SwitchJudgeFix f => Of("fix.switch-judge", f.ReviewTask.Value, f.Judge.Value),
            AddJudgeFix f => Of("fix.add-judge", f.Provider.Value, f.Vendor.Value),
            WidenAgentFoldersFix f => Of("fix.widen-agent-folders", f.Agent.Value, f.Folder),
            RaiseContextBudgetFix f => Of("fix.raise-context-budget", f.Agent.Value, f.Characters),
            RaiseRunBudgetFix f => Of("fix.raise-run-budget", f.MaxAgentRuns),
            ChangeModelFix { Effort: { } effort } f => Of("fix.change-model-effort", f.Agent.Value, f.Model, Models.ReasoningEfforts.Token(effort)),
            ChangeModelFix f => Of("fix.change-model", f.Agent.Value, f.Model),
            SetMemoryFix { Memory: MemoryPolicy.None } f => Of("fix.memory-off", f.Agent.Value),
            SetMemoryFix f => Of("fix.memory-on", f.Agent.Value),
            RemoveSkillsFix f => Of("fix.remove-skills", f.Agent.Value, f.Skills.Count),
            RemoveCapabilityFix f => Of("fix.remove-capability", f.Agent.Value, DraftFacts.ToolName(f.Tool)),
            _ => throw new ArgumentException($"Unknown fix {fix.GetType().Name}.", nameof(fix)),
        };
    }

    private static CodedSentence Of(string code, params object[] arguments) => new(code, TextArguments.Checked(arguments, nameof(arguments)));
}
