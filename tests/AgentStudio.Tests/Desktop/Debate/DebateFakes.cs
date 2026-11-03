using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// An <see cref="IDebateRun"/> driven by the test, like the monitor's <c>FakeRun</c>: <see cref="Emit"/> writes to the
/// single-consumer stream, <see cref="Finish"/> ends it and <see cref="Fail"/> breaks it. It records every action it is
/// asked, and <see cref="CurrentSnapshot"/> is what the room reads back. No coordinator behind it.
/// </summary>
internal sealed class FakeDebateRun : IDebateRun
{
    private readonly Channel<DebateProgress> _events = Channel.CreateUnbounded<DebateProgress>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<DebateResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeDebateRun(DebateSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        Id = snapshot.Debate;
    }

    public RunId Id { get; }

    public DebateSnapshot CurrentSnapshot { get; set; }

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>When set, every action throws it (the room shows it, never throws).</summary>
    public Exception? ActionFailure { get; set; }

    public bool Disposed { get; private set; }

    public IAsyncEnumerable<DebateProgress> Events => _events.Reader.ReadAllAsync();

    public Task<DebateResult> Completion => _completion.Task;

    public DebateSnapshot Snapshot() => CurrentSnapshot;

    public Task RequestJudgmentAsync() => Record("judgment");

    public Task RetryTurnAsync(AgentId participant) => Record($"retry:{participant}");

    public Task AddHumanEvidenceAsync(string title, string text) => Record($"evidence:{title}:{text}");

    public Task RunTestsAsync() => Record("tests");

    public Task DecideAsync(VerdictDecision? decision, string? chosenProposal, string comment) =>
        Record($"decide:{decision?.ToString() ?? "none"}:{chosenProposal ?? "none"}:{comment}");

    public Task AbortAsync(string reason) => Record($"abort:{reason}");

    public void Emit(params DebateProgress[] events)
    {
        foreach (var e in events)
        {
            Assert.True(_events.Writer.TryWrite(e));
        }
    }

