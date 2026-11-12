using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Localization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// The coded text of a reason or a summary the decision log anchored (docs/17 D-47, D-48, ADR-0054 addendum of 2026-11-12). The log
/// keeps the Spanish sentence byte for byte (it is chained by SHA-256), so a run read back from it, or resumed, has no code stored: the
/// code is <b>derived at read time</b> from the exact sentence the engine writes, as the memory does. A sentence that is not one of
/// those (a reason of a worker, an agent's own words, a sentence of an older version) gives null, and the screen shows it as it was
/// anchored. The sentences are the ones <see cref="OrchestrationRun"/> and <see cref="RunResumer"/> write; a test walks each of them.
/// </summary>
public static partial class LoggedTexts
{
    [GeneratedRegex(@"^Arranca el intento (\d+): el proveedor lanzó al agente\.$")]
    private static partial Regex AttemptLaunched();

    [GeneratedRegex(@"^Arranca el intento (\d+)\.$")]
    private static partial Regex AttemptStarted();

    [GeneratedRegex(@"^El juez aprobó \(veredicto (.+)\); se concluye al reanudar con el veredicto que ya estaba en el registro\.$")]
    private static partial Regex JudgeApprovedResumed();

    [GeneratedRegex(@"^El veredicto (.+) necesita a una persona; se concluye al reanudar con el veredicto que ya estaba en el registro\.$")]
    private static partial Regex NeedsPersonResumed();

    [GeneratedRegex(@"^Cancelada por el usuario: (.*)$", RegexOptions.Singleline)]
    private static partial Regex CancelledByUser();

    [GeneratedRegex(@"^Espera tu respuesta: el agente hace la pregunta .+\.$")]
    private static partial Regex AwaitingAnswer();

    [GeneratedRegex(@"^El agente de .+ quiso hacer una pregunta más de .+: demasiadas preguntas, revisa los requisitos\.$")]
    private static partial Regex TooManyQuestions();

    [GeneratedRegex(@"^Los nodos (\w+) no están disponibles en la Fase 3\.$")]
    private static partial Regex NodeUnavailable();

    [GeneratedRegex(@"^No hay ningún proveedor «(.+)» para (.+)\.$")]
    private static partial Regex NoProvider();

    [GeneratedRegex(@"^Terminó bien: (\d+) tareas completadas y (\d+) omitidas\.$")]
    private static partial Regex Succeeded();

    [GeneratedRegex(@"^Falló: la política de revisión exige una revisión independiente aprobada de (.+), y no la hubo\.$")]
    private static partial Regex ReviewRequired();

    [GeneratedRegex(@"^Falló\.(?: Sin reintentos: (.+?)\.)?(?: Inalcanzables: (.+?)\.)?(?: Se agotó el presupuesto de ejecuciones de agente \((\d+)\)\.)?$")]
    private static partial Regex Failed();

    /// <summary>The coded text of a reason of a transition of a task, or null when it is not one the engine writes with a template.</summary>
    public static UserText? TaskReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        if (Fixed.TryGetValue(reason, out var code))
        {
            return UserText.Of(code);
        }

        if (AttemptLaunched().Match(reason) is { Success: true } launched)
        {
            return UserText.Of(OrchestrationUserTexts.TaskAttemptLaunched, Int(launched, 1));
        }

        if (AttemptStarted().Match(reason) is { Success: true } started)
        {
            return UserText.Of(OrchestrationUserTexts.TaskAttemptStarted, Int(started, 1));
        }

        if (JudgeApprovedResumed().Match(reason) is { Success: true } approved)
        {
            return UserText.Of(OrchestrationUserTexts.TaskJudgeApprovedResumed, approved.Groups[1].Value);
        }

        if (NeedsPersonResumed().Match(reason) is { Success: true } needs)
        {
            return UserText.Of(OrchestrationUserTexts.TaskVerdictNeedsPersonResumed, needs.Groups[1].Value);
        }

        if (CancelledByUser().Match(reason) is { Success: true } cancelled)
        {
            return UserText.Of(OrchestrationUserTexts.TaskCancelledByUser, cancelled.Groups[1].Value);
        }

        if (NodeUnavailable().Match(reason) is { Success: true } node)
        {
            return UserText.Of(OrchestrationUserTexts.TaskNodeUnavailable, node.Groups[1].Value);
        }

        if (AwaitingAnswer().IsMatch(reason))
        {
            return UserText.Of(UserTextCodes.QuestionAwaitingAnswer);
        }

        if (TooManyQuestions().IsMatch(reason))
        {
            return UserText.Of(UserTextCodes.QuestionTooMany);
        }

