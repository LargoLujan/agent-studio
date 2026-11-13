using System.Globalization;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using Words = AgentStudio.Memory.Diagrams.DiagramGrammar.Words;

namespace AgentStudio.Memory.Diagrams;

/// <summary>One entry of the log of a run that the sequence diagram draws: its kind, its hash and what it says.</summary>
internal sealed record RunEvent(DecisionKind Kind, string Hash, DecisionPayload Payload);

/// <summary>What the verified log says of a run, in the order it was recorded: the entries the diagrams draw and the hash of the last one.</summary>
internal sealed class RunLog
{
    public RunLog(string run, string planHash, IReadOnlyList<RosterEntry> roster, IReadOnlyList<RunEvent> events)
    {
        Run = run;
        PlanHash = planHash;
        Roster = roster;
        Events = events;
        var last = events[^1].Hash;
        LastHash = ContentHash.IsSha256Hex(last) ? last : ContentHash.Sha256Hex(last);
    }

    public string Run { get; }

    /// <summary>The hash of the plan the run was started with.</summary>
    public string PlanHash { get; }

    public IReadOnlyList<RosterEntry> Roster { get; }

    public IReadOnlyList<RunEvent> Events { get; }

    /// <summary>
    /// The hash of the last entry of the run that the sequence draws. It is the fingerprint of the model of the diagram of the sequence (docs/20 D-25): an entry of
    /// another run, or of a kind the diagram does not draw, does not move it, and one that is drawn does.
    /// </summary>
    public string LastHash { get; }
}

/// <summary>Reads the runs out of a verified log, as data: an entry that does not read back is left out, never thrown.</summary>
internal static class RunLogReader
{
    private static readonly DecisionKind[] Drawn =
    [
        DecisionKind.RunStarted,
        DecisionKind.TaskCreated,
        DecisionKind.AgentRunStarted,
        DecisionKind.AgentRunFinished,
        DecisionKind.EvidenceRegistered,
        DecisionKind.IntegrationPerformed,
        DecisionKind.ReviewRequested,
        DecisionKind.VerdictIssued,
        DecisionKind.ApprovalRequested,
        DecisionKind.ApprovalGranted,
        DecisionKind.ApprovalDenied,
        DecisionKind.RunFinished,
    ];

    /// <summary>The runs that have a start, newest first, each with the entries of its own the diagrams draw.</summary>
    public static IReadOnlyList<RunLog> Read(IReadOnlyList<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byRun = new Dictionary<string, List<RunEvent>>(StringComparer.Ordinal);
        var order = new List<string>();
        var starts = new Dictionary<string, RunStartedPayload>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (Array.IndexOf(Drawn, entry.Kind) < 0)
            {
                continue;
            }

            DecisionPayload? payload;
            try
            {
                payload = DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                continue;
            }

            if (payload is null || string.IsNullOrEmpty(payload.RunId))
            {
                continue;
            }

            if (payload is EvidenceRegisteredPayload { Kind: not (EvidenceKind.TestRun or EvidenceKind.BuildLog) })
            {
                continue;
            }

            if (payload is RunStartedPayload start && starts.TryAdd(payload.RunId, start))
            {
                order.Add(payload.RunId);
            }

            if (!byRun.TryGetValue(payload.RunId, out var list))
            {
                byRun[payload.RunId] = list = [];
            }

            list.Add(new RunEvent(entry.Kind, entry.Hash, payload));
        }

        var runs = new List<RunLog>();
        foreach (var run in order)
        {
            var events = byRun[run];
            if (!DiagramGrammar.IsRunId(run) ||!ContentHash.IsSha256Hex(starts[run].PlanHash))
            {
                continue;
            }

            runs.Add(new RunLog(run, starts[run].PlanHash, starts[run].Roster ?? [], events));
        }

        order.Reverse();
        return [.. runs.OrderBy(r => order.IndexOf(r.Run))];
    }
}

