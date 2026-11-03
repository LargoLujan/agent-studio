using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Memory.Store;

namespace AgentStudio.Memory.Derivation;

/// <summary>
/// The decision «Confiar en la bitácora de este proyecto», and the same by days (docs/17 D-02; ADR-0047 and its addendum of
/// 2026-11-03), which the document «Memoria» puts on screen. Each change is a <c>JournalTrustChanged</c> entry of the log,
/// outside every run and signed by the person; withdrawing it is another one.
/// <list type="bullet">
/// <item><b>What it covers.</b> The journal entries Agent Studio did not write (<see cref="MemoryOrigin.Agent"/>), the days to
/// come included. The most concrete level decides: a review of an entry over its day, and a decision about a day over the
/// one about the project (<see cref="MemoryLedger.JournalTrustOf"/>); within a level, the most recent. An entry inherited from a history accepted as a new origin is never
/// covered, and a decision recorded before that acceptance does not count: a planted log cannot bring the journal's trust
/// with it.</item>
/// <item><b>Only changes.</b> A decision that would leave the state as it is records nothing. A day without a decision of
/// its own follows the project, so «no» for it is recorded: it keeps that day out while the project is trusted.</item>
/// <item><b>Like the memory.</b> Nothing is recorded on a log that does not verify
/// (<see cref="DecisionLogIntegrityException"/>), and a decision waits while a run or a planning of the project goes on
/// (<see cref="IRunActivity"/>), asking again every <see cref="ProjectMemory.BusyPollInterval"/>. The comment is redacted.</item>
/// </list>
/// </summary>
public sealed class JournalTrust(IDecisionRecorder recorder, IProjectMemory memory, IRunActivity activity, ISecretRedactor redactor, TimeProvider timeProvider) : IJournalTrust
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<JournalTrustState> ReadAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var ledger = await LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        var journal = await memory.RecallAsync(project, new MemoryQuery(Kinds: [MemoryKind.JournalEntry], MaxEntries: int.MaxValue, TrustedOnly: false), ct).ConfigureAwait(false);
        return new JournalTrustState(ledger.JournalTrusted, ledger.TrustedJournalDays, journal.Count(e => e.Trust == MemoryTrust.Quarantined));
    }

    public async Task SetAsync(ProjectContext project, JournalTrustScope scope, DateOnly? day, bool trusted, string decidedBy, string? comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        // The payload checks the scope, the day and who decides before anything waits.
        var note = string.IsNullOrWhiteSpace(comment) ? null : redactor.Redact(comment.Trim());
        var decision = JournalTrustChangedPayload.Deciding(scope, day, trusted, decidedBy, note);
        var projectId = project.ProjectId;
        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        while (true)
        {
            while (activity.IsBusy(projectId))
            {
                await Task.Delay(ProjectMemory.BusyPollInterval, timeProvider, ct).ConfigureAwait(false);
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (activity.IsBusy(projectId))
                {
                    continue;
                }

                // A day without a decision of its own follows the project: «not trusted» is then a change for it.
                var ledger = await LoadAsync(projectId, ct).ConfigureAwait(false);
                bool? current = decision.Day is { } date ? ledger.JournalDayDecision(date) : ledger.JournalTrusted;
                if (current == trusted)
                {
                    return;
                }

                await recorder.RecordAsync(
                    projectId,
                    new Actor(ActorKind.User, decidedBy),
                    DecisionKind.JournalTrustChanged,
                    DerivationTexts.JournalTrustSummary(scope, day, trusted, decidedBy),
                    decision,
                    cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>What the log of the project says about its memory; a log that does not verify is never trusted, nor extended.</summary>
    private async Task<MemoryLedger> LoadAsync(string projectId, CancellationToken ct)
    {
        var state = await recorder.LoadAsync(projectId, ct).ConfigureAwait(false);
        return state.Verification.IsValid
            ? MemoryLedger.From(state.Entries)
            : throw new DecisionLogIntegrityException(projectId, state.Verification);
    }
}
