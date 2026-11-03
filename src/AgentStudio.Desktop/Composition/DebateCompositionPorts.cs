using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Debate;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The trusted verification profile of a project (ADR-0016, the one the user saved), as the debate room («Ejecutar
/// pruebas», docs/15 D-27) and the audit of the whole project (D-29) need it. Both modules cannot see the engine
/// (ADR-0002), so the composition adapts the desktop's <see cref="IVerificationProfileSource"/>.
/// </summary>
internal sealed class VerificationProfilePorts(IVerificationProfileSource source) : IDebateTestProfiles, IAuditVerificationProfiles
{
    /// <summary>
    /// The profile for the room. A file that cannot be used means the room cannot run the tests, which it then says (it
    /// never pretends to); the audit, which needs the reason, gets it through <see cref="LoadAsync"/>.
    /// </summary>
    public async Task<VerificationProfile?> FindAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        try
        {
            return await source.LoadAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) =>
        source.LoadAsync(project, cancellationToken);
}

/// <summary>The fixed requirements of the debate modes that have them (docs/15 D-36): only the orchestrator review.</summary>
internal static class DebateModeRequirementSource
{
    public static DebateModeRequirements For(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return mode => mode == DebateMode.OrchestratorReview ? DebateModes.OrchestratorReview(time.GetUtcNow()) : null;
    }
}
