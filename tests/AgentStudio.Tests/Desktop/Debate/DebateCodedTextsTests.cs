using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Localization;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The coded texts of Debate (docs/17 D-47, ADR-0054 addendum of 2026-11-12): every code has its text in Spanish of Spain and in
/// English with the same placeholders, and what the person reads is painted from the resources in the language in use, never
/// from a sentence of the layers below.
/// </summary>
public sealed partial class DebateCodedTextsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static IReadOnlyList<string> Markers(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var code in DebateUserTexts.All.Concat(PendingProblems.TextCodes).Concat(AuditUserTexts.All))
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_code_has_its_text_in_both_languages_with_the_same_placeholders(string code)
    {
        var key = UserText.KeyOf(code);

        Assert.True(Spanish.Has(key), key + " (es)");
        Assert.True(English.Has(key), key + " (en)");
        Assert.Equal(Markers(Spanish.Get(key)), Markers(English.Get(key)));
        Assert.NotEqual(Spanish.Get(key), English.Get(key));
    }

    [Fact]
    public void The_codes_of_debate_and_of_the_audit_are_all_different_and_valid()
    {
        var all = DebateUserTexts.All.Concat(PendingProblems.TextCodes).Concat(AuditUserTexts.All).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, code => Assert.True(UserText.IsCode(code), code));
    }

    [Fact]
    public void The_lists_of_codes_hold_every_constant_of_their_class()
    {
        static IEnumerable<string> Constants(Type type, string prefix = "") => type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(Constants(typeof(DebateUserTexts)).Order(StringComparer.Ordinal), DebateUserTexts.All.Order(StringComparer.Ordinal));
        Assert.Equal(Constants(typeof(AuditUserTexts)).Order(StringComparer.Ordinal), AuditUserTexts.All.Order(StringComparer.Ordinal));
        Assert.Equal(Constants(typeof(PendingProblems), "Text").Order(StringComparer.Ordinal), PendingProblems.TextCodes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_audit_that_ends_without_a_judge_says_why_in_the_language_in_use()
    {
        var text = UserText.Of(AuditUserTexts.EscalationNotApproved, "user");

        Assert.Equal("No se aprobó la auditoría (user). No se ha ejecutado nada.", Spanish.Render(text));
        Assert.Equal("The audit was not approved (user). Nothing was run.", English.Render(text));
    }

    [Fact]
    public void A_refusal_with_a_code_is_painted_in_the_language_in_use_and_any_other_failure_is_shown_as_technical()
    {
        var refusal = new UserTextException(UserText.Of(DebateUserTexts.AlreadyClosed), "The debate is already closed.");

        Assert.Equal("El debate ya está cerrado.", DebateTexts.Reason(refusal, Spanish));
        Assert.Equal("The debate is already closed.", DebateTexts.Reason(refusal, English));
        Assert.Equal("Ha pasado algo inesperado (detalle técnico: boom)", DebateTexts.Reason(new InvalidOperationException("boom"), Spanish));
        Assert.Equal("Something unexpected happened (technical detail: boom)", DebateTexts.Reason(new InvalidOperationException("boom"), English));
    }

    [Fact]
    public void What_a_debate_waits_for_is_painted_from_its_coded_texts_and_keeps_its_sentence_when_it_has_none()
    {
        var at = new DateTimeOffset(2026, 11, 12, 9, 5, 0, TimeSpan.Zero);
        var failed = new DebateAwaiting(
            DebateAwaitReason.ProviderLimit,
            "P1: el límite de uso de claude-code se reinicia a las 09:05 UTC, después de lo que el debate espera solo",
            "P1",
            at,
            [UserText.Of(DebateUserTexts.TurnLimitResets, "P1", "Claude Code", at), UserText.Of(DebateUserTexts.TurnEndedBadly, "P2")]);

        Assert.Contains("P1: se alcanzó el límite de uso de tu plan en Claude Code. Se reinicia a las 09:05 (UTC)", DebateTexts.Awaiting(failed, Spanish), StringComparison.Ordinal);
        Assert.Contains("P2: el turno no terminó bien.", DebateTexts.Awaiting(failed, Spanish), StringComparison.Ordinal);
        Assert.Contains("P1: the usage limit of your plan was reached on Claude Code. It resets at 09:05 (UTC)", DebateTexts.Awaiting(failed, English), StringComparison.Ordinal);
        Assert.Contains("P2: the turn did not end well.", DebateTexts.Awaiting(failed, English), StringComparison.Ordinal);

        var plain = new DebateAwaiting(DebateAwaitReason.HumanResolution, "El juicio necesita a una persona.");
        Assert.Equal("El juicio necesita a una persona.", DebateTexts.Awaiting(plain, English));
    }

    [Fact]
    public void A_pending_problem_of_the_audit_is_painted_in_the_language_in_use()
    {
        var problem = new PendingProblem(PendingProblems.RunUnfinishedCode, "20261018-c4a1f2", "La ejecución 20261018-c4a1f2 no ha terminado: está interrumpida o espera a una persona.",
            UserText.Of(PendingProblems.TextRunUnfinished, "20261018-c4a1f2"));

        Assert.Equal("La ejecución 20261018-c4a1f2 no ha terminado. Está parada o espera a una persona. · 20261018-c4a1f2", AuditReportViewModel.PendingLine(problem, Spanish));
        Assert.Equal("Run 20261018-c4a1f2 has not finished. It stopped or is waiting for a person. · 20261018-c4a1f2", AuditReportViewModel.PendingLine(problem, English));
        Assert.Equal("Dicho. · x (run.failed)", AuditReportViewModel.PendingLine(new PendingProblem(PendingProblems.RunFailedCode, "x", "Dicho."), English));
    }
}
