using System.Diagnostics;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Security;

namespace AgentStudio.Security.Approvals;

/// <summary>How long the gate waits for a person before it takes the silence as a denial.</summary>
public sealed record ApprovalGateOptions
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    public ApprovalGateOptions(TimeSpan? timeout = null)
    {
        var value = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(timeout));
        Timeout = value;
    }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// The approval gate (F3-7): every request is classified, asked to a person through <see cref="IApprovalPrompt"/> and
/// recorded in the project's decision log, <c>ApprovalRequested</c> first and then <c>ApprovalGranted</c> or
/// <c>ApprovalDenied</c>. It fails closed: only an explicit yes from someone named, recorded in the log, is an approval.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The gate never approves by itself, whatever the level: a plan's human-approval node goes through it too, and the
/// level is chosen by the caller. <see cref="ApprovalRiskClassifier"/> only raises the level shown and recorded.</item>
/// <item>A request with no project cannot be recorded, so nobody is asked and the answer is no; the same when
/// <c>ApprovalRequested</c> cannot be written (a log that does not verify, a store that fails).</item>
/// <item>A denial, no answer within <see cref="ApprovalGateOptions.Timeout"/> (also from a dialog that ignores the
/// cancellation), the caller cancelling, the prompt failing, or an approval that does not say who gave it: all of them
/// are a denial, which is recorded and returned, never thrown.</item>
/// <item>Only an answer the prompt gives while the question is still open is the person's (actor User). Every other
/// denial is the system's (actor System, <see cref="SystemId"/>) and says why: the time ran out, the request was
/// cancelled, the prompt failed... even when the prompt answered "no" in someone's name as the question expired.</item>
/// <item>An approval that cannot be recorded is not an approval: the gate returns a denial (and tries to record it).</item>
/// <item>The answer is recorded even when the caller cancelled, so the log never shows a question without an answer.</item>
/// <item>The answer records how the person was confirmed (<see cref="ApprovalDecision.Verification"/>, docs/16 D-13 and
/// F8-11): <c>ApprovalGranted</c> and <c>ApprovalDenied</c> of version 2 with the method, only when it is not the dialog
/// alone. An answer of the dialog alone, and every denial the gate decides for its own reasons, is written as version 1,
/// byte for byte as before Phase 8: a version 1 answer is one the dialog gave.</item>
/// <item>A denial the prompt returns with the method <see cref="ApprovalVerification.WindowsHello"/> is a yes in the dialog
/// that Windows Hello did not confirm (cancelled, failed, or Windows could not be asked): Windows Hello is only asked after a
/// yes, so a person's no never carries it. That is no decision of the person, whose identity was not confirmed: the system
/// denies it (actor System), with the prompt's reason and the method, as it does with a question that expired. Read as the
/// person's no, it would mean something the person did not decide (a no to opening a revision debate drops the
/// challenge). The same holds for a denial with <see cref="ApprovalVerification.WindowsLock"/>: a yes the verified lock of
/// Windows did not confirm (docs/17 D-40 (a)).</item>
/// <item>The fixed barrier (docs/17 D-37 to D-39): an approval counts only when what confirmed the person satisfies its action,
/// by the one rule of the domain (<see cref="PresencePolicy.IsSatisfiedBy"/>, with the method the prompt recorded read as its
/// proof, <see cref="ApprovalRiskClassifier.ProofOf"/>). For an action of the list («Siempre»), only Windows Hello or the
/// verified lock of Windows do. Any other yes, whatever prompt gave it and whatever any setting or file says, is denied by the
/// system (<see cref="PresenceNotConfirmed"/>), with the method the prompt said. The gate knows no setting and needs none:
/// it judges with the setting off, the floor no setting can lower, so no prompt composed without Windows Hello, and no setting
/// turned off, lets one through. What the setting adds to the configurable approvals is the prompt's.</item>
/// </list>
/// </remarks>
public sealed class ApprovalGate(IApprovalPrompt prompt, IDecisionRecorder decisions, TimeProvider timeProvider, ApprovalGateOptions options) : IApprovalGate
{
    /// <summary>Who signs the denials the gate decides by itself.</summary>
    public const string SystemId = "agent-studio";

    /// <summary>The reason of the system's denial of a yes Windows Hello did not confirm, when the prompt gives none.</summary>
    public const string UnconfirmedYes = "Windows Hello no confirmó que quien aprobó eres tú: se deniega.";

    /// <summary>
    /// The reason of the system's denial of a yes to an action of the fixed barrier that neither Windows Hello nor the verified
    /// lock of Windows confirmed (docs/17 D-37 and D-38).
    /// </summary>
    public const string PresenceNotConfirmed =
        "Esta acción pide siempre Windows Hello (o, sin él, el bloqueo de Windows comprobado), y la aprobación no lo trae: se deniega.";

    private static readonly Actor SystemActor = new(ActorKind.System, SystemId);