    public void Finish(DebateResult result)
    {
        _events.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    public void Fail(Exception error)
    {
        _events.Writer.TryComplete();
        _completion.TrySetException(error);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private Task Record(string call)
    {
        if (ActionFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        Calls.Enqueue(call);
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IDebateCoordinator"/> that answers what the test scripts and records what it is asked.</summary>
internal sealed class FakeDebateCoordinator : IDebateCoordinator
{
    public Func<DebateRequest, DebateProposal> Prepare { get; set; } = request => DebateSamples.Proposal(request);

    public Func<DebateProposal, IDebateRun> Open { get; set; } = _ => throw new InvalidOperationException("No open was scripted.");

    public Func<RunId, IDebateRun> Resume { get; set; } = _ => throw new InvalidOperationException("No resume was scripted.");

    public Func<RunId, DebateTranscript> Read { get; set; } = id => throw new InvalidOperationException($"No transcript of {id} was scripted.");

    public List<DebateSummary> Debates { get; } = [];

    public List<DebateRequest> Prepared { get; } = [];

    public List<DebateProposal> Opened { get; } = [];

    public List<RunId> Resumed { get; } = [];

    public List<RunId> ReadDebates { get; } = [];

    public int Lists { get; private set; }

    public Task<DebateProposal> PrepareAsync(DebateRequest request, CancellationToken ct)
    {
        Prepared.Add(request);
        return Task.FromResult(Prepare(request));
    }

    public Task<IDebateRun> OpenAsync(DebateProposal proposal, CancellationToken ct)
    {
        Opened.Add(proposal);
        return Task.FromResult(Open(proposal));
    }

    public Task<IDebateRun> ResumeAsync(ProjectContext project, RunId debate, CancellationToken ct)
    {
        Resumed.Add(debate);
        return Task.FromResult(Resume(debate));
    }

    public Task<IReadOnlyList<DebateSummary>> ListAsync(ProjectContext project, CancellationToken ct)
    {
        Lists++;
        return Task.FromResult<IReadOnlyList<DebateSummary>>([.. Debates]);
    }

    public Task<DebateTranscript> ReadAsync(ProjectContext project, RunId debate, CancellationToken ct)
    {
        ReadDebates.Add(debate);
        return Task.FromResult(Read(debate));
    }
}

/// <summary>An <see cref="IVerdictChallenges"/> that answers what the test scripts and records what it is asked.</summary>
internal sealed class FakeVerdictChallenges : IVerdictChallenges
{
    public Func<VerdictChallengeRequest, DebateProposal> Prepare { get; set; } = request => DebateSamples.Proposal(request.Revision);

    public Func<DebateProposal, IDebateRun> Challenge { get; set; } = _ => throw new InvalidOperationException("No challenge was scripted.");

    public Dictionary<string, ChallengeStatus> Statuses { get; } = [];

    public List<VerdictChallengeRequest> Prepared { get; } = [];

    public List<DebateProposal> Challenged { get; } = [];

    public List<string> StatusAsked { get; } = [];

    public List<string> Accepted { get; } = [];

    public Task<DebateProposal> PrepareAsync(VerdictChallengeRequest request, CancellationToken ct)
    {
        Prepared.Add(request);
        return Task.FromResult(Prepare(request));
    }

    public Task<IDebateRun> ChallengeAsync(DebateProposal proposal, CancellationToken ct)
    {
        Challenged.Add(proposal);
        return Task.FromResult(Challenge(proposal));
    }

    public Task<ChallengeStatus> StatusAsync(ProjectContext project, string verdict, CancellationToken ct)
    {
        StatusAsked.Add(verdict);
        return Task.FromResult(Statuses.TryGetValue(verdict, out var status) ? status : ChallengeStatus.None);
    }

    public Task AcceptRevisionAsync(ProjectContext project, string revisionVerdict, CancellationToken ct)
    {
        Accepted.Add(revisionVerdict);
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IProjectAuditService"/> that answers what the test scripts and records what it is asked.</summary>
internal sealed class FakeProjectAuditService : IProjectAuditService
{
    public Func<ProjectAuditRequest, ProjectAuditProposal> Prepare { get; set; } = request => DebateSamples.AuditProposal(request);

    public Func<ProjectAuditProposal, IProgress<AgentEvent>?, AuditReport> Audit { get; set; } = (proposal, _) => DebateSamples.AuditReport();

    public List<ProjectAuditRequest> Prepared { get; } = [];

    public List<ProjectAuditProposal> Audited { get; } = [];

    public Task<ProjectAuditProposal> PrepareAsync(ProjectAuditRequest request, CancellationToken ct)
    {
        Prepared.Add(request);
        return Task.FromResult(Prepare(request));
    }

    public Task<AuditReport> AuditAsync(ProjectAuditProposal proposal, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        Audited.Add(proposal);
        return Task.FromResult(Audit(proposal, progress));
    }
}

/// <summary>An <see cref="IOrchestratorAuditService"/> that answers a scripted report and counts the passes.</summary>
internal sealed class FakeOrchestratorAuditService : IOrchestratorAuditService
{
    public Func<RunId, OrchestratorAuditReport> Report { get; set; } = run => new(run, DecisionLogVerification.Valid, [], [], false);

    public List<RunId> Audited { get; } = [];

    public Task<OrchestratorAuditReport> AuditRunAsync(ProjectContext project, RunId run, CancellationToken ct)
    {
        Audited.Add(run);
        return Task.FromResult(Report(run));
    }
}

/// <summary>Stored verdicts in memory, in the order given.</summary>
internal sealed class FakeVerdictStore(params StoredVerdict[] verdicts) : IVerdictStore
{
    public List<StoredVerdict> Verdicts { get; } = [.. verdicts];

    public Task<string> AppendAsync(string projectId, StoredVerdict verdict, CancellationToken cancellationToken)
    {
        Verdicts.Add(verdict);
        return Task.FromResult(verdict.Id);
    }

    public Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredVerdict>>([.. Verdicts.Where(v => runId is null || v.RunId == runId)]);
}

/// <summary>An <see cref="IApprovalGate"/> that answers what the test sets and records every request.</summary>
internal sealed class FakeApprovalGate(bool approve = true) : IApprovalGate
{
    public bool Approve { get; set; } = approve;

    public List<ApprovalRequest> Requests { get; } = [];

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new ApprovalDecision(Approve, "user", DebateSamples.Now, Approve ? null : "No.", request.Binding));
    }
}

/// <summary>The headers of the runs of a project, in memory.</summary>
internal sealed class FakeRunRegistry(params RunRecord[] runs) : IRunRegistry
{
    public List<RunRecord> Runs { get; } = [.. runs];

    public Task CreateAsync(RunRecord run, CancellationToken cancellationToken)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunRecord>>([.. Runs]);
}

/// <summary>Evidence in memory: the record carries the SHA-256 of the bytes it was given, unless a test tampers with them.</summary>
internal sealed class FakeEvidenceStore : IEvidenceStore
{
    private readonly Dictionary<EvidenceId, (EvidenceRecord Record, byte[] Content, string? Run)> _items = [];

    public void Add(EvidenceRecord record, string text, string? run = null) => _items[record.Id] = (record, System.Text.Encoding.UTF8.GetBytes(text), run);

    /// <summary>Puts a record with the hash of <paramref name="text"/>, captured for <paramref name="run"/>.</summary>
    public EvidenceRecord Capture(string id, string title, EvidenceKind kind, EvidenceOrigin origin, string text, string run)
    {
        var record = new EvidenceRecord(new EvidenceId(id), kind, origin, title, "agent-studio", DebateSamples.Now, Sha256(text), subjectCommit: DebateSamples.Commit);
        Add(record, text, run);
        return record;
    }

    /// <summary>Changes the bytes behind a record after capture, as a file edited behind the store's back would.</summary>
    public void Tamper(string id, string text)
    {
        var item = _items[new EvidenceId(id)];
        _items[item.Record.Id] = item with { Content = System.Text.Encoding.UTF8.GetBytes(text) };
    }

    public Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The screens never capture evidence themselves.");

    public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken) =>
        _items.TryGetValue(id, out var item)
            ? Task.FromResult<Stream>(new MemoryStream(item.Content, writable: false))
            : Task.FromException<Stream>(new FileNotFoundException($"No evidence {id}."));

    public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvidenceRecord>>([.. _items.Values.Select(i => i.Record)]);

    public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvidenceRecord>>([.. _items.Values.Where(i => i.Run == runId).Select(i => i.Record)]);

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}

/// <summary>Records what the review screens ask of the shell.</summary>
internal sealed class FakeReviewNavigation : IReviewNavigation
{
    public List<DocumentViewModel> Shown { get; } = [];

    public List<(string ProjectRoot, string Request)> CorrectiveTasks { get; } = [];

    public void Show(DocumentViewModel document) => Shown.Add(document);

    public Task CreateCorrectiveTasksAsync(string projectRoot, string request)
    {
        CorrectiveTasks.Add((projectRoot, request));
        return Task.CompletedTask;
    }
}
