using System.Globalization;
using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Localization;
using AgentStudio.Debate.Arena;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// What is left of the coded texts of Debate (docs/17 D-47, ADR-0054 addendum of 2026-11-12): the reason of a refusal on the
/// card of a verdict, the problems and the notes of the proposals and of the room, and the reasons of a refused close, all in
/// the language in use.
/// </summary>
public sealed class DebateRestosTextsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private static VerdictCardContext Context() => new(
        "Veredicto del debate deb-20261019-a1b2c3",
        DebateJudgingKind.CrossVendorPanel,
        [AgentStudio.Domain.Providers.VendorId.Anthropic, AgentStudio.Domain.Providers.VendorId.OpenAI],
        Participants,
        Requirements,
        true,
        Proposals: ["P1", "P2"]);

    private static UserTextException Refusal(string code, string message = "The decision was not approved.") => new(UserText.Of(code), message);

    [Fact]
    public async Task Deciding_for_yourself_without_approval_says_why_in_the_language_in_use()
    {
        foreach (var (provider, expected) in new[] { (Spanish, "No se pudo registrar tu decisión: "), (English, "Your decision could not be recorded: ") })
        {
            using var scope = Texts.Scope(provider);
            var card = new VerdictCardViewModel(
                PanelThatDisagrees,
                Disagree,
                Context(),
                Zone,
                new VerdictCardActions(Decide: (_, _, _) => throw Refusal(DebateUserTexts.DecisionNotApproved)));
            card.SelectedDecision = card.DecisionOptions.Single(o => o.Value == VerdictDecision.Approved);
            card.Comment = "Me quedo con P2.";

            await card.DecideCommand.ExecuteAsync(null);

            Assert.Equal(expected + provider.Render(UserText.Of(DebateUserTexts.DecisionNotApproved)), card.ErrorMessage);
        }
    }

    [Fact]
    public async Task A_challenge_that_cannot_be_opened_says_why_in_the_language_in_use()
    {
        foreach (var provider in new[] { Spanish, English })
        {
            using var scope = Texts.Scope(provider);
            var card = new VerdictCardViewModel(
                PanelThatDisagrees,
                Disagree,
                Context(),
                Zone,
                new VerdictCardActions(Challenge: _ => throw Refusal(DebateUserTexts.ChallengeCannotOpen, "The challenge cannot be opened: x")));

            await card.Verdicts[0].ChallengeCommand.ExecuteAsync(null);

            Assert.Contains(provider.Render(UserText.Of(DebateUserTexts.ChallengeCannotOpen)), card.Verdicts[0].ChallengeError, StringComparison.Ordinal);
            Assert.DoesNotContain("The challenge cannot be opened: x", card.Verdicts[0].ChallengeError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_failure_without_a_code_is_shown_as_technical_detail_on_the_card()
    {
        using var scope = Texts.Scope(English);
        var card = new VerdictCardViewModel(
            PanelThatDisagrees,
            Disagree,
            Context(),
            Zone,
            new VerdictCardActions(Decide: (_, _, _) => throw new InvalidOperationException("boom")));
        card.SelectedDecision = card.DecisionOptions.Single(o => o.Value == VerdictDecision.Approved);
        card.Comment = "ok";

        await card.DecideCommand.ExecuteAsync(null);

        Assert.Equal("Your decision could not be recorded: Something unexpected happened (technical detail: boom)", card.ErrorMessage);
    }

    [Fact]
    public void The_problems_and_notes_are_painted_from_their_codes_and_keep_their_sentences_without_them()
    {
        IReadOnlyList<string> sentences = ["El participante 'x' no está en la plantilla del proyecto."];
        IReadOnlyList<UserText> codes = [UserText.Of(DebateUserTexts.ProblemParticipantMissing, "x")];

        Assert.Equal(["El participante «x» no está en el equipo del proyecto."], DebateTexts.Painted(sentences, codes, Spanish));
        Assert.Equal(["Participant \"x\" is not in the project team."], DebateTexts.Painted(sentences, codes, English));
        Assert.Equal(sentences, DebateTexts.Painted(sentences, null, English));
        Assert.Equal(sentences, DebateTexts.Painted(sentences, [], English));
    }

    [Fact]
    public void The_notes_of_the_estimate_say_the_numbers_in_the_language_in_use()
    {
        var limit = UserText.Of(DebateUserTexts.NoteTokenLimit, 1_500_000L);

        Assert.Equal("El límite de 1.500.000 tokens corta el debate antes: se va a juicio al alcanzarlo.", Spanish.Render(limit));
        Assert.Equal("The limit of 1,500,000 tokens cuts the debate short: it goes to judgment when it is reached.", English.Render(limit));
    }

    [Fact]
    public void A_provider_is_named_by_the_program_and_never_by_its_id_in_the_texts_of_a_turn()
    {
        var text = UserText.Of(DebateUserTexts.TurnLimitUnknown, "P1", ProviderNames.Of(AgentStudio.Domain.Providers.ProviderId.ClaudeCode));

        Assert.Contains("Claude Code", Spanish.Render(text), StringComparison.Ordinal);
        Assert.DoesNotContain("claude-code", Spanish.Render(text), StringComparison.Ordinal);
        Assert.DoesNotContain("claude-code", English.Render(text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("milestone.closing")]
    [InlineData("log.not-intact")]
    [InlineData("milestone.audit-not-run")]
    [InlineData("milestone.requirements")]
    [InlineData("milestone.not-found")]
    [InlineData("milestone.problems")]
    [InlineData("milestone.not-open")]
    [InlineData("milestone.nothing-to-close")]
    [InlineData("milestone.no-judge")]
    public void Every_rule_a_milestone_close_refuses_with_has_its_text_in_both_languages(string rule)
    {
        var key = "Work_Refused_" + rule.Replace('.', '_').Replace('-', '_');

        Assert.True(Spanish.Has(key), key + " (es)");
        Assert.True(English.Has(key), key + " (en)");
        Assert.NotEqual(Spanish.Get(key), English.Get(key));
        using var scope = Texts.Scope(English);
        Assert.Equal(English.Get(key), WorkTexts.Refusal(rule));
    }

    [Fact]
    public void The_close_of_a_milestone_says_in_its_source_none_of_the_sentences_it_refuses_with_to_the_screen()
    {
        // The screen paints the rule (WorkTexts.Refusal), never the Spanish reasons the service keeps for logs and the report.
        var source = File.ReadAllText(Path.Combine(Root(), "src", "AgentStudio.Desktop", "Work", "MilestonesSection.cs"));

        Assert.DoesNotContain("refused.Reasons", source, StringComparison.Ordinal);
        Assert.DoesNotContain("refused.Message", source, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentStudio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The solution root was not found.");
    }
}
