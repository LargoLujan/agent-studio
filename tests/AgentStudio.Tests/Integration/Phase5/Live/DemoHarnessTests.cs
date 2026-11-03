using System.IO;
using System.Reflection;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Integration.Phase3;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// The harness of the demonstrations, without quota and without any CLI: the debates of decision 2 a open with the
/// disposition they ask for, every block of the plan runs a live test the suite under audit leaves out, the profile of the
/// audit always excludes the tests that spend quota, the captures never keep the user's profile folder, and the document is
/// composed with every figure and its origin.
/// </summary>
public sealed class DemoHarnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Debates => new(DebateDemos().Select(d => d.Block));

    [Theory]
    [MemberData(nameof(Debates))]
    public void Each_debate_of_the_demonstrations_opens_with_the_disposition_of_decision_2a(string block)
    {
        var demo = DebateDemos().Single(d => d.Block == block);
        var roster = DemoRoster.Debates();
        var participants = demo.Participants.Select(p => roster.Single(a => a.Id == p.Agent)).Zip(demo.Participants, (agent, p) => new DebateParticipant(agent.Id, p.Role, agent.Model)).ToList();
        var judges = demo.Judges.Select(id => roster.Single(a => a.Id == id)).Select(j => new JudgeIdentity(j.Id, j.Model)).ToList();
        var review = demo.Requirements is null;
        var subject = review ? DebateSubjectKind.Run : DebateSubjectKind.Commit;
        var producers = review ? new HashSet<VendorId> { VendorId.Anthropic } : [];

        var arrangement = DebateJudgingRules.Possible(demo.Mode, subject, participants, producers, judges).FirstOrDefault(a => a.Kind == demo.Judging);
        Assert.NotNull(arrangement);
        var requirements = demo.Requirements?.Invoke(T0) ?? (demo.Mode == DebateMode.OrchestratorReview ? DebateModes.OrchestratorReview(T0) : F38EndToEndLiveTests.Requirements());
        var session = DebateSession.Open(new DebateId("deb-20261020-abcdef"), demo.Mode, subject, demo.Question, requirements, participants, arrangement, DebateDemo.Limits, T0);

        Assert.Equal(DebatePhase.Proposals, session.Phase);
        Assert.DoesNotContain("{run}", demo.QuestionFor(review ? "20261020-0f0f0f" : null), StringComparison.Ordinal);
        Assert.Equal([VendorId.Anthropic.Value, VendorId.OpenAI.Value], participants.Select(p => p.Model.Vendor.Value).Order(StringComparer.Ordinal));
        Assert.Equal(2, DebateDemo.Limits.MaxRounds);
        if (review)
        {
            Assert.Equal(DebateJudgingKind.NonProducerJudge, arrangement.Kind);
            Assert.Equal(VendorId.OpenAI, Assert.Single(arrangement.Judges).Model.Vendor);
        }
        else
        {
            Assert.Equal(DebateJudgingKind.CrossVendorPanel, arrangement.Kind);
            Assert.All(requirements.Requirements.SelectMany(r => r.Criteria), c => Assert.Equal(VerificationMethod.JudgeAssessment, c.Method));
        }
    }

    [Fact]
    public void The_template_of_the_demonstrations_is_the_app_default_with_Claude_on_haiku()
    {
        var debates = DemoRoster.Debates();
        var audit = DemoRoster.Audit();
        var template = DefaultAgentRoster.Create();

        Assert.Equal(template.Select(a => a.Id), debates.Select(a => a.Id));
        Assert.All(debates.Concat(audit), a => Assert.Empty(a.Validate()));
        Assert.All(debates.Where(a => a.Role == AgentRole.Judge), a => Assert.Equal(MemoryPolicy.None, a.Memory));
        Assert.All(debates.Where(a => a.Model.Provider == ProviderId.ClaudeCode && a.Role != AgentRole.Judge), a => Assert.Equal(DemoRoster.ClaudeModel, a.Model.Model));
        Assert.Null(debates.Single(a => a.Id == DemoRoster.ClaudeJudge).Model.Model);
        Assert.All(debates.Where(a => a.Model.Provider == ProviderId.CodexCli && a.Id != DemoRoster.RevisionJudge), a => Assert.Null(a.Model.Model));
        Assert.Equal(DefaultAgentRoster.RevisionJudgeModel, debates.Single(a => a.Id == DemoRoster.RevisionJudge).Model.Model);
        Assert.All(debates, a =>
        {
            var original = template.Single(t => t.Id == a.Id);
            Assert.Equal((original.Role, original.Budget, original.Memory, original.Model.Provider, original.Model.Vendor), (a.Role, a.Budget, a.Memory, a.Model.Provider, a.Model.Vendor));
            Assert.Equal(original.AllowedTools, a.AllowedTools);
        });
        Assert.Equal(DemoRoster.AuditJudgeDuration, audit.Single(a => a.Id == DemoRoster.Judge).Budget.MaxDuration);
        Assert.Equal([DemoRoster.Orchestrator, DemoRoster.Executor, DemoRoster.Judge], DemoRoster.Planning(debates).Select(a => a.Id));
    }

    [Fact]
    public void Every_block_of_the_plan_runs_a_live_test_the_audited_suite_leaves_out()
    {
        var assembly = typeof(DemoPlan).Assembly;
        var categories = TestCategories.Map(assembly);
        foreach (var entry in DemoPlan.Entries)
        {
            var type = assembly.GetType(typeof(DemoPlan).Namespace + "." + entry.Class);
            Assert.True(type is not null, entry.Class);
            var method = type.GetMethod(entry.Method, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(method is not null, entry.Method);
            Assert.Contains(method.GetCustomAttributesData(), a => a.AttributeType.Name == "FactAttribute");
            Assert.Contains("ProviderLive", categories.Of(type.FullName + "." + entry.Method));
            Assert.StartsWith("dotnet test --project tests/AgentStudio.Tests/AgentStudio.Tests.csproj -- --filter-method \"AgentStudio.Tests.Integration.Phase5.Live.", DemoPlan.Command(entry), StringComparison.Ordinal);
        }

        // Every class of this folder that reads the switches carries the trait the test step of the audit leaves out:
        // otherwise the suite under audit, which inherits the switches, would run it.
        var testClasses = assembly.GetTypes()
            .Where(t => t.Namespace == typeof(DemoPlan).Namespace && t != typeof(DemoHarnessTests))
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name is "FactAttribute" or "TheoryAttribute")))
            .ToList();
        Assert.NotEmpty(testClasses);
        Assert.All(testClasses, t => Assert.All(
            t.GetMethods().Where(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name is "FactAttribute" or "TheoryAttribute")),
            m => Assert.Contains("ProviderLive", categories.Of(t.FullName + "." + m.Name))));
        Assert.Equal(DemoPlan.Entries.Count, DemoPlan.Entries.Select(e => e.Block).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_profile_of_the_audit_always_leaves_out_the_tests_that_spend_quota()
    {
        var plain = AuditDemo.Profile([]);
        var filtered = AuditDemo.Profile(["Category=ConPty", "AgentStudio.Tests.Foo.BarTests", "Category=ProviderLive"]);

        Assert.Empty(VerificationRules.Check(plain));
        Assert.Empty(VerificationRules.Check(filtered));
        Assert.Equal(["--filter-not-trait", "Category=ProviderLive"], plain.Find("test")!.Arguments.TakeLast(2));
        Assert.Equal(
            ["--filter-not-trait", "Category=ProviderLive", "--filter-not-trait", "Category=ConPty", "--filter-not-class", "AgentStudio.Tests.Foo.BarTests"],
            filtered.Find("test")!.Arguments.TakeLast(6));
        Assert.All(filtered.Steps, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
        Assert.Equal(DotNetVerificationProfile.Propose().Find("build")!.Arguments, plain.Find("build")!.Arguments);
        Assert.NotEqual(plain.ContentHash, filtered.ContentHash);
    }

    [Fact]
    public void The_requirements_of_the_audit_are_the_common_criteria_and_a_selection_of_phases_3_and_4()
    {
        var requirements = AuditDemo.Requirements(T0);

        Assert.Equal(["C-1", "C-2", "C-3", "C-4", "F3-4", "F3-5", "F3-7", "F4-1", "F4-4", "F4-5"], requirements.Requirements.Select(r => r.Id.Value).Order(StringComparer.Ordinal));
        Assert.All(requirements.Requirements, r => Assert.Equal(RequirementPriority.Mandatory, r.Priority));
        Assert.Equal(AuditDemo.Requirements(T0.AddDays(1)).ContentHash, requirements.ContentHash);
        Assert.Equal([VendorId.Anthropic], AuditDemo.DeclaredAuthors);
    }

    [Fact]
    public void Reads_the_failed_tests_of_the_console_and_their_categories()
    {
        var evidence = string.Join('\n',
            "# isolation: low integrity (S-1-16-4096)",
            "stdout| failed AgentStudio.Tests.Integration.Phase5.Live.Phase5DebateDemoLiveTests.Free_debate_judged_by_a_cross_vendor_panel (12ms)",
            "stdout|   Assert.True() Failure",
            "stderr| failed Tiny.Tests.TinyTests.Theory(value: 2) (3ms)",
            "stdout| passed Tiny.Tests.TinyTests.Passes (1ms)",
            "stdout| failed AgentStudio.Tests.Integration.Phase5.Live.Phase5DebateDemoLiveTests.Free_debate_judged_by_a_cross_vendor_panel (12ms)");

        var failed = TestCategories.FailedTests(evidence);
        var categories = TestCategories.Map(typeof(DemoPlan).Assembly);

        Assert.Equal(["AgentStudio.Tests.Integration.Phase5.Live.Phase5DebateDemoLiveTests.Free_debate_judged_by_a_cross_vendor_panel", "Tiny.Tests.TinyTests.Theory(value: 2)"], failed);
        Assert.Equal(["ProviderLive"], categories.Of(failed[0]));
        Assert.Empty(categories.Of(failed[1]));
    }

    [Fact]
    public void Replaces_the_user_profile_in_every_spelling_a_path_takes()
    {
        const string profile = @"C:\Users\alguien";
        const string text = @"a C:\Users\alguien\x b C:\\Users\\alguien\\y c C:/Users/alguien/z d Read(//c/Users/alguien/**) e c:\users\ALGUIEN\w f C:\Users\alguienmas";

        var scrubbed = DemoScrub.Text(text, profile);

        Assert.Equal(@"a <usuario>\x b <usuario>\\y c <usuario>/z d Read(<usuario>/**) e <usuario>\w f <usuario>mas", scrubbed);
        Assert.Equal(string.Empty, DemoScrub.Text(null, profile));
    }

    [Fact]
    public void A_capture_is_written_without_the_profile_folder_and_read_back_whole()
    {
        using var folder = new ScratchFolder("as-f5-");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var capture = Sample() with { Notes = [$"Copia en {Path.Combine(profile, "AppData", "Local", "Temp", "as-f5")}"] };

        var path = DemoCaptures.Write(folder.Path, capture);
        var (read, unreadable) = DemoCaptures.ReadAll(folder.Path);

        Assert.DoesNotContain(profile, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(unreadable);
        var back = Assert.Single(read);
        Assert.Equal(capture.Block, back.Block);
        Assert.Equal(@"Copia en <usuario>\AppData\Local\Temp\as-f5", Assert.Single(back.Notes));
        Assert.Equal(capture.Attempts, back.Attempts);
        Assert.Equal(
            capture.Debate!.Contributions.Select(c => (c.Label, c.Author, c.Kind, c.Summary, c.Argument, c.Sha256, c.AnsweredAt)),
            back.Debate!.Contributions.Select(c => (c.Label, c.Author, c.Kind, c.Summary, c.Argument, c.Sha256, c.AnsweredAt)));
        Assert.Equal(capture.Verdicts.Single().Issues, back.Verdicts.Single().Issues);
    }

    [Fact]
    public void Composes_a_debate_with_its_rounds_verdicts_and_every_figure_with_its_origin()
    {
        var document = DemoReport.Compose([Sample()], ["roto.json: no es una captura de la Fase 5"], T0);

        Assert.Contains("# Fase 5 · Demostraciones reales de la Debate Arena y el juez", document, StringComparison.Ordinal);
        Assert.Contains("## Debate libre (`libre`)", document, StringComparison.Ordinal);
        Assert.Contains("> ¿Cómo debería la biblioteca Calc tratar el desbordamiento?", document, StringComparison.Ordinal);
        Assert.Contains("| P1 | ", document, StringComparison.Ordinal);
        Assert.Contains("valoración del juez, no verificable por pruebas", document, StringComparison.Ordinal);
        Assert.Contains("exacto · informado por el CLI", document, StringComparison.Ordinal);
        Assert.Contains("estimado por el CLI", document, StringComparison.Ordinal);
        Assert.Contains("no expuesto", document, StringComparison.Ordinal);
        Assert.Contains("`ApprovedOnAgentClaimsOnly`", document, StringComparison.Ordinal);
        Assert.Contains("Regla del jurado", document, StringComparison.Ordinal);
        Assert.Contains("sin captura todavía", document, StringComparison.Ordinal);
        Assert.Contains("## Capturas que no se pudieron leer", document, StringComparison.Ordinal);
        Assert.All(DemoPlan.Entries, e => Assert.Contains(DemoPlan.Command(e), document, StringComparison.Ordinal));
        Assert.Contains(DemoPlan.Totals, document, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DebateDemo> DebateDemos() =>
        [DebateDemo.Free, DebateDemo.Adversarial, DebateDemo.Architecture, DebateDemo.Tribunal, DebateDemo.Implementation, DebateDemo.Orchestrator];

    /// <summary>A capture of a debate as a demonstration writes it, with the figures of both CLIs.</summary>
    private static DemoCapture Sample() => new()
    {
        Block = DebateDemo.Free.Block,
        Title = DebateDemo.Free.Title,
        Test = nameof(Phase5DebateDemoLiveTests) + "." + nameof(Phase5DebateDemoLiveTests.Free_debate_judged_by_a_cross_vendor_panel),
        StartedAt = T0,
        FinishedAt = T0.AddMinutes(12),
        Result = "Cerrado: Resuelto, Approved, propuesta P2 (regla coinciden); 2 veredictos guardados",
        Completed = true,
        Tools = [new DemoTool("Claude Code", "Installed", "2.1.290", "Authenticated")],
        Debate = new DemoDebateFacts
        {
            Id = "deb-20261020-abcdef",
            Mode = nameof(DebateMode.Free),
            Subject = nameof(DebateSubjectKind.Commit),
            SnapshotCommit = new string('a', 40),
            Question = "¿Cómo debería la biblioteca Calc tratar el desbordamiento?",
            RequirementSetHash = new string('b', 64),
            Requirements = [new DemoRequirement("RQ-L1", "Define qué hace Calc.", "Mandatory", [new DemoCriterion("c1", "JudgeAssessment", "Es concreto.")])],
            Participants =
            [
                new DemoParticipant("P1", nameof(DebateRole.Panelist), "arquitecto", "claude-code", "anthropic", "haiku"),
                new DemoParticipant("P2", nameof(DebateRole.Panelist), "critico", "codex-cli", "openai", null),
            ],
            Judges = [new DemoJudge("juez-claude", "claude-code", "anthropic", "haiku"), new DemoJudge("juez", "codex-cli", "openai", null)],
            Judging = nameof(DebateJudgingKind.CrossVendorPanel),
            WaivedVendors = ["anthropic", "openai"],
            MaxRounds = 2,
            MaxMinutes = 30,
            MaxTokens = 400_000,
            MaxAgentRuns = 6,
            EstimatedTokens = 400_000,
            ApprovalText = "Abrir un debate Free sobre Commit.",
            Rounds = [new DemoRound(1, nameof(DebatePhase.Deliberation), null, nameof(DebateLimitReached.None), new DemoFigure(120_000, "ReportedByProvider", "debate tokens"), false, 95, "Sigue la ronda 2.")],
            Contributions = [new DemoContribution("C1", 1, "P1", nameof(ContributionKind.Proposal), null, "Lanzar OverflowException.", "Usar checked en Add.", 2, 1, [], [], false, 0, T0.AddMinutes(2), new string('c', 64))],
            Phase = nameof(DebatePhase.Closed),
            Outcome = nameof(DebateOutcome.Resolved),
            Rule = "agree",
            Decision = "Approved",
            ChosenProposal = "P2",
            JudgmentReason = nameof(DebateJudgmentReason.Limit),
            LimitReached = nameof(DebateLimitReached.Rounds),
            ResultSummary = "Los dos jueces coinciden.",
        },
        Verdicts =
        [
            new DemoVerdictFacts
            {
                Id = "v1",
                Unit = "deb-20261020-abcdef",
                Scope = "Debate",
                Judge = new DemoJudge("juez", "codex-cli", "openai", null),
                Decision = "Approved",
                ChosenProposal = "P2",
                Confidence = "Medium",
                Valid = false,
                Issues = [new DemoIssue("ApprovedOnAgentClaimsOnly", "Mandatory requirement 'RQ-L1' is supported only by agent statements.")],
                Summary = "P2 es mejor.",
                IssuedAt = T0.AddMinutes(11),
            },
        ],
        Attempts =
        [
            new DemoAttemptFacts("deb-20261020-abcdef", "r1-arquitecto", 1, "DebateParticipant", "arquitecto", "claude-code", "anthropic", "haiku", "claude-haiku-4-5", "Succeeded", 31.5,
                new DemoFigure(41_000, "ReportedByProvider", "claude result.usage"), new DemoFigure(39_000, "ReportedByProvider", "claude result.usage"), new DemoFigure(0, "ReportedByProvider", "claude result.usage"),
                new DemoFigure(900, "ReportedByProvider", "claude result.usage"), new DemoFigure(null, "NotExposed", "claude: no reasoning"), new DemoFigure(0.0512m, "EstimatedByProvider", "claude total_cost_usd")),
            new DemoAttemptFacts("deb-20261020-abcdef", "r1-critico", 1, "DebateParticipant", "critico", "codex-cli", "openai", null, null, "Succeeded", 22.0,
                new DemoFigure(25_000, "ReportedByProvider", "codex turn.completed"), new DemoFigure(10_000, "ReportedByProvider", "codex turn.completed"), new DemoFigure(null, "NotExposed", "codex: no cache writes"),
                new DemoFigure(700, "ReportedByProvider", "codex turn.completed"), new DemoFigure(0, "ReportedByProvider", "codex turn.completed"), new DemoFigure(null, "NotExposed", "codex exec does not report cost")),
        ],
        Journal = ["- **09:01:00** · P-0001 · agente:arquitecto · claude-code/haiku · #debate · «Debate deb-20261020-abcdef» (tarea r1-arquitecto · debate deb-20261020-abcdef)"],
        LogIntact = true,
        HeadHash = new string('d', 64),
    };
}
