using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit.Fixtures;

/// <summary>
/// The «Auditar proyecto completo» service over the log of a <see cref="PreparedRun"/> (empty until a test starts the run,
/// or with Claude's F3-8 run in it) and doubles for everything that is not the log: the template of agents (Codex's judge
/// <see cref="AuditWorld.Judge"/> and Claude's <see cref="ClaudeJudge"/>), the repository (its HEAD and the files a copy of
/// it gets), the isolated verifier, the approval gate (which answers with the binding, as the real one does), the project's
/// activity, the trusted profile and a scripted judge that approves on the green test run of HEAD.
/// </summary>
internal sealed class ProjectAuditBench : IDisposable
{
    public static readonly AgentId ClaudeJudge = new("juez-claude");

    public static readonly string Head = CommitOf("head");

    public ProjectAuditBench(PreparedRun? world = null)
    {
        World = world ?? PreparedRun.Create();
        Root = Path.Combine(Path.GetTempPath(), "agentstudio-audit-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(Root, "repo"));
        Project = new ProjectContext(AuditWorld.Project, Path.Combine(Root, "repo"));
        Template = new FakeTemplate([.. AuditWorld.Roster(), Agent(ClaudeJudge, AgentRole.Judge, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), ["Read", "Grep", "Glob"])]);
        Repository = new AuditRepository(Head);
        Verifier = new AuditVerifier(World.Evidence);
        Profiles = new FixedProfiles(Profile());
        Judges = new FakeJudgeFactory(launch =>
        {
            Launches.Add(launch);
            SnapshotSeen.Add(Directory.Exists(launch.WorkingDirectory) ? [.. Directory.EnumerateFiles(launch.WorkingDirectory, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(launch.WorkingDirectory, f).Replace('\\', '/')).Order(StringComparer.Ordinal)] : []);
            var identity = IdentityOf?.Invoke(launch) ?? new JudgeIdentity(launch.Agent.Id, launch.Agent.Model with { Model = "gpt-5" });
            return Judge = new ScriptedJudge(identity, d => Evaluate(d, identity), Revise is { } revise ? (d, v, issues) => revise(d, v, issues, identity) : null) { ReviseFailure = ReviseFailure };
        });
        Dossiers = new ProjectDossierBuilder(World.Evidence, new JudgingOptions(Path.Combine(Root, "dossiers")));
        Options = new AuditOptions(Path.Combine(Root, "snapshots"));
        Recorder = new LoadSpyRecorder(World.Recorder);
        Service = new ProjectAuditService(
            Recorder,
            World.Requirements,
            Template,
            Repository,
            Verifier,
            World.Evidence,
            Verdicts,
            Audits,
            Judges,
            Gate,
            Activity,
            Profiles,
            Dossiers,
            Options,
            World.Clock);
    }

    public PreparedRun World { get; }

    public string Root { get; }

    public ProjectContext Project { get; }

    public FakeTemplate Template { get; }

    public AuditRepository Repository { get; }

    public AuditVerifier Verifier { get; }

    public FixedProfiles Profiles { get; }

    public FakeVerdictStore Verdicts { get; } = new();

    public FakeProjectAudits Audits { get; } = new();

    public FakeJudgeFactory Judges { get; }

    public BindingApprovalGate Gate { get; } = new();

    public FakeProjectActivity Activity { get; } = new();

    public ProjectDossierBuilder Dossiers { get; }

    public AuditOptions Options { get; }

    /// <summary>The log of the world as the service reads and writes it, telling which loads it asks for.</summary>
    public LoadSpyRecorder Recorder { get; }

    public ProjectAuditService Service { get; }

    /// <summary>What every launch of a judge was given, and the files of its working directory at that moment.</summary>
    public List<JudgeLaunch> Launches { get; } = [];

    public List<IReadOnlyList<string>> SnapshotSeen { get; } = [];

    /// <summary>The judge of the last launch.</summary>
    public ScriptedJudge? Judge { get; private set; }

    /// <summary>The identity the factory gives the judge (by default the template's agent on its own binding).</summary>
    public Func<JudgeLaunch, JudgeIdentity>? IdentityOf { get; set; }

