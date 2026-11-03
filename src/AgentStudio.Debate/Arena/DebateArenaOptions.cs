using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// Where the arena works and the defaults of a turn (docs/15 D-08, D-09 and D-22). The composition root decides the folder,
/// <c>%LOCALAPPDATA%\AgentStudio\debates</c>, like the worktrees and the dossiers.
/// </summary>
public sealed record DebateArenaOptions
{
    /// <summary>A turn runs at most this long when its agent asks for more (D-09).</summary>
    public static readonly TimeSpan DefaultMaxTurnDuration = TimeSpan.FromMinutes(5);

    /// <summary>A turn takes at most these turns of its CLI when its agent asks for more (D-09).</summary>
    public const int DefaultMaxTurnCliTurns = 12;

    /// <summary>How long a debate waits by itself for the usage limit of a provider to reset, when the CLI said when (D-22).</summary>
    public static readonly TimeSpan DefaultMaxAutoWaitForQuota = TimeSpan.FromMinutes(15);

    public DebateArenaOptions(string debatesRoot, TimeSpan? maxTurnDuration = null, int maxTurnCliTurns = DefaultMaxTurnCliTurns, TimeSpan? maxAutoWaitForQuota = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debatesRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTurnCliTurns);
        var duration = maxTurnDuration ?? DefaultMaxTurnDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero, nameof(maxTurnDuration));
        var wait = maxAutoWaitForQuota ?? DefaultMaxAutoWaitForQuota;
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero, nameof(maxAutoWaitForQuota));
        DebatesRoot = debatesRoot;
        MaxTurnDuration = duration;
        MaxTurnCliTurns = maxTurnCliTurns;
        MaxAutoWaitForQuota = wait;
    }

    /// <summary>The root of the copies of the commits and of the folders of the turns: <c>&lt;root&gt;\&lt;repo8&gt;\&lt;debate6&gt;</c>.</summary>
    public string DebatesRoot { get; }

    public TimeSpan MaxTurnDuration { get; }

    public int MaxTurnCliTurns { get; }

    public TimeSpan MaxAutoWaitForQuota { get; }
}

/// <summary>The ids of the debate (D-01) and of its turns (D-44).</summary>
public static partial class DebateIds
{
    /// <summary>The prefix of a debate: <c>deb-</c>, the one the prompt journal tags <c>#debate</c>.</summary>
    public const string Prefix = JournalingAgentProvider.DebatePrefix;

    /// <summary>The prefix of the task of a turn: <c>r&lt;round&gt;-&lt;agent&gt;</c>.</summary>
    public const string TurnPrefix = "r";

    /// <summary>
    /// A new debate id: <c>deb-</c>, the day in UTC and six random hexadecimal digits, none already taken. It is a valid
    /// <see cref="RunId"/>, and no orchestration run can have it.
    /// </summary>
    public static RunId New(TimeProvider time, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(isTaken);
        var day = time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var i = 0; i < 64; i++)
        {
            var id = $"{Prefix}{day}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            if (!isTaken(id))
            {
                return new RunId(id);
            }
        }

        throw new InvalidOperationException("Could not allocate a free debate id.");
    }

    /// <summary>Whether <paramref name="id"/> has the shape of a debate id.</summary>
    public static bool IsDebate(string? id) => id is not null && DebatePattern().IsMatch(id);

    /// <summary>The six hexadecimal digits of a debate id, the name of its folder; any other id is hashed.</summary>
    public static string Key(RunId debate) =>
        DebatePattern().Match(debate.Value) is { Success: true } match
            ? match.Groups[1].Value
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(debate.Value)))[..6];

    /// <summary>The task of the turn of <paramref name="agent"/> in <paramref name="round"/>: <c>r2-arquitecto</c>.</summary>
    public static TaskId Turn(int round, AgentId agent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);
        return new TaskId(TurnPrefix + round.ToString(CultureInfo.InvariantCulture) + "-" + agent.Value);
    }

    /// <summary>Reads the round and the agent of the task of a turn; false for any other task (the judge's, say).</summary>
    public static bool TryReadTurn(string? task, out int round, out AgentId agent)
    {
        round = 0;
        agent = default;
        if (task is null || TurnPattern().Match(task) is not { Success: true } match
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out round) || round < 1)
        {
            return false;
        }

        agent = new AgentId(match.Groups[2].Value);
        return true;
    }

    /// <summary>The label of the participant at <paramref name="index"/> (zero-based, in the order of the header): <c>P1</c>…</summary>
    public static string Participant(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return "P" + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^deb-[0-9]{8}-([0-9a-f]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex DebatePattern();

    [GeneratedRegex("^r([1-9][0-9]{0,3})-(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TurnPattern();
}
