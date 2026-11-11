using AgentStudio.Application.Coordination;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;
using static AgentStudio.Tests.Desktop.Background.PanelSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-8 and docs/16 D-15: the panel shows, project by project, the phase, the agents with their provider and model, the
/// tasks, the tokens with their origin, the budget, the context in bars, the quota with its reset and its age, the blocks,
/// the errors and what waits for the person, all from the ports that already exist (here, doubles), and «sin dato» or «no
/// expuesto» for what is not known. Locked, it hides it all.
/// </summary>
public sealed class StatusPanelViewModelTests
{
    [Fact]
    public async Task A_run_shows_its_phase_agents_tasks_context_tokens_budget_blocks_and_approvals()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);
        var panel = Panel(work, clock);

        await panel.RefreshAsync();

        var project = Assert.Single(panel.Projects);
        Assert.Equal(("monorepo", RootA), (project.Name, project.Root));
        var card = Assert.Single(project.Work);
        Assert.Equal("Ejecución 20261021-c4a1f2", card.Title);
        Assert.Equal("En ejecución · 4 intentos en curso", card.State);
        Assert.Equal(Tone.Active, card.Tone);
        Assert.Equal(
            [
                "orquestador · Orquestador · Claude Code · modelo claude-opus-5-5",
                "ejecutor · Ejecutor · Claude Code · modelo predeterminado del programa del agente (no expuesto) · informado por el programa del agente: claude-sonnet-5-5 · en marcha",
                "ejecutor-2 · Ejecutor · Claude Code · modelo claude-sonnet-5-5 · en marcha",
                "juez-revision · Juez · Codex CLI · modelo predeterminado del programa del agente (no expuesto) · en marcha",
            ],
            card.Agents);
        Assert.Equal("Tareas: 4 en curso · 1 espera aprobación · 1 bloqueada · 1 completada", card.Tasks);
        Assert.Equal("Presupuesto: 5 de 12 ejecuciones de agente", card.Budget);
        Assert.Equal("5 de 12", card.BudgetBar!.ValueText);
        Assert.Equal(5d / 12, card.BudgetBar!.Fraction!.Value, 6);
        Assert.Equal(["T3 · Esperando cuota hasta las 14:05"], card.Blocks);
        Assert.Equal(["T5 espera tu aprobación: delivery.ff"], card.Approvals);
        Assert.Empty(card.Errors);
    }

    [Fact]
    public async Task The_context_of_each_task_in_course_is_the_line_of_the_monitor_with_a_bar_only_when_there_is_a_percentage()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);

        var card = Assert.Single(Assert.Single(Panel(work, clock).Projects).Work);

        Assert.Equal(["T1 Implementar Calc.Multiply", "T2 Revisión independiente", "T6 Refactorizar el parser", "T7 Añadir pruebas"], card.Context.Select(c => c.Task));
        foreach (var bar in card.Context.Take(3))
        {
            // The same line the monitor shows, word for word: nothing is computed apart.
            var task = monitor.Tasks.Single(t => bar.Task.StartsWith(t.Id.Value + " ", StringComparison.Ordinal));
            Assert.Equal(task.Context, "Contexto: " + bar.Text);
            Assert.Equal(task.ContextTone, bar.Tone);
        }

        Assert.Equal([0.47, null, 0.9, null], card.Context.Select(c => c.Fraction is { } f ? Math.Round(f, 2) : (double?)null));
        Assert.Equal([true, false, true, false], card.Context.Select(c => c.HasBar));
        Assert.Equal(47, card.Context[0].Percent, 1);
        Assert.Contains("exacto · informado por el CLI", card.Context[0].Text, StringComparison.Ordinal);
        Assert.Equal("no expuesto", card.Context[1].Text);
        Assert.Contains("estimado por Agent Studio", card.Context[2].Text, StringComparison.Ordinal);
        Assert.Equal(Tone.Warning, card.Context[2].Tone);
        Assert.Equal(new ContextBarRow("T7 Añadir pruebas", null, "sin observaciones todavía", Tone.Neutral) { Bar = card.Context[3].Bar }, card.Context[3]);

        // The bars are the monitor's own (docs/18 P3): an empty track that says why when there is no percentage.
        Assert.Equal(card.Context.Select(c => monitor.Tasks.Single(t => c.Task.StartsWith(t.Id.Value + " ", StringComparison.Ordinal)).ContextBar), card.Context.Select(c => c.Bar));
        Assert.Equal(["no expuesto", "sin observaciones todavía"], new[] { card.Context[1].Bar!.ValueText, card.Context[3].Bar!.ValueText });
        Assert.True(card.Context[2].Bar!.IsEstimate);

        // Before the header of the run is read, the budget says the runs used and that its maximum is not known.
        Assert.False(card.BudgetBar!.HasValue);
        Assert.Equal("5 ejecuciones de agente usadas · máximo sin dato", card.BudgetBar!.Text);
    }

    [Fact]
    public async Task The_tokens_are_those_of_the_finished_attempts_of_the_run_each_with_its_origin()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);
        var usage = new FakeUsageReport(() => Usage);
        var panel = new StatusPanelViewModel(work, clock, new StatusPanelSources(usage, new FakeRunRegistry(Header)));

        await panel.RefreshAsync();

        var card = Assert.Single(Assert.Single(panel.Projects).Work);
        var totals = UsageSamples.ClaudeTotals;
        Assert.Equal(
            [
                ("Entrada", MetricText.Tokens(totals.InputTokens).Text),
                ("Caché leída", MetricText.Tokens(totals.CacheReadInputTokens).Text),
                ("Caché escrita", MetricText.Tokens(totals.CacheWriteInputTokens).Text),
                ("Salida", MetricText.Tokens(totals.OutputTokens).Text),
                ("Razonamiento", "no expuesto"),
                ("Coste", MetricText.Cost(totals.CostUsd).Text),
            ],
            card.Tokens.Select(t => (t.Name, t.Text)));
        Assert.Contains("estimado por el CLI", card.Tokens[^1].Text, StringComparison.Ordinal);
        Assert.Equal("De 4 intentos terminados de esta ejecución; los intentos en curso cuentan al terminar.", card.TokensNote);

        // The usage of this run only, of its project, since its header was created.
        var (roots, query) = Assert.Single(usage.Queries);
        Assert.Equal([RootA], roots);
        Assert.Equal([Run], query.Filter!.Runs);
        Assert.Empty(query.GroupBy);
        Assert.Equal(Header.CreatedAt, query.From);
    }

    [Fact]
    public async Task What_is_not_composed_says_sin_dato_and_nothing_is_made_up()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);
        var panel = new StatusPanelViewModel(work, clock);

        await panel.RefreshAsync();

        var card = Assert.Single(Assert.Single(panel.Projects).Work);
        Assert.Equal(["Agentes: sin dato (no se pudieron leer los datos con los que empezó la ejecución)"], card.Agents);
        Assert.Empty(card.Tokens);
        Assert.Equal("Tokens: sin dato (el consumo no está disponible aquí).", card.TokensNote);
        Assert.Equal("Presupuesto: 5 ejecuciones de agente usadas · máximo sin dato (faltan los datos con los que empezó la ejecución)", card.Budget);
        Assert.Empty(panel.Quota);
        Assert.Equal("Límite de uso: sin dato (no está disponible aquí).", panel.QuotaNote);
    }

    [Fact]
    public async Task A_log_the_usage_leaves_out_says_why_and_a_run_without_finished_attempts_says_so()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);
        var usage = new FakeUsageReport(() => UsageSamples.Empty with { Excluded = [new ExcludedProject(RootA, "p-a", "El registro no verifica desde la entrada 12")] });
        var panel = new StatusPanelViewModel(work, clock, new StatusPanelSources(usage));

        await panel.RefreshAsync();
        Assert.Equal("Tokens: sin dato (El registro no verifica desde la entrada 12).", Assert.Single(Assert.Single(panel.Projects).Work).TokensNote);

        usage.Report = () => UsageSamples.Empty;
        await panel.RefreshAsync();
        var card = Assert.Single(Assert.Single(panel.Projects).Work);
        Assert.Empty(card.Tokens);
        Assert.Equal("Tokens: ningún intento de esta ejecución ha terminado todavía.", card.TokensNote);
    }

    [Fact]
    public async Task The_quota_of_the_account_shows_each_window_with_its_reset_and_its_age()
    {
        var clock = Clock();
        using var work = Registry();
        var panel = new StatusPanelViewModel(work, clock, new StatusPanelSources(Quota: new QuotaViewModel(Quota(), null, clock)));

        await panel.RefreshAsync();

        Assert.Equal(["Claude Code", "Claude Code", "Codex CLI"], panel.Quota.Select(q => q.Provider));
        Assert.StartsWith("5 horas · ", panel.Quota[0].Text, StringComparison.Ordinal);
        Assert.Contains("exacto · informado por el CLI", panel.Quota[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("se reinicia hoy a las 16:05 · vista hace 12 min", panel.Quota[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("7 días · ", panel.Quota[1].Text, StringComparison.Ordinal);
        Assert.Contains("se reinicia el 24/10 a las 14:00", panel.Quota[1].Text, StringComparison.Ordinal);
        Assert.StartsWith("Desactivado: activa «Leer la cuota de Codex»", panel.Quota[2].Text, StringComparison.Ordinal);
        Assert.Equal("Límite de uso de tu plan (no del proyecto), tal como lo anuncia cada empresa del modelo.", panel.QuotaNote);
        Assert.Equal("Actualizado a las 14:00:00", panel.Updated);
    }

    [Fact]
    public async Task A_debate_shows_its_participants_tokens_budget_and_what_it_waits_for()
    {
        var clock = Clock();
        using var work = Registry();
        var awaiting = new DebateAwaiting(DebateAwaitReason.HumanResolution, "El jurado discrepa.");
        var debate = new FakeDebateRun(DebateSamples.Snapshot(turnsInFlight: 0, awaiting: awaiting));
        work.TrackDebate(RootC, ProjectC, debate);

        var card = Assert.Single(Assert.Single(new StatusPanelViewModel(work, clock).Projects).Work);

        Assert.Equal("Debate deb-20261019-a1b2c3", card.Title);
        Assert.Equal("En deliberación · ronda 2", card.State);
        Assert.Equal(DebateSamples.Snapshot().Participants.Count, card.Agents.Count);
        Assert.All(card.Agents, a => Assert.Contains(" · ", a, StringComparison.Ordinal));
        Assert.Equal([("Tokens", MetricText.Tokens(DebateSamples.Snapshot().Tokens).Text)], card.Tokens.Select(t => (t.Name, t.Text)));
        Assert.Equal("Presupuesto: 3 de 10 ejecuciones de agente", card.Budget);
        Assert.Equal([DebateTexts.Await(DebateAwaitReason.HumanResolution)], card.Approvals);
        Assert.Equal("Su detalle está en «Debates».", card.Detail);
    }

    [Fact]
    public void An_audit_that_no_screen_follows_here_says_only_what_its_activity_says()
    {
        var activity = new FakeProjectActivity { Holding = { ["p-c"] = ProjectActivityKind.Audit } };
        using var work = Registry(activity);
        work.TrackProject(RootC, () => ProjectC);

        var card = Assert.Single(Assert.Single(new StatusPanelViewModel(work, Clock()).Projects).Work);

        Assert.Equal(("Auditoría del proyecto", "En curso (aquí no hay más detalle)", "Su detalle está en «Auditoría»."), (card.Title, card.State, card.Detail));
        Assert.False(card.HasAgents || card.HasTokens || card.HasContext);
    }

    [Fact]
    public async Task Locked_the_panel_hides_what_goes_on_and_only_counts_it()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);
        var panel = Panel(work, clock);

        panel.IsLocked = true;

        Assert.False(panel.ShowsWork);
        Assert.False(panel.ShowsNothing);
        Assert.Equal("Agent Studio está bloqueado. El trabajo sigue: 1 trabajo en marcha. 1 espera tu decisión. Desbloquéalo para ver el detalle.", panel.LockedText);
        Assert.DoesNotContain("monorepo", panel.LockedText, StringComparison.Ordinal);

        panel.IsLocked = false;
        Assert.True(panel.ShowsWork);
    }

    [Fact]
    public async Task The_panel_follows_the_work_as_it_changes_and_says_when_nothing_goes_on()
    {
        var clock = Clock();
        var (run, monitor) = await FollowAsync(clock);
        using var work = Registry();
        work.TrackRun(RootA, monitor, () => ProjectA);
        var panel = Panel(work, clock);
        Assert.True(panel.ShowsWork);
        Assert.Equal("1 ejecución · 1 en espera por el límite de uso hasta las 14:05 · 1 espera tu decisión", panel.Summary);

        // The monitor applies the end on a thread of its own here (in the app, the UI's): the panel is rebuilt there, so
        // the test waits for a rebuild that saw no work, not for an empty list a rebuild in course may still refill.
        run.Finish(new RunResult(new RunId(Run), RunPhase.Succeeded, FakeRun.Healthy, null, null, "Terminada."));
        await WaitUntilAsync(() => panel.ShowsNothing && panel.Projects.Count == 0, "The panel did not see the run end.");

        Assert.Equal("sin trabajo en marcha", panel.Summary);
    }

    [Fact]
    public void Opening_the_app_from_the_panel_asks_the_shell_and_without_it_is_not_offered()
    {
        using var work = Registry();
        var opened = 0;

        var panel = new StatusPanelViewModel(work, Clock(), openApp: () => opened++);
        panel.OpenAppCommand.Execute(null);

        Assert.Equal(1, opened);
        Assert.False(new StatusPanelViewModel(work, Clock()).OpenAppCommand.CanExecute(null));
    }
}
