using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// The names of what a debate shows (docs/15 D-07, D-13, D-20 and D-41): modes and the roles each one needs, roles, phases,
/// outcomes, kinds of contribution, limits, why it went to judgment, dispositions of the judgment with their explanation,
/// subjects, what it waits for and the rule that closed it. Every text comes from <c>Resources/DebateStrings.resx</c> in the
/// language in use (docs/17 D-47, ADR-0054); the product names (Anthropic, Claude Code…) are names, never translated. Other
/// screens use these names too, so the signatures do not change.
/// </summary>
public static class DebateTexts
{
    /// <summary>The text of <c>prefix + value</c>; a value the resources do not know (a new one) is shown as its name, never as <c>[key]</c>.</summary>
    private static string Named(string prefix, Enum value, string? fallback = null) =>
        Texts.Current.Has(prefix + value) ? Texts.Get(prefix + value) : fallback ?? value.ToString();

    /// <summary>
    /// Why a debate refused or failed, for the person: a refusal with a code (<see cref="UserTextException"/>) is painted from the
    /// resources in the language in use; any other failure is a technical one, shown as such with its message (in English).
    /// </summary>
    public static string Reason(Exception exception, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        texts ??= Texts.Current;
        return exception is UserTextException coded ? texts.Render(coded.Text) : texts.Format("Debate_Error_Unexpected", exception.Message);
    }

    /// <summary>
    /// A list of problems or notes of a debate, in the language in use: painted from its coded texts when they go one for one with
    /// the sentences, and as the sentences themselves (the layer's own) when a producer gave no codes.
    /// </summary>
    public static IReadOnlyList<string> Painted(IReadOnlyList<string> messages, IReadOnlyList<UserText>? texts, IUiTextProvider? provider = null)
    {
        provider ??= Texts.Current;
        return CodedList.Paint(messages, texts, provider.Render);
    }

    /// <summary>What a debate waits for, in the language in use: its coded texts when it gives them, or its own sentence.</summary>
    public static string Awaiting(DebateAwaiting waiting, IUiTextProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        provider ??= Texts.Current;
        return waiting.Texts is { Count: > 0 } texts ? string.Join(" ", texts.Select(provider.Render)) : waiting.Message;
    }

    public static string Mode(DebateMode mode) => Named("Debate_Mode_", mode);

    /// <summary>The participants each mode needs and what it is about (D-07).</summary>
    public static string ModeHelp(DebateMode mode) => Named("Debate_ModeHelp_", mode, string.Empty);

    public static string Role(DebateRole role) => Named("Debate_Role_", role);

    public static string Phase(DebatePhase phase) => Named("Debate_Phase_", phase);

    public static string Outcome(DebateOutcome outcome) => Named("Debate_Outcome_", outcome);

    /// <summary>The tone of a debate's state dot: running is active, waiting for a person is a warning, an abort is neutral.</summary>
    public static Tone ToneOf(DebatePhase phase, DebateOutcome outcome, bool interrupted = false) => (phase, outcome) switch
    {
        _ when interrupted => Tone.Warning,
        (_, DebateOutcome.Resolved) => Tone.Success,
        (_, DebateOutcome.Unresolved or DebateOutcome.EscalatedToUser) => Tone.Warning,
        (_, DebateOutcome.Aborted) => Tone.Neutral,
        (DebatePhase.Closed, _) => Tone.Neutral,
        _ => Tone.Active,
    };

    public static string Kind(ContributionKind kind) => Named("Debate_Kind_", kind);

    /// <summary>The limit that cut the debate; null when none did.</summary>
    public static string? Limit(DebateLimitReached limit) =>
        Texts.Current.Has("Debate_Limit_" + limit) ? Texts.Get("Debate_Limit_" + limit) : null;

    /// <summary>Why the debate went to judgment, as the end of «Pasa a juicio porque …».</summary>
    public static string Judgment(DebateJudgmentReason reason, DebateLimitReached limit = DebateLimitReached.None) => reason switch
    {
        DebateJudgmentReason.Limit => Limit(limit) is { } cut ? Texts.Format("Debate_Judgment_LimitOf", cut) : Texts.Get("Debate_Judgment_LimitAny"),
        _ => Named("Debate_Judgment_", reason),
    };

    public static string Subject(DebateSubjectKind subject, string? reference = null) => subject switch
    {
        DebateSubjectKind.None => Texts.Get("Debate_Subject_None"),
        DebateSubjectKind.Commit => reference is { Length: > 0 } commit ? Texts.Format("Debate_Subject_CommitOf", Short(commit)) : Texts.Get("Debate_Subject_Commit"),
        DebateSubjectKind.Run => reference is { Length: > 0 } run ? Texts.Format("Debate_Subject_RunOf", run) : Texts.Get("Debate_Subject_Run"),
        DebateSubjectKind.Verdict => reference is { Length: > 0 } verdict ? Texts.Format("Debate_Subject_VerdictOf", verdict) : Texts.Get("Debate_Subject_Verdict"),
        _ => subject.ToString(),
    };

    public static string Judging(DebateJudgingKind kind) => Named("Debate_Judging_", kind);

    /// <summary>What each disposition means (D-13), in the words the form explains it with.</summary>
    public static string JudgingHelp(DebateJudgingKind kind) => Named("Debate_JudgingHelp_", kind, string.Empty);

    /// <summary>The waiver a disposition carries, said plainly; null when there is none (D-12: rule P is never waived).</summary>
    public static string? Waiver(IReadOnlyCollection<VendorId> waived) => waived.Count == 0
        ? null
        : Texts.Format("Debate_Waiver", string.Join(Texts.Get("Debate_Join_And"), waived.Select(Vendor).Order(StringComparer.Ordinal)));

    public static string Vendor(VendorId vendor) => vendor.Value switch
    {
        "anthropic" => "Anthropic",
        "openai" => "OpenAI",
        "google" => "Google",
        var other => other,
    };

    public static string Provider(ProviderId provider) => provider.Value switch
    {
        "claude-code" => "Claude Code",
        "codex-cli" => "Codex CLI",
        "gemini-cli" => "Gemini CLI",
        var other => other,
    };

    public static string Await(DebateAwaitReason reason) => Named("Debate_Await_", reason);

    /// <summary>The rule that closed a debate (<see cref="JudgingPanel"/> and <c>DebateClosedPayload.AbortedRule</c>).</summary>
    public static string Rule(string? rule) => rule switch
    {
        JudgingPanel.Single => Texts.Get("Debate_Rule_Single"),
        JudgingPanel.Agree => Texts.Get("Debate_Rule_Agree"),
        JudgingPanel.Disagree => Texts.Get("Debate_Rule_Disagree"),
        JudgingPanel.Invalid => Texts.Get("Debate_Rule_Invalid"),
        JudgingPanel.User => Texts.Get("Debate_Rule_User"),
        DebateClosedPayload.AbortedRule => Texts.Get("Debate_Rule_Aborted"),
        null or "" => Texts.Get("Debate_Rule_None"),
        var other => other,
    };

    public static string Turn(AttemptOutcome outcome) => Named("Debate_Turn_", outcome);

    /// <summary>«1 turno», «3 turnos»: a count with the nouns the caller gives (for the screens that have them in code).</summary>
    public static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    /// <summary>
    /// «1 turno», «3 turnos» from the resources: the texts <c>&lt;key&gt;_One</c> and <c>&lt;key&gt;_Other</c>, each with the
    /// number as <c>{0}</c>, so every language can write its own plural.
    /// </summary>
    public static string Plural(int count, string key) => Texts.Format(key + (count == 1 ? "_One" : "_Other"), count);

    public static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
