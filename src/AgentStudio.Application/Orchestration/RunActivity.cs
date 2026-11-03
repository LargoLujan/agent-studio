namespace AgentStudio.Application.Orchestration;

/// <summary>
/// Whether a run or a planning of a project is going on in this process (docs/14 D-19 and D-22). While it is, the writes
/// of memory to the log wait (so the head of the log does not move between showing an approval or a verdict and using
/// it) and the Markdown export of the memory is refused (the check of the user's repository compares everything under
/// <c>.agentstudio/</c>, so an export would fail the task). The orchestrator reports to it (<c>OrchestrationActivity</c>,
/// not the orchestrator itself, which would make a cycle through the memory), and releases a project before the end of
/// its run can be seen, so what reacts to that end finds it no longer busy.
/// </summary>
public interface IRunActivity
{
    bool IsBusy(string projectId);
}
