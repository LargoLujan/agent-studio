using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Security;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// Which action of the fixed barrier approving a plan is (docs/17 D-37 and D-41): the capabilities are the writers at medium
/// integrity <see cref="MediumIntegrityWriters"/> finds for the plan, its template and its profile, the same reading the check
/// of the user's repository and the warning of the plan use (ADR-0018, point 3). The barrier has no list of its own.
/// </summary>
public static class PlanPresence
{
    /// <summary>
    /// <see cref="PresenceAction.ApprovePlanWithHighRiskCapability"/> when <paramref name="plan"/> makes any writer at medium
    /// integrity (a shell, Codex writing, a CLI or tools whose confinement was not measured, a step without isolation, an
    /// unknown agent or step); <see cref="PresenceAction.ApprovePlan"/> otherwise. Without the template or the profile the
    /// writers cannot be read, so it counts as one: fail closed.
    /// </summary>
    /// <param name="claudeWritesConfined">Whether the installed Claude Code is a version whose confinement was measured, as the engine asks it.</param>
    public static PresenceAction Of(PlanDefinition plan, IEnumerable<AgentDefinition>? roster, VerificationProfile? profile, bool claudeWritesConfined)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (roster is null || profile is null)
        {
            return PresenceAction.ApprovePlanWithHighRiskCapability;
        }

        return MediumIntegrityWriters.Find(plan, roster, profile, claudeWritesConfined).Count > 0
            ? PresenceAction.ApprovePlanWithHighRiskCapability
            : PresenceAction.ApprovePlan;
    }}
