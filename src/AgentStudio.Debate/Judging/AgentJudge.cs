using System.Diagnostics;
using System.Text;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// What one judge needs besides the dossier: the agent of the roster that judges, the folder it works in (the workspace
/// with the integrated code), the package of the dossier, and who follows its runs.
/// </summary>
/// <param name="Purpose">The prompt-journal summary of its runs.</param>
/// <param name="TaskId">The review task, for the prompt journal.</param>
/// <param name="Brief">What the judge is asked and how its answer is read (docs/15 D-23); null is the review of a run (<see cref="JudgeBrief.RunReview"/>).</param>
/// <param name="Language">
/// V44 (docs/17 D-48): the language the person chose, which the judge writes the free text of its verdict in. It travels as a block of
/// data at the end of the prompt (<see cref="ResponseLanguage"/>), never inside the brief, so no prompt of a brief changes with it;
/// null is <see cref="ResponseLanguage.Default"/> (the factory fills it from the <see cref="IResponseLanguage"/> of the app).
/// </param>
public sealed record JudgeLaunch(
    AgentDefinition Agent,
    string WorkingDirectory,
    DossierFiles? Files,
    IProgress<AgentEvent>? Progress = null,
    IJudgeRunObserver? Observer = null,
    string? Purpose = null,
    string? TaskId = null,
    JudgeBrief? Brief = null,
    string? Language = null);

/// <summary>
/// What a judge is asked and how its answer is read (docs/15 D-23): the rubric with the material (the prompt of the verdict
/// and of its one correction), the schema it answers with and the reading of its answer into a <see cref="Verdict"/>, plus
/// how its runs are journaled. <see cref="RunReview"/> is the brief of the review of a run, the one of Phase 3, unchanged;
/// the judgment of a debate brings its own. Whatever the brief, <see cref="AgentJudge"/> keeps the judge read-only and sets
/// who judged and when: a brief cannot change the permissions of the run nor the identity of the verdict.
/// </summary>
public abstract class JudgeBrief
{
    /// <summary>The review of an orchestration run (Phase 3): <see cref="JudgePrompt"/>, <see cref="VerdictSchema"/> and <see cref="VerdictJson"/>.</summary>
    public static JudgeBrief RunReview { get; } = new RunReviewBrief();

    /// <summary>The journal summary of a run of the judge when the launch gives none.</summary>
    public virtual string Purpose => "Revisión independiente (juez)";

    /// <summary>The tags of the journal entry of each run.</summary>
    public virtual IReadOnlyList<string> Tags { get; } = ["#revision"];

    /// <summary>The debate (<c>deb-…</c>) or audit (<c>aud-…</c>) the runs belong to, for the journal (D-45); null for the review of a run.</summary>
    public virtual string? DebateId => null;

    /// <summary>The prompt of the verdict: the rubric, where the material is and the dossier as data.</summary>
    public abstract string Evaluation(JudgeDossier dossier, DossierFiles? files, string workingDirectory);

    /// <summary>The prompt of the one correction: only the broken rules when the session is resumed, the whole first prompt with the previous verdict otherwise.</summary>
    public abstract string Correction(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, bool resumed, DossierFiles? files, string workingDirectory);

    /// <summary>The JSON schema the judge answers with, built for this dossier.</summary>
    public abstract string Schema(JudgeDossier dossier);

    /// <summary>Reads the judge's answer into a verdict, with the identity and the time the system gives.</summary>
    /// <exception cref="JudgeOutputException">The answer is not a verdict.</exception>
    public abstract Verdict Read(string json, JudgeIdentity judge, DateTimeOffset issuedAt);

    private sealed class RunReviewBrief : JudgeBrief
    {
        public override string Evaluation(JudgeDossier dossier, DossierFiles? files, string workingDirectory) =>
            JudgePrompt.Evaluation(dossier, files, workingDirectory);

        public override string Correction(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, bool resumed, DossierFiles? files, string workingDirectory) =>
            JudgePrompt.Correction(dossier, previous, violations, resumed, files, workingDirectory);