/// <summary>
/// The two diagrams of a run (docs/20 D-23): its plan (<c>flowchart TD</c>) and its sequence (<c>sequenceDiagram</c>). Pure, with the same guarantees as
/// <see cref="GraphDiagrams"/>. In the sequence, the messages come from a closed vocabulary (<see cref="Words"/>): no entry of the log puts free text in a message;
/// the title of a task goes in a note, escaped, and the name of an agent only in the declaration of its participant, escaped.
/// </summary>
internal static class RunDiagrams
{
    /// <summary>The plan of a run: a node per task, in the order of its ids, and an arrow from each prerequisite (with the name of its branch, for a condition).</summary>
    public static DiagramSource Plan(string run, PlanDefinition plan, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireRun(run);
        var tasks = plan.Tasks.Take(DiagramGrammar.MaxNodes).ToList();
        var omittedNodes = plan.Tasks.Count - tasks.Count;
        var ids = new Dictionary<string, string>(tasks.Count, StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            ids[tasks[i].Id.Value] = "n" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder();
        builder.Append("flowchart TD\n");
        foreach (var task in tasks)
        {
            var label = MermaidText.Label(task.Id.Value) + " · " + MermaidText.Label(task.Title);
            builder.Append("  ").Append(ids[task.Id.Value]).Append(task.Kind switch
            {
                WorkTaskKind.Condition => "{\"" + label + "\"}",
                WorkTaskKind.HumanApproval => "([\"" + label + "\"])",
                _ => "[\"" + label + "\"]",
            }).Append('\n');
        }

        var edges = 0;
        var omittedEdges = 0;
        foreach (var task in tasks)
        {
            foreach (var dependency in task.DependsOn)
            {
                if (!ids.TryGetValue(dependency.Prerequisite.Value, out var from))
                {
                    continue;
                }

                if (edges == DiagramGrammar.MaxEdges)
                {
                    omittedEdges++;
                    continue;
                }

                edges++;
                builder.Append("  ").Append(from);
                if (dependency.Branch is { } branch)
                {
                    builder.Append(" -->|\"").Append(MermaidText.Label(branch)).Append("\"| ");
                }
                else
                {
                    builder.Append(" --> ");
                }

                builder.Append(ids[task.Id.Value]).Append('\n');
            }
        }

        if (omittedNodes > 0 || omittedEdges > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  %% omitido: {omittedNodes} nodos y {omittedEdges} relaciones\n");
        }

        return new DiagramSource(
            DiagramKind.RunPlan,
            run,
            plan.ContentHash,
            DiagramGrammar.Header(DiagramGrammar.Word(DiagramKind.RunPlan), DiagramGrammar.RunOrigin(run), plan.ContentHash, at) + builder);
    }

    /// <summary>The sequence of a run: the person, the orchestrator, each agent, the system (verification and integration) and the judge.</summary>
    public static DiagramSource Sequence(RunLog log, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(log);
        RequireRun(log.Run);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var created in log.Events.Select(e => e.Payload).OfType<TaskCreatedPayload>())
        {
            titles.TryAdd(created.Task.Value, created.Title);
        }

        // The agents of the roster that are the orchestrator or a judge are drawn as those two participants, not as one of their own.
        var special = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in log.Roster.Where(r => r.Role is AgentRole.Orchestrator or AgentRole.Judge or AgentRole.Auditor))
        {
            special.TryAdd(entry.Agent.Value, entry.Role == AgentRole.Orchestrator ? "p2" : "p4");
        }

