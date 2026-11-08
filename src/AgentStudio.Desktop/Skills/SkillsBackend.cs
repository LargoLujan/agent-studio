using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Infrastructure.Skills;

namespace AgentStudio.Desktop.Skills;

/// <summary>
/// The services the Skills screen talks to (docs/14 D-26 and D-29), all through Application ports. Any of them may be
/// missing (a shell composed without them): the screen then says what is missing instead of pretending.
/// </summary>
/// <param name="Catalog">Reads the catalog with its warnings (<see cref="SkillCatalog.ListWithReportAsync"/>).</param>
/// <param name="Activations">The history of activation decisions; the screen only appends the user's own.</param>
/// <param name="Roster">The agents of the project, one switch each.</param>
/// <param name="Health">The local health of each skill, without network.</param>
/// <param name="Recommendations">The deterministic and the assisted recommendation.</param>
/// <param name="Workspace">Opens the project, for its id (activations and roster are per project).</param>
/// <param name="Approvals">
/// The approval gate that asks the user for the OK before every assisted recommendation and records it in the project's log
/// (<c>ApprovalRequested</c>, then <c>ApprovalGranted</c> or <c>ApprovalDenied</c>; docs/17 §0.2 and F13-20), as every other
/// approval.
/// </param>
public sealed record SkillsBackend(
    Func<CancellationToken, Task<SkillCatalogReport>>? Catalog = null,
    ISkillActivationStore? Activations = null,
    IAgentRoster? Roster = null,
    ISkillHealthReader? Health = null,
    ISkillRecommendationService? Recommendations = null,
    IProjectWorkspace? Workspace = null,
    IApprovalGate? Approvals = null)
{
    public static SkillsBackend None { get; } = new();
}

/// <summary>Creates the «Skills» document for the open project (or for none).</summary>
public interface ISkillsDocumentFactory
{
    SkillsDocumentViewModel Create(string? projectRoot);
}