    /// <summary>The judge's verdict; by default an approval resting on the green test run of HEAD.</summary>
    public Func<JudgeDossier, JudgeIdentity, Verdict>? Verdict { get; set; }

    public Func<JudgeDossier, Verdict, IReadOnlyList<VerdictViolation>, JudgeIdentity, Verdict>? Revise { get; set; }

    public Exception? ReviseFailure { get; set; }

    public IReadOnlyList<DecisionEntry> Entries => World.Entries;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The green test run of HEAD the verifier captured for the audit, as the dossier holds it.</summary>
    public static EvidenceRecord HeadTestRun(JudgeDossier dossier) =>
        dossier.Evidence.Last(e => e.Kind == EvidenceKind.TestRun && e.SubjectCommit == Head);

    public static Verdict Approval(JudgeDossier dossier, JudgeIdentity identity) =>
        ScriptedJudge.From(JudgeAnswers.Approval(dossier, HeadTestRun(dossier).Id), identity);

    public ProjectAuditRequest Request(AgentId? judge = null, params VendorId[] declared) =>
        new(Project, World.Frozen.ContentHash, judge ?? AuditWorld.Judge, declared);

    public async Task SaveRequirementsAsync() => await World.Requirements.SaveAsync(AuditWorld.Project, World.Frozen, Ct);

    public Task<ProjectAuditProposal> PrepareAsync(ProjectAuditRequest request) => Service.PrepareAsync(request, Ct);

    public Task<AuditReport> AuditAsync(ProjectAuditProposal proposal) => Service.AuditAsync(proposal, null, Ct);

    /// <summary>The entries of the audit's unit in the log.</summary>
    public IReadOnlyList<DecisionEntry> EntriesOf(RunId audit) => RunLog.EntriesOf(Entries, audit.Value);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary folder a test left open is not a failure of the test.
        }
    }

    private Verdict Evaluate(JudgeDossier dossier, JudgeIdentity identity) => (Verdict ?? Approval)(dossier, identity);
}

/// <summary>The project's template of agents.</summary>
internal sealed class FakeTemplate(IReadOnlyList<AgentDefinition> agents) : IAgentRoster
{
    private readonly List<AgentDefinition> _agents = [.. agents];

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents]);

    public AgentDefinition? Find(AgentId id) => _agents.FirstOrDefault(a => a.Id == id);

    public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
    {
        _agents.RemoveAll(a => a.Id == agent.Id);
        _agents.Add(agent);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken)
    {
        _agents.RemoveAll(a => a.Id == agent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The user's repository as the audit sees it: where HEAD is, whether there are uncommitted changes, and the files a copy
/// of HEAD gets (a README, a guide under <c>docs/</c> and a source file by default). Everything else is not for the audit.
/// </summary>
internal sealed class AuditRepository(string head) : IWorktreeManager
{
    public string Head { get; set; } = head;

    public bool Uncommitted { get; set; }

    public Exception? InspectFailure { get; set; }

    public Exception? ArchiveFailure { get; set; }

    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal)
    {
        ["README.md"] = "# Calc\nUna librería de cálculo.",
        ["docs/guia.md"] = "Guía de Calc.",
        ["docs/adr/0001-decision.md"] = "Decisión 1.",
        ["src/Calc.cs"] = "public static class Calc { public static int Multiply(int a, int b) => a * b; }",
    };

    public List<(string Repository, string Commit, string Destination)> Archives { get; } = [];

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) =>
        InspectFailure is { } failure
            ? Task.FromException<RepositoryState>(failure)
            : Task.FromResult(new RepositoryState(repoRoot, Head, "main", Uncommitted, new Version(2, 45, 2)));

    public Task ArchiveCommitAsync(string repoRoot, string commit, string destination, CancellationToken cancellationToken)
    {
        Archives.Add((repoRoot, commit, destination));
        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException("The destination of a copy must be empty.");
        }

        foreach (var (path, content) in Files)
        {
            var target = Path.Combine(destination, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        return ArchiveFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken) =>
        throw NotForTheAudit();

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken) => throw NotForTheAudit();

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken) => throw NotForTheAudit();

    private static NotSupportedException NotForTheAudit() => new("The audit only inspects the repository and copies a commit.");
}

