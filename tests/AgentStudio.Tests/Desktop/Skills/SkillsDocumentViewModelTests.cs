using AgentStudio.Application.Decisions;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Skills;
using static AgentStudio.Tests.Desktop.Skills.SkillsScreenSamples;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// docs/14 D-26 (F4-7), the catalog screen over in-memory ports: the sectors with their counts, the search, activation by
/// scope with the user as <c>decidedBy</c> and the effective state, the estimated context cost, the local health with
/// «sin comprobar», the warnings of linked skills, the stack-driven deterministic recommendation and the assisted one,
/// off by default and with an OK for every use. Nothing is ever activated on its own.
/// </summary>
public sealed class SkillsDocumentViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(SkillsWorld World, SkillsDocumentViewModel Document)> OpenAsync(SkillsWorld? world = null)
    {
        world ??= new SkillsWorld();
        var document = world.Document();
        await document.OpenAsync(Ct);
        return (world, document);
    }

    private static SkillItemViewModel Find(SkillsDocumentViewModel document, string id) =>
        document.Sectors.SelectMany(s => s.Skills).Single(s => s.Id == id);

    private static SkillScopeViewModel Scope(SkillsDocumentViewModel document, string title) =>
        document.Detail!.Scopes.Single(s => s.Title == title);

    // ---- the tree and the search ----

    [Fact]
    public async Task The_tree_has_one_folder_per_sector_with_its_count_in_the_order_of_the_taxonomy()
    {
        var (_, document) = await OpenAsync();

        Assert.Equal(["Ingeniería de datos (2)", "UX/UI (1)", "Desarrollo de escritorio (1)", "Ingeniería de software (1)"], document.Sectors.Select(s => s.Header));
        Assert.Equal(["spark-optimization", "sql-optimization"], document.Sectors[0].Skills.Select(s => s.Id));
        Assert.Contains("5 skills", document.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_search_keeps_the_skills_whose_name_summary_or_tags_match_and_recounts_the_sectors()
    {
        var (_, document) = await OpenAsync();

        document.SearchText = "delta";

        var sector = Assert.Single(document.Sectors);
        Assert.Equal("Ingeniería de datos (1)", sector.Header);
        Assert.Equal(["spark-optimization"], sector.Skills.Select(s => s.Id));

        document.SearchText = "ESCRITORIO";
        Assert.Equal(["wpf-mvvm"], document.Sectors.SelectMany(s => s.Skills).Select(s => s.Id));

        document.SearchText = "nada-de-esto";
        Assert.Empty(document.Sectors);
        Assert.Contains("Ninguna skill", document.Summary, StringComparison.Ordinal);

        document.SearchText = string.Empty;
        Assert.Equal(5, document.Sectors.Sum(s => s.Count));
    }

    [Fact]
    public async Task Selecting_a_skill_in_the_tree_shows_its_card_with_source_tags_and_estimated_cost()
    {
        var (_, document) = await OpenAsync();

        Find(document, "spark-optimization").IsSelected = true;

        var detail = document.Detail!;
        Assert.Equal("spark-optimization", detail.Name);
        Assert.Equal("Ingeniería de datos", detail.SectorTitle);
        Assert.Contains("anthropics/skills", detail.SourceText, StringComparison.Ordinal);
        Assert.Contains("8a1541c", detail.SourceText, StringComparison.Ordinal);
        Assert.Contains("MIT", detail.SourceText, StringComparison.Ordinal);
        Assert.Equal("#delta #ingenieria-datos #spark", detail.TagsText);
        var cost = SkillContextCost.Of(Catalog[0].Entry);
        Assert.Equal(cost, detail.Cost);
        Assert.Contains($"~{cost.Value} tokens", detail.CostText, StringComparison.Ordinal);
        Assert.Contains("Estimado por Agent Studio", detail.CostText, StringComparison.Ordinal);
    }

    // ---- health ----

    [Fact]
    public async Task The_health_shows_the_worst_check_and_what_waits_for_phase_6_as_unverified()
    {
        var world = new SkillsWorld();
        world.Health.Statuses["sql-optimization"] = SkillHealthStatus.Critical;
        var (_, document) = await OpenAsync(world);

        Assert.Equal(SkillHealthStatus.Unknown, Find(document, "spark-optimization").HealthStatus);
        Assert.Equal("Sin comprobar", Find(document, "spark-optimization").HealthLabel);
        Assert.Equal("Crítico", Find(document, "sql-optimization").HealthLabel);

        Find(document, "spark-optimization").IsSelected = true;
        var upstream = Assert.Single(document.Detail!.Checks, c => c.Code == SkillHealthCodes.Upstream);
        Assert.Equal("Sin comprobar", upstream.StatusLabel);
        Assert.Contains("Fase 6", upstream.Message, StringComparison.Ordinal);
        Assert.Contains(document.Detail.Checks, c => c.Code == SkillHealthCodes.License && c.StatusLabel == "Sana");
    }

    [Fact]
    public async Task Without_a_health_reader_every_skill_is_unverified_and_the_screen_says_why()
    {
        var world = new SkillsWorld();
        var document = world.Document(world.Backend(health: false));

        await document.OpenAsync(Ct);

        Assert.All(document.Sectors.SelectMany(s => s.Skills), s => Assert.Equal(SkillHealthStatus.Unknown, s.HealthStatus));
        Assert.Contains(document.Notices, n => n.Contains("salud", StringComparison.Ordinal));
    }

    // ---- activation by scope ----

    [Fact]
    public async Task Each_scope_has_its_switch_global_project_and_one_per_agent_of_the_roster()
    {
        var (_, document) = await OpenAsync();

        Find(document, "spark-optimization").IsSelected = true;

        Assert.Equal(["Global", "Proyecto", "Agente «ejecutor»", "Agente «juez»", "Agente «orquestador»"], document.Detail!.Scopes.Select(s => s.Title));
        Assert.All(document.Detail.Scopes, s => Assert.Null(s.Decision));
        Assert.All(document.Detail.Scopes, s => Assert.True(s.CanToggle));
        Assert.Equal("Sin activar (ninguna decisión)", Scope(document, "Agente «ejecutor»").EffectiveText);
    }

    [Fact]
    public async Task Activating_for_the_project_appends_a_decision_of_the_user_and_every_agent_then_has_it()
    {
        var (world, document) = await OpenAsync();
        Find(document, "spark-optimization").IsSelected = true;

        await Scope(document, "Proyecto").ToggleCommand.ExecuteAsync(null);

        var appended = Assert.Single(world.Activations.Appended);
        Assert.Equal(ProjectId, appended.ProjectId);
        Assert.Equal(new SkillActivation(new SkillId("spark-optimization"), SkillScopeKind.Project, ProjectId, true), appended.Activation);
        Assert.Equal("user", appended.DecidedBy);
        Assert.True(Scope(document, "Proyecto").IsChecked);
        Assert.Equal("Activada (por el proyecto)", Scope(document, "Agente «ejecutor»").EffectiveText);
        Assert.Equal("Proy.", Find(document, "spark-optimization").ActiveBadge);
    }

    [Fact]
    public async Task An_agent_decision_wins_over_the_project_and_the_global_ones()
    {
        var world = new SkillsWorld();
        world.Activations.Decisions.Add(new SkillActivation(new SkillId("spark-optimization"), SkillScopeKind.Global, null, true));
        var (_, document) = await OpenAsync(world);
        Find(document, "spark-optimization").IsSelected = true;

        // The agent's switch starts unchecked (no decision of its own): turning it on and then off leaves a «no».
        await Scope(document, "Agente «juez»").ToggleCommand.ExecuteAsync(null);
        await Scope(document, "Agente «juez»").ToggleCommand.ExecuteAsync(null);

        Assert.Equal([true, false], world.Activations.Appended.Select(a => a.Activation.Enabled));
        Assert.All(world.Activations.Appended, a => Assert.Equal((SkillScopeKind.Agent, "juez"), (a.Activation.Scope, a.Activation.ScopeId)));
        Assert.Equal("Desactivada (por el agente)", Scope(document, "Agente «juez»").EffectiveText);
        Assert.Equal("Activada (global)", Scope(document, "Agente «ejecutor»").EffectiveText);
        Assert.False(Scope(document, "Agente «juez»").IsChecked);
        Assert.True(Scope(document, "Global").IsChecked);
    }

    [Fact]
    public async Task A_global_decision_names_no_project()
    {
        var (world, document) = await OpenAsync();
        Find(document, "wpf-mvvm").IsSelected = true;

        await Scope(document, "Global").ToggleCommand.ExecuteAsync(null);

        Assert.Equal(new SkillActivation(new SkillId("wpf-mvvm"), SkillScopeKind.Global, null, true), Assert.Single(world.Activations.Appended).Activation);
    }

    [Fact]
    public async Task A_linked_skill_cannot_be_activated_and_the_card_says_why()
    {
        var (world, document) = await OpenAsync();

        Find(document, "design-tokens").IsSelected = true;

        Assert.True(document.Detail!.IsLinked);
        Assert.All(document.Detail.Scopes, s => Assert.False(s.CanToggle));
        Assert.Contains(document.Detail.Warnings, w => w.Contains("enlazada", StringComparison.Ordinal) && w.Contains("agente", StringComparison.Ordinal));
        await Scope(document, "Proyecto").ToggleCommand.ExecuteAsync(null);
        Assert.Empty(world.Activations.Appended);
    }

    [Fact]
    public async Task The_cost_of_each_agent_adds_the_skills_it_really_gets()
    {
        var world = new SkillsWorld();
        world.Activations.Decisions.Add(new SkillActivation(new SkillId("spark-optimization"), SkillScopeKind.Project, ProjectId, true));
        world.Activations.Decisions.Add(new SkillActivation(new SkillId("wpf-mvvm"), SkillScopeKind.Agent, "ejecutor", true));
        world.Activations.Decisions.Add(new SkillActivation(new SkillId("design-tokens"), SkillScopeKind.Agent, "ejecutor", true));
        var (_, document) = await OpenAsync(world);

        var ejecutor = Assert.Single(document.AgentCosts, c => c.Agent == "ejecutor");
        var orquestador = Assert.Single(document.AgentCosts, c => c.Agent == "orquestador");

        // The linked one is active but never reaches the agent, so it does not count.
        Assert.Equal(2, ejecutor.Skills);
        Assert.Equal(SkillContextCost.Total([Catalog[0].Entry, Catalog[2].Entry]), ejecutor.Tokens);
        Assert.Equal(MetricOrigin.EstimatedBySystem, ejecutor.Tokens.Origin);
        Assert.Contains("estimado", ejecutor.Text, StringComparison.Ordinal);
        Assert.Equal(1, orquestador.Skills);
    }

    // ---- what is missing, said instead of pretended ----

    [Fact]
    public async Task Without_a_project_the_catalog_is_shown_but_nothing_can_be_activated()
    {
        var world = new SkillsWorld();
        var document = world.Document(projectRoot: null);

        await document.OpenAsync(Ct);
        Find(document, "spark-optimization").IsSelected = true;

        Assert.Equal(5, document.Sectors.Sum(s => s.Count));
        Assert.False(document.CanActivate);
        Assert.All(document.Detail!.Scopes, s => Assert.False(s.CanToggle));
        Assert.Contains(document.Notices, n => n.Contains("Abre un proyecto", StringComparison.Ordinal));
        Assert.Empty(world.Workspace.Opened);
    }

    [Fact]
    public async Task Missing_services_are_listed_and_nothing_is_made_up()
    {
        var world = new SkillsWorld();
        var document = world.Document(world.Backend(catalog: false, activations: false, recommendations: false));

        await document.OpenAsync(Ct);

        Assert.Empty(document.Sectors);
        Assert.Contains(document.Notices, n => n.Contains("catálogo", StringComparison.Ordinal));
        Assert.Contains(document.Notices, n => n.Contains("activaciones", StringComparison.Ordinal));
        Assert.Contains(document.Notices, n => n.Contains("recomendación asistida", StringComparison.Ordinal));
        Assert.False(document.CanActivate);
    }

    [Fact]
    public async Task The_catalog_warnings_and_the_codex_notice_are_shown()
    {
        var world = new SkillsWorld();
        world.CatalogWarnings.Add("«design-tokens» está enlazada: el índice no guarda una copia local y no se puede dar a un agente.");
        var (_, document) = await OpenAsync(world);

        // One warning of the index about an entry is said as it is, with no detail to open.
        Assert.Equal([world.CatalogWarnings[0]], document.CatalogWarnings);
        Assert.Equal(world.CatalogWarnings[0], document.CatalogWarningsSummary);
        Assert.False(document.HasCatalogWarningDetail);
        Assert.DoesNotContain(world.CatalogWarnings[0], document.Notices);
        Assert.Contains(document.Notices, n => n.Contains("Codex", StringComparison.Ordinal) && n.Contains("globales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dozens_of_warnings_of_the_index_are_one_line_with_their_count_and_their_detail_on_demand()
    {
        var world = new SkillsWorld();
        for (var i = 1; i <= 27; i++)
        {
            var name = $"enlazada-{i:00}";
            world.Catalog.Add(Skill(name, "Una skill enlazada.", SkillSectors.IngenieriaSoftware, ["enlazada"], linked: true));
            world.CatalogWarnings.Add($"«{name}» está enlazada: el índice no guarda una copia local y no se puede dar a un agente.");
        }

        // design-tokens of the samples is linked too; plus an entry left out by the importer and a warning about the whole catalog.
        world.CatalogWarnings.Add("«design-tokens» está enlazada: el índice no guarda una copia local y no se puede dar a un agente.");
        world.CatalogWarnings.Add("Entrada #31 omitida: sin nombre.");
        world.CatalogWarnings.Add("El catálogo no contiene la lista 'skills'.");
        var (_, document) = await OpenAsync(world);

        Assert.Equal(29, document.CatalogWarnings.Count);
        Assert.Equal("28 skills enlazadas no se pueden dar a un agente · 1 aviso más del índice.", document.CatalogWarningsSummary);
        Assert.True(document.HasCatalogWarningDetail);
        Assert.False(document.ShowCatalogWarnings);
        Assert.Equal("Ver el detalle (29)", document.CatalogWarningsToggleText);

        // The warning about the whole catalog stays among the notices, in full; none of the others does.
        Assert.Contains("El catálogo no contiene la lista 'skills'.", document.Notices);
        Assert.DoesNotContain(document.Notices, n => n.StartsWith('«') || n.StartsWith("Entrada #", StringComparison.Ordinal));

        document.ShowCatalogWarnings = true;
        Assert.Equal("Ocultar el detalle", document.CatalogWarningsToggleText);
    }

    [Theory]
    [InlineData(0, 0, 0, null)]
    [InlineData(2, 2, 0, "2 skills enlazadas no se pueden dar a un agente.")]
    [InlineData(2, 1, 0, "1 skill enlazada no se puede dar a un agente · 1 aviso más del índice.")]
    [InlineData(3, 0, 1, "1 skill sin copia local válida no se puede dar a un agente · 2 avisos más del índice.")]
    [InlineData(30, 27, 2, "27 skills enlazadas y 2 sin copia local válida no se pueden dar a un agente · 1 aviso más del índice.")]
    [InlineData(4, 0, 0, "4 avisos del índice de skills.")]
    public void The_summary_of_the_warnings_of_the_index_counts_what_cannot_be_given_to_an_agent(int warnings, int linked, int withoutCopy, string? summary)
    {
        var lines = Enumerable.Range(1, warnings).Select(i => $"«skill-{i}» …").ToList();

        Assert.Equal(summary, SkillsDocumentViewModel.SummarizeCatalogWarnings(lines, linked, withoutCopy));
    }

    [Fact]
    public void A_single_warning_of_the_index_is_its_own_summary()
    {
        Assert.Equal("«x»: etiqueta inválida descartada 'Ñ'.", SkillsDocumentViewModel.SummarizeCatalogWarnings(["«x»: etiqueta inválida descartada 'Ñ'."], 0, 0));
    }

    [Fact]
    public async Task Refreshing_reads_the_catalog_again()
    {
        var (world, document) = await OpenAsync();
        world.Catalog.RemoveAt(0);

        await document.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, world.CatalogReads);
        Assert.Equal(4, document.Sectors.Sum(s => s.Count));
    }

    // ---- the deterministic recommendation ----

    [Fact]
    public async Task The_stack_comes_from_the_file_names_and_drives_the_deterministic_recommendation()
    {
        var (world, document) = await OpenAsync();

        Assert.Equal(["csharp", "dotnet"], document.Stack);
        Assert.Equal("#csharp #dotnet", document.StackText);

        document.TaskText = "Una pantalla nueva";
        document.RecommendCommand.Execute(null);

        var query = Assert.Single(world.Recommendations.Queries);
        Assert.Equal("Una pantalla nueva", query.Text);
        Assert.Equal(["csharp", "dotnet"], query.ContextTags!.Order(StringComparer.Ordinal));
        var first = document.Recommendations.First();
        Assert.Equal("wpf-mvvm", first.SkillId);
        Assert.Contains("coincide con #dotnet", first.Detail, StringComparison.Ordinal);
        Assert.Empty(world.Activations.Appended);
    }

    [Fact]
    public async Task Showing_a_recommended_skill_selects_it_in_the_tree()
    {
        var (_, document) = await OpenAsync();
        document.TaskText = "optimizar spark";
        document.RecommendCommand.Execute(null);

        document.Recommendations.Single(r => r.SkillId == "spark-optimization").ShowCommand.Execute(null);

        Assert.True(Find(document, "spark-optimization").IsSelected);
        Assert.Equal("spark-optimization", document.Detail!.Name);
    }

    // ---- the assisted recommendation: off by default, an OK for every use, never activates ----

    [Fact]
    public async Task The_assisted_recommendation_is_off_by_default()
    {
        var (_, document) = await OpenAsync();
        document.TaskText = "Optimizar Spark";

        Assert.False(document.IsAssistedAllowed);
        Assert.False(document.AskAgentCommand.CanExecute(null));

        document.IsAssistedAllowed = true;
        Assert.True(document.AskAgentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Each_use_asks_for_the_OK_and_a_no_launches_nothing()
    {
        var (world, document) = await OpenAsync();
        document.TaskText = "Optimizar Spark";
        document.IsAssistedAllowed = true;
        world.Prompt.Approve = false;

        await document.AskAgentCommand.ExecuteAsync(null);

        var question = Assert.Single(world.Prompt.Asked);
        Assert.Contains("límite de uso", question.Description, StringComparison.Ordinal);
        Assert.Contains("haiku", question.Description, StringComparison.Ordinal);
        Assert.Equal(ProjectId, question.ProjectId);
        Assert.Empty(world.Recommendations.Asked);
        Assert.Contains("no diste el OK", document.AssistedStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_OK_the_agent_is_asked_with_that_consent_and_its_proposals_are_shown_without_activating_anything()
    {
        var (world, document) = await OpenAsync();
        world.Recommendations.Answer = new AssistedRecommendationResult(
            [
                new AssistedRecommendation(new SkillId("spark-optimization"), SkillScopeKind.Project, null, "El proyecto tiene trabajos de Spark."),
                new AssistedRecommendation(new SkillId("test-driven-development"), SkillScopeKind.Agent, new AgentStudio.Domain.Common.AgentId("ejecutor"), "Que pruebe primero."),
            ],
            ["Se descarta «no-existe»: no está en el catálogo."],
            new AgentStudio.Domain.Orchestration.RunId("aux-20260101-abcdef"));
        document.TaskText = "Optimizar Spark";
        document.IsAssistedAllowed = true;

        await document.AskAgentCommand.ExecuteAsync(null);

        var (project, task, consent) = Assert.Single(world.Recommendations.Asked);
        var question = Assert.Single(world.Prompt.Asked);
        Assert.Equal(ProjectId, project.ProjectId);
        Assert.StartsWith("Optimizar Spark", task, StringComparison.Ordinal);
        Assert.Contains("#csharp #dotnet", task, StringComparison.Ordinal);
        Assert.Equal(("user", question.Description), (consent.DecidedBy, consent.Description));
        Assert.Equal(["spark-optimization", "test-driven-development"], document.AssistedRecommendations.Select(r => r.SkillId));
        Assert.Contains("Proyecto", document.AssistedRecommendations[0].Detail, StringComparison.Ordinal);
        Assert.Contains("Razón del agente: El proyecto tiene trabajos de Spark.", document.AssistedRecommendations[0].Detail, StringComparison.Ordinal);
        Assert.Contains("«ejecutor»", document.AssistedRecommendations[1].Detail, StringComparison.Ordinal);
        Assert.Equal(["Se descarta «no-existe»: no está en el catálogo."], document.AssistedWarnings);
        Assert.Contains("aux-20260101-abcdef", document.AssistedStatus, StringComparison.Ordinal);
        Assert.Contains("Nada se ha activado", document.AssistedStatus, StringComparison.Ordinal);
        Assert.Empty(world.Activations.Appended);
    }

    /// <summary>
    /// F13-20 (docs/17 §0.2 and D-39): the consent of the assisted recommendation goes through the approval gate, so the log
    /// shows the question and the answer, as every approval. It grants nothing (D-37: never), so it stays the dialog's alone
    /// and its answer is written as version 1, byte for byte as any answer of the dialog.
    /// </summary>
    [Theory]
    [InlineData(true, DecisionKind.ApprovalGranted)]
    [InlineData(false, DecisionKind.ApprovalDenied)]
    public async Task The_consent_of_the_assisted_recommendation_leaves_its_question_and_its_answer_in_the_log(bool approve, DecisionKind answered)
    {
        var (world, document) = await OpenAsync();
        document.TaskText = "Optimizar Spark";
        document.IsAssistedAllowed = true;
        world.Prompt.Approve = approve;

        await document.AskAgentCommand.ExecuteAsync(null);

        var log = world.Log.Of(ProjectId);
        Assert.Equal([DecisionKind.ApprovalRequested, answered], log.Select(e => e.Kind));
        var asked = DecisionPayloadSerializer.Deserialize<ApprovalRequestedPayload>(log[0].PayloadJson);
        Assert.Equal((SkillsDocumentViewModel.AssistedAction, SkillsDocumentViewModel.AssistedDescription), (asked.Action, asked.Description));
        var answer = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(log[1].PayloadJson);
        Assert.Equal((SkillsDocumentViewModel.AssistedAction, approve, "user"), (answer.Action, answer.Approved, answer.DecidedBy));
        Assert.Equal(new Actor(ActorKind.User, "user"), log[1].Actor);
        Assert.DoesNotContain("verification", log[1].PayloadJson, StringComparison.Ordinal);
        Assert.Equal(approve ? 1 : 0, world.Recommendations.Asked.Count);
    }

    [Fact]
    public async Task A_second_use_asks_for_the_OK_again()
    {
        var (world, document) = await OpenAsync();
        document.TaskText = "Optimizar Spark";
        document.IsAssistedAllowed = true;

        await document.AskAgentCommand.ExecuteAsync(null);
        await document.AskAgentCommand.ExecuteAsync(null);

        Assert.Equal(2, world.Prompt.Asked.Count);
        Assert.Equal(2, world.Recommendations.Asked.Count);
        Assert.NotEqual(world.Recommendations.Asked[0].Consent, world.Recommendations.Asked[1].Consent);
    }

    [Fact]
    public async Task Without_the_service_the_prompt_or_a_project_the_agent_cannot_be_asked()
    {
        var world = new SkillsWorld();
        foreach (var document in new[]
        {
            world.Document(world.Backend(recommendations: false)),
            world.Document(world.Backend(prompt: false)),
            world.Document(projectRoot: null),
        })
        {
            await document.OpenAsync(Ct);
            document.TaskText = "Optimizar Spark";
            document.IsAssistedAllowed = true;

            Assert.False(document.AskAgentCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task A_blank_task_cannot_be_asked_about()
    {
        var (_, document) = await OpenAsync();
        document.IsAssistedAllowed = true;
        document.TaskText = "   ";

        Assert.False(document.AskAgentCommand.CanExecute(null));
        Assert.False(document.RecommendCommand.CanExecute(null));
    }

    /// <summary>
    /// D-48 and F14-4: what goes to the decision log (the description of the consent) and to the agent (the context added to the
    /// task) is fixed text of the code, whatever the language of the interface. It is built here and compared with its literal, it
    /// is a constant, and neither it nor any piece of it is in the resources of Spanish or English, so no language can change it.
    /// </summary>
    [Fact]
    public async Task What_goes_to_the_log_and_to_the_agent_is_fixed_text_that_does_not_follow_the_language_of_the_interface()
    {
        var (world, document) = await OpenAsync();
        document.TaskText = "Optimizar Spark";
        document.IsAssistedAllowed = true;
        world.Prompt.Approve = true;

        await document.AskAgentCommand.ExecuteAsync(null);

        const string StackContext = "Contexto del proyecto (detectado por los nombres de sus ficheros): ";
        var expected = document.Stack.Count == 0 ? "Optimizar Spark" : "Optimizar Spark\n\n" + StackContext + string.Join(' ', document.Stack.Select(tag => "#" + tag));
        Assert.Equal(expected, Assert.Single(world.Recommendations.Asked).Task);
        Assert.NotEmpty(document.Stack);
        Assert.Equal("Pedir a un agente que recomiende skills para esta tarea. Se lanza Claude Code con el modelo haiku, de solo lectura, sin red y en una carpeta vacía, con el catálogo local de skills como única herramienta extra. Gasta un poco del límite de uso de tu plan (una ejecución corta). Solo propone: activar sigue siendo cosa tuya.", SkillsDocumentViewModel.AssistedDescription);
        Assert.True(typeof(SkillsDocumentViewModel).GetField(nameof(SkillsDocumentViewModel.AssistedDescription))!.IsLiteral);

        foreach (var neutralOrSatellite in AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport.ResourceFiles().Values
            .SelectMany(f => f.Satellites.Values.Append(f.Neutral)))
        {
            foreach (var value in AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport.Read(neutralOrSatellite).Values)
            {
                Assert.DoesNotContain("Contexto del proyecto (detectado", value, StringComparison.Ordinal);
                Assert.DoesNotContain(SkillsDocumentViewModel.AssistedDescription, value, StringComparison.Ordinal);
                Assert.DoesNotContain("Project context (detected", value, StringComparison.Ordinal);
            }
        }
    }
}