        var agents = log.Roster
            .Select(r => r.Agent.Value)
            .Concat(log.Events.Select(e => e.Payload).OfType<AgentRunStartedPayload>().Select(p => p.Agent.Value))
            .Where(a => !special.ContainsKey(a))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var participants = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < agents.Count; i++)
        {
            participants[agents[i]] = "p" + (i + 5).ToString(CultureInfo.InvariantCulture);
        }

        var body = new StringBuilder();
        body.Append("sequenceDiagram\n");
        body.Append("  participant p1 as \"").Append(Words.Person).Append("\"\n");
        body.Append("  participant p2 as \"").Append(Words.Orchestrator).Append("\"\n");
        body.Append("  participant p3 as \"").Append(Words.System).Append("\"\n");
        body.Append("  participant p4 as \"").Append(Words.Judge).Append("\"\n");
        foreach (var agent in agents)
        {
            body.Append("  participant ").Append(participants[agent]).Append(" as \"").Append(MermaidText.Participant(agent)).Append("\"\n");
        }

        var launched = new Dictionary<(string Task, int Attempt), string>();
        var messages = 0;
        var omitted = 0;

        bool Message(string from, string arrow, string to, string text)
        {
            if (messages == DiagramGrammar.MaxMessages)
            {
                omitted++;
                return false;
            }

            messages++;
            body.Append("  ").Append(from).Append(arrow).Append(to).Append(": ").Append(text).Append('\n');
            return true;
        }

        foreach (var ev in log.Events)
        {
            switch (ev.Payload)
            {
                case RunStartedPayload:
                    Message("p1", "->>", "p2", Words.StartsRun);
                    break;
                case AgentRunStartedPayload { Task: { } task } start when !special.ContainsKey(start.Agent.Value):
                    var participant = participants[start.Agent.Value];
                    launched[(task.Value, start.Attempt)] = participant;
                    if (Message("p2", "->>", participant, Words.LaunchesTask))
                    {
                        var title = titles.TryGetValue(task.Value, out var known) ? known : task.Value;
                        body.Append("  Note over ").Append(participant).Append(": ").Append(MermaidText.Label(title)).Append('\n');
                    }

                    break;
                case AgentRunFinishedPayload { Task: { } task } end when launched.TryGetValue((task.Value, end.Attempt), out var from):
                    Message(from, "-->>", "p2", end.Status switch
                    {
                        AttemptOutcome.Succeeded => Words.DeliversReport,
                        AttemptOutcome.Failed => Words.Fails,
                        AttemptOutcome.Cancelled => Words.IsCancelled,
                        _ => Words.IsInterrupted,
                    });
                    break;
                case EvidenceRegisteredPayload:
                    Message("p3", "->>", "p2", Words.Verifies);
                    break;
                case IntegrationPerformedPayload integration:
                    Message(
                        "p2",
                        "->>",
                        "p3",
                        integration.IsDelivery ? Words.Delivers : integration.ConflictPaths.Count > 0 ? Words.IntegratesWithConflict : Words.Integrates);
                    break;
                case ReviewRequestedPayload:
                    Message("p2", "->>", "p4", Words.AsksVerdict);
                    break;
                case VerdictIssuedPayload verdict:
                    Message("p4", "-->>", "p2", Verdict(verdict));
                    break;
                case ApprovalRequestedPayload:
                    Message("p2", "->>", "p1", Words.AsksApproval);
                    break;
                case ApprovalAnsweredPayload answer:
                    Message("p1", "->>", "p2", answer.Approved ? Words.Approves : Words.Denies);
                    break;
                case RunFinishedPayload:
                    Message("p2", "->>", "p1", Words.EndsRun);
                    break;
                default:
                    break;
            }
        }

        if (omitted > 0)
        {
            body.Append(CultureInfo.InvariantCulture, $"  Note over p2: se omiten {omitted} mensajes\n");
        }

        return new DiagramSource(
            DiagramKind.RunSequence,
            log.Run,
            log.LastHash,
            DiagramGrammar.Header(DiagramGrammar.Word(DiagramKind.RunSequence), DiagramGrammar.RunOrigin(log.Run), log.LastHash, at) + body);
    }

    private static string Verdict(VerdictIssuedPayload verdict)
    {
        if (!verdict.Valid)
        {
            return Words.VerdictInvalid;
        }

        return verdict.Decision switch
        {
            VerdictDecision.Approved => Words.VerdictApproved,
            VerdictDecision.ChangesRequested => Words.VerdictChanges,
            VerdictDecision.Rejected => Words.VerdictRejected,
            VerdictDecision.EscalateToHuman => Words.VerdictEscalated,
            _ => Words.VerdictUnresolved,
        };
    }

    private static void RequireRun(string run)
    {
        if (!DiagramGrammar.IsRunId(run))
        {
            throw new ArgumentException("A run is named by an id of letters, digits, '_' and '-'.", nameof(run));
        }
    }
}