        public override string Schema(JudgeDossier dossier)
        {
            ArgumentNullException.ThrowIfNull(dossier);
            return VerdictSchema.Build(dossier.Requirements);
        }

        public override Verdict Read(string json, JudgeIdentity judge, DateTimeOffset issuedAt) => VerdictJson.Parse(json, judge, issuedAt);
    }
}

/// <summary>A launch of the judge that is about to happen: <see cref="Attempt"/> is 1 for the verdict and 2 for the correction.</summary>
public sealed record JudgeRunStart(int Attempt, AgentRunRequest Request, ProviderId Provider, VendorId Vendor);

/// <summary>How a launch of the judge ended, with the usage and the model the CLI reported.</summary>
public sealed record JudgeRunEnd(int Attempt, AgentRunOutcome Outcome, TokenUsage? Usage, string? Model);

/// <summary>
/// Follows the launches of a judge. <see cref="RunStartingAsync"/> runs before the agent is launched, and if it throws
/// the agent is not launched (a launch that cannot be recorded does not happen). <see cref="RunFinishedAsync"/> runs
/// after every launch, whatever the outcome; a failure there never hides the verdict.
/// </summary>
public interface IJudgeRunObserver
{
    Task RunStartingAsync(JudgeRunStart start, CancellationToken cancellationToken);

    Task RunFinishedAsync(JudgeRunEnd end, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>AgentRunStarted</c> that <see cref="RunStartingAsync"/> recorded for <paramref name="start"/>, which the
    /// launch carries so the journal can name it (docs/17 D-02); null when the observer records none.
    /// </summary>
    AgentRunAnchor? AnchorOf(JudgeRunStart start) => null;
}

/// <summary>
/// The judge's run did not give an answer to read: it failed, hit a limit, was rate limited, or succeeded without
/// structured output. <see cref="Status"/> is how the run ended.
/// </summary>
public sealed class JudgeRunException : Exception
{
    public JudgeRunException(string message, AgentRunStatus status)
        : base(message)
    {
        Status = status;
    }

    public AgentRunStatus Status { get; }
}

/// <summary>
/// An <see cref="IJudge"/> that runs an agent of the roster through its CLI, read-only, and answers with the verdict
/// schema.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Read-only on every CLI: no file writes, no shell, no network; only <c>Read</c>, <c>Grep</c> and <c>Glob</c>,
/// with <c>Write</c>, <c>Edit</c>, <c>NotebookEdit</c> and <c>Bash</c> denied. Claude Code turns that into
/// <c>--permission-mode dontAsk</c> with those tools in <c>--disallowedTools</c> (never <c>plan</c>, which changes the
/// model and costs more); Codex into <c>-s read-only</c>, enforced by its sandbox. The dossier folder is added with
/// <c>--add-dir</c> where the CLI supports it.</item>
/// <item>Only a judge or an auditor that the domain allows (no write grants) can judge; anything else is refused before
/// a run starts.</item>
/// <item>The system, not the judge, says who judged and when: the agent, the provider and vendor that really ran (not
/// the vendor the roster claims) and the model the session reported (else the configured one), with the time of the
/// system clock. The judge only echoes the two hashes.</item>
/// <item>There is one correction at most, which resumes the session of the verdict when the CLI can.</item>
/// </list>
/// </remarks>
public sealed class AgentJudge : IJudge
{
    private static readonly string[] ReadTools = ["Read", "Grep", "Glob"];
    private static readonly string[] WriteTools = ["Write", "Edit", "NotebookEdit", "Bash"];

    private readonly IAgentProvider _provider;
    private readonly JudgeLaunch _launch;
    private readonly TimeProvider _time;
    private readonly string _workingDirectory;
    private string? _sessionId;
    private int _attempts;
    private int _corrections;

