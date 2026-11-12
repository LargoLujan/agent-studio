using AgentStudio.Application.Diagnostics;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Tests.Domain.Diagnostics;

/// <summary>
/// docs/20 D-01 (P0 of Phase 6): a check of Auto Doctor is a code, a section, a state, facts and a fix. It is valid by construction, compared by what
/// it holds, and «sin comprobar» weighs more than «correcto». The facts may come from a file of a repository, so they are data.
/// </summary>
public sealed class DoctorCheckTests
{
    private static DoctorCheck Check(string code = "doctor.dotnet.sdk", DoctorStatus status = DoctorStatus.Ok, params DoctorFact[] facts) =>
        new(code, DoctorSection.Tools, status, facts);

    // ---- codes ----

    [Theory]
    [InlineData("doctor.dotnet.sdk")]
    [InlineData("doctor.project.path-inside")]
    [InlineData("doc.link-broken")]
    [InlineData("graph.outside-root")]
    [InlineData("a")]
    [InlineData("a1.b2-c3")]
    public void A_code_is_lowercase_words_joined_by_dots_and_hyphens(string code)
    {
        Assert.True(DoctorCodes.IsCode(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Doctor.x")]
    [InlineData("1doctor")]
    [InlineData(".doctor")]
    [InlineData("doctor.")]
    [InlineData("doctor..x")]
    [InlineData("doctor.-x")]
    [InlineData("doctor_x")]
    [InlineData("doctor x")]
    [InlineData("doctor.x\n")]
    [InlineData("doctor.ñ")]
    public void Anything_else_is_not_a_code(string? code)
    {
        Assert.False(DoctorCodes.IsCode(code));
    }

    [Fact]
    public void A_code_longer_than_the_cap_is_not_one()
    {
        Assert.True(DoctorCodes.IsCode(new string('a', DoctorCodes.MaxLength)));
        Assert.False(DoctorCodes.IsCode(new string('a', DoctorCodes.MaxLength + 1)));
    }

    [Fact]
    public void A_check_has_a_doctor_code_a_real_section_and_a_real_state()
    {
        Assert.Throws<ArgumentException>(() => Check(code: "graph.outside-root"));
        Assert.Throws<ArgumentException>(() => Check(code: "Doctor.x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DoctorCheck("doctor.x", (DoctorSection)99, DoctorStatus.Ok, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DoctorCheck("doctor.x", DoctorSection.Tools, (DoctorStatus)99, []));
        Assert.Throws<ArgumentNullException>(() => new DoctorCheck("doctor.x", DoctorSection.Tools, DoctorStatus.Ok, null!));
        Assert.Throws<ArgumentException>(() => new DoctorCheck("doctor.x", DoctorSection.Tools, DoctorStatus.Ok, [], "Not a code"));
    }

    [Fact]
    public void A_check_holds_up_to_forty_facts_none_of_them_null()
    {
        var fact = new DoctorFact("version", "1.0");

        Assert.Equal(DoctorCheck.MaxFacts, Check(facts: [.. Enumerable.Repeat(fact, DoctorCheck.MaxFacts)]).Facts.Count);
        Assert.Throws<ArgumentException>(() => Check(facts: [.. Enumerable.Repeat(fact, DoctorCheck.MaxFacts + 1)]));
        Assert.Throws<ArgumentException>(() => new DoctorCheck("doctor.x", DoctorSection.Tools, DoctorStatus.Ok, [null!]));
    }

    [Fact]
    public void The_facts_are_copied_so_the_list_of_the_caller_cannot_change_the_check()
    {
        var facts = new List<DoctorFact> { new("version", "10.0.303") };
        var check = new DoctorCheck("doctor.dotnet.sdk", DoctorSection.Tools, DoctorStatus.Ok, facts);

        facts.Add(new DoctorFact("path", "C:/tools"));

        Assert.Single(check.Facts);
        Assert.Equal("10.0.303", check.Fact(DoctorFactNames.Version));
        Assert.Null(check.Fact(DoctorFactNames.Path));
    }

    // ---- equality ----

    [Fact]
    public void Two_checks_are_equal_when_their_parts_are_facts_in_order()
    {
        var a = new DoctorFact("version", "1");
        var b = new DoctorFact("path", "x");

        Assert.Equal(Check(facts: [a, b]), Check(facts: [new DoctorFact("version", "1"), new DoctorFact("path", "x")]));
        Assert.NotEqual(Check(facts: [a, b]), Check(facts: [b, a]));
        Assert.NotEqual(Check(facts: [a]), Check(facts: [a, b]));
        Assert.NotEqual(Check(status: DoctorStatus.Ok), Check(status: DoctorStatus.Warning));
        Assert.NotEqual(Check(), new DoctorCheck("doctor.dotnet.sdk", DoctorSection.Tools, DoctorStatus.Ok, [], "doctor.fix.install"));
        Assert.Equal(Check(facts: [a]).GetHashCode(), Check(facts: [a]).GetHashCode());
    }

    // ---- the facts ----

    [Fact]
    public void A_fact_is_a_name_and_a_one_line_value_of_up_to_two_hundred_characters()
    {
        Assert.Equal("10.0.303", new DoctorFact("version", "10.0.303").Value);
        Assert.Equal(DoctorFact.MaxValueLength, new DoctorFact("path", new string('x', DoctorFact.MaxValueLength)).Value.Length);
        Assert.Throws<ArgumentException>(() => new DoctorFact("path", new string('x', DoctorFact.MaxValueLength + 1)));
        Assert.Throws<ArgumentException>(() => new DoctorFact("path", string.Empty));
        Assert.Throws<ArgumentException>(() => new DoctorFact("path", "a\nb"));
        Assert.Throws<ArgumentException>(() => new DoctorFact("path", "a\tb"));
        Assert.Throws<ArgumentException>(() => new DoctorFact("Path", "x"));
        Assert.Throws<ArgumentException>(() => new DoctorFact("path name", "x"));
        Assert.Throws<ArgumentException>(() => new DoctorFact(new string('a', DoctorFact.MaxNameLength + 1), "x"));
    }

    [Fact]
    public void A_value_read_from_a_file_is_cleaned_and_cut_never_refused_for_being_long()
    {
        Assert.Equal("a b c", DoctorFact.Of("declared", "a\nb\tc").Value);
        Assert.Equal("a", DoctorFact.Of("declared", "  a \r\n").Value);

        var long200 = DoctorFact.Of("declared", new string('x', 500)).Value;
        Assert.Equal(DoctorFact.MaxValueLength, long200.Length);
        Assert.EndsWith("…", long200, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => DoctorFact.Of("declared", "\n\t "));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 198 characters, then a pair of two UTF-16 units that would be cut in half at 199.
        var text = new string('x', DoctorFact.MaxValueLength - 2) + "😀" + "yyyy";

        var value = DoctorFact.Of("declared", text).Value;

        Assert.True(value.Length <= DoctorFact.MaxValueLength);
        Assert.False(char.IsHighSurrogate(value[^2]));
        Assert.EndsWith("…", value, StringComparison.Ordinal);
    }

    // ---- weights ----

    [Fact]
    public void Not_checked_weighs_more_than_correct_and_less_than_a_warning()
    {
        var order = new[] { DoctorStatus.NotApplicable, DoctorStatus.Ok, DoctorStatus.Unknown, DoctorStatus.Warning, DoctorStatus.Error };

        Assert.Equal(order, Enum.GetValues<DoctorStatus>().OrderBy(DoctorStatusRules.Severity));
        Assert.Equal(DoctorStatus.Unknown, DoctorStatusRules.Worst([DoctorStatus.Ok, DoctorStatus.Unknown, DoctorStatus.Ok]));
        Assert.Equal(DoctorStatus.Error, DoctorStatusRules.Worst([DoctorStatus.Warning, DoctorStatus.Error, DoctorStatus.Unknown]));
        Assert.Equal(DoctorStatus.Ok, DoctorStatusRules.Worst([DoctorStatus.Ok, DoctorStatus.NotApplicable]));
        Assert.Equal(DoctorStatus.NotApplicable, DoctorStatusRules.Worst([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DoctorStatusRules.Severity((DoctorStatus)99));
    }

    [Fact]
    public void The_states_and_the_sections_keep_the_order_of_the_design()
    {
        Assert.Equal(["Ok", "Warning", "Error", "Unknown", "NotApplicable"], Enum.GetNames<DoctorStatus>());
        Assert.Equal(["Providers", "Tools", "Project", "DeclaredEnvironment", "Containers"], Enum.GetNames<DoctorSection>());
    }

    // ---- the report ----

    [Fact]
    public void A_report_holds_each_check_once_and_gives_the_worst_state_of_each_section()
    {
        var report = new DoctorReport(
        [
            new DoctorCheck("doctor.dotnet.sdk", DoctorSection.Tools, DoctorStatus.Ok, []),
            new DoctorCheck("doctor.git.version", DoctorSection.Tools, DoctorStatus.Unknown, []),
            new DoctorCheck("doctor.project.hooks", DoctorSection.Project, DoctorStatus.Warning, []),
        ]);

        Assert.Equal(DoctorStatus.Unknown, report.WorstOf(DoctorSection.Tools));
        Assert.Equal(DoctorStatus.Warning, report.WorstOf(DoctorSection.Project));
        Assert.Equal(DoctorStatus.NotApplicable, report.WorstOf(DoctorSection.Containers));
        Assert.Equal(DoctorStatus.Warning, report.Worst);
        Assert.Throws<ArgumentException>(() => new DoctorReport([Check(), Check()]));
        Assert.Throws<ArgumentException>(() => new DoctorReport([null!]));
        Assert.Equal(new DoctorReport([Check()]), new DoctorReport([Check()]));
    }
}
