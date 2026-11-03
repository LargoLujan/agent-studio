using System.Globalization;
using System.Text;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>One block of the demonstrations: the test that runs it, what it needs first, and what it is expected to spend («estimado por Agent Studio»).</summary>
internal sealed record DemoPlanEntry(
    string Block,
    string Title,
    string Class,
    string Method,
    string Requires,
    string Runs,
    string Claude,
    string Codex,
    string Duration,
    bool SpendsQuota);

/// <summary>
/// The order the lead runs the demonstrations in, each by itself with the exact command, and its estimate. The figures are
/// «estimado por Agent Studio» from what was measured (docs/15 §3 and D-21, docs/evidencias/fase3-e2e.md and the
/// measurements of WP4): a turn of Claude with haiku of 40 000 to 160 000 tokens and 0,05 to 0,10 USD at list price, one
/// of Codex of 20 000 to 80 000 tokens, a judge of a debate of 50 000 to 150 000, and the judge of the audit of 150 000 to
/// 500 000 (decision 4). With a subscription the cost of Claude is not what is paid; Codex gives no cost in money.
/// </summary>
internal static class DemoPlan
{
    public const string Preparation = "preparacion";
    public const string Measurement = "medicion-integridad-baja";
    public const string SubjectRun = "ejecucion-objeto";
    public const string Challenge = "impugnacion";
    public const string Audit = "auditoria-proyecto";

    public const string Project = "tests/AgentStudio.Tests/AgentStudio.Tests.csproj";

    public static IReadOnlyList<DemoPlanEntry> Entries { get; } =
    [
        new(Preparation, "Preparación sin cuota", nameof(Phase5DemoPreparationTests), nameof(Phase5DemoPreparationTests.Prepares_every_demonstration_without_spending_quota),
            "—", "0", "0", "0", "1 a 3 min (clona el repositorio la primera vez)", false),
        new(Measurement, "Suite de Agent Studio a integridad Low", nameof(Phase5AuditDemoLiveTests), nameof(Phase5AuditDemoLiveTests.Agent_Studio_suite_at_low_integrity_without_quota),
            "—", "0", "0", "0", "20 a 60 min (build y suite completos a integridad Low, NuGet en frío)", false),
        new(SubjectRun, "Ejecución objeto de los modos de revisión", nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Subject_run_where_Claude_plans_and_executes_and_Codex_reviews),
            "—", "3 (7 como mucho, como F3-8)", "2 ejecuciones · 0,10 a 0,30 USD", "1 ejecución · 50 000 a 150 000", "3 a 10 min", true),
        new(DebateDemo.Free.Block, DebateDemo.Free.Title, nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Free_debate_judged_by_a_cross_vendor_panel),
            "—", "6 (10 como mucho)", "3 ejecuciones · 0,15 a 0,40 USD", "3 ejecuciones · 100 000 a 300 000", "10 a 25 min", true),
        new(DebateDemo.Adversarial.Block, DebateDemo.Adversarial.Title, nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Adversarial_debate_judged_by_a_cross_vendor_panel),
            "—", "6 (10 como mucho)", "3 ejecuciones · 0,15 a 0,40 USD", "3 ejecuciones · 100 000 a 300 000", "10 a 25 min", true),
        new(DebateDemo.Architecture.Block, DebateDemo.Architecture.Title, nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Architecture_review_judged_by_a_cross_vendor_panel),
            "—", "6 (10 como mucho)", "3 ejecuciones · 0,15 a 0,40 USD", "3 ejecuciones · 100 000 a 300 000", "10 a 25 min", true),
        new(DebateDemo.Tribunal.Block, DebateDemo.Tribunal.Title, nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Tribunal_judged_by_a_cross_vendor_panel),
            "—", "4 (8 como mucho)", "2 ejecuciones · 0,10 a 0,25 USD", "2 ejecuciones · 70 000 a 200 000", "6 a 15 min", true),
        new(DebateDemo.Implementation.Block, DebateDemo.Implementation.Title, nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Implementation_audit_of_the_subject_run_judged_by_Codex),
            SubjectRun, "5 (8 como mucho), más «Ejecutar pruebas» sin cuota", "2 ejecuciones · 0,10 a 0,25 USD", "3 ejecuciones · 120 000 a 350 000", "10 a 25 min", true),
        new(DebateDemo.Orchestrator.Block, DebateDemo.Orchestrator.Title, nameof(Phase5DebateDemoLiveTests), nameof(Phase5DebateDemoLiveTests.Orchestrator_review_of_the_subject_run_judged_by_Codex),
            SubjectRun, "5 (8 como mucho)", "2 ejecuciones · 0,10 a 0,25 USD", "3 ejecuciones · 120 000 a 350 000", "10 a 25 min", true),
        new(Challenge, "Impugnación de un veredicto de las demostraciones", nameof(Phase5ChallengeDemoLiveTests), nameof(Phase5ChallengeDemoLiveTests.Challenge_of_a_demo_verdict_opens_a_revision_debate_with_another_judge),
            DebateDemo.Implementation.Block, "5 (8 como mucho)", "2 ejecuciones · 0,10 a 0,25 USD", "3 ejecuciones · 100 000 a 300 000 (juez con otro modelo)", "10 a 25 min", true),
        new(Audit, "Auditar proyecto completo: el propio Agent Studio", nameof(Phase5AuditDemoLiveTests), nameof(Phase5AuditDemoLiveTests.Whole_project_audit_of_Agent_Studio_judged_by_Codex),
            Measurement, "1 (2 con la corrección)", "0", "1 ejecución · 150 000 a 500 000 (hasta el doble con la corrección)", "30 a 75 min (verificación de HEAD sin cuota y el juez, 5 a 15 min)", true),
    ];

    /// <summary>The totals of the blocks that spend quota, in their normal case.</summary>
    public const string Totals = "41 ejecuciones de agente en el caso normal (71 como mucho): Claude 19, entre 0,95 y 2,50 USD a precio de lista; Codex 22, entre 0,9 y 2,8 millones de tokens. Con las 3 mediciones de WP4 ya hechas, unas 45.";

    public static string Command(DemoPlanEntry entry) =>
        $"dotnet test --project {Project} -- --filter-method \"AgentStudio.Tests.Integration.Phase5.Live.{entry.Class}.{entry.Method}\"";

    /// <summary>The PowerShell lines that set the switches before a command.</summary>
    public static string Switches(bool spendsQuota) => spendsQuota
        ? "$env:AGENTSTUDIO_PROVIDER_TESTS = '1'; $env:AGENTSTUDIO_PHASE5_DEMOS = '1'; $env:AGENTSTUDIO_CAPTURE_DIR = '<carpeta de capturas>'"
        : "$env:AGENTSTUDIO_PHASE5_DEMOS = '1'; $env:AGENTSTUDIO_CAPTURE_DIR = '<carpeta de capturas>'";

    public static string ComposeCommand =>
        $"dotnet test --project {Project} -- --filter-method \"AgentStudio.Tests.Integration.Phase5.Live.{nameof(Phase5DemoReportTests)}.{nameof(Phase5DemoReportTests.Writes_the_evidence_document_from_the_captures)}\"";
}

