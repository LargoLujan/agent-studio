using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Verification;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Verification;

public sealed class VerificationStepTextTests
{
    /// <summary>How every warning of a plan starts and ends; what is in between names each task and why (ADR-0018, decision 3).</summary>
    private const string Opening = "Atención: este plan tiene escritores con integridad Medium. ";

    private const string Meaning = " Pueden escribir en cualquier sitio donde puedas escribir tú, y lo que hagan queda fuera del modelo de amenaza (ADR-0018). Por eso la comprobación de tu repositorio será estricta: una carpeta ignorada que no se pueda verificar bloquea la ejecución.";

    private static readonly AgentDefinition WithBash = Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]);

    private static readonly AgentDefinition OnGemini = Agent("ejecutorGemini", AgentRole.Executor, ProviderId.GeminiCli, VendorId.Google, ["Read", "Edit", "Write"]);

    private static readonly AgentDefinition WithMoreTools = Agent("ejecutorMas", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Monitor", "Workflow"]);

    /// <summary>The fixtures' roster (Claude executors with Read, Edit and Write, a Codex executor, a Codex judge) and <paramref name="extra"/>.</summary>
    private static IReadOnlyList<AgentDefinition> RosterWith(params AgentDefinition[] extra) => [.. Roster(), .. extra];

    private static VerificationProfile WithoutIsolation(params string[] steps) =>
        Profile() with { Steps = [.. Profile().Steps.Select(s => steps.Contains(s.Id) ? s with { Isolation = ProcessIsolation.None } : s)] };

    /// <summary>The warning of <paramref name="plan"/> with the fixtures' roster, on a Claude Code whose confinement was measured unless said otherwise.</summary>
    private static string? Warning(PlanDefinition plan, IReadOnlyList<AgentDefinition>? roster = null, VerificationProfile? profile = null, bool claudeWritesConfined = true) =>
        VerificationStepText.PlanWarning(plan, roster ?? Roster(), profile ?? Profile(), claudeWritesConfined);

    [Fact]
    public void An_isolated_step_reads_as_its_id_title_and_exact_command()
    {
        var test = DotNetVerificationProfile.Propose().Find("test")! with { Arguments = ["test", "--filter-class", "Mi Clase"], Requires = null };

        Assert.Equal("test · Pruebas · dotnet test --filter-class \"Mi Clase\"", VerificationStepText.Line(test));
    }

    [Fact]
    public void A_step_that_requires_others_says_which_before_the_plan_is_approved()
    {
        // B6: the test of the proposed profile always runs after its build; the review of the plan says so.
        var test = DotNetVerificationProfile.Propose().Find("test")! with { Arguments = ["test", "--no-build"] };

        Assert.Equal("test · Pruebas · dotnet test --no-build · requiere build", VerificationStepText.Line(test));
        Assert.Equal(
            "test · Pruebas · dotnet test --no-build · requiere restore y build · SIN AISLAMIENTO: el código que escriben los agentes se ejecuta con tus permisos en el worktree",
            VerificationStepText.Line(test with { Requires = ["restore", "build"], Isolation = ProcessIsolation.None }));
    }

    [Fact]
    public void A_step_whose_profile_turns_isolation_off_says_so_before_it_is_approved()
    {
        var build = DotNetVerificationProfile.Propose().Find("build")! with { Isolation = ProcessIsolation.None };

        Assert.Equal(
            "build · Compilar · dotnet build · SIN AISLAMIENTO: el código que escriben los agentes se ejecuta con tus permisos en el worktree",
            VerificationStepText.Line(build));
    }

    // ---- no writer at medium integrity: no warning ---------------------------------------------------------------------

    [Fact]
    public void The_default_roster_on_a_measured_Claude_Code_with_isolated_steps_needs_no_warning()
    {
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("R1", WorkTaskKind.Review).After(On("T1")),
            Node("L1", WorkTaskKind.ToolRun).After(On("R1")));

        Assert.Null(VerificationStepText.PlanWarning(plan, DefaultAgentRoster.Create(), Profile(), claudeWritesConfined: true));
    }

    [Theory]
    [InlineData("ejecutorShell")]
    [InlineData("ejecutorCodex")]
    [InlineData("ejecutorGemini")]
    public void An_executor_that_cannot_change_its_worktree_writes_nowhere(string agent)
    {
        var plan = Plan(Work("T1", agent: agent, workspace: WorkspaceMode.ReadOnly));

        Assert.Null(Warning(plan, RosterWith(WithBash, OnGemini)));
    }

    [Fact]
    public void A_step_without_isolation_that_no_task_runs_needs_no_warning()
    {
        // The profile would run «lint» at the user's rights, but no task of this plan runs it, so it never runs.
        var plan = Plan(Work("T1").Verified("build"));

        Assert.Null(Warning(plan, profile: WithoutIsolation("lint")));
    }

    // ---- each kind of writer is named with its task ---------------------------------------------------------------------

    [Fact]
    public void A_task_whose_agent_has_Bash_is_named_and_the_warning_says_what_that_means()
    {
        var plan = Plan(Work("T1"), Work("T2", agent: "ejecutorShell"));

        Assert.Equal(
            "Atención: este plan tiene escritores con integridad Medium. T2: el agente «ejecutorShell» tiene Bash. Pueden escribir en cualquier sitio donde puedas escribir tú, y lo que hagan queda fuera del modelo de amenaza (ADR-0018). Por eso la comprobación de tu repositorio será estricta: una carpeta ignorada que no se pueda verificar bloquea la ejecución.",
            Warning(plan, RosterWith(WithBash)));
    }

    [Fact]
    public void A_task_that_Codex_writes_is_named()
    {
        var plan = Plan(Work("T3", agent: "ejecutorCodex"));

        Assert.Equal(Opening + "T3: el agente «ejecutorCodex» escribe con Codex, cuyo sandbox siempre tiene shell." + Meaning, Warning(plan));
    }

    [Fact]
    public void A_step_the_plan_runs_without_isolation_is_named_with_the_task_that_runs_it()
    {
        var plan = Plan(Work("T1").Verified("build"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        Assert.Equal(
            Opening + "L1: el paso «lint» se ejecuta sin aislamiento. T1: el paso «build» se ejecuta sin aislamiento." + Meaning,
            Warning(plan, profile: WithoutIsolation("build", "lint")));
    }

    [Fact]
    public void A_step_that_runs_because_a_named_one_requires_it_is_named_too_with_the_task()
    {
        // B6: T1 names only «test», and «test» requires «build»: the engine runs both, so the warning names both.
        var plan = Plan(Work("T1").Verified("test"));
        static VerificationProfile Requiring(VerificationProfile profile) =>
            profile with { Steps = [.. profile.Steps.Select(s => s.Id == "test" ? s with { Requires = ["build"] } : s)] };

        Assert.Equal(
            Opening + "T1: el paso «build» se ejecuta sin aislamiento y el paso «test» se ejecuta sin aislamiento." + Meaning,
            Warning(plan, profile: Requiring(WithoutIsolation("build", "test"))));
        Assert.Null(Warning(plan, profile: Requiring(Profile())));
    }

    [Fact]
    public void A_task_whose_agent_is_not_in_the_roster_is_named()
    {
        var plan = Plan(Work("T4", agent: "fantasma"), Work("T5") with { Agent = null });

        Assert.Equal(
            Opening + "T4: el agente «fantasma» no está en la plantilla. T5: no tiene agente asignado." + Meaning,
            Warning(plan));
    }

    [Fact]
    public void A_step_the_profile_does_not_have_is_named()
    {
        var plan = Plan(Work("T1").Verified("format"));

        Assert.Equal(Opening + "T1: el paso «format» no está en el perfil." + Meaning, Warning(plan));
    }

    [Fact]
    public void A_Claude_executor_on_a_version_whose_confinement_was_not_measured_is_named()
    {
        var plan = Plan(Work("T1"));

        Assert.Equal(
            Opening + "T1: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido." + Meaning,
            Warning(plan, claudeWritesConfined: false));
    }

    [Fact]
    public void A_Claude_executor_with_tools_beyond_the_measured_profile_is_named_with_them()
    {
        var plan = Plan(Work("T1", agent: "ejecutorMas"));

        Assert.Equal(
            Opening + "T1: el agente «ejecutorMas» tiene herramientas fuera del perfil medido (Monitor y Workflow)." + Meaning,
            Warning(plan, RosterWith(WithMoreTools)));
    }

    [Fact]
    public void An_executor_that_writes_on_a_CLI_whose_confinement_was_never_measured_is_named()
    {
        var plan = Plan(Work("T1", agent: "ejecutorGemini"));

        Assert.Equal(
            Opening + "T1: el agente «ejecutorGemini» escribe con gemini-cli, cuyo confinamiento no se ha medido." + Meaning,
            Warning(plan, RosterWith(OnGemini)));
    }

    [Fact]
    public void Every_task_with_a_writer_is_named_in_the_order_of_the_plan_with_all_its_reasons()
    {
        var plan = Plan(
            Work("T1", agent: "ejecutorShell").Verified("build", "test"),
            Work("T2"),
            Work("T3", agent: "ejecutorCodex"),
            Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        Assert.Equal(
            Opening
            + "L1: el paso «lint» se ejecuta sin aislamiento. "
            + "T1: el agente «ejecutorShell» tiene Bash, el paso «build» se ejecuta sin aislamiento y el paso «test» se ejecuta sin aislamiento. "
            + "T3: el agente «ejecutorCodex» escribe con Codex, cuyo sandbox siempre tiene shell."
            + Meaning,
            Warning(plan, RosterWith(WithBash), WithoutIsolation("build", "test", "lint")));
    }
}
