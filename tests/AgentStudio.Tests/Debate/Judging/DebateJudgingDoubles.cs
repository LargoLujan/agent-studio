using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Projects;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Debate.Fakes;

namespace AgentStudio.Tests.Debate.Judging;

/// <summary>Lets everyone through once they have all arrived; a straggler that never comes makes the others give up after a while instead of hanging the test.</summary>
internal sealed class AsyncBarrier(int participants)
{
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public Task ArriveAndWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _arrived) == participants)
        {
            _all.TrySetResult();
        }

        return _all.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }
}

/// <summary>A judge that answers only once every judge of the panel has been asked: it proves they run at the same time.</summary>
internal sealed class GatedJudge(JudgeIdentity identity, AsyncBarrier barrier, Func<JudgeDossier, JudgeIdentity, Verdict> evaluate) : IJudge
{
    public List<JudgeDossier> Evaluated { get; } = [];

    public JudgeIdentity Identity => identity;

    public async Task<Verdict> EvaluateAsync(JudgeDossier dossier, CancellationToken cancellationToken)
    {
        Evaluated.Add(dossier);
        await barrier.ArriveAndWaitAsync(cancellationToken);
        return evaluate(dossier, identity);
    }

    public Task<Verdict> ReviseAsync(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No correction was scripted.");
}

/// <summary>A git layer that only copies commits: it writes one file, as the hardened copy would write the tree, and remembers what it copied.</summary>
internal sealed class CopyingWorktreeManager : IWorktreeManager
{
    public List<(string RepoRoot, string Commit, string Destination)> Copies { get; } = [];

    public Exception? Failure { get; init; }

    public Task ArchiveCommitAsync(string repoRoot, string commit, string destination, CancellationToken cancellationToken)
    {
        Copies.Add((repoRoot, commit, destination));
        if (Failure is not null)
        {
            File.WriteAllText(Path.Combine(destination, "partial.txt"), "a medias");
            throw Failure;
        }

        File.WriteAllText(Path.Combine(destination, "Cache.cs"), "public sealed class Cache { }");
        return Task.CompletedTask;
    }

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Answers a judge of a debate could give, built from what its prompt shows, in the JSON shape of the debate schema.</summary>
internal static class DebateAnswers
{
    /// <summary>
    /// The answer of a judge that read its dossier: every requirement satisfied by the contributions it read, every proposal
    /// compared in the order it was shown, <paramref name="chosen"/> chosen (or «ninguna»), and both hashes echoed.
    /// </summary>
    public static JsonObject Choosing(string prompt, string? chosen, VerdictDecision decision = VerdictDecision.Approved, string confidence = "High")
    {
        var data = PromptData.Dossier(prompt);
        var cited = data.GetProperty("debate").GetProperty("contributions").EnumerateArray().Select(c => (JsonNode)JsonValue.Create(c.GetProperty("evidence").GetString())!).ToList();
        var answer = new JsonObject
        {
            ["assessments"] = new JsonArray([.. data.GetProperty("requirements").EnumerateArray().Select(r => (JsonNode)new JsonObject
            {
                ["requirement"] = r.GetProperty("id").GetString(),
                ["status"] = "Satisfied",
                ["evidence"] = new JsonArray([.. cited.Select(c => c.DeepClone())]),
                ["rationale"] = "Las contribuciones lo sostienen.",
            })]),
            ["evidenceExamined"] = new JsonArray([.. cited.Select(c => c.DeepClone())]),
            ["findings"] = new JsonArray(),
            ["pendingObjections"] = new JsonArray(),
            ["risks"] = new JsonArray(),
            ["correctiveActions"] = decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected ? new JsonArray("Rehacer la propuesta") : new JsonArray(),
            ["confidence"] = new JsonObject { ["level"] = confidence, ["justification"] = "Las contribuciones coinciden." },
        };

        var proposals = data.GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!).ToList();
        if (proposals.Count > 0)
        {
            answer["resolution"] = new JsonObject
            {
                ["comparison"] = new JsonArray([.. proposals.Select(p => (JsonNode)new JsonObject { ["proposal"] = p, ["strengths"] = "Fuerte.", ["weaknesses"] = "Débil." })]),
                ["chosenProposal"] = chosen ?? VerdictJson.NoProposal,
            };
        }

        answer["requiresHumanIntervention"] = decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved;
        answer["decision"] = decision.ToString();
        answer["summary"] = "Resumen del juicio.";
        answer["requirementSetHash"] = data.GetProperty("requirementSetHash").GetString();
        answer["decisionLogHeadHash"] = data.GetProperty("decisionLogHeadHash").GetString();
        return answer;
    }

    /// <summary>The labels of the proposals in the order the prompt shows them.</summary>
    public static IReadOnlyList<string> Proposals(string prompt) =>
        [.. PromptData.Dossier(prompt).GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!)];

    public static JsonElement Dossier(string prompt) => PromptData.Dossier(prompt);

    /// <summary>The run of a judge that answers <paramref name="answer"/> in session <paramref name="session"/>, which reports <paramref name="model"/>.</summary>
    public static ScriptedRun Run(JsonNode answer, string session, string? model) => JudgeAnswers.Run(answer, session, model);

}
