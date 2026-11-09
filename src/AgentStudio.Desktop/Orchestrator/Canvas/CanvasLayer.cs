namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// The layer of the canvas that is shown (docs/17 D-07): «Equipo» while the person prepares, «Plan» as soon as a proposal
/// arrives, and during a run the one the person chooses. The strip of the team is above both and never goes away.
/// </summary>
public enum CanvasLayer
{
    /// <summary>The Plan layer: the tasks and their dependencies; its model has not changed.</summary>
    Plan,

    /// <summary>The Team layer: the agents of the template and the repository.</summary>
    Team,
}
