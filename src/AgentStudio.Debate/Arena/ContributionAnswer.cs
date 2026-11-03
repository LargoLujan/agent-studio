using System.Globalization;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Debate;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// One contribution as a participant answered it, read and checked by <see cref="ContributionParser"/> (docs/15 D-10). It is
/// what the participant says, never proof: once redacted (<see cref="Redact"/>), its canonical JSON
/// (<see cref="ContributionParser.ToJson"/>) is the <c>AgentStatement</c> reported by the agent that the other participants
/// and the judge read, and its citations are resolved in the copy of the commit (<see cref="CitationResolver"/>).
/// </summary>
/// <param name="RepliesTo">The label of the published contribution it replies to (<c>C3</c>), or null when it replies to nothing (<see cref="ContributionLabels.None"/>).</param>
/// <param name="Claims">What it asserts, each with its support: published contributions, evidence of the material and its own citations.</param>
/// <param name="Concedes">The labels of the published contributions it concedes.</param>
/// <param name="Finished">The participant has nothing more to add: when every participant of a round says so, the debate goes to judgment (D-18).</param>
public sealed record ContributionAnswer(
    ContributionKind Kind,
    string? RepliesTo,
    string Summary,
    string Argument,
    IReadOnlyList<ContributionClaim> Claims,
    IReadOnlyList<ContributionCitation> Citations,
    IReadOnlyList<string> Concedes,
    bool Finished)
{
    /// <summary>The zero-based index of <see cref="RepliesTo"/> in publication order, as the domain and the log keep it.</summary>
    public int? RepliesToIndex => ContributionLabels.IndexOf(RepliesTo);

    /// <summary>
    /// The contribution with every free text through <paramref name="redactor"/>: the summary, the argument, each claim, and
    /// the path and the reason of each citation, in that order. Labels, evidence ids and citation ids belong to closed sets
    /// the system gave the turn, so they are left as they are. <see cref="RedactedContribution.RedactedTexts"/> counts the
    /// texts the redactor changed (<see cref="ISecretRedactor"/> says nothing about how many secrets a text held).
    /// </summary>
    /// <remarks>Resolve the citations before redacting: a redacted path may no longer name its file.</remarks>
    public RedactedContribution Redact(ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);

        var changed = 0;
        string Clean(string text)
        {
            var clean = redactor.Redact(text);
            if (!string.Equals(clean, text, StringComparison.Ordinal))
            {
                changed++;
            }

            return clean;
        }

        var summary = Clean(Summary);
        var argument = Clean(Argument);
        var claims = new List<ContributionClaim>(Claims.Count);
        foreach (var claim in Claims)
        {
            claims.Add(claim with { Claim = Clean(claim.Claim) });
        }

        var citations = new List<ContributionCitation>(Citations.Count);
        foreach (var citation in Citations)
        {
            var path = Clean(citation.Path);
            var why = Clean(citation.Why);
            citations.Add(citation with { Path = path, Why = why });
        }

        return new RedactedContribution(this with { Summary = summary, Argument = argument, Claims = claims, Citations = citations }, changed);
    }
}

/// <param name="Support">Labels of published contributions, ids of evidence of the material and ids of citations of the same contribution.</param>
public sealed record ContributionClaim(string Claim, IReadOnlyList<string> Support);

/// <summary>A citation of code: lines <paramref name="StartLine"/> to <paramref name="EndLine"/> (both included, from 1) of a file of the copy of the commit.</summary>
/// <param name="Id">Its id in the contribution, <c>CITA-1</c> to <c>CITA-8</c>.</param>
/// <param name="Path">Relative to the root of the copy of the commit, as the participant wrote it.</param>
/// <param name="Why">Why the participant cites those lines.</param>
public sealed record ContributionCitation(string Id, string Path, int StartLine, int EndLine, string Why)
{
    /// <summary>The citation as people write it: <c>src/Calc.cs:10-40</c>.</summary>
    public string Locator => string.Create(CultureInfo.InvariantCulture, $"{Path}:{StartLine}-{EndLine}");
}

/// <param name="RedactedTexts">How many texts of the contribution the redactor changed (the redactions the log records, D-10).</param>
public sealed record RedactedContribution(ContributionAnswer Answer, int RedactedTexts);
