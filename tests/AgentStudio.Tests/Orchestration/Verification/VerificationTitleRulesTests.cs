using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// The title of a step is written on one line of the header of the evidence: what ends the line or reorders it is refused, and the joiners that real titles carry (emoji made of
/// several, soft hyphens) are admitted because none of them can forge a header. The characters are given by code point so that no invisible character sits in this source.
/// </summary>
public sealed class VerificationTitleRulesTests
{
    private static VerificationStep Step(string title) =>
        new("build", title, "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(1));

    private static string Inside(int codePoint) => "Compilar" + char.ConvertFromUtf32(codePoint) + "falso";

    [Theory]
    [InlineData(0x200D)]
    [InlineData(0x200C)]
    [InlineData(0x00AD)]
    public void The_joiners_and_the_soft_hyphen_of_real_titles_are_admitted_and_never_break_the_line(int codePoint)
    {
        var title = Inside(codePoint);

        Assert.Empty(VerificationRules.Check(Step(title)));

        // The proof that they cannot forge a header: none of them is a character that ends a line or a paragraph.
        Assert.DoesNotContain(title, c => c is '\n' or '\r' or (char)0x0085 or (char)0x2028 or (char)0x2029);
    }

    [Fact]
    public void An_emoji_made_of_several_is_admitted()
    {
        var family = char.ConvertFromUtf32(0x1F468) + char.ConvertFromUtf32(0x200D) + char.ConvertFromUtf32(0x1F469) + char.ConvertFromUtf32(0x200D) + char.ConvertFromUtf32(0x1F467);

        Assert.Empty(VerificationRules.Check(Step("Familia " + family)));
    }

    [Theory]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    [InlineData(0x0009)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x200E)]
    [InlineData(0x200B)]
    [InlineData(0xFEFF)]
    [InlineData(0xE0041)]
    public void Controls_line_separators_marks_of_direction_and_the_other_hidden_characters_are_still_refused(int codePoint)
    {
        Assert.Contains(VerificationRules.Check(Step(Inside(codePoint))), p => p.Contains("control character", StringComparison.Ordinal));
    }

    [Fact]
    public void A_joiner_does_not_open_the_door_to_a_line_break_next_to_it()
    {
        var forged = "Compilar" + char.ConvertFromUtf32(0x200D) + "\n# falso";

        Assert.Contains(VerificationRules.Check(Step(forged)), p => p.Contains("control character", StringComparison.Ordinal));
    }
}
