using System.IO;
using AgentStudio.Application.Orchestration;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// The machine-local folders of the app, under <c>%LOCALAPPDATA%\AgentStudio</c> (docs/13 section 2): the worktrees of
/// the runs (<c>wt</c>), the dossier packages of the judge (<c>dossiers</c>) and the skill sessions of each agent run
/// (<c>skill-sessions</c>). The CLI adapters keep their per-run files in <c>runs</c>, a folder the providers fix
/// themselves. Injectable so tests use a temporary folder.
/// </summary>
public sealed record LocalDataPaths(string BaseDirectory)
{
    public string WorktreeRoot => Path.Combine(BaseDirectory, "wt");

    public string DossierRoot => Path.Combine(BaseDirectory, "dossiers");

    public string SkillSessionRoot => Path.Combine(BaseDirectory, "skill-sessions");

    /// <summary>The folders the orchestration engine works in.</summary>
    public OrchestrationPaths Orchestration => new(WorktreeRoot, DossierRoot, SkillSessionRoot);

    /// <summary>%LOCALAPPDATA%\AgentStudio.</summary>
    public static LocalDataPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio"));
}
