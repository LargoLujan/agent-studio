using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Tracing;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Infers links of the trace (docs/16 D-30, ADR-0039) from what the repository says: the trailers the system writes in its commits, the
/// ids of a run or a milestone mentioned in a message, and the branch of a run. It reads only and never records: an inference is a
/// suggestion until a person confirms it (<see cref="ITraceLinkService.ConfirmAsync"/>), and it never reaches a judge as a fact.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only what the verified log knows is suggested: a trailer or a mention of a run or a milestone the log has no entries for is left
/// out, since anyone can write a message.</item>
/// <item>A suggestion whose link is in force (the person confirmed it, or the system or a milestone asserts it) or was retracted by a person
/// is not offered again.</item>
/// <item>The commits come through <see cref="IRepositoryHistoryReader"/> (the hardened git layer); what it returns is data of whoever wrote
/// it. A commit that is not named by its full id is skipped.</item>
/// </list>
/// </remarks>
public sealed partial class TraceSuggester(IDecisionRecorder recorder, IRepositoryHistoryReader history) : ITraceSuggester
{
    /// <summary>How many commits are read to infer from: the same bound the screen's graph has.</summary>
    public const int MaxCommits = 200;

    private const string RunBranchPrefix = "agentstudio/run-";

    [GeneratedRegex(@"\b\d{8}-[0-9a-f]{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"\bmil-[A-Za-z0-9_-]{1,60}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MilestoneIdPattern();

    public async Task<IReadOnlyList<TraceSuggestion>> SuggestAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        var state = await TraceLinkService.ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var known = RunLog.RunIds(state.Entries);
        var commits = await history.CommitsAsync(project, new CommitGraphQuery(MaxCommits), cancellationToken).ConfigureAwait(false);
        var branches = await history.BranchesAsync(project, cancellationToken).ConfigureAwait(false);

        var all = new List<TraceSuggestion>();
        foreach (var commit in commits.Where(c => TraceRef.IsFullCommitId(c.Id)))
        {
            all.AddRange(FromMessage(commit, known));
        }

        foreach (var branch in branches.Where(b => b.Kind == BranchKind.Run && TraceRef.IsFullCommitId(b.Commit) && b.Name.StartsWith(RunBranchPrefix, StringComparison.Ordinal)))
        {
            var run = branch.Name[RunBranchPrefix.Length..];
            if (known.Contains(run) && IsRunId(run))
            {
                all.Add(new TraceSuggestion(TraceRef.Run(run), TraceRef.Commit(branch.Commit), TraceSuggestionSource.RunBranchName, Cut(branch.Name)));
            }
        }

        var offered = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TraceSuggestion>();
        foreach (var suggestion in all.Where(s => s.From == node || s.To == node))
        {
            var id = TraceLinks.IdOf(suggestion.From, suggestion.To, TraceAssertedBy.User);
            if (!offered.Add(id) || IsSettled(state, suggestion, id))
            {
                continue;
            }

            result.Add(suggestion);
        }

        return result;
    }

    /// <summary>
    /// The suggestions of each commit given, by the commit's id (one read of the log for all of them, none of the history): what the annotator of the
    /// graph shows next to each commit. The branch of a run is not read here, only what each commit's message says.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TraceSuggestion>>> SuggestForCommitsAsync(ProjectContext project, IReadOnlyList<CommitNode> commits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(commits);
        var state = await TraceLinkService.ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var known = RunLog.RunIds(state.Entries);
        var result = new Dictionary<string, IReadOnlyList<TraceSuggestion>>(StringComparer.Ordinal);
        foreach (var commit in commits.Where(c => TraceRef.IsFullCommitId(c.Id)))
        {
            var offered = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<TraceSuggestion>();
            foreach (var suggestion in FromMessage(commit, known))
            {
                var id = TraceLinks.IdOf(suggestion.From, suggestion.To, TraceAssertedBy.User);
                if (offered.Add(id) && !IsSettled(state, suggestion, id))
                {
                    list.Add(suggestion);
                }
            }

            result[commit.Id] = list;
        }

        return result;
    }

    private static IEnumerable<TraceSuggestion> FromMessage(CommitNode commit, IReadOnlySet<string> known)
    {
        var from = TraceRef.Commit(commit.Id);

        // The whole message when the reader gives it (the trailers are in its last paragraph), else the subject.
        var message = commit.Message ?? commit.Subject ?? string.Empty;
        var trailers = CommitTrailers.Read(message);
        if (trailers is not null && known.Contains(trailers.Run.Value))
        {
            var origin = $"{CommitTrailers.RunKey}: {trailers.Run.Value}, {CommitTrailers.TaskKey}: {trailers.Task.Value}, {CommitTrailers.AttemptKey}: {trailers.Attempt}";
            yield return new TraceSuggestion(from, TraceRef.Attempt(trailers.Run.Value, trailers.Task.Value, trailers.Attempt), TraceSuggestionSource.CommitTrailer, Cut(origin));
        }

        foreach (Match match in RunIdPattern().Matches(message))
        {
            // The run its own trailers name is the attempt's, not a mention.
            if (known.Contains(match.Value) && !string.Equals(match.Value, trailers?.Run.Value, StringComparison.Ordinal))
            {
                yield return new TraceSuggestion(from, TraceRef.Run(match.Value), TraceSuggestionSource.CommitMessageMention, Cut(match.Value));
            }
        }

        foreach (Match match in MilestoneIdPattern().Matches(message))
        {
            if (known.Contains(match.Value))
            {
                yield return new TraceSuggestion(from, TraceRef.Milestone(match.Value), TraceSuggestionSource.CommitMessageMention, Cut(match.Value));
            }
        }
    }

    /// <summary>Whether the link is already in force (by a person, the system or a milestone) or a person retracted it.</summary>
    private static bool IsSettled(TraceState state, TraceSuggestion suggestion, string userLinkId) =>
        state.Links.Find(userLinkId) is not null
        || state.Links.Find(TraceLinks.IdOf(suggestion.From, suggestion.To, TraceAssertedBy.System)) is not null
        || state.Links.Retracted.Any(r => string.Equals(r.Link.Id, userLinkId, StringComparison.Ordinal))
        || state.MilestoneLinks.Any(l => l.From == suggestion.From && l.To == suggestion.To);

    private static bool IsRunId(string value) => RunIdPattern().IsMatch(value) && RunIdPattern().Match(value).Length == value.Length;

    private static string Cut(string text)
    {
        var line = string.Concat(text.Where(c => !char.IsControl(c)));
        return line.Length <= TraceSuggestion.MaxInferredFromLength ? line : line[..TraceSuggestion.MaxInferredFromLength];
    }
}