/// <summary>
/// Composes <c>docs/evidencias/fase5-demostraciones.md</c> from the captures of the demonstrations: per mode, the question,
/// the participants and the judges, the rounds and the limits, the result and the verdicts with the rule of the panel, the
/// issues of the validator, and the cost and the tokens with their origin; and how each block is run, with its estimate.
/// It reads only the captures (already without the user's profile folder) and writes Spanish.
/// </summary>
internal static class DemoReport
{
    public const string FileName = "fase5-demostraciones.md";

    private static readonly CultureInfo Spanish = MetricText.Spanish;

    public static string Compose(IReadOnlyList<DemoCapture> captures, IReadOnlyList<string> unreadable, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(unreadable);
        var byBlock = captures.GroupBy(c => c.Block, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(c => c.FinishedAt).Last(), StringComparer.Ordinal);
        var md = new StringBuilder();
        md.AppendLine("# Fase 5 · Demostraciones reales de la Debate Arena y el juez");
        md.AppendLine();
        md.AppendLine(Invariant($"> Generado por `{nameof(DemoReport)}` el {generatedAt:yyyy-MM-dd HH:mm} UTC a partir de {byBlock.Count} capturas de `AGENTSTUDIO_CAPTURE_DIR\\fase5`. Las rutas del perfil del usuario se sustituyen por `<usuario>`."));
        md.AppendLine("> Cada cifra lleva su origen: el coste de Claude es «estimado por el CLI» (precio de lista; con suscripción no es lo que pagas) y Codex no informa coste en dinero; sus tokens son «exacto · informado por el CLI». Lo que la demostración no pudo leer sale «sin dato», nunca como cero.");
        md.AppendLine();
        Summary(md, byBlock);
        Plan(md, byBlock);
        foreach (var entry in DemoPlan.Entries)
        {
            if (byBlock.TryGetValue(entry.Block, out var capture))
            {
                Block(md, entry, capture);
            }
        }

        foreach (var extra in byBlock.Values.Where(c => DemoPlan.Entries.All(e => e.Block != c.Block)).OrderBy(c => c.Block, StringComparer.Ordinal))
        {
            Block(md, null, extra);
        }

        if (unreadable.Count > 0)
        {
            md.AppendLine("## Capturas que no se pudieron leer");
            md.AppendLine();
            foreach (var line in unreadable)
            {
                md.AppendLine("- " + Line(line));
            }

            md.AppendLine();
        }

        return DemoScrub.Text(md.ToString().TrimEnd() + Environment.NewLine);
    }

