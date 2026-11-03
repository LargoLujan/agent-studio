using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>The session facade: it reads what exists and lists what is missing, never filling a gap with made-up data.</summary>
public sealed class OrchestrationSessionTests
{
    private const string Root = @"C:\proyectos\calc";

    [Fact]
    public async Task Without_a_backend_the_engine_and_the_stores_are_listed_as_missing()
    {
        var session = new OrchestrationSession(OrchestrationBackend.None);

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.False(session.HasEngine);
        Assert.Null(context.Project);
        Assert.Null(context.Requirements);
        Assert.Empty(context.Roster);
        Assert.Null(context.PlanContext(new PlanLimits()));
        Assert.StartsWith("Motor no disponible", context.Missing[0], StringComparison.Ordinal);
        Assert.StartsWith("Falta el almacén del proyecto", context.Missing[1], StringComparison.Ordinal);
        Assert.Equal(2, context.Missing.Count);
        Assert.Empty(await session.ListRunsAsync(new("p", Root), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task With_every_port_the_context_validates_plans_against_the_project()
    {
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()));

        var context = await session.OpenAsync(Root, CancellationToken.None);
        var planContext = context.PlanContext(new PlanLimits());

        Assert.True(session.HasEngine);
        Assert.Empty(context.Missing);
        Assert.Equal("p-1", context.Project?.ProjectId);
        Assert.NotNull(planContext);
        Assert.Equal(["build", "lint", "test"], planContext.VerificationSteps.Order(StringComparer.Ordinal));
        Assert.Equal(planContext.VerificationSteps, planContext.ToolIds);
        Assert.Equal(["test"], planContext.TestRunSteps!); // the one step of the profile whose evidence is a test run
        Assert.Equal("orquestador", context.Orchestrator?.Id.Value);
        Assert.Empty(PlanValidator.Validate(PlanFixtures.Plan(PlanFixtures.Replace("T8", t => t with { ToolId = "lint" })), planContext));
        Assert.Equal(["R1", "R2"], context.EditorOptions.Requirements.Select(r => r.Id.Value));
    }

    [Fact]
    public async Task A_failing_port_is_listed_and_the_rest_still_loads()
    {
        var backend = Backends.Complete(new FakeOrchestrator()) with
        {
            Roster = new FakeRoster(() => throw new IOException("La base de datos está bloqueada.")),
        };

        var context = await new OrchestrationSession(backend).OpenAsync(Root, CancellationToken.None);

        Assert.Equal(["No se pudo leer la plantilla de agentes: La base de datos está bloqueada."], context.Missing);
        Assert.Empty(context.Roster);
        Assert.NotNull(context.Requirements);
        Assert.NotNull(context.PlanContext(new PlanLimits()));
    }

    [Fact]
    public async Task A_project_without_frozen_requirements_or_profile_says_so()
    {
        var backend = Backends.Complete(new FakeOrchestrator()) with
        {
            Requirements = new FakeRequirementStore(),
            Verification = new FakeProfileSource(null),
        };

        var context = await new OrchestrationSession(backend).OpenAsync(Root, CancellationToken.None);

        Assert.Equal(
            [
                "El proyecto no tiene requisitos congelados: escríbelos y congélalos en «Requisitos» para poder planificar.",
                "El proyecto no tiene perfil de verificación (.agentstudio/verification.json).",
            ],
            context.Missing);
        Assert.Null(context.PlanContext(new PlanLimits()));
        Assert.Null(context.VerificationProposal);
        Assert.Equal(AgentRole.Orchestrator, context.Orchestrator?.Role);
    }

    [Fact]
    public async Task A_project_without_a_profile_is_offered_one_that_is_not_applied()
    {
        var source = new FakeProfileSource(null, proposal: Backends.Profile);
        var backend = Backends.Complete(new FakeOrchestrator()) with { Verification = source };

        var context = await new OrchestrationSession(backend).OpenAsync(Root, CancellationToken.None);

        Assert.Null(context.Verification);
        Assert.Same(Backends.Profile, context.VerificationProposal);
        Assert.Empty(source.Saved);
        Assert.Null(context.PlanContext(new PlanLimits()));
        Assert.Equal(
            ["El proyecto no tiene perfil de verificación (.agentstudio/verification.json): revisa el propuesto y pulsa «Usar perfil propuesto»."],
            context.Missing);
    }

    [Fact]
    public async Task A_profile_file_that_cannot_be_used_is_reported_and_nothing_is_proposed_over_it()
    {
        var source = new FakeProfileSource(null, proposal: Backends.Profile)
        {
            LoadFailure = new InvalidDataException("The verification profile 'verification.json' cannot be used: Step 'build' has no executable."),
        };
        var backend = Backends.Complete(new FakeOrchestrator()) with { Verification = source };

        var context = await new OrchestrationSession(backend).OpenAsync(Root, CancellationToken.None);

        Assert.Null(context.Verification);
        Assert.Null(context.VerificationProposal);
        Assert.Equal(
            ["No se pudo leer el perfil de verificación: The verification profile 'verification.json' cannot be used: Step 'build' has no executable."],
            context.Missing);
    }

    [Fact]
    public async Task Saving_the_proposal_makes_it_the_profile_of_the_project()
    {
        var source = new FakeProfileSource(null, proposal: Backends.Profile);
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { Verification = source });
        var context = await session.OpenAsync(Root, CancellationToken.None);

        await session.SaveVerificationProfileAsync(context.Project!, context.VerificationProposal!, CancellationToken.None);
        var reopened = await session.OpenAsync(Root, CancellationToken.None);

        Assert.Equal([Backends.Profile], source.Saved);
        Assert.Same(Backends.Profile, reopened.Verification);
        Assert.Null(reopened.VerificationProposal);
        Assert.Empty(reopened.Missing);
    }

