using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/20 D-13 to D-15 and D-23 to D-26 (P0 of Phase 6, ADR-0045 and ADR-0063): the contracts of the diagrams and of the doctor of skills as values with
/// their invariants. The text of a diagram has one line end and no byte-order mark so the golden files are equal byte by byte; the token of GitHub is in
/// one place only and never in a text.
/// </summary>
public sealed class Phase6DiagramAndSkillContractTests
{
    private const string Flow = "flowchart LR\n    n1[\"A\"] --> n2[\"B\"]\n";

    private static DiagramSource Solution(string? mermaid = null) => new(DiagramKind.Solution, string.Empty, HashA, mermaid ?? Flow);

    // ---- the diagrams ----

    [Fact]
    public void The_four_kinds_state_and_export_states_are_the_ones_of_the_design_and_are_appended_never_inserted()
    {
        Assert.Equal(["Solution", "ProjectNamespaces", "RunPlan", "RunSequence"], Enum.GetNames<DiagramKind>());
        Assert.Equal(["NotGenerated", "Current", "Stale"], Enum.GetNames<DiagramState>());
        Assert.Equal(["NotExported", "SameAsCache", "Older", "ChangedOutside", "Unknown"], Enum.GetNames<DiagramExportState>());
        Assert.Equal(["Ask", "Overwrite", "SaveAsNew", "Skip"], Enum.GetNames<DiagramExportConflictChoice>());
    }

    [Fact]
    public void A_diagram_source_keeps_its_text_its_model_and_its_subject()
    {
        var source = new DiagramSource(DiagramKind.ProjectNamespaces, "src/App/App.csproj", HashB, Flow);

        Assert.Equal((DiagramKind.ProjectNamespaces, "src/App/App.csproj", HashB, Flow), (source.Kind, source.Subject, source.ModelHash, source.Mermaid));
        Assert.Equal(Solution(), Solution());
    }

    [Theory]
    [InlineData("flowchart LR\r\n    a --> b\r\n")]
    [InlineData("flowchart LR\r")]
    [InlineData("\uFEFFflowchart LR\n")]
    [InlineData("")]
    public void The_text_of_a_diagram_has_only_line_feeds_no_byte_order_mark_and_is_not_empty(string mermaid)
    {
        Assert.Throws<ArgumentException>(() => Solution(mermaid));
    }

    [Fact]
    public void The_text_of_a_diagram_has_a_cap()
    {
        Assert.Equal(DiagramSource.MaxMermaidLength, Solution(new string('a', DiagramSource.MaxMermaidLength)).Mermaid.Length);
        Assert.Throws<ArgumentException>(() => Solution(new string('a', DiagramSource.MaxMermaidLength + 1)));
    }