    /// <exception cref="ArgumentException">The agent is not a read-only judge or auditor, or it runs on another CLI than <paramref name="provider"/>.</exception>
    public AgentJudge(IAgentProvider provider, JudgeLaunch launch, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var agent = launch.Agent;
        if (agent.Role is not (AgentRole.Judge or AgentRole.Auditor))
        {
            throw new ArgumentException($"Agent '{agent.Id}' is a {agent.Role}; only a judge or an auditor can judge.", nameof(launch));
        }

        var problems = agent.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"Agent '{agent.Id}' cannot judge: {string.Join(", ", problems)}.", nameof(launch));
        }

        if (provider.Provider != agent.Model.Provider)
        {
            throw new ArgumentException($"Agent '{agent.Id}' runs on '{agent.Model.Provider}', not on '{provider.Provider}'.", nameof(provider));
        }

        _workingDirectory = !string.IsNullOrWhiteSpace(launch.WorkingDirectory)
            ? launch.WorkingDirectory
            : launch.Files?.Folder ?? throw new ArgumentException("The judge needs a working directory or a dossier folder.", nameof(launch));
        _provider = provider;
        _launch = launch;
        _time = timeProvider;
        Identity = IdentityFor(agent.Model.Model);
    }

    /// <summary>Who judges. Before a run, the configured model; after one, the model the session reported.</summary>
    public JudgeIdentity Identity { get; private set; }

    /// <summary>What the judge is asked and how its answer is read: the launch's brief, or the review of a run.</summary>
    private JudgeBrief Brief => _launch.Brief ?? JudgeBrief.RunReview;

    public Task<Verdict> EvaluateAsync(JudgeDossier dossier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        return JudgeAsync(dossier, Brief.Evaluation(dossier, _launch.Files, _workingDirectory), resume: null, cancellationToken);
    }

    /// <exception cref="InvalidOperationException">The judge already had its correction.</exception>
    public Task<Verdict> ReviseAsync(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(violations);
        if (Interlocked.Exchange(ref _corrections, 1) != 0)
        {
            throw new InvalidOperationException("The judge already had its one correction.");
        }

        var resume = _provider.Capabilities.SupportsResume ? _sessionId : null;
        var prompt = Brief.Correction(dossier, previous, violations, resumed: resume is not null, _launch.Files, _workingDirectory);
        return JudgeAsync(dossier, prompt, resume, cancellationToken);
    }

    /// <summary>The prompt of a brief followed by the block of data with the language of the person (V44).</summary>
    internal static string WithLanguage(string prompt, string? language)
    {
        var text = new StringBuilder(prompt);
        if (!prompt.EndsWith('\n'))
        {
            text.AppendLine();
        }

        text.AppendLine();
        ResponseLanguage.AppendEnglish(text, language);
        return text.ToString();
    }

    private async Task<Verdict> JudgeAsync(JudgeDossier dossier, string prompt, string? resume, CancellationToken cancellationToken)
    {
        var brief = Brief;

        // A resumed correction already has the language the first prompt gave; every other prompt carries it, after the material.
        if (resume is null)
        {
            prompt = WithLanguage(prompt, _launch.Language);
        }

        var request = new AgentRunRequest(
            _launch.Agent,
            prompt,
            _workingDirectory,
            new PermissionProfile(
                AllowFileWrites: false,
                AllowShell: false,
                AllowNetwork: false,
                ReadTools,
                WriteTools,
                _launch.Files is { } files ? [files.Folder] : []),
            OutputJsonSchema: brief.Schema(dossier),
            ResumeSessionId: resume,
            Purpose: _launch.Purpose ?? brief.Purpose,
            TaskId: _launch.TaskId,
            Tags: [.. brief.Tags],
            DebateId: brief.DebateId);

        var attempt = Interlocked.Increment(ref _attempts);
        var (outcome, model) = await RunAsync(request, attempt, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != AgentRunStatus.Succeeded)
        {
            throw new JudgeRunException($"The judge's run ended {outcome.Status}: {outcome.Error ?? "no error was reported"}.", outcome.Status);
        }

        if (string.IsNullOrWhiteSpace(outcome.StructuredOutputJson))
        {
            throw new JudgeRunException("The judge's run succeeded without the structured verdict the schema asked for.", outcome.Status);
        }

        Identity = IdentityFor(model ?? _launch.Agent.Model.Model);
        return brief.Read(outcome.StructuredOutputJson, Identity, _time.GetUtcNow());
    }

    private JudgeIdentity IdentityFor(string? model) => new(_launch.Agent.Id, new ModelBinding(_provider.Provider, _provider.Vendor, model));

    private async Task<(AgentRunOutcome Outcome, string? Model)> RunAsync(AgentRunRequest request, int attempt, CancellationToken cancellationToken)
    {
        if (_launch.Observer is { } observer)
        {
            var start = new JudgeRunStart(attempt, request, _provider.Provider, _provider.Vendor);
            await observer.RunStartingAsync(start, cancellationToken).ConfigureAwait(false);
            if (observer.AnchorOf(start) is { } anchor)
            {
                request = request with { Anchor = anchor };
            }
        }

        IAgentRun run;
        try
        {
            run = await _provider.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var status = ex is OperationCanceledException ? AgentRunStatus.Cancelled : AgentRunStatus.Failed;
            await FinishedAsync(new JudgeRunEnd(attempt, new AgentRunOutcome(status, null, null, null, null, ex.Message), null, null)).ConfigureAwait(false);
            throw;
        }

        string? model = null;
        TokenUsage? usage = null;
        await using (run.ConfigureAwait(false))
        {
            try
            {
                await foreach (var evt in run.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case SessionStarted started:
                            model = started.Model ?? model;
                            _sessionId = string.IsNullOrWhiteSpace(started.SessionId) ? _sessionId : started.SessionId;
                            break;
                        case UsageReported reported:
                            usage = reported.Usage;
                            break;
                    }

                    _launch.Progress?.Report(evt);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await run.CancelAsync().ConfigureAwait(false);
                await FinishedAsync(new JudgeRunEnd(attempt, new AgentRunOutcome(AgentRunStatus.Cancelled, null, null, null, _sessionId, "The review was cancelled."), usage, model)).ConfigureAwait(false);
                throw;
            }

            var outcome = await run.Completion.ConfigureAwait(false);
            _sessionId = outcome.SessionId ?? _sessionId;
            await FinishedAsync(new JudgeRunEnd(attempt, outcome, usage, model)).ConfigureAwait(false);
            return (outcome, model);
        }
    }

    private async Task FinishedAsync(JudgeRunEnd end)
    {
        if (_launch.Observer is not { } observer)
        {
            return;
        }

        try
        {
            // Recorded even when the review was cancelled: the launch happened and must be closed.
            await observer.RunFinishedAsync(end, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Judge: could not record the end of attempt {end.Attempt}: {ex.Message}");
        }
    }
}

/// <summary>Creates the judges of the reviews.</summary>
public interface IAgentJudgeFactory
{
    IJudge Create(ProjectContext project, JudgeLaunch launch);
}

/// <summary>
/// Runs each judge on the project's provider for its CLI, taken from <see cref="IProjectAgentProviders"/>: composed
/// with the prompt journal and the per-provider concurrency limit, never a bare adapter.
/// </summary>
public sealed class AgentJudgeFactory(IProjectAgentProviders providers, TimeProvider timeProvider, IResponseLanguage? language = null) : IAgentJudgeFactory
{
    /// <exception cref="InvalidOperationException">The project has no provider for the judge's CLI.</exception>
    public IJudge Create(ProjectContext project, JudgeLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(launch);
        var cli = launch.Agent.Model.Provider;
        var provider = providers.For(project.RootPath).FirstOrDefault(p => p.Provider == cli)
            ?? throw new InvalidOperationException($"Project '{project.ProjectId}' has no provider for '{cli}', the CLI of judge '{launch.Agent.Id}'.");
        return new AgentJudge(provider, launch with { Language = launch.Language ?? ResponseLanguage.Resolve(language) }, timeProvider);
    }
}