/// <summary>The isolated verifier: captures, as the system, a build log and a test run (green unless told) of the commit of the scope.</summary>
internal sealed class AuditVerifier(IEvidenceStore evidence) : IVerificationRunner
{
    public List<(string WorkingDirectory, EvidenceScope Scope, IReadOnlyList<VerificationStep> Steps)> Calls { get; } = [];

    public TestRunSummary TestRun { get; set; } = Green;

    public async Task<IReadOnlyList<VerificationResult>> RunAsync(string workingDirectory, IReadOnlyList<VerificationStep> steps, EvidenceScope scope, IProgress<ProcessOutputLine>? output, CancellationToken cancellationToken)
    {
        Calls.Add((workingDirectory, scope, steps));
        var results = new List<VerificationResult>();
        foreach (var step in steps)
        {
            var summary = step.Kind == EvidenceKind.TestRun ? TestRun : null;
            var succeeded = summary?.Succeeded ?? true;
            using var content = new MemoryStream(Encoding.UTF8.GetBytes($"$ {step.Executable} {string.Join(' ', step.Arguments)}\nexit {(succeeded ? 0 : 1)}\n"));
            var record = await evidence.CaptureAsync(
                scope.ProjectId,
                new EvidenceDraft(step.Kind, EvidenceOrigin.CapturedBySystem, $"{step.Title} ({step.Id})", "agent-studio", summary, RunId: scope.Run?.Value, TaskId: scope.Task?.Value, Attempt: scope.Attempt, MediaType: "text/plain", SubjectCommit: scope.SubjectCommit),
                content,
                cancellationToken);
            results.Add(new VerificationResult(step.Id, record, succeeded, false, succeeded ? 0 : 1));
            if (!succeeded)
            {
                break;
            }
        }

        return results;
    }
}

/// <summary>The project's trusted verification profile, or the failure of reading it.</summary>
internal sealed class FixedProfiles(VerificationProfile? profile) : IAuditVerificationProfiles
{
    public VerificationProfile? Profile { get; set; } = profile;

    public Exception? Failure { get; set; }

    public Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) =>
        Failure is { } failure ? Task.FromException<VerificationProfile?>(failure) : Task.FromResult(Profile);
}

/// <summary>The <c>project_audits</c> table: insert-only headers.</summary>
internal sealed class FakeProjectAudits : IProjectAuditRegistry
{
    public List<ProjectAuditRecord> Created { get; } = [];

    public Task CreateAsync(ProjectAuditRecord audit, CancellationToken cancellationToken)
    {
        Created.Add(audit);
        return Task.CompletedTask;
    }

    public Task<ProjectAuditRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken) =>
        Task.FromResult(Created.LastOrDefault(a => a.ProjectId == projectId && a.Id == id));

    public Task<IReadOnlyList<ProjectAuditRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProjectAuditRecord>>([.. Created.Where(a => a.ProjectId == projectId)]);
}

/// <summary>The approval gate as the user answers it: it approves unless told, and answers with the binding of the request.</summary>
internal sealed class BindingApprovalGate : IApprovalGate
{
    public List<ApprovalRequest> Requests { get; } = [];

    public bool Approve { get; set; } = true;

    /// <summary>When set, the binding the decision answers instead of the request's.</summary>
    public string? Answers { get; set; }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new ApprovalDecision(Approve, "usuario", DateTimeOffset.UnixEpoch, Approve ? "adelante" : "ahora no", Answers ?? request.Binding));
    }
}

/// <summary>One activity per project: a test holds the project with another activity, and sees what the audit entered.</summary>
internal sealed class FakeProjectActivity : IProjectActivity
{
    private ProjectActivityKind? _held;

    public List<ProjectActivityKind> Entered { get; } = [];

    public int Released { get; private set; }

    public void Hold(ProjectActivityKind kind) => _held = kind;

    public IDisposable? TryEnter(string projectId, ProjectActivityKind kind)
    {
        if (_held is not null)
        {
            return null;
        }

        _held = kind;
        Entered.Add(kind);
        return new Lease(this);
    }

    public ProjectActivityKind? Current(string projectId) => _held;

    public bool IsBusy(string projectId) => _held is not null;

    private sealed class Lease(FakeProjectActivity owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner._held = null;
                owner.Released++;
            }
        }
    }
}