    [Fact]
    public void The_subject_is_empty_for_the_solution_and_one_line_for_the_rest_and_the_model_is_a_sha256()
    {
        Assert.Throws<ArgumentException>(() => new DiagramSource(DiagramKind.Solution, "src/App", HashA, Flow));
        Assert.Throws<ArgumentException>(() => new DiagramSource(DiagramKind.RunPlan, string.Empty, HashA, Flow));
        Assert.Throws<ArgumentException>(() => new DiagramSource(DiagramKind.RunSequence, "two\nlines", HashA, Flow));
        Assert.Throws<ArgumentException>(() => new DiagramSource(DiagramKind.RunSequence, new string('x', DiagramSource.MaxSubjectLength + 1), HashA, Flow));
        Assert.Throws<ArgumentException>(() => new DiagramSource(DiagramKind.RunPlan, "20261112-a1b2c3", "abc", Flow));
        Assert.Throws<ArgumentException>(() => new DiagramSource(DiagramKind.RunPlan, "20261112-a1b2c3", HashA.ToUpperInvariant(), Flow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagramSource((DiagramKind)99, string.Empty, HashA, Flow));
        Assert.Equal("20261112-a1b2c3", new DiagramSource(DiagramKind.RunPlan, "20261112-a1b2c3", HashA, Flow).Subject);
    }

    [Fact]
    public void A_diagram_is_named_by_its_kind_and_its_subject_under_the_same_rules()
    {
        Assert.Equal(new DiagramRef(DiagramKind.Solution, string.Empty), new DiagramRef(DiagramKind.Solution, string.Empty));
        Assert.NotEqual(new DiagramRef(DiagramKind.RunPlan, "r1"), new DiagramRef(DiagramKind.RunSequence, "r1"));
        Assert.Throws<ArgumentException>(() => new DiagramRef(DiagramKind.Solution, "x"));
        Assert.Throws<ArgumentException>(() => new DiagramRef(DiagramKind.RunPlan, "a\nb"));
    }

    [Fact]
    public void A_diagram_that_is_not_generated_has_nothing_and_one_that_is_has_a_fingerprint_and_a_date()
    {
        var solution = new DiagramRef(DiagramKind.Solution, string.Empty);

        var none = new DiagramStatus(solution, DiagramState.NotGenerated, DiagramExportState.NotExported, null, null, null, false);
        var current = new DiagramStatus(solution, DiagramState.Current, DiagramExportState.SameAsCache, CommitA, HashA, T0, true);
        var stale = new DiagramStatus(solution, DiagramState.Stale, DiagramExportState.Older, CommitA, HashA, T0, false);

        Assert.Equal(DiagramState.NotGenerated, none.State);
        Assert.Equal((CommitA, HashA, T0, true), (current.Describes, current.ModelHash, current.GeneratedAt, current.HasPrevious));
        Assert.Equal(DiagramState.Stale, stale.State);
        Assert.Throws<ArgumentException>(() => new DiagramStatus(solution, DiagramState.NotGenerated, DiagramExportState.NotExported, CommitA, null, null, false));
        Assert.Throws<ArgumentException>(() => new DiagramStatus(solution, DiagramState.NotGenerated, DiagramExportState.NotExported, null, null, null, true));
        Assert.Throws<ArgumentException>(() => new DiagramStatus(solution, DiagramState.Current, DiagramExportState.NotExported, null, null, T0, false));
        Assert.Throws<ArgumentException>(() => new DiagramStatus(solution, DiagramState.Current, DiagramExportState.NotExported, null, HashA, null, false));
        Assert.Throws<ArgumentException>(() => new DiagramStatus(solution, DiagramState.Current, DiagramExportState.NotExported, "HEAD", HashA, T0, false));
        Assert.Throws<ArgumentException>(() => new DiagramStatus(solution, DiagramState.Current, DiagramExportState.NotExported, null, "abc", T0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagramStatus(solution, (DiagramState)99, DiagramExportState.NotExported, null, null, null, false));
    }

    [Fact]
    public void An_export_names_one_to_two_hundred_diagrams_once_and_a_folder_inside_the_project_that_is_not_the_one_of_git()
    {
        DiagramRef[] one = [new(DiagramKind.Solution, string.Empty)];

        Assert.Equal("docs/generado/diagramas", DiagramExportRequest.DefaultFolder);
        Assert.Equal(DiagramExportRequest.DefaultFolder, new DiagramExportRequest(one, DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask).Folder);
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest([], DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest([one[0], one[0]], DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest([.. Enumerable.Range(0, DiagramExportRequest.MaxDiagrams + 1).Select(i => new DiagramRef(DiagramKind.RunPlan, $"r{i}"))], DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, "../outside", DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, "docs\\generado", DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, "C:/docs", DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, "/docs", DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, ".git", DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, ".GIT/hooks", DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentException>(() => new DiagramExportRequest(one, string.Empty, DiagramExportConflictChoice.Ask));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagramExportRequest(one, "docs", (DiagramExportConflictChoice)99));
    }

    [Fact]
    public void An_exported_file_and_a_conflict_have_a_relative_path_and_a_conflict_is_only_a_file_that_was_changed_or_is_not_ours()
    {
        var solution = new DiagramRef(DiagramKind.Solution, string.Empty);

        Assert.Equal("docs/generado/diagramas/solucion.md", new DiagramExportedFile(solution, "docs/generado/diagramas/solucion.md", HashA).Path);
        Assert.Throws<ArgumentException>(() => new DiagramExportedFile(solution, "../solucion.md", HashA));
        Assert.Throws<ArgumentException>(() => new DiagramExportedFile(solution, "solucion.md", "abc"));
        Assert.Equal(DiagramExportState.ChangedOutside, new DiagramExportConflict(solution, "docs/solucion.md", DiagramExportState.ChangedOutside).State);
        Assert.Equal(DiagramExportState.Unknown, new DiagramExportConflict(solution, "docs/solucion.md", DiagramExportState.Unknown).State);
        Assert.Throws<ArgumentException>(() => new DiagramExportConflict(solution, "docs/solucion.md", DiagramExportState.SameAsCache));
        Assert.Throws<ArgumentException>(() => new DiagramExportConflict(solution, "../solucion.md", DiagramExportState.Unknown));

        var result = new DiagramExportResult([new DiagramExportedFile(solution, "a.md", HashA)], [new DiagramExportConflict(solution, "b.md", DiagramExportState.Unknown)]);
        Assert.Equal(result, new DiagramExportResult([new DiagramExportedFile(solution, "a.md", HashA)], [new DiagramExportConflict(solution, "b.md", DiagramExportState.Unknown)]));
        Assert.Throws<ArgumentNullException>(() => new DiagramExportResult(null!, []));
    }

    [Fact]
    public void The_catalog_and_the_generator_have_the_members_the_design_lists()
    {
        Assert.Equal(["GenerateAsync", "get_Kind"], typeof(IDiagramGenerator).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["ExportAsync", "GenerateAsync", "ListAsync", "RestorePreviousAsync"], typeof(IDiagramCatalog).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
    }

    // ---- the doctor of skills ----

    [Fact]
    public void The_token_of_github_shows_itself_only_through_reveal_and_never_in_a_text()
    {
        var token = new GitHubToken("github_pat_SENTINEL1234567890");

        Assert.Equal("github_pat_SENTINEL1234567890", token.Reveal());
        Assert.DoesNotContain("SENTINEL", token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", $"{token}", StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", SkillsDoctorTokenUse.None.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", new SkillsDoctorTokenUse(SkillsDoctorTokenState.Granted, token).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("two\nlines")]
    [InlineData("tab\tbed")]
    [InlineData("ñandú")]
    public void A_token_is_visible_ascii_without_spaces_and_not_empty(string value)
    {
        Assert.Throws<ArgumentException>(() => new GitHubToken(value));
    }

    [Fact]
    public void A_token_has_a_cap_of_two_hundred_and_fifty_five_characters()
    {
        Assert.Equal(GitHubToken.MaxLength, new GitHubToken(new string('a', GitHubToken.MaxLength)).Reveal().Length);
        Assert.Throws<ArgumentException>(() => new GitHubToken(new string('a', GitHubToken.MaxLength + 1)));
    }

    [Fact]
    public void A_token_comes_with_the_state_granted_and_only_with_it()
    {
        var token = new GitHubToken("t");

        Assert.Same(token, new SkillsDoctorTokenUse(SkillsDoctorTokenState.Granted, token).Token);
        Assert.Null(SkillsDoctorTokenUse.None.Token);
        Assert.Equal(SkillsDoctorTokenState.NoneStored, SkillsDoctorTokenUse.None.State);
        Assert.Null(SkillsDoctorTokenUse.Denied.Token);
        Assert.Equal(SkillsDoctorTokenState.PresenceDenied, SkillsDoctorTokenUse.Denied.State);
        Assert.Throws<ArgumentException>(() => new SkillsDoctorTokenUse(SkillsDoctorTokenState.Granted, null));
        Assert.Throws<ArgumentException>(() => new SkillsDoctorTokenUse(SkillsDoctorTokenState.NoneStored, token));
        Assert.Throws<ArgumentException>(() => new SkillsDoctorTokenUse(SkillsDoctorTokenState.PresenceDenied, token));
        Assert.Throws<ArgumentException>(() => new SkillsDoctorTokenUse((SkillsDoctorTokenState)99, null));
    }

    [Fact]
    public void The_doctor_has_a_code_for_each_failure_and_the_ports_the_design_lists()
    {
        Assert.Equal(
            ["NetworkNotAllowed", "InvalidOrigin", "NoNetwork", "Forbidden", "NotFound", "RateLimited", "RedirectRefused", "UnreadableResponse"],
            Enum.GetNames<SkillHealthFailure>());
        Assert.Equal(["get_NetworkAllowed"], typeof(ISkillsDoctorPolicy).GetMethods().Select(m => m.Name));
        Assert.Equal(["UseTokenAsync"], typeof(ISkillsDoctorSecrets).GetMethods().Select(m => m.Name));
        Assert.Equal(["MarkReviewedAsync", "ReviewedSha256Async"], typeof(ISkillScriptReviews).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["CheckAsync"], typeof(ISkillHealthBatchChecker).GetMethods().Select(m => m.Name));
        Assert.Equal(["CheckAsync"], typeof(ISkillHealthChecker).GetMethods().Select(m => m.Name));
    }

    [Fact]
    public void The_origin_of_a_skill_is_checked_by_the_domain_before_anything_is_asked()
    {
        Assert.True(GitHubSkillOrigin.TryCreate("anthropics", "skills", "main", new string('a', 40), "skills/pdf", out _, out _));
        Assert.False(GitHubSkillOrigin.TryCreate("anthropics", "skills", "main", new string('a', 40), "skills/../../x", out _, out var problem));
        Assert.Equal(GitHubOriginProblem.Folder, problem);
    }
}