    private static void Summary(StringBuilder md, IReadOnlyDictionary<string, DemoCapture> byBlock)
    {
        md.AppendLine("## Resumen");
        md.AppendLine();
        md.AppendLine("| Bloque | Resultado | Completa | Veredictos | Ejecuciones de agente | Claude: coste | Codex: tokens (entrada + salida) | Duración |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var entry in DemoPlan.Entries)
        {
            if (!byBlock.TryGetValue(entry.Block, out var capture))
            {
                md.AppendLine(Invariant($"| {entry.Title} | sin captura todavía | — | — | — | — | — | — |"));
                continue;
            }

            var claude = capture.Attempts.Where(a => a.Provider == ProviderId.ClaudeCode.Value).ToList();
            var codex = capture.Attempts.Where(a => a.Provider == ProviderId.CodexCli.Value).ToList();
            md.AppendLine(Invariant($"| {Cell(capture.Title)} | {Cell(capture.Result)} | {(capture.Completed ? "sí" : "no")} | {capture.Verdicts.Count} | {capture.Attempts.Count} | {(claude.Count == 0 ? "—" : Cell(Cost(claude)))} | {(codex.Count == 0 ? "—" : Cell(TokenTotal(codex)))} | {Minutes(capture.FinishedAt - capture.StartedAt)} |"));
        }

        md.AppendLine();
    }

    private static void Plan(StringBuilder md, IReadOnlyDictionary<string, DemoCapture> byBlock)
    {
        md.AppendLine("## Cómo se ejecuta cada demostración");
        md.AppendLine();
        md.AppendLine("Una a una, desde la raíz del repositorio, en PowerShell y con el OK del usuario para las que gastan cuota. Precondiciones: `claude` nativo con sesión (`claude auth status`), `codex` con sesión (`codex login status`), git y el SDK de `global.json`, y red (cada verificación aislada restaura NuGet en frío). El espacio de trabajo que comparten es `%TEMP%\\as-f5` (o `AGENTSTUDIO_PHASE5_WORKSPACE`): guarda el repositorio de los debates, su base SQLite y la copia del repositorio auditado, para que cada prueba encuentre lo que dejó la anterior. Ninguna otra variable `AGENTSTUDIO_*` debería estar puesta: la verificación aislada hereda el entorno.");
        md.AppendLine();
        md.AppendLine("Lo que gasta cuota necesita las tres variables:");
        md.AppendLine();
        md.AppendLine("```powershell");
        md.AppendLine(DemoPlan.Switches(spendsQuota: true));
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("Lo que no gasta (la preparación, la medición y este documento) solo necesita `AGENTSTUDIO_PHASE5_DEMOS=1` (y la carpeta de capturas para dejar evidencia).");
        md.AppendLine();
        md.AppendLine("| # | Bloque | Orden exacta | Requiere antes | Ejecuciones de agente | Claude (estimado) | Codex, tokens (estimado) | Duración | Captura |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        var index = 0;
        foreach (var entry in DemoPlan.Entries)
        {
            index++;
            var requires = entry.Requires == "—" ? "—" : TitleOf(entry.Requires);
            md.AppendLine(Invariant($"| {index} | {Cell(entry.Title)} | `{Cell(DemoPlan.Command(entry))}` | {Cell(requires)} | {Cell(entry.Runs)} | {Cell(entry.Claude)} | {Cell(entry.Codex)} | {Cell(entry.Duration)} | {(byBlock.ContainsKey(entry.Block) ? "sí" : "no")} |"));
        }

        md.AppendLine();
        md.AppendLine("- Total estimado por Agent Studio: " + DemoPlan.Totals);
        md.AppendLine("- El documento se vuelve a componer, sin cuota, con `" + DemoPlan.ComposeCommand + "`.");
        md.AppendLine("- Si la medición encuentra pruebas que fallan a integridad Low por el entorno, `AGENTSTUDIO_PHASE5_AUDIT_EXCLUDE` (separado por `;`: `Category=ConPty` o una clase) las deja fuera del paso de pruebas de la auditoría; `Category=ProviderLive` se excluye siempre.");
        md.AppendLine("- Política de las demostraciones: aprueban como el usuario la apertura de cada debate, de la revisión de la impugnación y de la auditoría; nunca «Decidir yo», la aceptación de una revisión ni una entrega. Un debate que espera a una persona se cierra como abortado con ese motivo, y su evidencia lo dice.");
        md.AppendLine();
    }

    private static void Block(StringBuilder md, DemoPlanEntry? entry, DemoCapture capture)
    {
        md.AppendLine(Invariant($"## {capture.Title} (`{capture.Block}`)"));
        md.AppendLine();
        md.AppendLine(Invariant($"- Prueba: `{capture.Test}`; empezó el {capture.StartedAt:yyyy-MM-dd HH:mm} UTC y duró {Minutes(capture.FinishedAt - capture.StartedAt)}."));
        if (entry is not null)
        {
            md.AppendLine(Invariant($"- Orden: `{DemoPlan.Command(entry)}`; estimación: {entry.Runs} ejecuciones; Claude {entry.Claude}; Codex {entry.Codex}."));
        }

        md.AppendLine("- Resultado: " + Line(capture.Result) + (capture.Completed ? " La demostración llegó hasta el final." : " **La demostración no llegó hasta el final.**"));
        foreach (var tool in capture.Tools)
        {
            md.AppendLine(Invariant($"- {tool.Name}: {tool.Installation}, versión {tool.Version ?? "?"}, sesión {tool.Authentication}."));
        }

        foreach (var note in capture.Notes)
        {
            md.AppendLine("- " + Line(note));
        }

        md.AppendLine();
        if (capture.Prepared.Count > 0)
        {
            Prepared(md, capture.Prepared);
        }

        if (capture.Suite is { } suite)
        {
            Suite(md, suite);
        }

        if (capture.Run is { } run)
        {
            Run(md, run);
        }

        if (capture.Challenge is { } challenge)
        {
            Challenge(md, challenge);
        }

        if (capture.Debate is { } debate)
        {
            Debate(md, debate);
        }

        if (capture.Audit is { } audit)
        {
            Audit(md, audit);
        }

        if (capture.Verdicts.Count > 0 || capture.Debate is not null || capture.Audit is not null)
        {
            Verdicts(md, capture);
        }

        Incidents(md, capture);
        if (capture.Attempts.Count > 0 || capture.Quota.Count > 0)
        {
            Usage(md, capture);
        }

        Records(md, capture);
    }

    private static void Prepared(StringBuilder md, IReadOnlyList<DemoPreparedFacts> prepared)
    {
        md.AppendLine("### Lo que se enseñaría para aprobar (sin gastar nada)");
        md.AppendLine();
        md.AppendLine("| Bloque | Modo | Juicio | Jueces | Dispensa | Ejecuciones como mucho | Tokens (estimado por Agent Studio) | Problemas |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var p in prepared)
        {
            md.AppendLine(Invariant($"| {Cell(p.Block)} | {Cell(ModeText(p.Mode))} | {Cell(JudgingText(p.Judging))} | {Cell(Judges(p.Judges))} | {Cell(WaiverText(p.WaivedVendors))} | {p.MaxAgentRuns} | {Number(p.EstimatedTokens)} | {Cell(p.Problems.Count == 0 ? "ninguno" : string.Join(" ", p.Problems))} |"));
        }

        md.AppendLine();
    }

    private static void Suite(StringBuilder md, DemoSuiteFacts suite)
    {
        md.AppendLine("### La suite a integridad Low");
        md.AppendLine();
        md.AppendLine(Invariant($"- Repositorio: `{suite.Repository}`, commit `{suite.Head}`; {suite.Minutes.ToString("0.0", Spanish)} min."));
        Profile(md, suite.Profile);
        md.AppendLine("| Paso | Resultado | Código de salida | Correctas | Fallidas | Omitidas | Aislamiento |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var step in suite.Steps)
        {
            md.AppendLine(Invariant($"| {Cell(step.Title)} | {(step.Succeeded ? "bien" : step.TimedOut ? "tiempo agotado" : "falla")} | {step.ExitCode} | {Number(step.Passed)} | {Number(step.Failed)} | {Number(step.Skipped)} | {Cell(step.Isolation ?? "-")} |"));
        }

        md.AppendLine();
        if (suite.Failed.Count == 0)
        {
            md.AppendLine("- Ninguna prueba fallida con nombre en la consola.");
        }
        else
        {
            md.AppendLine("| Prueba fallida | Categorías |");
            md.AppendLine("|---|---|");
            foreach (var failed in suite.Failed)
            {
                md.AppendLine(Invariant($"| `{Cell(failed.Name)}` | {Cell(failed.Categories.Count == 0 ? "sin categoría" : string.Join(", ", failed.Categories))} |"));
            }

            md.AppendLine();
        }

        md.AppendLine("- Exclusiones propuestas para la auditoría: " + (suite.SuggestedExclusions.Count == 0 ? "ninguna." : "`" + string.Join(';', suite.SuggestedExclusions) + "`."));
        if (suite.Uncategorized.Count > 0)
        {
            md.AppendLine(Invariant($"- {suite.Uncategorized.Count} pruebas sin categoría fallan: no las quita ningún filtro de rasgo."));
        }

        md.AppendLine();
    }

    private static void Run(StringBuilder md, DemoRunFacts run)
    {
        md.AppendLine("### La ejecución");
        md.AppendLine();
        md.AppendLine("> " + Line(run.Request));
        md.AppendLine();
        Requirements(md, run.Requirements, run.RequirementSetHash);
        md.AppendLine(Invariant($"- Ejecución `{run.Run}` (planificación `{run.PlanningRun}`): **{run.Phase}**. {Line(run.Summary)}"));
        md.AppendLine(Invariant($"- Plan: {(run.PlanFromOrchestrator ? "el del orquestador, sin cambios" : "el del lienzo de F3-8, porque el del orquestador no sirvió")}{(run.PlanCorrected ? ", tras una corrección" : string.Empty)}."));
        foreach (var violation in run.PlanViolations)
        {
            md.AppendLine("  - " + Line(violation));
        }

        md.AppendLine(Invariant($"- Commit base `{run.BaseCommit ?? "-"}`; commit de integración (candidato) `{run.IntegrationCommit ?? "-"}`; veredicto de la revisión `{run.VerdictId ?? "-"}`."));
        if (run.Stopped is not null)
        {
            md.AppendLine("- " + Line(run.Stopped));
        }

        md.AppendLine();
        md.AppendLine("| Tarea | Tipo | Agente | Depende de |");
        md.AppendLine("|---|---|---|---|");
        foreach (var task in run.Tasks)
        {
            md.AppendLine(Invariant($"| {task.Id} | {task.Kind} | {task.Agent ?? "sistema"} | {(task.DependsOn.Count == 0 ? "-" : string.Join(", ", task.DependsOn))} |"));
        }

        md.AppendLine();
        md.AppendLine(Invariant($"- Auditor del orquestador (determinista, sin cuota): registro {(run.AuditorLogValid ? "íntegro" : "NO íntegro o sin auditar")}; {run.AuditorFindings.Count} hallazgos; replanificación {(run.ReplanRequested ? "pedida" : "no pedida")}."));
        foreach (var finding in run.AuditorFindings)
        {
            md.AppendLine(Invariant($"  - `{finding.Code}` ({finding.Severity}): {Line(finding.Message)}"));
        }

        md.AppendLine();
    }

    private static void Challenge(StringBuilder md, DemoChallengeFacts challenge)
    {
        md.AppendLine("### La impugnación");
        md.AppendLine();
        md.AppendLine(Invariant($"- Veredicto impugnado `{challenge.Verdict}` ({challenge.Decision}) de `{challenge.Unit}`, que emitió {JudgeText(challenge.Judge, ofVerdict: true)}."));
        md.AppendLine(Invariant($"- Vendors que no pueden juzgar la revisión, heredados del objeto: {(challenge.ExcludedVendors.Count == 0 ? "ninguno" : string.Join(", ", challenge.ExcludedVendors))}."));
        md.AppendLine(Invariant($"- Debate de revisión `{challenge.Revision ?? "-"}`; estado de la impugnación al terminar: **{challenge.Status}** (la entrega lo lee así, D-40; la demostración nunca acepta una revisión)."));
        foreach (var note in challenge.Notes)
        {
            md.AppendLine("- " + Line(note));
        }

        md.AppendLine();
        md.AppendLine("Razones del usuario (la ronda 1 parte de ellas):");
        md.AppendLine();
        md.AppendLine("> " + Line(challenge.Reasons));
        md.AppendLine();
    }

    private static void Debate(StringBuilder md, DemoDebateFacts debate)
    {
        md.AppendLine("### Pregunta y objeto");
        md.AppendLine();
        md.AppendLine(Invariant($"- Debate `{debate.Id}` · modo **{ModeText(debate.Mode)}** · objeto: {SubjectText(debate)}."));
        md.AppendLine();
        md.AppendLine("> " + Line(debate.Question ?? "(la pregunta no se pudo leer contra su hash)"));
        md.AppendLine();
        Requirements(md, debate.Requirements, debate.RequirementSetHash);

        md.AppendLine("### Participantes y jueces");
        md.AppendLine();
        md.AppendLine("| Etiqueta (lo que vio el juez) | Papel | Agente | CLI | Vendor | Modelo configurado |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var p in debate.Participants)
        {
            md.AppendLine(Invariant($"| {p.Label} | {Cell(RoleText(p.Role))} | {p.Agent} | {p.Provider} | {p.Vendor} | {p.Model ?? "el del CLI"} |"));
        }

        md.AppendLine();
        md.AppendLine(Invariant($"- Juicio: **{JudgingText(debate.Judging)}**; {WaiverText(debate.WaivedVendors)}. Jueces: {Judges(debate.Judges)}."));
        md.AppendLine(Invariant($"- Vendors productores del objeto (regla P, no se dispensa): {(debate.ProducerVendors.Count == 0 ? "ninguno" : string.Join(", ", debate.ProducerVendors))}."));
        md.AppendLine();
        md.AppendLine("<details><summary>Texto de la aprobación «Abrir debate»</summary>");
        md.AppendLine();
        md.AppendLine("```text");
        md.AppendLine(debate.ApprovalText.TrimEnd());
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("</details>");
        md.AppendLine();

        md.AppendLine("### Rondas y límites");
        md.AppendLine();
        md.AppendLine(Invariant($"- Límites: {debate.MaxRounds} rondas, {debate.MaxMinutes.ToString("0", Spanish)} minutos, {Number(debate.MaxTokens)} tokens y {debate.MaxAgentRuns} ejecuciones como mucho; estimación de la apertura: {Number(debate.EstimatedTokens)} tokens, estimado por Agent Studio."));
        if (debate.Tokens is { } tokens)
        {
            md.AppendLine(Invariant($"- Tokens de los participantes al terminar (los del juez no cuentan, D-19): {FigureText(tokens, isCost: false)}; {debate.ReportedTurns} intentos informados; {debate.AgentRuns} lanzamientos de turno."));
        }

        md.AppendLine();
        md.AppendLine("| Ronda | Sigue | Motivo del juicio | Límite | Tokens hasta aquí | ¿Con estimación? | Segundos | Motivo |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var round in debate.Rounds)
        {
            md.AppendLine(Invariant($"| {round.Round} | {Cell(PhaseText(round.Next))} | {Cell(JudgmentText(round.Judgment, round.Limit))} | {Cell(LimitText(round.Limit))} | {Cell(FigureText(round.Tokens, isCost: false))} | {(round.IncludesEstimates ? "sí" : "no")} | {round.ElapsedSeconds} | {Cell(round.Reason)} |"));
        }

        md.AppendLine();
        if (debate.Tests.Count > 0)
        {
            md.AppendLine("«Ejecutar pruebas» durante el debate (capturado por el sistema, sin cuota):");
            md.AppendLine();
            Evidence(md, debate.Tests);
        }

        md.AppendLine("### Contribuciones");
        md.AppendLine();
        if (debate.Contributions.Count == 0)
        {
            md.AppendLine("- Ninguna contribución publicada.");
            md.AppendLine();
        }

        foreach (var c in debate.Contributions)
        {
            md.AppendLine(Invariant($"**{c.Label} · {c.Author} · {KindText(c.Kind)}** (ronda {c.Round}{(c.RepliesTo is null ? string.Empty : ", responde a " + c.RepliesTo)}{(c.Finished ? ", da su posición por terminada" : string.Empty)}): {Line(c.Summary ?? "-")}"));
            md.AppendLine();
            var extras = new List<string>();
            if (c.Claims > 0)
            {
                extras.Add(Invariant($"{c.Claims} afirmaciones"));
            }

            if (c.Citations > 0)
            {
                extras.Add(Invariant($"{c.Citations} citas"));
            }

            if (c.Unresolved.Count > 0)
            {
                extras.Add("citas no resueltas: " + string.Join("; ", c.Unresolved.Select(Line)));
            }

            if (c.Concedes.Count > 0)
            {
                extras.Add("concede " + string.Join(", ", c.Concedes));
            }

            if (c.Redactions > 0)
            {
                extras.Add(Invariant($"{c.Redactions} redacciones de secretos"));
            }

            if (extras.Count > 0)
            {
                md.AppendLine("- " + string.Join("; ", extras) + ".");
                md.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(c.Argument))
            {
                md.AppendLine("<details><summary>Argumento</summary>");
                md.AppendLine();
                md.AppendLine(Quote(c.Argument));
                md.AppendLine();
                md.AppendLine("</details>");
                md.AppendLine();
            }
        }

        md.AppendLine("### Resultado");
        md.AppendLine();
        md.AppendLine(Invariant($"- Fase **{PhaseText(debate.Phase)}**, resultado **{OutcomeText(debate.Outcome)}**; regla: {DebateTexts.Rule(debate.Rule)}; decisión: {debate.Decision ?? "ninguna"}; propuesta elegida: {debate.ChosenProposal ?? "ninguna"}."));
        md.AppendLine(Invariant($"- Fue a juicio por: {JudgmentText(debate.JudgmentReason, debate.LimitReached)}; límite alcanzado: {LimitText(debate.LimitReached)}."));
        if (debate.ResultSummary.Length > 0)
        {
            md.AppendLine("- Motivo del cierre: " + Line(debate.ResultSummary));
        }

        if (debate.Stopped is not null)
        {
            md.AppendLine("- La demostración lo detuvo: " + Line(debate.Stopped));
        }

        md.AppendLine("- Regla del jurado (D-14): con jurado cruzado, la decisión solo vale si los dos veredictos son válidos, repiten la misma cabeza del registro y los mismos requisitos y coinciden en la decisión y en la propuesta elegida («coinciden», con la menor confianza); cualquier otra cosa es «no resuelto» y decide el usuario. Con un solo juez («juez único»), decide su veredicto válido.");
        md.AppendLine();
        if (debate.Timeline.Count > 0)
        {
            md.AppendLine("<details><summary>Lo que enseñó la sala, en orden</summary>");
            md.AppendLine();
            foreach (var line in debate.Timeline)
            {
                md.AppendLine("- " + Line(line));
            }

            md.AppendLine();
            md.AppendLine("</details>");
            md.AppendLine();
        }
    }

    private static void Audit(StringBuilder md, DemoAuditFacts audit)
    {
        md.AppendLine("### La auditoría");
        md.AppendLine();
        md.AppendLine(Invariant($"- Repositorio auditado: `{audit.Repository}`; commit HEAD `{audit.Head ?? "-"}`{(audit.Uncommitted ? " (había cambios sin confirmar: no entran)" : string.Empty)}; auditoría `{audit.Audit ?? "-"}`."));
        md.AppendLine(Invariant($"- Juez: {(audit.Judge is null ? "ninguno" : JudgeText(audit.Judge))}; vendors productores según el registro: {(audit.Producers.Count == 0 ? "ninguno" : string.Join(", ", audit.Producers))}; autores declarados: {string.Join(", ", audit.Declared)}."));
        md.AppendLine(Invariant($"- Con el juez de Claude la preparación {(audit.ClaudeJudgeCanJudge ? "**dejaba juzgar** (no debería)" : "no deja juzgar")}: {Line(string.Join(" ", audit.ClaudeJudgeProblems))}"));
        md.AppendLine(Invariant($"- Pasos sin aislamiento que no se ejecutan: {(audit.Skipped.Count == 0 ? "ninguno" : string.Join(", ", audit.Skipped))}; estimación del juez: {Number(audit.EstimatedTokens)} tokens, estimado por Agent Studio."));
        md.AppendLine(Invariant($"- Disposición **{audit.Disposition ?? "-"}**; {(audit.Trustworthy == true ? "fiable" : "no fiable")}; registro {(audit.LogIntact == true ? "íntegro" : "no íntegro")}; {(audit.Corrected == true ? "con corrección" : "sin corrección")}; veredicto `{audit.VerdictId ?? "-"}`."));
        md.AppendLine(Invariant($"- `dossier.json` del paquete: {(audit.DossierBytes is { } bytes ? Number(bytes) + " bytes" : "no encontrado")} (tope 262 144); la copia de HEAD para el juez {(audit.SnapshotRemoved == true ? "ya no está" : "sigue en disco")}."));
        md.AppendLine();
        Requirements(md, audit.Requirements, audit.RequirementSetHash);
        Profile(md, audit.Profile);
        if (audit.Verification.Count > 0)
        {
            md.AppendLine("Verificación de HEAD (capturada por el sistema):");
            md.AppendLine();
            Evidence(md, audit.Verification);
        }

        md.AppendLine("Problemas pendientes que el sistema lista sin juez (D-31):");
        md.AppendLine();
        if (audit.Pending.Count == 0)
        {
            md.AppendLine("- Ninguno.");
        }

        foreach (var pending in audit.Pending)
        {
            md.AppendLine(Invariant($"- `{pending.Code}` en {pending.Source}: {Line(pending.Message)}"));
        }

        md.AppendLine();
        md.AppendLine("<details><summary>Texto de la aprobación «audit.run»</summary>");
        md.AppendLine();
        md.AppendLine("```text");
        md.AppendLine(audit.ApprovalText.TrimEnd());
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("</details>");
        md.AppendLine();
    }

    private static void Verdicts(StringBuilder md, DemoCapture capture)
    {
        md.AppendLine("### Veredictos");
        md.AppendLine();
        if (capture.Verdicts.Count == 0)
        {
            md.AppendLine("- Ningún veredicto guardado.");
            md.AppendLine();
            return;
        }

        foreach (var v in capture.Verdicts)
        {
            var role = capture.Challenge is { } challenge && v.Id == challenge.Verdict ? " · el impugnado" : string.Empty;
            md.AppendLine(Invariant($"#### `{v.Id}`: {v.Decision} ({(v.Valid ? "válido" : "inválido")}){role}"));
            md.AppendLine();
            md.AppendLine(Invariant($"- Juez: {JudgeText(v.Judge, ofVerdict: true)}; emitido el {v.IssuedAt:yyyy-MM-dd HH:mm:ss} UTC; unidad `{v.Unit ?? "-"}` ({v.Scope}){(v.Supersedes is null ? string.Empty : $"; sustituye a `{v.Supersedes}`")}."));
            if (v.VendorNote is { Length: > 0 } note)
            {
                md.AppendLine("- Nota del registro (`VerdictIssued`): " + Line(note));
            }

            md.AppendLine("- Resumen: " + Line(v.Summary));
            if (v.ChosenProposal is not null || v.Comparison.Count > 0)
            {
                md.AppendLine("- Propuesta elegida: " + (v.ChosenProposal ?? "ninguna") + ".");
                foreach (var c in v.Comparison)
                {
                    md.AppendLine(Invariant($"  - {c.Proposal}: a favor, {Line(c.Strengths)}; en contra, {Line(c.Weaknesses)}"));
                }
            }

            foreach (var a in v.Assessments)
            {
                md.AppendLine(Invariant($"- {a.Requirement}: {a.Status} ({a.Evidence} evidencias). {Line(a.Rationale)}"));
            }

            foreach (var f in v.Findings)
            {
                md.AppendLine(Invariant($"- Hallazgo {f.Category} ({f.Severity}): {Line(f.Description)}"));
            }

            foreach (var risk in v.Risks)
            {
                md.AppendLine("- Riesgo: " + Line(risk));
            }

            foreach (var action in v.CorrectiveActions)
            {
                md.AppendLine("- Acción correctiva: " + Line(action));
            }

            foreach (var objection in v.PendingObjections)
            {
                md.AppendLine("- Objeción pendiente: " + Line(objection));
            }

            md.AppendLine(Invariant($"- Confianza: {v.Confidence}. {Line(v.ConfidenceJustification)}"));
            md.AppendLine(Invariant($"- Requiere intervención humana: {(v.RequiresHumanIntervention ? "sí" : "no")}."));
            md.AppendLine("- Incidencias del validador: " + (v.Issues.Count == 0 ? "ninguna." : string.Join("; ", v.Issues.Select(i => $"`{i.Code}`: {Line(i.Message)}"))));
            md.AppendLine(Invariant($"- Hashes que repite: requisitos `{v.RequirementSetHash}`, cabeza del registro `{v.DecisionLogHeadHash}`."));
            md.AppendLine();
        }
    }

    private static void Incidents(StringBuilder md, DemoCapture capture)
    {
        var problems = capture.Debate?.Problems ?? [];
        var issues = capture.Verdicts.SelectMany(v => v.Issues.Select(i => $"`{v.Id}` · `{i.Code}`: {Line(i.Message)}")).ToList();
        if (problems.Count == 0 && capture.Alerts.Count == 0 && issues.Count == 0 && capture.Approvals.Count == 0)
        {
            return;
        }

        md.AppendLine("### Incidencias del validador y del sistema");
        md.AppendLine();
        md.AppendLine("- Validador: " + (issues.Count == 0 ? "ninguna incidencia." : string.Empty));
        foreach (var issue in issues)
        {
            md.AppendLine("  - " + issue);
        }

        md.AppendLine("- Alertas del registro (`AuditAlertRaised`): " + (capture.Alerts.Count == 0 ? "ninguna." : string.Empty));
        foreach (var alert in capture.Alerts)
        {
            md.AppendLine("  - " + Line(alert));
        }

        if (problems.Count > 0)
        {
            md.AppendLine("- Problemas que encontró la lectura:");
            foreach (var problem in problems)
            {
                md.AppendLine("  - " + Line(problem));
            }
        }

        if (capture.Approvals.Count > 0)
        {
            md.AppendLine("- Aprobaciones pedidas a la persona:");
            foreach (var approval in capture.Approvals)
            {
                md.AppendLine(Invariant($"  - `{approval.Action}`: {(approval.Approved ? "aprobada" : "denegada")}."));
            }
        }

        md.AppendLine();
    }

    private static void Usage(StringBuilder md, DemoCapture capture)
    {
        md.AppendLine("### Coste y tokens con su origen");
        md.AppendLine();
        if (capture.Attempts.Count > 0)
        {
            md.AppendLine("| Tarea | Intento | Agente | CLI / modelo | Propósito | Resultado | Duración | Entrada | Caché leída | Caché escrita | Salida | Coste (USD) |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var a in capture.Attempts)
            {
                var model = a.ReportedModel ?? a.ConfiguredModel ?? "el del CLI (no expuesto)";
                md.AppendLine(Invariant($"| {a.Task ?? "-"} | {a.Attempt} | {a.Agent} | {a.Provider} / {Cell(model)} | {a.Purpose ?? "-"} | {a.Outcome} | {(a.DurationSeconds is { } seconds ? seconds.ToString("0.0", Spanish) + " s" : "sin dato")} | {Cell(FigureText(a.Input, false))} | {Cell(FigureText(a.CacheRead, false))} | {Cell(FigureText(a.CacheWrite, false))} | {Cell(FigureText(a.Output, false))} | {Cell(FigureText(a.Cost, true))} |"));
            }

            md.AppendLine();
            foreach (var provider in capture.Attempts.GroupBy(a => a.Provider, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var attempts = provider.ToList();
                md.AppendLine(Invariant($"- {provider.Key}: {attempts.Count} intentos; entrada {Cell(MetricText.Tokens(MetricAggregate.Sum(attempts.Select(a => a.Input.AsLong()))).Text)}; salida {Cell(MetricText.Tokens(MetricAggregate.Sum(attempts.Select(a => a.Output.AsLong()))).Text)}; coste {Cell(Cost(attempts))}."));
            }

            md.AppendLine();
        }

        if (capture.Quota.Count > 0)
        {
            md.AppendLine("Cuota observada al terminar (lo último de cada ventana en el registro del usuario):");
            md.AppendLine();
            md.AppendLine("| Proveedor | Ventana | Uso | Se reinicia | Observado | Origen |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var q in capture.Quota)
            {
                var use = q.Utilization.Value is { } value ? MetricText.PercentText((double)value) + " · " + MetricText.Origin(q.Utilization.OriginValue) : MetricText.Origin(q.Utilization.OriginValue);
                md.AppendLine(Invariant($"| {q.Provider} | {Cell(q.Label ?? q.Window)} | {Cell(use)} | {(q.ResetsAt is { } at ? at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "-")} | {q.ObservedAt:yyyy-MM-dd HH:mm} UTC | {Cell(q.Source)} |"));
            }

            md.AppendLine();
        }
    }

    private static void Records(StringBuilder md, DemoCapture capture)
    {
        if (capture.Log.Count == 0 && capture.Journal.Count == 0)
        {
            return;
        }

        md.AppendLine("### Registro y bitácora");
        md.AppendLine();
        md.AppendLine(Invariant($"- Registro de decisiones: {(capture.LogIntact ? "verifica" : "NO verifica")}; cabeza `{capture.HeadHash ?? "-"}`; {capture.Log.Count} entradas de esta unidad."));
        var tags = Tags(capture.Journal).ToList();
        md.AppendLine(capture.Run is not null
            ? Invariant($"- Bitácora de prompts: {capture.Journal.Count} lanzamientos escritos durante la ejecución (sus entradas nombran la tarea, no la ejecución).")
            : Invariant($"- Bitácora de prompts: {capture.Journal.Count} lanzamientos con el id de la unidad{(tags.Count == 0 ? "." : ", con su etiqueta (" + string.Join(", ", tags) + ").")}"));
        md.AppendLine();
        if (capture.Log.Count > 0)
        {
            md.AppendLine("<details><summary>Cronología del registro</summary>");
            md.AppendLine();
            md.AppendLine("| # | Hora (UTC) | Actor | Tipo | Resumen |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var entry in capture.Log)
            {
                md.AppendLine(Invariant($"| {entry.Sequence} | {entry.At:HH:mm:ss} | {Cell(entry.Actor)} | {entry.Kind} | {Cell(entry.Summary)} |"));
            }

            md.AppendLine();
            md.AppendLine("</details>");
            md.AppendLine();
        }
    }

    private static void Requirements(StringBuilder md, IReadOnlyList<DemoRequirement> requirements, string hash)
    {
        md.AppendLine(Invariant($"Requisitos congelados (`{hash}`):"));
        md.AppendLine();
        foreach (var r in requirements)
        {
            md.AppendLine(Invariant($"- **{r.Id}** ({(r.Priority == "Mandatory" ? "obligatorio" : "deseable")}): {Line(r.Statement)}"));
            foreach (var c in r.Criteria)
            {
                md.AppendLine(Invariant($"  - {c.Id} · {MethodText(c.Method)}: {Line(c.Description)}"));
            }
        }

        md.AppendLine();
    }

    private static void Profile(StringBuilder md, IReadOnlyList<DemoStep> steps)
    {
        md.AppendLine();
        md.AppendLine("| Paso | Orden | Tiempo máximo | Aislamiento |");
        md.AppendLine("|---|---|---|---|");
        foreach (var step in steps)
        {
            md.AppendLine(Invariant($"| {Cell(step.Title)} (`{step.Id}`) | `{Cell(step.Executable + " " + string.Join(' ', step.Arguments))}` | {step.TimeoutMinutes.ToString("0", Spanish)} min | {step.Isolation} |"));
        }

        md.AppendLine();
    }

    private static void Evidence(StringBuilder md, IReadOnlyList<DemoEvidence> evidence)
    {
        md.AppendLine("| Evidencia | Tipo | Commit | Aislamiento | Correctas | Fallidas | Omitidas | Código de salida |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var e in evidence)
        {
            md.AppendLine(Invariant($"| {Cell(e.Title)} | {e.Kind} | `{e.SubjectCommit ?? "-"}` | {Cell(e.Isolation ?? "-")} | {Number(e.Passed)} | {Number(e.Failed)} | {Number(e.Skipped)} | {Number(e.ExitCode)} |"));
        }

        md.AppendLine();
    }

    private static IEnumerable<string> Tags(IReadOnlyList<string> journal) =>
        new[] { "#debate", "#auditoria", "#revision" }.Where(tag => journal.Any(l => l.Contains(tag, StringComparison.Ordinal)));

    private static string Cost(IReadOnlyList<DemoAttemptFacts> attempts) => MetricText.Cost(MetricAggregate.Sum(attempts.Select(a => a.Cost.AsDecimal()))).Text;

    private static string TokenTotal(IReadOnlyList<DemoAttemptFacts> attempts) =>
        MetricText.Tokens(MetricAggregate.Sum(attempts.SelectMany(a => new[] { a.Input.AsLong(), a.Output.AsLong() }))).Text;

    private static string FigureText(DemoFigure figure, bool isCost) =>
        isCost ? MetricText.Cost(figure.AsDecimal()).Text : MetricText.Tokens(figure.AsLong()).Text;

    /// <summary>A judge; <paramref name="ofVerdict"/> says the model is the one the CLI reported («no informado» when it gave none), not the configured one.</summary>
    private static string JudgeText(DemoJudge judge, bool ofVerdict = false) =>
        $"{judge.Agent} ({judge.Provider}, {judge.Vendor}, modelo {judge.Model ?? (ofVerdict ? "no informado" : "el del CLI")})";

    private static string Judges(IReadOnlyList<DemoJudge> judges) => judges.Count == 0 ? "ninguno" : string.Join(" y ", judges.Select(j => JudgeText(j)));

    private static string SubjectText(DemoDebateFacts debate) => debate.Subject switch
    {
        nameof(DebateSubjectKind.Commit) => $"el commit `{debate.SnapshotCommit ?? debate.SubjectReference ?? "-"}` del repositorio de las demostraciones",
        nameof(DebateSubjectKind.Run) => $"la ejecución `{debate.SubjectReference}` (commit candidato `{debate.SnapshotCommit ?? "-"}`)",
        nameof(DebateSubjectKind.Verdict) => $"el veredicto `{debate.SubjectReference}`",
        _ => "ninguno",
    };

    private static string WaiverText(IReadOnlyList<string> waived) =>
        DebateTexts.Waiver([.. waived.Select(v => new VendorId(v))]) ?? "sin dispensa: ADR-0006 tal cual";

    private static string ModeText(string mode) => Enum.TryParse<DebateMode>(mode, out var m) ? DebateTexts.Mode(m) : mode;

    private static string RoleText(string role) => Enum.TryParse<DebateRole>(role, out var r) ? DebateTexts.Role(r) : role;

    private static string PhaseText(string phase) => Enum.TryParse<DebatePhase>(phase, out var p) ? DebateTexts.Phase(p) : phase;

    private static string OutcomeText(string outcome) => Enum.TryParse<DebateOutcome>(outcome, out var o) ? DebateTexts.Outcome(o) : outcome;

    private static string KindText(string kind) => Enum.TryParse<ContributionKind>(kind, out var k) ? DebateTexts.Kind(k) : kind;

    private static string JudgingText(string judging) => Enum.TryParse<DebateJudgingKind>(judging, out var j) ? DebateTexts.Judging(j) : judging;

    private static string LimitText(string limit) =>
        Enum.TryParse<DebateLimitReached>(limit, out var l) ? DebateTexts.Limit(l) ?? "ninguno" : limit;

    private static string JudgmentText(string? reason, string limit)
    {
        if (reason is null || !Enum.TryParse<DebateJudgmentReason>(reason, out var r))
        {
            return reason ?? "-";
        }

        return DebateTexts.Judgment(r, Enum.TryParse<DebateLimitReached>(limit, out var l) ? l : DebateLimitReached.None);
    }

    private static string MethodText(string method) => method switch
    {
        "AutomatedTest" => "prueba automática",
        "CommandCheck" => "comprobación por comando",
        "HumanReview" => "revisión humana",
        "JudgeAssessment" => "valoración del juez, no verificable por pruebas",
        _ => method,
    };

    private static string TitleOf(string block) => DemoPlan.Entries.FirstOrDefault(e => e.Block == block)?.Title ?? block;

    private static string Number(long? value) => value is { } v ? v.ToString("N0", Spanish) : "-";

    private static string Number(int? value) => value is { } v ? v.ToString("N0", Spanish) : "-";

    private static string Minutes(TimeSpan duration) => duration.TotalMinutes < 1
        ? duration.TotalSeconds.ToString("0", Spanish) + " s"
        : duration.TotalMinutes.ToString("0.0", Spanish) + " min";

    private static string Quote(string text) => string.Join(Environment.NewLine, text.ReplaceLineEndings("\n").Split('\n').Select(l => "> " + l));

    private static string Line(string? text) => (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

    private static string Cell(string? text) => Line(text).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
