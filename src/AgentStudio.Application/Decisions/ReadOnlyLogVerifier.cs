using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Application.Decisions;

/// <summary>What a read-only verification found: the log checked against its anchor, and how that anchor stands.</summary>
public sealed record ReadOnlyLogVerification(DecisionLogVerification Verification, AnchorStatus Anchor);

/// <summary>
/// Verifies a decision log read from another project against the anchor of its location, writing nothing (docs/14 D-05):
/// the checks of <see cref="DecisionRecorder"/> when it loads a log (ADR-0010 and its addenda), without the writer. It
/// never confirms an anchor to the store (<see cref="IDecisionLogAnchorStore.ConfirmAsync"/>), so an anchor kept under an
/// old name stays where it is, and it offers no acceptance of an unanchored history: only the project's own writer can
/// record one.
/// <list type="bullet">
/// <item>An anchor that recognises the log's location (or the old one of its id): the chain and the log against it
/// (<see cref="AnchorStatus.Anchored"/>, valid or not).</item>
/// <item>No anchor: an empty log is a project that never recorded anything (<see cref="AnchorStatus.NoHistory"/>); one
/// with entries is not trusted (<see cref="AnchorStatus.Unanchored"/>).</item>
/// <item>The anchor of another project at the location (<see cref="AnchorStatus.AnotherProject"/>), or one that cannot
/// be read (<see cref="AnchorStatus.Unreadable"/>): no trust.</item>
/// </list>
/// A broken chain is reported as such whatever the anchor says.
/// </summary>
public sealed class ReadOnlyLogVerifier(IDecisionLogAnchorStore anchors)
{
    public async Task<ReadOnlyLogVerification> VerifyAsync(string projectId, IReadOnlyList<DecisionEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(entries);
        var log = DecisionLog.Load(entries);
        var chain = log.Verify();

        DecisionLogAnchor? anchor;
        try
        {
            anchor = await anchors.ReadAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (DecisionLogAnchorMismatchException ex)
        {
            return new(chain.IsValid ? Untrusted($"{ex.Message} The log of this database can only be trusted against the anchor of its location.") : chain, AnchorStatus.AnotherProject);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(chain.IsValid ? Untrusted($"The anchor of the decision log outside the project cannot be read, so the log cannot be trusted: {ex.Message}") : chain, AnchorStatus.Unreadable);
        }

        if (anchor is not null)
        {
            return new(log.Verify(anchor), AnchorStatus.Anchored);
        }

        var count = log.Entries.Count;
        if (count == 0)
        {
            return new(chain, AnchorStatus.NoHistory);
        }

        return new(
            chain.IsValid
                ? Untrusted($"The decision log has {count} entries, but there is no anchor outside the project for its location: the project was moved or copied there, or its log was written elsewhere.")
                : chain,
            AnchorStatus.Unanchored);
    }

    private static DecisionLogVerification Untrusted(string problem) => new(false, null, problem);
}
