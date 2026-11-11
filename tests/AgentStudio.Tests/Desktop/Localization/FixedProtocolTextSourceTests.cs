using System.IO;
using System.Text.RegularExpressions;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>
/// What goes to the agents, to the decision log or to a saved test is fixed Spanish whatever the language of the interface (docs/17
/// D-48, F14-4). Changing <c>CurrentUICulture</c> in a test proves nothing, because <c>Texts.Current</c> is pinned in Spanish for the
/// whole run; so this reads the source: none of those texts may reach the resources, the text provider or a <c>*Strings</c> class.
/// </summary>
public sealed partial class FixedProtocolTextSourceTests
{
    [GeneratedRegex(@"\bTexts\.|ResourceTextProvider|IUiTextProvider|\b[A-Za-z]+Strings\b|ResourceManager|CultureInfo|nameof\(\w*Strings")]
    private static partial Regex ReadsTheResources();

    /// <summary>The files that are fixed text from the first line to the last (null members), and the fixed members of the mixed ones.</summary>
    public static TheoryData<string, string?> Sources => new()
    {
        { "Debate/ProtocolTexts.cs", null },
        { "Orchestrator/OrchestratorProtocolTexts.cs", null },
        { "Lock/HelloAnswers.cs", "WhyNotConfirmedForLog" },
        { "Lock/HelloAnswers.cs", "ApprovalNotConfirmed" },
        { "Lock/HelloAnswers.cs", "ApprovalNotAsked" },
        { "Background/RunExitRecorder.cs", "Summary" },
        { "Skills/SkillsDocumentViewModel.cs", "AssistedAction" },
        { "Skills/SkillsDocumentViewModel.cs", "AssistedDescription" },
        { "Skills/SkillsDocumentViewModel.cs", "StackContextPrefix" },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void The_fixed_texts_of_the_protocol_never_read_the_resources(string file, string? member)
    {
        var code = CodeOf(File.ReadAllText(Path.Combine(DesktopRoot, file.Replace('/', Path.DirectorySeparatorChar))), member);

        Assert.True(code.Length > 40, $"{file} {member}: nothing was read, so nothing was checked.");
        Assert.DoesNotMatch(ReadsTheResources(), code);
    }

    [Fact]
    public void The_scan_does_catch_a_text_that_reads_the_resources()
    {
        const string Source = "public static class T\n{\n    public static string A => \"fixed\";\n\n    public static string B => Texts.Get(\"Key\");\n}\n";

        Assert.DoesNotMatch(ReadsTheResources(), CodeOf(Source, "A"));
        Assert.Matches(ReadsTheResources(), CodeOf(Source, "B"));
        Assert.Matches(ReadsTheResources(), CodeOf(Source, null));
    }

    /// <summary>
    /// The code of the whole <paramref name="source"/>, or of one member of it: from its declaration up to the next member (the next
    /// line at the indent of a member that starts a doc comment, an attribute or a declaration). Comments are left out.
    /// </summary>
    private static string CodeOf(string source, string? member)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        IEnumerable<string> selected = lines;
        if (member is not null)
        {
            var start = Array.FindIndex(lines, l => l.StartsWith("    ", StringComparison.Ordinal) && !l.StartsWith("     ", StringComparison.Ordinal)
                && Regex.IsMatch(l, @"\b" + Regex.Escape(member) + @"\b\s*(=|\(|\{|$)"));
            Assert.True(start >= 0, $"{member} is not declared.");
            var end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, @"^    (///|\[|public|private|internal|protected)"));
            selected = lines.Skip(start).Take((end < 0 ? lines.Length : end) - start);
        }

        return string.Join('\n', selected.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }
}
