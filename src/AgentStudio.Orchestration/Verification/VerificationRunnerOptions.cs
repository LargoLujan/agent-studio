namespace AgentStudio.Orchestration.Verification;

/// <param name="ScratchRoot">
/// Folder, outside every worktree, where the runner keeps the output of a step while it runs and, for a step without
/// isolation, the fresh folder that replaces <see cref="VerificationRunner.ResultsFolderToken"/>. Each step gets its own
/// subfolder, removed when the step ends. The default is <c>%TEMP%\AgentStudio\verification</c>.
/// </param>
/// <param name="IsolationGate">
/// The gate isolated runs take, so that only one runs at a time in this process (ADR-0017). Null, the default, is one gate
/// for the whole process. Only tests give their own, so that they do not wait for the isolated runs of other tests. Across
/// processes the work area factory orders them: it holds the lock of the user for as long as an area exists.
/// </param>
public sealed record VerificationRunnerOptions(string ScratchRoot, SemaphoreSlim? IsolationGate = null)
{
    public static VerificationRunnerOptions Default { get; } = new(Path.Combine(Path.GetTempPath(), "AgentStudio", "verification"));
}
