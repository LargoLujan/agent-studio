using System.Globalization;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Prompts;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Work;

using VerdictStanding = AgentStudio.Application.Hosting.VerdictStanding;

namespace AgentStudio.Orchestration.Publication;

/// <summary>
/// The title and the body of the pull request, composed by the system (docs/21 D-19). No agent writes in them: GitHub interprets the body (a mention warns someone, <c>Closes #12</c>
/// closes an issue, an image is loaded from where it says), so every variable datum goes inside a code span, with the backticks taken out of the datum, where GitHub interprets nothing.
/// The sentences are templates of the resources (<c>Text_hosting_pr_*</c>), painted in the language of the app.
/// </summary>
public sealed class PullRequestTextComposer
{
    /// <summary>The most a title holds (D-19), and the most a body holds.</summary>
    public const int MaxTitle = 200;

    public const int MaxBody = 20_000;

    private const int MaxRequirements = 60;
    private const int MaxEvidence = 20;

    private readonly IUserTextRenderer _texts;
    private readonly IResponseLanguage? _language;
    private readonly IVerdictStore _verdicts;
    private readonly IEvidenceStore _evidence;

    internal PullRequestTextComposer(IUserTextRenderer texts, IResponseLanguage? language, IVerdictStore verdicts, IEvidenceStore evidence)
    {
        _texts = texts ?? throw new ArgumentNullException(nameof(texts));
        _language = language;
        _verdicts = verdicts ?? throw new ArgumentNullException(nameof(verdicts));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    /// <summary>Data inside a code span: the backticks and the line breaks of the datum are taken out, so it cannot close the span nor start another line.</summary>
    public static string Fence(string? datum)
    {
        // Out go the backticks, the control characters and the format ones (zero width, bidirectional marks…): nothing that closes the span, starts a line or hides what the text says.
        var clean = new string((datum ?? string.Empty).Where(c => c != '`' && !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        return "`" + (clean.Length == 0 ? "-" : clean) + "`";
    }

    public async Task<PullRequestText> ComposeAsync(ProjectContext project, RunId run, string commit, CancellationToken ct)
    {
        var language = ResponseLanguage.Resolve(_language);
        string Say(string code, params string[] arguments) => _texts.Render(UserText.Of(code, [.. arguments]), language);

        var title = Say("hosting.pr.title", Fence(run.Value));
        var body = new StringBuilder();
        body.AppendLine(Say("hosting.pr.body-run", Fence(run.Value), Fence(commit)));

        var verdict = (await _verdicts.ListAsync(project.ProjectId, run.Value, ct).ConfigureAwait(false)).LastOrDefault(v => v.Valid);
        if (verdict is not null)
        {
            body.AppendLine();
            foreach (var assessment in verdict.Verdict.Assessments.Take(MaxRequirements))
            {
                var code = assessment.Status switch
                {
                    AssessmentStatus.Satisfied => "hosting.pr.body-requirement-met",
                    AssessmentStatus.NotSatisfied => "hosting.pr.body-requirement-unmet",
                    _ => "hosting.pr.body-requirement-unverifiable",
                };
                body.AppendLine("- " + Say(code, Fence(assessment.Requirement.Value)));
            }

            body.AppendLine();
            body.AppendLine(Say(
                "hosting.pr.body-judge",
                Fence(verdict.Verdict.Decision.ToString()),
                Fence(verdict.Verdict.Judge.Model.Provider.Value),
                Fence(verdict.Verdict.Judge.Model.Model)));
        }

        var records = await _evidence.ListByRunAsync(project.ProjectId, run.Value, ct).ConfigureAwait(false);
        var tests = records.Where(r => r.TestRun is not null && string.Equals(r.SubjectCommit, commit, StringComparison.OrdinalIgnoreCase)).LastOrDefault();
        if (tests?.TestRun is { } summary)
        {
            body.AppendLine();
            body.AppendLine(Say(
                "hosting.pr.body-verification",
                Fence(summary.Passed.ToString(CultureInfo.InvariantCulture)),
                Fence(summary.Failed.ToString(CultureInfo.InvariantCulture)),
                Fence(summary.Skipped.ToString(CultureInfo.InvariantCulture))));
        }

        var fingerprints = records.Where(r => r.Kind != EvidenceKind.AgentStatement).Take(MaxEvidence).ToList();
        if (fingerprints.Count > 0)
        {
            body.AppendLine();
            foreach (var record in fingerprints)
            {
                body.AppendLine("- " + Say("hosting.pr.body-evidence", Fence(record.Id.Value), Fence(record.ContentSha256.Length > 12 ? record.ContentSha256[..12] : record.ContentSha256)));
            }
        }

        body.AppendLine();
        body.AppendLine(Say("hosting.pr.body-footer"));
        return new PullRequestText(Cut(title, MaxTitle), Cut(body.ToString().TrimEnd(), MaxBody));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>What is published of a verdict: the notice, the description of the status and the body of the comment (docs/21 D-21).</summary>
internal sealed record VerdictPackage(VerdictNotice Notice, string StatusDescription, string CommentBody);

/// <summary>
/// Builds what the platform shows of the verdict (docs/21 D-21 and D-22). The standing and the judged commit are those of <see cref="RemoteJudgement.VerdictInForce"/>, the very function
/// the view of the pull request uses, so what is shown and what is published cannot disagree. Nothing of the judge's reasoning, nor of any agent, goes into the status or the comment: the ids
/// of the requirements, a closed vocabulary and the fixed sentences of the resources. It is never a review nor an approval.
/// </summary>
internal sealed class VerdictNoticeBuilder(IDecisionRecorder recorder, IVerdictStore verdicts, IUserTextRenderer texts, IResponseLanguage? language)
{
    /// <summary>The mark of the first line of the comment, by which it is found again (D-21).</summary>
    public static string MarkOf(RunId run) => $"<!-- agent-studio:verdict run={run.Value} -->";

    /// <summary>The package, or null when the log does not verify or no valid verdict judged the run (nothing is published then).</summary>
    public async Task<VerdictPackage?> BuildAsync(ProjectContext project, RunId run, string? head, CancellationToken ct)
    {
        var log = await recorder.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return null;
        }

        var stored = (await verdicts.ListAsync(project.ProjectId, run.Value, ct).ConfigureAwait(false)).ToList();
        var byId = stored.GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        // The standing, the commit, the requirements and the judge all come from the one verdict that decides (never the last valid one of the store, which may be a revision).
        var (commit, standing, deciding) = RemoteJudgement.VerdictInForceWithVerdict(WorkLog.Parse(log.Entries), log.Entries, run.Value, byId);
        if (commit is null || standing is not { } value)
        {
            return null;
        }

        var requirements = deciding?.Verdict.Assessments.Select(a => new RequirementOutcome(a.Requirement.Value, a.Status == AssessmentStatus.Satisfied)).ToList() ?? [];
        var provider = deciding?.Verdict.Judge.Model.Provider.Value ?? "-";
        var model = deciding?.Verdict.Judge.Model.Model ?? "-";
        var headIsJudgedCommit = head is null || string.Equals(head, commit, StringComparison.Ordinal);
        var notice = new VerdictNotice(run, commit, value, requirements, provider, model, headIsJudgedCommit);

        var tag = ResponseLanguage.Resolve(language);
        string Say(string code, params string[] arguments) => texts.Render(UserText.Of(code, [.. arguments]), tag);
        var description = Say(value switch
        {
            VerdictStanding.Approved => PublicationCodes.StatusApproved,
            VerdictStanding.Rejected => PublicationCodes.StatusRejected,
            _ => PublicationCodes.StatusInReview,
        });

        var body = new StringBuilder();
        body.AppendLine(MarkOf(run));
        body.AppendLine(Say(
            value switch
            {
                VerdictStanding.Approved => "hosting.verdict.comment-approved",
                VerdictStanding.Rejected => "hosting.verdict.comment-rejected",
                _ => "hosting.verdict.comment-in-review",
            },
            PullRequestTextComposer.Fence(commit)));
        body.AppendLine();
        foreach (var requirement in requirements.Take(60))
        {
            body.AppendLine("- " + Say(requirement.Satisfied ? "hosting.pr.body-requirement-met" : "hosting.pr.body-requirement-unmet", PullRequestTextComposer.Fence(requirement.RequirementId)));
        }

        body.AppendLine();
        body.AppendLine(Say("hosting.verdict.comment-judge", PullRequestTextComposer.Fence(provider), PullRequestTextComposer.Fence(model)));
        if (!headIsJudgedCommit)
        {
            body.AppendLine(Say("hosting.verdict.comment-head-moved"));
        }

        body.AppendLine();
        body.AppendLine(Say("hosting.verdict.comment-footer"));
        return new VerdictPackage(notice, description, body.ToString().TrimEnd());
    }
}
