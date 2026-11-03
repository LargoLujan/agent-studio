using System.Globalization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The caps of one contribution (docs/15 D-10). The summary, the argument, the number of claims and citations and the 80
/// lines of a citation come from the design. The caps on the text of a claim, on the reason and the path of a citation and
/// on the whole answer are this module's: without them a single claim could carry what the argument cap keeps out, and
/// everything a participant writes reaches the other participants and the judge.
/// </summary>
public static class ContributionLimits
{
    /// <summary>
    /// The whole answer, checked before it is read. It is above the canonical form of the longest contribution the other
    /// caps allow, with every character of its texts escaped (<see cref="ContributionParser.ToJson"/>), so the canonical
    /// form of an accepted answer always reads back.
    /// </summary>
    public const int MaxAnswerChars = 256 * 1024;

    public const int MaxSummaryChars = 600;

    public const int MaxArgumentChars = 8_000;

    public const int MaxClaims = 10;

    public const int MaxClaimChars = 600;

    public const int MaxCitations = 8;

    /// <summary>The most lines one citation takes, both ends included.</summary>
    public const int MaxCitedLines = 80;

    public const int MaxPathChars = 260;

    public const int MaxWhyChars = 600;
}

/// <summary>
/// The labels of a debate (docs/15 D-10 and D-18): <c>C1</c>, <c>C2</c>… name the published contributions in publication
/// order, <c>CITA-1</c> to <c>CITA-8</c> name the citations of one contribution, and <see cref="None"/> is what a
/// contribution that replies to nothing says, because the portable schema has no nullable member.
/// </summary>
public static class ContributionLabels
{
    /// <summary>The sentinel of <c>repliesTo</c> for a contribution that replies to nothing.</summary>
    public const string None = "ninguna";

    private const char ContributionPrefix = 'C';

    private const string CitationPrefix = "CITA-";

    /// <summary>The ids a citation may take, <c>CITA-1</c> to <c>CITA-8</c>.</summary>
    public static IReadOnlyList<string> Citations { get; } =
        [.. Enumerable.Range(1, ContributionLimits.MaxCitations).Select(n => CitationPrefix + n.ToString(CultureInfo.InvariantCulture))];

    /// <summary>The label of the contribution published at <paramref name="index"/> (zero-based, as the log and the domain count): index 0 is <c>C1</c>.</summary>
    public static string Of(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return ContributionPrefix + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The zero-based index a label names, or null when <paramref name="label"/> is not a label exactly as <see cref="Of"/> writes it.</summary>
    public static int? IndexOf(string? label)
    {
        if (label is not { Length: > 1 } || label[0] != ContributionPrefix || label[1] == '0')
        {
            return null;
        }

        var digits = label.AsSpan(1);
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number - 1 : null;
    }

    /// <summary>True when <paramref name="text"/> reads as a label of the debate, a citation id or the sentinel.</summary>
    internal static bool IsReserved(string text) =>
        IndexOf(text) is not null || Citations.Contains(text, StringComparer.Ordinal) || string.Equals(text, None, StringComparison.Ordinal);
}

/// <summary>
/// What one turn may write (docs/15 D-10): the kinds its round admits, the published contributions it may reply to, cite
/// as support or concede (<see cref="Contributions"/>: the labels <c>C1</c>…<c>Cn</c> of everything published before the
/// round, D-18), and the evidence of the material it may cite (<see cref="Evidence"/>). <see cref="ContributionParser"/>
/// checks an answer against these closed sets, and the schema of the turn offers the same sets as enums.
/// </summary>
/// <remarks>
/// Round 1 is sealed (D-17): nothing is published before it, so the rules of round 1 never carry a contribution and an
/// independent proposal replies to nothing.
/// </remarks>
public sealed class ContributionRules
{
    /// <param name="round">The round of the turn, from 1.</param>
    /// <param name="kinds">The kinds the round admits (<see cref="KindsFor"/>).</param>
    /// <param name="published">How many contributions were published before the round: they are <c>C1</c> to <c>C</c><paramref name="published"/>.</param>
    /// <param name="evidence">The evidence of the material a claim may cite, each once.</param>
    /// <exception cref="ArgumentOutOfRangeException">The round is below 1, or <paramref name="published"/> is negative.</exception>
    /// <exception cref="ArgumentException">No kind, or one twice; round 1 with published contributions; an evidence twice, blank or with an id that reads as a label.</exception>
    public ContributionRules(int round, IReadOnlyList<ContributionKind> kinds, int published, IReadOnlyList<EvidenceId> evidence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentOutOfRangeException.ThrowIfNegative(published);
        ArgumentNullException.ThrowIfNull(evidence);

        if (kinds.Count == 0 || kinds.Distinct().Count() != kinds.Count || kinds.Any(k => !Enum.IsDefined(k)))
        {
            throw new ArgumentException("A turn admits at least one kind of contribution, each once.", nameof(kinds));
        }

        if (round == 1 && published > 0)
        {
            throw new ArgumentException(
                "Round 1 is sealed: nothing is published before it, so no participant sees a contribution of another one (docs/15 D-17).",
                nameof(published));
        }

        var ids = evidence.Select(e => e.Value).ToList();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ArgumentException("The evidence of the material is listed once each, never blank.", nameof(evidence));
        }

        if (ids.FirstOrDefault(ContributionLabels.IsReserved) is { } clash)
        {
            throw new ArgumentException(
                $"The evidence id '{clash}' reads as a label of the debate, a citation id or '{ContributionLabels.None}': the support of a claim could not tell them apart.",
                nameof(evidence));
        }

        Round = round;
        Kinds = [.. kinds];
        Contributions = [.. Enumerable.Range(0, published).Select(ContributionLabels.Of)];
        Evidence = [.. evidence];
    }

    public int Round { get; }

    public IReadOnlyList<ContributionKind> Kinds { get; }

    /// <summary>The labels of the contributions published before this round, in publication order.</summary>
    public IReadOnlyList<string> Contributions { get; }

    public IReadOnlyList<EvidenceId> Evidence { get; }

    /// <summary>The rules of a turn of <paramref name="mode"/> in <paramref name="round"/> (see <see cref="KindsFor"/>).</summary>
    public static ContributionRules For(DebateMode mode, int round, int published, IReadOnlyList<EvidenceId> evidence) =>
        new(round, KindsFor(mode, round), published, evidence);

    /// <summary>
    /// The kinds a round admits, as the domain does (<see cref="DebateSession.Submit"/>): an independent proposal in round 1
    /// (an assessment in a tribunal), and an objection, a rebuttal or evidence afterwards.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The round is below 1, or above 1 in a tribunal, which never deliberates.</exception>
    public static IReadOnlyList<ContributionKind> KindsFor(DebateMode mode, int round)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);
        if (round == 1)
        {
            return mode == DebateMode.Tribunal ? [ContributionKind.Assessment] : [ContributionKind.Proposal];
        }

        if (mode == DebateMode.Tribunal)
        {
            throw new ArgumentOutOfRangeException(nameof(round), round, "A tribunal never deliberates: its panelists only assess, in round 1.");
        }

        return [ContributionKind.Objection, ContributionKind.Rebuttal, ContributionKind.Evidence];
    }
}
