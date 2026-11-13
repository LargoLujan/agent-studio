using System.Buffers;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The JSON of the writes (docs/21 D-10), written with <see cref="Utf8JsonWriter"/> from typed values and sent through standard input: no text of a person or of an agent is ever an argument.
/// The default encoder of the writer escapes everything that is not plain ASCII, so what travels is valid JSON whatever the text holds. Closed fields only: a pull request is opened or has its
/// title and body changed or is closed; a status has its state, its context and its description; a merge has its method and the head it expects.
/// </summary>
internal static class GitHubRequestBodies
{
    /// <summary>The context of the status of the verdict: publishing again replaces it (D-21).</summary>
    internal const string VerdictContext = "agent-studio/verdict";

    /// <summary>The most characters of the description of a status: the documentation gives no cap for the status of a commit, so it is cut at 120 (D-21, §13).</summary>
    internal const int MaxStatusDescription = 120;

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string OpenPullRequest(PullRequestDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Write(w =>
        {
            w.WriteString("title", draft.Text.Title);
            w.WriteString("head", draft.Head.Name);
            w.WriteString("base", draft.Base.Name);
            w.WriteString("body", draft.Text.Body);
            w.WriteBoolean("draft", draft.Draft);
        });
    }

    internal static string UpdatePullRequest(PullRequestText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Write(w =>
        {
            w.WriteString("title", text.Title);
            w.WriteString("body", text.Body);
        });
    }

    internal static string ClosePullRequest() => Write(w => w.WriteString("state", "closed"));

    internal static string RequestReviewers(IReadOnlyList<RemoteLogin> reviewers)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        return Write(w =>
        {
            w.WriteStartArray("reviewers");
            foreach (var reviewer in reviewers)
            {
                w.WriteStringValue(reviewer.Value);
            }

            w.WriteEndArray();
        });
    }

    /// <summary>The status of the verdict: a state of the closed vocabulary, the fixed context and a description of the system (never a text of the judge).</summary>
    internal static string CommitStatus(VerdictStanding standing, string description) =>
        Write(w =>
        {
            w.WriteString("state", StatusState(standing));
            w.WriteString("context", VerdictContext);
            w.WriteString("description", description.Length > MaxStatusDescription ? description[..MaxStatusDescription] : description);
        });

    internal static string StatusState(VerdictStanding standing) => standing switch
    {
        VerdictStanding.Approved => "success",
        VerdictStanding.Rejected => "failure",
        VerdictStanding.InReview => "pending",
        _ => throw new ArgumentOutOfRangeException(nameof(standing), standing, "Unknown standing of a verdict."),
    };

    internal static string Comment(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Write(w => w.WriteString("body", body));
    }

    /// <summary>The merge: its method and the head it expects, and nothing else: no title and no message of the merge (the platform writes them), and no way to ask for an administrator.</summary>
    internal static string Merge(MergeMethod method, string expectedHead) =>
        Write(w =>
        {
            w.WriteString("merge_method", MergeMethodName(method));
            w.WriteString("sha", expectedHead);
        });

    internal static string MergeMethodName(MergeMethod method) => method switch
    {
        MergeMethod.MergeCommit => "merge",
        MergeMethod.Squash => "squash",
        MergeMethod.Rebase => "rebase",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown merge method."),
    };
}
