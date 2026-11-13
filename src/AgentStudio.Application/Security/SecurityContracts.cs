using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Security;

// Phase 2-7 contracts.

public enum RiskLevel
{
    Low,
    Destructive,
    Critical,
}

/// <param name="ProjectId">The project, run and task the action belongs to, so the answer can be recorded in the right place.</param>
/// <param name="Binding">
/// What exactly is approved: a token made from the fields the action will act on (for a delivery,
/// <c>DeliveryApproval.Bind</c>). The gate answers with it (<see cref="ApprovalDecision.Answers"/>), and the code that
/// acts checks both against what it is about to do, so the approval cannot be used for anything else.
/// </param>
public sealed record ApprovalRequest(
    string Action,
    string Description,
    RiskLevel Risk,
    AgentId? RequestedBy,
    IReadOnlyList<string> AffectedPaths,
    string? ProjectId = null,
    string? RunId = null,
    string? TaskId = null,
    string? Binding = null);

/// <summary>How the person who answered an approval was confirmed (docs/16 D-13, F8-11). The answer in the log records it.</summary>
public enum ApprovalVerification
{
    /// <summary>The dialog alone, as always: Windows Hello was not asked (the action is not critical, or it is turned off). Every answer recorded before Phase 8 was given this way.</summary>
    Dialog,

    /// <summary>Windows Hello was asked after the dialog: with an approval, it confirmed the person in front of the computer.</summary>
    WindowsHello,

    /// <summary>
    /// «Sin Hello: no disponible»: Windows Hello was to be asked but could not be (not set up for the user, no device, turned
    /// off by a policy), so the dialog decided alone, with a warning.
    /// </summary>
    WindowsHelloUnavailable,

    /// <summary>
    /// Phase 13 (docs/17 D-40 (a)): Windows Hello could not be asked for an action of the fixed barrier, so the app locked
    /// Windows and the person unlocked it with their PIN or password, which Agent Studio never sees. The state of the session
    /// confirmed the unlock came after the request (<see cref="IWindowsLockConfirmation"/>). Appended: the answers record the
    /// method by its name, so no stored answer changes.
    /// </summary>
    WindowsLock,
}

/// <param name="Answers">The <see cref="ApprovalRequest.Binding"/> of the request this decision answers; the gate sets it.</param>
/// <param name="Verification">How the person was confirmed (docs/16 D-13): the dialog alone unless Windows Hello was asked. The gate records it with the answer.</param>
public sealed record ApprovalDecision(
    bool Approved,
    string DecidedBy,
    DateTimeOffset At,
    string? Comment,
    string? Answers = null,
    ApprovalVerification Verification = ApprovalVerification.Dialog);

/// <summary>Asks a human before destructive or critical actions. Every request and answer goes to the decision log.</summary>
public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// What the gate calls to ask a person (the desktop app shows a dialog). Not answering, cancelling or failing to ask is
/// the gate's problem: it treats all of them as a denial. An implementation only returns what the person answered; when
/// there is no answer it throws (the cancellation, or the failure), so the gate denies in its own name.
/// </summary>
public interface IApprovalPrompt
{
    Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Secrets owned by Agent Studio (for example, an API key the user chooses to configure), encrypted with DPAPI.
/// It never reads, copies or stores the credentials of the provider CLIs.
/// </summary>
public interface ICredentialVault
{
    Task StoreAsync(string name, string secret, CancellationToken cancellationToken);

    Task<string?> ReadAsync(string name, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Whether a secret exists, without decrypting it (docs/20 D-15): what lets the screen say «hay un token guardado», and lets the checker skip the
/// presence when there is none, without reading the vault. Separate from <see cref="ICredentialVault"/> so that contract does not change.
/// </summary>
public interface ICredentialVaultProbe
{
    Task<bool> ContainsAsync(string name, CancellationToken cancellationToken);
}
