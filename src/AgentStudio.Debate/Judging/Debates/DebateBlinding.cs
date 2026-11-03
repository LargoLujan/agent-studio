using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>
/// Keeps who took part out of what the judge reads (docs/15 D-15). The system writes the titles and the authors of the
/// evidence of a debate, and they could name a participant: every agent id and name, CLI, vendor and model of the
/// participants of the debates of a dossier is replaced in them, and the evidence a participant produced is attributed to
/// its label. The text of a contribution is never changed: it is the material, and its hash is what the log anchored.
/// </summary>
internal sealed class DebateBlinding
{
    /// <summary>What replaces an agent of a participant in a text the system wrote.</summary>
    public const string HiddenParticipant = "[participante]";

    /// <summary>What replaces a CLI, a vendor or a model of a participant.</summary>
    public const string Hidden = "[oculto]";

    /// <summary>Shorter tokens are left alone: replacing them would mangle ordinary words.</summary>
    private const int MinTokenLength = 3;

    private readonly List<(string Token, string Replacement)> _tokens = [];

    /// <summary>Adds the identities of the participants of one debate.</summary>
    public void Add(IEnumerable<DebateParticipantDefinition> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        foreach (var participant in participants)
        {
            var agent = participant.Agent;
            Token(agent.Id.Value, HiddenParticipant);
            Token(agent.Name, HiddenParticipant);
            Token(agent.Model.Provider.Value, Hidden);
            Token(agent.Model.Vendor.Value, Hidden);
            Token(agent.Model.Model, Hidden);
        }

        // Longest first, so a model that contains a vendor's name is replaced whole.
        _tokens.Sort((a, b) => b.Token.Length.CompareTo(a.Token.Length));
    }

    /// <summary>The text with every identity of a participant replaced.</summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scrubbed = text;
        foreach (var (token, replacement) in _tokens)
        {
            scrubbed = scrubbed.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(scrubbed) ? Hidden : scrubbed;
    }

    /// <summary>
    /// The evidence as the judge may read it: its title and author scrubbed or, when the system knows who produced it, the
    /// title it gives and the label of its author. Everything the validator reads (kind, origin, time, hash, test counters,
    /// commit) is kept.
    /// </summary>
    public EvidenceRecord Blind(EvidenceRecord record, string? title = null, string? producedBy = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new EvidenceRecord(
            record.Id,
            record.Kind,
            record.Origin,
            title ?? Scrub(record.Title),
            producedBy ?? Scrub(record.ProducedBy),
            record.CapturedAt,
            record.ContentSha256,
            record.TestRun,
            record.Covers,
            record.SubjectCommit);
    }

    private void Token(string? value, string replacement)
    {
        var token = value?.Trim();
        if (token is { Length: >= MinTokenLength } && !_tokens.Any(t => string.Equals(t.Token, token, StringComparison.OrdinalIgnoreCase)))
        {
            _tokens.Add((token, replacement));
        }
    }
}
