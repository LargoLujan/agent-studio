using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Tracing;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Annotates the commits of the graph of «Repositorio y cambios» from the verified log (docs/16 D-44, F9-9): for each commit, the run, task, attempt, agent
/// and model when it is a commit of the system, its explicit links (D-29) and the suggestions (D-30). It reads only; a log that does not verify throws
/// <see cref="DecisionLogIntegrityException"/>.
/// </summary>
/// <remarks>
/// A commit is «of the system» only when its trailers name an attempt the log has (run, task and attempt) <b>and</b> the log registered that very commit for the
/// attempt (the <c>SubjectCommit</c> of one of its evidence): then the agent and the model are the log's, not the message's. Trailers the log does not back (a message
/// copied or typed by hand, a cherry-pick or a rebase that carries them to another commit) annotate nothing as a commit of the system, since anyone can write them; at most
/// they are a suggestion (<see cref="TraceSuggester"/>), never an annotation of the attempt.
/// A commit with nothing to say has no annotation (<see cref="AnnotatedCommit.Annotation"/> null).
/// </remarks>
public sealed class RepositoryAnnotator(IDecisionRecorder recorder, TraceSuggester suggester) : IRepositoryAnnotator
{
    public async Task<IReadOnlyList<AnnotatedCommit>> AnnotateAsync(ProjectContext project, IReadOnlyList<CommitNode> commits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(commits);
        var state = await TraceLinkService.ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var attempts = state.Entries
            .Select(RunLog.Payload)
            .OfType<AgentRunStartedPayload>()
            .Where(p => p.Task is not null && p.Purpose is null or AgentRunPurpose.Executor)
            .GroupBy(p => (p.RunId, Task: p.Task!.Value.Value, p.Attempt))
            .ToDictionary(g => g.Key, g => g.First());
        var registered = state.Entries
            .Select(RunLog.Payload)
            .OfType<EvidenceRegisteredPayload>()
            .Where(p => p.Task is not null && p.Attempt is not null && TraceRef.IsFullCommitId(p.SubjectCommit))
            .Select(p => (p.RunId, Task: p.Task!.Value.Value, Attempt: p.Attempt!.Value, Commit: p.SubjectCommit!.ToLowerInvariant()))
            .ToHashSet();
        var suggestions = suggester.SuggestForCommits(state, commits);

        var annotated = new List<AnnotatedCommit>(commits.Count);
        foreach (var commit in commits)
        {
            var links = TraceRef.IsFullCommitId(commit.Id) ? state.Links.Around(TraceRef.Commit(commit.Id)) : [];
            var offered = suggestions.TryGetValue(commit.Id, out var found) ? found : [];
            AgentRunStartedPayload? attempt = null;
            var trailers = CommitTrailers.Read(commit.Message ?? commit.Subject);
            if (trailers is not null)
            {
                if (TraceRef.IsFullCommitId(commit.Id) && registered.Contains((trailers.Run.Value, trailers.Task.Value, trailers.Attempt, commit.Id.ToLowerInvariant())))
                {
                    attempts.TryGetValue((trailers.Run.Value, trailers.Task.Value, trailers.Attempt), out attempt);
                }
            }

            annotated.Add(new AnnotatedCommit(
                commit,
                attempt is null && links.Count == 0 && offered.Count == 0
                    ? null
                    : new CommitAnnotation(attempt?.RunId, attempt?.Task?.Value, attempt?.Attempt, attempt?.Agent, attempt?.Model, links, offered)));
        }

        return annotated;
    }
}
