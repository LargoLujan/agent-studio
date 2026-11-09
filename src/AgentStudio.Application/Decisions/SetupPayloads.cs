using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Decisions;

/// <summary>
/// The configurator of a project applied what the person reviewed (docs/17 D-26 and D-27): the hash of each thing it wrote
/// through its usual gate, null for a step the person skipped. The template of agents leaves no trace in the log today; with
/// this entry it does. It belongs to no run (<see cref="DecisionPayload.NoRun"/>). The configurator has no gates of its own:
/// the profile still asked Windows Hello, the skills were still decided by the person, the template still withdrew the approval
/// of the plan.
/// </summary>
/// <param name="RosterHash">The hash of the template saved (<c>RosterHash</c>), or null.</param>
/// <param name="BriefSha256">The SHA-256 the log announced for the brief saved (<c>MemoryContent.Sha256</c>), or null.</param>
/// <param name="VerificationProfileHash">The content hash of the verification profile written, or null.</param>
/// <param name="SkillActivationsSha256">The SHA-256 of the activations of skills decided, or null.</param>
/// <param name="OptionsSha256">The SHA-256 of the options of the project saved (budget, tasks at once, time per attempt), or null.</param>
/// <param name="DecidedBy">Who applied it.</param>
public sealed record ProjectSetupAppliedPayload(
    string? RosterHash,
    string? BriefSha256,
    string? VerificationProfileHash,
    string? SkillActivationsSha256,
    string? OptionsSha256,
    string DecidedBy) : DecisionPayload
{
    /// <summary>What the configurator applied, outside every run.</summary>
    /// <exception cref="ArgumentException">Nothing applied, a hash that is not a SHA-256, or no one decided.</exception>
    public static ProjectSetupAppliedPayload Applying(
        string? rosterHash,
        string? briefSha256,
        string? verificationProfileHash,
        string? skillActivationsSha256,
        string? optionsSha256,
        string decidedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        string?[] hashes = [rosterHash, briefSha256, verificationProfileHash, skillActivationsSha256, optionsSha256];
        if (hashes.All(h => h is null))
        {
            throw new ArgumentException("The configurator applied nothing: there is nothing to record.", nameof(rosterHash));
        }

        if (hashes.Any(h => h is not null && !ContentHash.IsSha256Hex(h)))
        {
            throw new ArgumentException("Each thing applied is named by its SHA-256 (64 lowercase hex characters).", nameof(rosterHash));
        }

        return new ProjectSetupAppliedPayload(rosterHash, briefSha256, verificationProfileHash, skillActivationsSha256, optionsSha256, decidedBy)
        {
            RunId = NoRun,
        };
    }
}
