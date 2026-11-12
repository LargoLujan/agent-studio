using System.Globalization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Domain.Tracing;

/// <summary>What the trailers of a commit of the system say (docs/16 D-30): the run, the task, the attempt, the agent and, when there is one, the model it was set with.</summary>
public sealed record SystemCommitTrailers(RunId Run, TaskId Task, int Attempt, AgentId Agent, string? Model);

/// <summary>
/// The trailers the system writes in its own commits (docs/16 D-30, F9-3): <c>Agent-Studio-Run</c>, <c>Agent-Studio-Task</c>,
/// <c>Agent-Studio-Attempt</c> and <c>Agent-Studio-Agent</c> (the agent and, after <c>; model=</c>, the model it was set with).
/// They survive a push and a rebase; after a squash it depends on the platform. They are an aid to infer
/// (<see cref="TraceSuggestionSource.CommitTrailer"/>), never the source of the trace: the ids live in the log, not in git,
/// and what is read from a message is a suggestion until a person confirms it.
/// </summary>
/// <remarks>
/// <para>Format: a block at the end of the message, after a blank line, one <c>Key: value</c> per line, in this order. Reading
/// takes only the last paragraph of the message, in which every line is a trailer of this form; a key that appears twice, or
/// a value that is not what the key takes, reads as none, so a message copied or edited by hand never reads as a commit of the
/// system by half.</para>
/// <para>Writing refuses a value with a line break or another control character: a trailer cannot be used to add one more.</para>
/// </remarks>
public static class CommitTrailers
{
    public const string RunKey = "Agent-Studio-Run";

    public const string TaskKey = "Agent-Studio-Task";

    public const string AttemptKey = "Agent-Studio-Attempt";

    public const string AgentKey = "Agent-Studio-Agent";

    private const string ModelSeparator = "; model=";

    /// <summary>The block of trailers for the commit of an attempt, without the blank line that must come before it.</summary>
    /// <exception cref="ArgumentException">A value has a control character, the attempt is not from 1, or the model is blank.</exception>
    public static string Format(RunId run, TaskId task, int attempt, AgentId agent, string? model)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var taskValue = Plain(task.Value, nameof(task));
        var agentValue = Plain(agent.Value, nameof(agent));
        if (agentValue.Contains(ModelSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The agent id cannot contain '{ModelSeparator}'.", nameof(agent));
        }

        var line = model is null ? agentValue : agentValue + ModelSeparator + Plain(model, nameof(model));
        return string.Join(
            '\n',
            $"{RunKey}: {run.Value}",
            $"{TaskKey}: {taskValue}",
            $"{AttemptKey}: {attempt.ToString(CultureInfo.InvariantCulture)}",
            $"{AgentKey}: {line}");
    }

    /// <summary>The message with the trailers after it, a blank line between them.</summary>
    public static string Append(string message, RunId run, TaskId task, int attempt, AgentId agent, string? model)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.TrimEnd('\r', '\n') + "\n\n" + Format(run, task, attempt, agent, model) + "\n";
    }

    /// <summary>The trailers of a commit message, or null when it has no block of ours, an incomplete one, a repeated key or a value the key does not take.</summary>
    public static SystemCommitTrailers? Read(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var start = lines.Length;
        while (start > 0 && lines[start - 1].Length > 0)
        {
            start--;
        }

        if (start == 0)
        {
            // No blank line: the whole message is one paragraph, the subject, which is no trailer block.
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines[start..])
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                return null;
            }

            var key = line[..colon];
            if ((key is RunKey or TaskKey or AttemptKey or AgentKey) && !values.TryAdd(key, line[(colon + 2)..]))
            {
                return null;
            }
        }

        if (!values.TryGetValue(RunKey, out var run) || !values.TryGetValue(TaskKey, out var task)
            || !values.TryGetValue(AttemptKey, out var attemptText) || !values.TryGetValue(AgentKey, out var agentText))
        {
            return null;
        }

        if (!int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1
            || string.IsNullOrWhiteSpace(task) || string.IsNullOrWhiteSpace(agentText))
        {
            return null;
        }

        string? model = null;
        var agent = agentText;
        var at = agentText.IndexOf(ModelSeparator, StringComparison.Ordinal);
        if (at >= 0)
        {
            agent = agentText[..at];
            model = agentText[(at + ModelSeparator.Length)..];
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(agent))
            {
                return null;
            }
        }

        try
        {
            return new SystemCommitTrailers(new RunId(run), new TaskId(task), attempt, new AgentId(agent), model);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Plain(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value != value.Trim())
        {
            throw new ArgumentException("A trailer carries a value on one line, without control characters nor spaces at its ends.", name);
        }

        return value;
    }
}
