using AgentStudio.Domain.Orchestration.Analysis.Rules;

namespace AgentStudio.Domain.Orchestration.Analysis;

/// <summary>
/// The analyzer of the assembly (docs/17 D-18, WP1): it reads an immutable copy of the draft and joins the findings of every
/// rule, in a stable order: the validator wrapped first, then the rules in the order of the table of D-18, each in its own
/// stable order (the order of the plan, then of the team). Deterministic, pure and free: it lives in the domain, which
/// references nothing, so it can never launch an agent nor spend quota; it reads no file and changes nothing.
/// </summary>
/// <remarks>
/// It never replaces anyone (D-21): the validator decides whether a plan may be approved, the auditor reviews what happened
/// and the judge the work. It only says beforehand, while the person assembles, what can already be known.
/// </remarks>
public static class MontageAnalyzer
{
    /// <summary>The rules, in the order their findings come out: the validator, then the table of D-18.</summary>
    public static IReadOnlyList<IMontageRule> Rules { get; } =
    [
        new ValidatorRule(),
        new NoIntegrationRule(),
        new TestWithoutBuildRule(),
        new JudgeSameVendorRule(),
        new ParallelWriteOverlapRule(),
        new WriteOutsideAreaRule(),
        new ParallelCapacityRule(),
        new PackTooSmallRule(),
        new WindowTightRule(),
        new ContextThinRule(),
        new ModelOversizedRule(),
        new ModelUndersizedRule(),
        new CostOverBudgetRule(),
        new QuotaWindowNearRule(),
        new OrderNoteRule(),
        new HighRiskCapabilityRule(),
    ];

    /// <summary>The findings of every rule on <paramref name="draft"/>, with the generation it was copied at.</summary>
    public static MontageAnalysis Analyze(MontageDraft draft) => Analyze(draft, Rules);

    /// <summary>The findings of <paramref name="rules"/> on <paramref name="draft"/>, in their order.</summary>
    public static MontageAnalysis Analyze(MontageDraft draft, IReadOnlyList<IMontageRule> rules)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(rules);
        var findings = new List<Finding>();
        var state = new MontageEditState(draft.Plan, draft.Context.Roster ?? [], draft.Run.MaxAgentRuns);
        var environment = new FixEnvironment(SimulatedTitle, draft.Steps);
        foreach (var rule in rules)
        {
            // A fix is offered only if it applies to the draft as it is (FixApplier, whose net refuses one that would leave the
            // plan unsound); the activations of skills go through their own store, and are offered as they are.
            findings.AddRange(rule.Check(draft).Select(f => f.Fix is { } fix && FixApplier.Apply(state, fix, environment) is { Applied: false, Refusal: not FixRefusals.ThroughSkillStore }
                ? f with { Fix = null }
                : f));
        }

        return new MontageAnalysis(draft.Generation, findings.AsReadOnly());
    }

    /// <summary>The title of an integration a fix adds, only to try the fix: the real one comes from the resources when it is applied.</summary>
    private const string SimulatedTitle = "-";
}