    [Fact]
    public async Task A_project_without_agents_gets_the_default_roster()
    {
        var roster = new FakeRoster();
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { Roster = roster });

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.True(context.RosterCreated);
        Assert.Equal(["orquestador", "ejecutor", "juez", "arquitecto", "critico", "juez-claude", "juez-revision"], roster.Saved.Select(a => a.Id.Value));
        Assert.Equal(roster.Saved, context.Roster);
        Assert.Equal("orquestador", context.Orchestrator?.Id.Value);
        Assert.Empty(context.Missing);

        var reopened = await session.OpenAsync(Root, CancellationToken.None);

        Assert.False(reopened.RosterCreated);
        Assert.Equal(7, roster.Saved.Count);
    }

    [Fact]
    public async Task A_roster_that_cannot_be_read_is_never_overwritten_with_the_default()
    {
        var roster = new FakeRoster(() => throw new IOException("La base de datos está bloqueada."));
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { Roster = roster });

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.False(context.RosterCreated);
        Assert.Empty(roster.Saved);
    }

    /// <summary>
    /// The screen warns about the writers at medium integrity of a plan as the engine will (ADR-0018), and whether the
    /// installed Claude Code keeps an executor's writes in its worktree is part of that: the context carries the probe's
    /// answer, and without a probe nobody can say, so it does not (fail closed, as the engine).
    /// </summary>
    [Fact]
    public async Task The_context_carries_whether_the_installed_Claude_Code_was_measured_to_keep_its_writes_in_the_worktree()
    {
        var measured = await new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { WriteConfinement = new FixedWriteConfinement(ProviderId.ClaudeCode) }).OpenAsync(Root, CancellationToken.None);
        var neverMeasured = await new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { WriteConfinement = new FixedWriteConfinement() }).OpenAsync(Root, CancellationToken.None);
        var withoutProbe = await new OrchestrationSession(Backends.Complete(new FakeOrchestrator())).OpenAsync(Root, CancellationToken.None);

        Assert.True(measured.ClaudeWritesConfined);
        Assert.False(neverMeasured.ClaudeWritesConfined);
        Assert.False(withoutProbe.ClaudeWritesConfined);
    }

    /// <summary>A Claude Code updated between two openings of the project is seen by the second one: the probe is asked every time.</summary>
    [Fact]
    public async Task The_probe_is_asked_again_each_time_the_project_is_opened()
    {
        var probe = new SwitchableWriteConfinement { Confined = true };
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { WriteConfinement = probe });

        var first = await session.OpenAsync(Root, CancellationToken.None);
        probe.Confined = false;
        var second = await session.OpenAsync(Root, CancellationToken.None);

        Assert.True(first.ClaudeWritesConfined);
        Assert.False(second.ClaudeWritesConfined);
        Assert.Equal([ProviderId.ClaudeCode, ProviderId.ClaudeCode], probe.Asked);
    }

    private sealed class SwitchableWriteConfinement : IWriteConfinementProbe
    {
        public bool Confined { get; set; }

        public List<ProviderId> Asked { get; } = [];

        public Task<bool> IsWriteConfinedAsync(ProviderId provider, CancellationToken cancellationToken)
        {
            Asked.Add(provider);
            return Task.FromResult(Confined && provider == ProviderId.ClaudeCode);
        }
    }

    [Fact]
    public async Task A_failure_creating_the_default_roster_is_listed()
    {
        var roster = new FakeRoster { SaveFailure = new IOException("Disco lleno.") };
        var session = new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { Roster = roster });

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.False(context.RosterCreated);
        Assert.Empty(context.Roster);
        Assert.Equal(["No se pudo crear la plantilla de agentes por defecto: Disco lleno."], context.Missing);
    }

    // ---- a decision log no anchor recognises at the project's location (ADR-0010, addendum of 2026-10-18) ----

    [Fact]
    public async Task A_history_without_an_anchor_for_this_location_is_an_integrity_problem_that_the_person_can_accept()
    {
        var history = new HistoryBackend();
        await history.WriteUnanchoredAsync(2);
        var session = new OrchestrationSession(history.On(Backends.Complete(new FakeOrchestrator())));

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.Equal(new UnanchoredHistory(2, history.Entries[^1].Hash), context.Unanchored);
        Assert.Contains("2 entradas", context.IntegrityProblem, StringComparison.Ordinal);
        Assert.Contains("no hay ancla para esta ubicación", context.IntegrityProblem, StringComparison.Ordinal);

        var accepted = await session.AcceptHistoryAsync(context.Project!, context.Unanchored!, CancellationToken.None);
        var reopened = await session.OpenAsync(Root, CancellationToken.None);

        Assert.True(accepted);
        var asked = Assert.Single(history.Prompt.Requests);
        Assert.Equal(UnanchoredHistory.AcceptAction, asked.Action);
        Assert.Equal(RiskLevel.Critical, asked.Risk);
        Assert.Equal(HistoryBackend.ProjectId, asked.ProjectId);
        Assert.Equal(context.Unanchored!.Binding(HistoryBackend.ProjectId), asked.Binding);
        Assert.Contains("2 entradas", asked.Description, StringComparison.Ordinal);
        Assert.Null(reopened.IntegrityProblem);
        Assert.Null(reopened.Unanchored);
        Assert.Equal([DecisionKind.AuditAlertRaised, DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted], history.Entries.Skip(2).Select(e => e.Kind));
        Assert.Equal(new Actor(ActorKind.User, ScriptedApprovalPrompt.Person), history.Entries[^1].Actor);
    }

    [Fact]
    public async Task A_person_who_does_not_accept_the_history_leaves_it_blocked_and_nothing_is_written()
    {
        var history = new HistoryBackend(approve: false);
        await history.WriteUnanchoredAsync(2);
        var session = new OrchestrationSession(history.On(Backends.Complete(new FakeOrchestrator())));
        var context = await session.OpenAsync(Root, CancellationToken.None);

        var accepted = await session.AcceptHistoryAsync(context.Project!, context.Unanchored!, CancellationToken.None);

        Assert.False(accepted);
        Assert.Single(history.Prompt.Requests);
        Assert.Equal(2, history.Entries.Count);
        Assert.Empty(history.Anchors.Written);
        Assert.NotNull((await session.OpenAsync(Root, CancellationToken.None)).Unanchored);
    }

    [Fact]
    public async Task An_empty_history_where_another_project_lived_says_which_history_is_gone()
    {
        var history = new HistoryBackend();
        history.Anchors.Foreign[HistoryBackend.ProjectId] = ("proyecto-anterior", 5);
        var session = new OrchestrationSession(history.On(Backends.Complete(new FakeOrchestrator())));

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.Equal(new UnanchoredHistory(0, DecisionLog.GenesisHash, "proyecto-anterior", 5), context.Unanchored);
        Assert.Contains("proyecto-anterior", context.IntegrityProblem, StringComparison.Ordinal);
        Assert.Contains("5 entradas", context.IntegrityProblem, StringComparison.Ordinal);
        Assert.True(await session.AcceptHistoryAsync(context.Project!, context.Unanchored!, CancellationToken.None));
        Assert.Contains("proyecto-anterior", Assert.Single(history.Prompt.Requests).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_log_is_an_integrity_problem_with_nothing_to_accept()
    {
        var history = new HistoryBackend();
        await history.WriteTamperedAsync();
        var session = new OrchestrationSession(history.On(Backends.Complete(new FakeOrchestrator())));

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.StartsWith("El registro de decisiones no es íntegro", context.IntegrityProblem, StringComparison.Ordinal);
        Assert.Null(context.Unanchored);
    }

    [Fact]
    public async Task A_trusted_log_is_no_problem()
    {
        var history = new HistoryBackend();
        var session = new OrchestrationSession(history.On(Backends.Complete(new FakeOrchestrator())));

        var context = await session.OpenAsync(Root, CancellationToken.None);

        Assert.Null(context.IntegrityProblem);
        Assert.Null(context.Unanchored);
        Assert.Empty(context.Missing);
    }
}