    /// <summary>
    /// The decision on <paramref name="request"/>, which answers it: its <see cref="ApprovalDecision.Answers"/> is the
    /// request's <see cref="ApprovalRequest.Binding"/>, whatever the prompt returned, so the code that acts can check that
    /// the approval in its hands is the one given to what it is about to do.
    /// </summary>
    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await DecideAsync(request, cancellationToken).ConfigureAwait(false)) with { Answers = request.Binding };
    }

    private async Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var risk = ApprovalRiskClassifier.Classify(request);
        var asked = request with { Risk = risk };
        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return Denial("No hay proyecto en el que registrar la decisión: se deniega sin preguntar.");
        }

        var projectId = request.ProjectId;
        var run = request.RunId ?? string.Empty;
        TaskId? task = string.IsNullOrWhiteSpace(request.TaskId) ? null : new TaskId(request.TaskId);

        try
        {
            await decisions.RecordAsync(
                projectId,
                request.RequestedBy is { } agent ? new Actor(ActorKind.Agent, agent.Value) : SystemActor,
                DecisionKind.ApprovalRequested,
                $"Aprobación pedida ({risk}): {request.Action}",
                new ApprovalRequestedPayload(request.Action, request.Description, risk, task, request.AffectedPaths ?? []) { RunId = run },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing may be approved that the log does not show was asked.
            return Denial($"La petición no se pudo registrar ({ex.Message}): se deniega sin preguntar.");
        }

        var (answer, problem, method) = await AskAsync(asked, cancellationToken).ConfigureAwait(false);
        if (problem is null && answer is { Approved: true })
        {
            try
            {
                await RecordAnswerAsync(projectId, run, task, request.Action, answer, byPerson: true).ConfigureAwait(false);
                return answer;
            }
            catch (Exception ex)
            {
                problem = $"La aprobación de {answer.DecidedBy} no se pudo registrar ({ex.Message}): se deniega.";
                method = ApprovalVerification.Dialog;
            }
        }

        var denial = problem is null && answer is not null ? answer : Denial(problem ?? "Sin respuesta: se deniega.", method);
        try
        {
            await RecordAnswerAsync(projectId, run, task, request.Action, denial, byPerson: problem is null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Approval gate: could not record the denial of '{request.Action}': {ex.Message}");
        }

        return denial;
    }

    /// <summary>
    /// The person's answer, or why there is none that counts, with how the person was confirmed when that is the reason (a
    /// yes Windows Hello did not confirm).
    /// </summary>
    private async Task<(ApprovalDecision? Answer, string? Problem, ApprovalVerification Method)> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        const ApprovalVerification None = ApprovalVerification.Dialog;
        var timeout = options.Timeout;
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // WaitAsync also ends the wait for a dialog that ignores its token.
            var answer = await prompt.AskAsync(request, linked.Token).WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);

            // An answer that comes once the question was withdrawn or expired is nobody's decision, whatever the prompt
            // says (a dialog closed by the cancellation is not a person saying no): the system denies it, with the reason.
            if (cancellationToken.IsCancellationRequested)
            {
                return (null, Cancelled, None);
            }

            if (deadline.IsCancellationRequested)
            {
                return (null, NoAnswer(timeout), None);
            }

            if (answer is null)
            {
                return (null, "El diálogo no devolvió respuesta: se deniega.", None);
            }

            // A yes Windows Hello or the verified lock of Windows did not confirm: whoever said it was not confirmed to be
            // the person (see the remarks).
            if (answer is { Approved: false, Verification: ApprovalVerification.WindowsHello or ApprovalVerification.WindowsLock })
            {
                return (null, string.IsNullOrWhiteSpace(answer.Comment) ? UnconfirmedYes : answer.Comment, answer.Verification);
            }

            if (answer.Approved && string.IsNullOrWhiteSpace(answer.DecidedBy))
            {
                return (null, "La aprobación no dice quién la dio: se deniega.", None);
            }

            // The fixed barrier: a yes counts only with what the one rule of the domain asks of its action, whatever the
            // prompt. The gate knows no setting, so it judges with it off: the floor no setting can lower.
            if (answer.Approved
                && ApprovalRiskClassifier.PresenceActionOf(request) is { } action
                && !PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: false, ApprovalRiskClassifier.ProofOf(answer.Verification)))
            {
                return (null, PresenceNotConfirmed, answer.Verification);
            }

            return (answer, null, None);
        }
        catch (TimeoutException)
        {
            return (null, NoAnswer(timeout), None);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, NoAnswer(timeout), None);
        }
        catch (OperationCanceledException)
        {
            return (null, Cancelled, None);
        }
        catch (Exception ex)
        {
            return (null, $"No se pudo preguntar ({ex.Message}): se deniega.", None);
        }
    }

    private async Task RecordAnswerAsync(string projectId, string run, TaskId? task, string action, ApprovalDecision answer, bool byPerson)
    {
        var decidedBy = string.IsNullOrWhiteSpace(answer.DecidedBy) ? SystemId : answer.DecidedBy;

        // Version 2 only with a method other than the dialog alone (ApprovalAnswers): an answer of the dialog alone keeps the
        // text of version 1.
        await decisions.RecordAsync(
            projectId,
            byPerson ? new Actor(ActorKind.User, decidedBy) : SystemActor,
            answer.Approved ? DecisionKind.ApprovalGranted : DecisionKind.ApprovalDenied,
            $"{(answer.Approved ? "Aprobado" : "Denegado")} por {decidedBy}: {action}",
            ApprovalAnswers.Payload(action, answer.Approved, decidedBy, answer.Comment, task, answer.Verification, run),
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    private const string Cancelled = "La petición se canceló antes de la respuesta: se deniega.";

    private static string NoAnswer(TimeSpan timeout) => $"Sin respuesta en {timeout.TotalMinutes:0.#} min: se deniega.";

    private ApprovalDecision Denial(string why, ApprovalVerification method = ApprovalVerification.Dialog) =>
        new(false, SystemId, timeProvider.GetUtcNow(), why, Verification: method);
}
