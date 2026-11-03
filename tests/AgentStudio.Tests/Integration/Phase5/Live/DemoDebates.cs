using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Desktop.Debate;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// The debates of the demonstrations (docs/15 §12, decision 2 a): one per mode, each with a participant of each vendor
/// (<c>arquitecto</c>, Claude with haiku, as P1; <c>critico</c>, Codex with its default model, as P2), two rounds and the
/// limits of decision 5 a (30 minutes and 400 000 tokens). The four modes that choose are about the commit HEAD of the
/// repository of the demonstrations and are judged by a cross-vendor panel (<c>juez-claude</c> and <c>juez</c>); the two that
/// review are about the run of F3-8 on that repository and are judged by Codex, the vendor that did not produce it.
/// The questions and the criteria are the user's, in Spanish; the criteria of the modes that choose are valuations of the
/// judge (D-25).
/// </summary>
internal sealed record DebateDemo(
    string Block,
    string Title,
    DebateMode Mode,
    string Question,
    IReadOnlyList<DebateParticipantRequest> Participants,
    IReadOnlyList<AgentId> Judges,
    DebateJudgingKind Judging,
    Func<DateTimeOffset, FrozenRequirementSet>? Requirements)
{
    /// <summary>Two rounds (decision 2 a), 30 minutes and 400 000 tokens (decision 5 a).</summary>
    public static readonly DebateLimits Limits = new(MaxRounds: 2, MaxDuration: TimeSpan.FromMinutes(30), MaxTotalTokens: 400_000);

    public static readonly DebateDemo Free = new(
        "libre",
        "Debate libre",
        DebateMode.Free,
        "¿Cómo debería la biblioteca Calc tratar el desbordamiento de enteros en Add, y en las operaciones que se le añadan después, como Multiply? Propón un comportamiento concreto y justifícalo con el código de este commit.",
        [new(DemoRoster.Architect, DebateRole.Panelist), new(DemoRoster.Critic, DebateRole.Panelist)],
        [DemoRoster.ClaudeJudge, DemoRoster.Judge],
        DebateJudgingKind.CrossVendorPanel,
        at => Freeze(at,
            Valuation("RQ-L1", "La respuesta elegida define qué hace Calc cuando el resultado no cabe en un int.", "El comportamiento propuesto es concreto (lanzar, saturar o ampliar el tipo) y está justificado."),
            Valuation("RQ-L2", "La respuesta se apoya en el código actual del repositorio.", "Cita src/Calc/Calc.cs o sus pruebas y explica qué cambiaría.")));

    public static readonly DebateDemo Adversarial = new(
        "adversarial",
        "Debate adversarial",
        DebateMode.Adversarial,
        "Propuesta: que Calc.Add use aritmética checked y lance OverflowException cuando el resultado no quepa en un int. ¿Se adopta tal cual, con cambios o se rechaza?",
        [new(DemoRoster.Architect, DebateRole.Proponent), new(DemoRoster.Critic, DebateRole.Critic)],
        [DemoRoster.ClaudeJudge, DemoRoster.Judge],
        DebateJudgingKind.CrossVendorPanel,
        at => Freeze(at,
            Valuation("RQ-A1", "La propuesta no rompe sin decirlo a quien ya usa Calc.Add.", "El juez valora si el cambio de comportamiento y su impacto en los usuarios actuales quedan explícitos."),
            Valuation("RQ-A2", "Los riesgos del cambio y la forma de probarlo quedan identificados.", "Hay al menos un riesgo concreto y una prueba que lo cubriría.")));

    public static readonly DebateDemo Architecture = new(
        "revision-arquitectura",
        "Revisión de arquitectura",
        DebateMode.ArchitectureReview,
        "Diseña cómo añadir a Calc un historial de operaciones (qué se calculó, con qué operandos y cuándo) sin acoplar la biblioteca a la consola ni a un fichero concreto.",
        [new(DemoRoster.Architect, DebateRole.Proponent), new(DemoRoster.Critic, DebateRole.Proponent)],
        [DemoRoster.ClaudeJudge, DemoRoster.Judge],
        DebateJudgingKind.CrossVendorPanel,
        at => Freeze(at,
            Valuation("RQ-R1", "El diseño registra cada operación con sus operandos, su resultado y el momento.", "El diseño elegido cubre los tres datos sin depender de la consola ni de un fichero."),
            Valuation("RQ-R2", "Mantenibilidad y complejidad: el diseño es fácil de mantener y no complica la API estática actual.", "El juez compara cuánto cambia la API y cuánto código nuevo exige cada diseño."),
            Valuation("RQ-R3", "Seguridad y rendimiento: no introduce estado global inseguro entre hilos ni un coste notable por operación.", "El juez valora el estado compartido y el coste por llamada de cada diseño."),
            Valuation("RQ-R4", "Coste: el cambio cabe en un incremento pequeño, con sus pruebas.", "El diseño elegido se puede entregar en un cambio pequeño y probado.")));

    public static readonly DebateDemo Tribunal = new(
        "tribunal",
        "Tribunal",
        DebateMode.Tribunal,
        "Evalúa las pruebas de la biblioteca Calc en este commit: ¿bastan para confiar en Add?",
        [new(DemoRoster.Architect, DebateRole.Panelist), new(DemoRoster.Critic, DebateRole.Panelist)],
        [DemoRoster.ClaudeJudge, DemoRoster.Judge],
        DebateJudgingKind.CrossVendorPanel,
        at => Freeze(at,
            Valuation("RQ-T1", "La evaluación dice qué cubren las pruebas actuales y qué no.", "El juez valora si la síntesis distingue lo cubierto de lo que falta, con el código del commit."),
            Valuation("RQ-T2", "La evaluación propone como mucho tres pruebas que faltan, por orden de importancia.", "Las pruebas propuestas son concretas y están ordenadas.")));

    /// <summary>The audit of the run of F3-8: P1 defends that it meets its frozen requirements and P2 questions it.</summary>
    public static readonly DebateDemo Implementation = new(
        "auditoria-implementacion",
        "Auditoría de implementación",
        DebateMode.ImplementationAudit,
        "¿La ejecución {run} cumple sus requisitos congelados (RQ-01: Calc.Multiply con su prueba xUnit) en su commit candidato?",
        [new(DemoRoster.Architect, DebateRole.Proponent), new(DemoRoster.Critic, DebateRole.Critic)],
        [DemoRoster.Judge],
        DebateJudgingKind.NonProducerJudge,
        null);

    /// <summary>The review of the orchestrator of the run of F3-8, with the fixed requirements orq-1 to orq-7 (D-36).</summary>
    public static readonly DebateDemo Orchestrator = new(
        "revision-orquestador",
        "Revisión del orquestador",
        DebateMode.OrchestratorReview,
        "¿El orquestador de la ejecución {run} siguió su protocolo (orq-1 a orq-7), según su registro de decisiones y el informe de su auditor?",
        [new(DemoRoster.Architect, DebateRole.Panelist), new(DemoRoster.Critic, DebateRole.Critic)],
        [DemoRoster.Judge],
        DebateJudgingKind.NonProducerJudge,
        null);

    /// <summary>The question as the user asks it about <paramref name="run"/> (the review modes name the run they judge).</summary>
    public string QuestionFor(string? run) => run is null ? Question : Question.Replace("{run}", run, StringComparison.Ordinal);

    /// <summary>The challenge of a verdict (D-38): P1 defends the verdict, P2 defends the reasons of the challenge.</summary>
    public static IReadOnlyList<DebateParticipantRequest> ChallengeParticipants { get; } =
        [new(DemoRoster.Architect, DebateRole.Proponent), new(DemoRoster.Critic, DebateRole.Critic)];

    private static FrozenRequirementSet Freeze(DateTimeOffset at, params Requirement[] requirements) => FrozenRequirementSet.Freeze(requirements, at);

    private static Requirement Valuation(string id, string statement, string criterion) =>
        new(new RequirementId(id), statement, RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", criterion, VerificationMethod.JudgeAssessment)]);
}

/// <summary>How a demonstration followed a debate: its result, the last state of the room, why the test stopped it (if it did) and what the room showed.</summary>
internal sealed record DebateWatch(DebateResult? Result, DebateSnapshot? Snapshot, string? Stopped, DebateAwaiting? Awaiting, IReadOnlyList<string> Timeline);

/// <summary>
/// Follows a debate to its end, as the room does. The demonstration never acts for the user: when the debate waits for a
/// person (a turn that failed twice, a usage limit without a reset the debate waits for by itself, or a judgment that needs
/// «Decidir yo», D-22 and D-26), or outlives <c>limit</c>, it is aborted with that reason, so its evidence is always
/// written and nobody's decision is made up. The abort is recorded in the log, with the reason.
/// </summary>
internal static class DebateWatcher
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    public static async Task<DebateWatch> FollowAsync(IDebateRun run, TimeSpan limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var timeline = new ConcurrentQueue<string>();
        var reader = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var progress in run.Events)
                    {
                        timeline.Enqueue(Describe(progress));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    // The room stops listening when the debate ends.
                }
            },
            CancellationToken.None);

        var watch = Stopwatch.StartNew();
        string? stopped = null;
        DebateAwaiting? awaiting = null;
        try
        {
            while (!run.Completion.IsCompleted)
            {
                await Task.WhenAny(run.Completion, Task.Delay(Poll, ct));
                ct.ThrowIfCancellationRequested();
                if (run.Completion.IsCompleted)
                {
                    break;
                }

                if (run.Snapshot().Awaiting is { } waiting)
                {
                    awaiting = waiting;
                    stopped = $"El debate esperaba a una persona ({DebateTexts.Await(waiting.Reason)}: {waiting.Message}). La demostración no decide por el usuario: lo cierra como abortado y deja la evidencia.";
                }
                else if (watch.Elapsed > limit)
                {
                    stopped = string.Create(CultureInfo.InvariantCulture, $"El debate superó los {limit.TotalMinutes:0} minutos que la demostración le da y se abortó.");
                }

                if (stopped is not null)
                {
                    await TryAbortAsync(run, "Demostración: " + stopped);
                    break;
                }
            }
        }
        finally
        {
            if (!run.Completion.IsCompleted && stopped is null)
            {
                // The test was cancelled: the debate is not left running behind it.
                await TryAbortAsync(run, "Demostración cancelada: la prueba se detuvo con el debate en curso.");
            }
        }

        DebateResult? result = null;
        try
        {
            result = await run.Completion.WaitAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            stopped = (stopped is null ? string.Empty : stopped + " ") + "El debate no terminó tres minutos después de abortarlo.";
        }

        var snapshot = run.Snapshot();
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            timeline.Enqueue("(la sala dejó de contar)");
        }

        return new DebateWatch(result, snapshot, stopped, awaiting, [.. timeline]);
    }

    private static async Task TryAbortAsync(IDebateRun run, string reason)
    {
        try
        {
            await run.AbortAsync(reason);
        }
        catch (InvalidOperationException)
        {
            // It closed meanwhile.
        }
    }

    private static string Describe(DebateProgress progress)
    {
        var at = progress.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return progress switch
        {
            DebateTurnStarted s => Invariant($"{at} · {s.Participant} · ronda {s.Round} · intento {s.Attempt}: arranca"),
            DebateTurnFinished f => Invariant($"{at} · {f.Participant} · ronda {f.Round} · intento {f.Attempt}: {DebateTexts.Turn(f.Outcome)}"),
            DebateRoundSealed r => Invariant($"{at} · ronda {r.Round}: {r.TurnsInFlight} turnos en curso, sellados hasta que terminen todos"),
            DebateContributionPublished c => Invariant($"{at} · publicada {c.Contribution.Label} de {c.Contribution.Author} ({DebateTexts.Kind(c.Contribution.Kind)}, ronda {c.Contribution.Round})"),
            DebateRoundEnded e => Invariant($"{at} · ronda {e.Round} cerrada: sigue {DebateTexts.Phase(e.Next)}{(e.Judgment is { } j ? " · " + DebateTexts.Judgment(j, e.Limit) : string.Empty)}; tokens {e.Tokens.Sum?.ToString(CultureInfo.InvariantCulture) ?? "sin dato"}"),
            DebateJudgeLaunched j => Invariant($"{at} · juez {j.Judge} · intento {j.Attempt}: arranca"),
            DebateVerdictReceived v => Invariant($"{at} · veredicto {v.Verdict.Id} de {v.Verdict.Verdict.Judge.Agent}: {v.Verdict.Verdict.Decision} ({(v.Verdict.Violations.Count == 0 ? "válido" : "con incidencias")}{(v.Verdict.Corrected ? ", corregido" : string.Empty)})"),
            DebateAwaitingUser w => Invariant($"{at} · espera a una persona: {DebateTexts.Await(w.Awaiting.Reason)} · {w.Awaiting.Message}"),
            DebateFinished f => Invariant($"{at} · termina: {DebateTexts.Phase(f.Result.Phase)}, {DebateTexts.Outcome(f.Result.Outcome)} · {f.Result.Summary}"),
            _ => Invariant($"{at} · {progress.GetType().Name}"),
        };
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
