using System.Globalization;

namespace AgentStudio.Orchestration.Engine;

/// <summary>Why a task is blocked (docs/13 section 3.7): the reason decides what happens next, so it is structured.</summary>
internal enum BlockKind
{
    /// <summary>The app stopped while the task was being worked on. Nothing is wrong with it; resuming is the user's decision.</summary>
    Interrupted,

    /// <summary>The agent (or a tool behind the task) could not be launched: CLI missing, no session, journal not writable.</summary>
    ProviderUnavailable,

    /// <summary>The provider reported a quota or rate limit; it ends at a known time.</summary>
    RateLimitedUntil,

    /// <summary>The agent said it cannot go on without a person.</summary>
    AwaitingHuman,
}

/// <summary>
/// A structured block reason. Its text form (<see cref="ToString"/>) is what the decision log and the UI carry:
/// <c>Interrupted</c>, <c>ProviderUnavailable</c>, <c>AwaitingHuman</c>, <c>RateLimitedUntil(2026-10-15T10:00:00.0000000+00:00)</c>
/// or, for a wait the system estimated because the provider did not say when its limit resets (docs/16 D-37),
/// <c>RateLimitedUntilEstimated(2026-10-15T10:00:00.0000000+00:00)</c>. The second form does not start with
/// <c>RateLimitedUntil(</c>: a reader that looks for that exact prefix does not see it, so whoever reads a block reason
/// reads both forms (<see cref="Parse"/>, the monitor's <c>BlockReasonText</c> and the memory's <c>DerivationTexts</c> do).
/// </summary>
internal sealed record BlockReason(BlockKind Kind, DateTimeOffset? Until = null, bool Estimated = false)
{
    public static readonly BlockReason Interrupted = new(BlockKind.Interrupted);
    public static readonly BlockReason ProviderUnavailable = new(BlockKind.ProviderUnavailable);
    public static readonly BlockReason AwaitingHuman = new(BlockKind.AwaitingHuman);

    private const string EstimatedSuffix = "Estimated";

    public static BlockReason RateLimited(DateTimeOffset until) => new(BlockKind.RateLimitedUntil, until);

    /// <summary>A wait for quota that the system estimated: the provider did not say when its limit resets.</summary>
    public static BlockReason EstimatedRateLimit(DateTimeOffset until) => new(BlockKind.RateLimitedUntil, until, Estimated: true);

    public override string ToString() => Kind == BlockKind.RateLimitedUntil && Until is { } until
        ? $"{nameof(BlockKind.RateLimitedUntil)}{(Estimated ? EstimatedSuffix : string.Empty)}({until.ToString("O", CultureInfo.InvariantCulture)})"
        : Kind.ToString();

    public static BlockReason? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var estimated in new[] { false, true })
        {
            var prefix = nameof(BlockKind.RateLimitedUntil) + (estimated ? EstimatedSuffix : string.Empty) + "(";
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith(')'))
            {
                return DateTimeOffset.TryParseExact(text[prefix.Length..^1], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
                    ? new BlockReason(BlockKind.RateLimitedUntil, until, estimated)
                    : null;
            }
        }

        return Enum.TryParse<BlockKind>(text, ignoreCase: false, out var kind) && kind != BlockKind.RateLimitedUntil && Enum.IsDefined(kind)
            && string.Equals(kind.ToString(), text, StringComparison.Ordinal)
            ? new BlockReason(kind)
            : null;
    }
}
