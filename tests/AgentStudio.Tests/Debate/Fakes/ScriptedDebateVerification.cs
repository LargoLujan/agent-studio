using System.IO;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>The trusted verification profile of the project as the composition root reads it; null for none.</summary>
internal sealed class ScriptedDebateTestProfiles(VerificationProfile? profile) : IDebateTestProfiles
{
    public Task<VerificationProfile?> FindAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult(profile);

    /// <summary>A profile with an isolated test step and a step that would run with the user's permissions.</summary>
    public static VerificationProfile Profile() => new(
        "pruebas del proyecto",
        [
            new VerificationStep("tests", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.None, null, TimeSpan.FromMinutes(5)),
            new VerificationStep("deploy", "Paso sin aislamiento", "deploy.cmd", [], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(1), ProcessIsolation.None),
        ]);
}

/// <summary>
/// The isolated verifier as a double: every call is kept, and each step leaves its evidence in the store, captured by the
/// system, in the scope it was given (the debate, the task <c>pruebas</c>, its attempt and the commit it verified).
/// </summary>
internal sealed class ScriptedDebateVerificationRunner(IEvidenceStore evidence) : IVerificationRunner
{
    private readonly Lock _gate = new();
    private readonly List<(string WorkingDirectory, IReadOnlyList<VerificationStep> Steps, EvidenceScope Scope)> _calls = [];

    public IReadOnlyList<(string WorkingDirectory, IReadOnlyList<VerificationStep> Steps, EvidenceScope Scope)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public async Task<IReadOnlyList<VerificationResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            _calls.Add((workingDirectory, steps, scope));
        }

        var results = new List<VerificationResult>();
        foreach (var step in steps)
        {
            var draft = new EvidenceDraft(
                step.Kind,
                EvidenceOrigin.CapturedBySystem,
                step.Title,
                "agent-studio",
                step.Kind == EvidenceKind.TestRun ? new TestRunSummary(12, 0, 0, 0) : null,
                RunId: scope.Run?.Value,
                TaskId: scope.Task?.Value,
                Attempt: scope.Attempt,
                MediaType: "text/plain",
                SubjectCommit: scope.SubjectCommit);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes($"{step.Id}: 12 correctas"));
            var record = await evidence.CaptureAsync(scope.ProjectId, draft, content, cancellationToken);
            results.Add(new VerificationResult(step.Id, record, Succeeded: true, TimedOut: false, ExitCode: 0));
        }

        return results;
    }
}