        return NoProvider().Match(reason) is { Success: true } provider
            ? UserText.Of(OrchestrationUserTexts.TaskNoProvider, provider.Groups[1].Value, provider.Groups[2].Value)
            : null;
    }

    /// <summary>The coded text of the summary a run finished with (<c>RunFinished</c>), or null when it is not one the engine writes with a template.</summary>
    public static UserText? RunSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        if (FixedSummaries.TryGetValue(summary, out var code))
        {
            return UserText.Of(code);
        }

        if (Succeeded().Match(summary) is { Success: true } done)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictSucceeded, Int(done, 1), Int(done, 2));
        }

        if (CancelledByUser().Match(summary) is { Success: true } cancelled)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictCancelledByUser, cancelled.Groups[1].Value);
        }

        if (ReviewRequired().Match(summary) is { Success: true } review)
        {
            return UserText.Of(OrchestrationUserTexts.VerdictReviewRequired, OrchestrationUserTexts.List(Split(review.Groups[1].Value)));
        }

        if (Failed().Match(summary) is { Success: true } failed && (failed.Groups[1].Success || failed.Groups[2].Success || failed.Groups[3].Success))
        {
            var parts = new List<UserText>();
            if (failed.Groups[1].Success)
            {
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictNoRetries, OrchestrationUserTexts.List(Split(failed.Groups[1].Value))));
            }

            if (failed.Groups[2].Success)
            {
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictUnreachable, OrchestrationUserTexts.List(Split(failed.Groups[2].Value))));
            }

            if (failed.Groups[3].Success)
            {
                parts.Add(UserText.Of(OrchestrationUserTexts.VerdictBudgetSpent, Int(failed, 3)));
            }

            return UserText.Of(OrchestrationUserTexts.VerdictFailed, OrchestrationUserTexts.Sentences(parts));
        }

        return null;
    }

    /// <summary>The reasons the engine writes without arguments, as it anchors them.</summary>
    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["El usuario pidió reintentarla."] = OrchestrationUserTexts.TaskRetryByUser,
        ["Terminó la espera de cuota del proveedor."] = OrchestrationUserTexts.TaskQuotaWaitEnded,
        ["Esperando la aprobación del usuario."] = OrchestrationUserTexts.TaskAwaitingApproval,
        ["Evaluando la condición."] = OrchestrationUserTexts.TaskEvaluatingCondition,
        [RunInspector.InterruptedReason] = OrchestrationUserTexts.TaskInterrupted,
        ["Su pregunta ya tenía respuesta cuando se cerró la aplicación: sigue con un intento nuevo."] = OrchestrationUserTexts.TaskQuestionAnsweredBeforeClose,
        [RunResumer.ReopenedByUser] = OrchestrationUserTexts.TaskResumedByUser,
        [RunResumer.ReopenedByConsent] = OrchestrationUserTexts.TaskResumedByConsent,
    };

    /// <summary>The summaries the engine writes without arguments, as it anchors them.</summary>
    private static readonly Dictionary<string, string> FixedSummaries = new(StringComparer.Ordinal)
    {
        ["Cancelada: la cancelación se había aplicado a todas las tareas antes de la interrupción."] = OrchestrationUserTexts.VerdictCancelledAfterStop,
        ["Se detuvo: el repositorio del usuario cambió durante la ejecución."] = OrchestrationUserTexts.VerdictStoppedRepository,
        ["Se detuvo: la ref de una tarea ya no apunta al commit que se verificó, y no se integró nada de ella."] = OrchestrationUserTexts.VerdictStoppedSourceMoved,
        ["Se detuvo: una evidencia de la integración cambió en el almacén entre su captura y su lectura; esa integración no se da por buena, ni se revisa ni se entrega."] = OrchestrationUserTexts.VerdictStoppedEvidence,
        [RunInspector.InterruptedSummary] = OrchestrationUserTexts.VerdictInterruptedInLog,
    };

    /// <summary>A pause or a resume of the dispatch, as the inspector shows it: the own of the person, or of whoever else decided.</summary>
    public static UserText Dispatch(bool paused, string decidedBy, bool byUser) => byUser
        ? UserText.Of(paused ? OrchestrationUserTexts.RunPausedByYou : OrchestrationUserTexts.RunResumedByYou)
        : UserText.Of(paused ? OrchestrationUserTexts.RunPausedBy : OrchestrationUserTexts.RunResumedBy, decidedBy);

    private static List<string> Split(string list) => [.. list.Split(", ", StringSplitOptions.RemoveEmptyEntries)];

    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
}
