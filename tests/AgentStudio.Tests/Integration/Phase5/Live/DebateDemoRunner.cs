using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Arena;
using AgentStudio.Desktop.Debate;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>Who started a demonstration, and when: what every capture repeats.</summary>
internal sealed record DemoStart(string Block, string Title, string Test, DateTimeOffset StartedAt, IReadOnlyList<DemoTool> Tools);

/// <summary>What a demonstration of a debate found once it ended: the capture it wrote, how it was followed and the checks of F5-2.</summary>
internal sealed record DebateDemoOutcome(DemoCapture Capture, DebateWatch Watch, RunId Debate, bool Parallel, bool Sealed, bool Independent, IReadOnlyList<string> RoundOneAuthors);

/// <summary>
/// Opens a prepared debate, follows it to its end (<see cref="DebateWatcher"/>) and writes its capture, whatever happened:
/// the quota is spent by then, so the evidence goes first and the assertions after. Besides the facts of the stores, it
/// checks in the log what F5-2 promises of the real run: the turns of round 1 were launched together (each started before
/// any ended), nothing of the round was published before its last turn ended, and no prompt of round 1 carries text, id or
/// hash of another participant's contribution.
/// </summary>
internal static class DebateDemoRunner
{
    /// <summary>The time a demonstration gives a debate: its 30 minutes, the judges with their corrections and a margin.</summary>
    public static readonly TimeSpan FollowLimit = TimeSpan.FromMinutes(75);

    public static async Task<DebateDemoOutcome> RunAsync(
        DemoApp app,
        DemoStart start,
        DebateProposal proposal,
        Func<Task<IDebateRun>> open,
        bool runTests,
        string? challengedUnit,
        DemoChallengeFacts? challenge,
        IReadOnlyList<string> notes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(open);
        var allNotes = new List<string>(notes);
        var run = await open();
        try
        {
            return await FollowAndCaptureAsync(app, start, proposal, run, runTests, challengedUnit, challenge, allNotes, ct);
        }
        catch (Exception ex)
        {
            // The quota is spent: whatever stopped the demonstration, it leaves a capture that says so.
            DemoCaptures.WriteFailure(start.Block, start.Title, start.Test, start.StartedAt, start.Tools, run.Id.Value, allNotes, ex);
            throw;
        }
    }

    private static async Task<DebateDemoOutcome> FollowAndCaptureAsync(
        DemoApp app,
        DemoStart start,
        DebateProposal proposal,
        IDebateRun run,
        bool runTests,
        string? challengedUnit,
        DemoChallengeFacts? challenge,
        List<string> allNotes,
        CancellationToken ct)
    {
        DebateWatch watch;
        await using (run)
        {
            if (runTests)
            {
                // «Ejecutar pruebas» (D-27) while round 1 goes on: free, isolated at Low integrity on the commit of the subject;
                // the participants of round 2 and the judge see its evidence.
                try
                {
                    await run.RunTestsAsync();
                    allNotes.Add("Durante la ronda 1 se pidió «Ejecutar pruebas» sobre el commit del objeto (verificación aislada, sin cuota).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The tests are the user's extra evidence: the debate goes on without them, and the evidence says why.
                    allNotes.Add($"«Ejecutar pruebas» no se pudo completar ({ex.GetType().Name}): {ex.Message}");
                }
            }

            watch = await DebateWatcher.FollowAsync(run, FollowLimit, ct);
        }

        var debate = run.Id;
        var log = await app.LoadLogAsync(CancellationToken.None);
        var tests = new List<DemoEvidence>();
        if (runTests)
        {
            foreach (var record in (await app.Get<IEvidenceStore>().ListByRunAsync(app.Project.ProjectId, debate.Value, CancellationToken.None))
                .Where(e => e.Kind is EvidenceKind.BuildLog or EvidenceKind.TestRun && e.Origin == EvidenceOrigin.CapturedBySystem))
            {
                tests.Add(await DemoFacts.EvidenceAsync(app, record, CancellationToken.None));
            }
        }

        var facts = await DemoFacts.DebateAsync(app, proposal, debate, watch, tests, CancellationToken.None);
        var verdicts = await DemoFacts.VerdictsAsync(app, log.Entries, challengedUnit ?? debate.Value, null, CancellationToken.None);
        var units = new HashSet<string>(StringComparer.Ordinal) { debate.Value };
        if (challengedUnit is not null)
        {
            units.Add(challengedUnit);
        }

        var (entries, alerts) = DemoFacts.Log(log.Entries, units);
        var (parallel, sealedRound, independent, authors, checks) = await RoundOneAsync(app, log.Entries, debate, facts);
        allNotes.AddRange(checks);
        var judges = facts.Judges.Select(j => j.Agent).ToHashSet(StringComparer.Ordinal);
        var judged = verdicts.Where(v => challengedUnit is null || v.Id != challenge?.Verdict).Select(v => v.Judge.Agent).Where(judges.Contains).ToHashSet(StringComparer.Ordinal);
        var completed = facts.Phase == nameof(DebatePhase.Closed) && authors.Count == facts.Participants.Count && judges.Count > 0 && judged.SetEquals(judges);
        var capture = new DemoCapture
        {
            Block = start.Block,
            Title = start.Title,
            Test = start.Test,
            StartedAt = start.StartedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            Result = ResultLine(facts, verdicts.Count),
            Completed = completed,
            Tools = start.Tools,
            Notes = allNotes,
            Debate = facts,
            Challenge = challenge,
            Verdicts = verdicts,
            Attempts = DemoFacts.Attempts(app.Project.ProjectId, log.Entries, new HashSet<string>(StringComparer.Ordinal) { debate.Value }),
            Quota = await DemoFacts.QuotaAsync(app, CancellationToken.None),
            Journal = DemoFacts.Journal(app.Project.RootPath, debate.Value),
            Log = entries,
            LogIntact = log.Verification.IsValid,
            HeadHash = log.HeadHash,
            Alerts = alerts,
            Approvals = DemoFacts.Approvals(app.Person),
        };
        DemoCaptures.Write(DemoSettings.CaptureDirectory!, capture);
        return new DebateDemoOutcome(capture, watch, debate, parallel, sealedRound, independent, authors);
    }

    /// <summary>The assertions every demonstration of a debate shares, made after its capture is written.</summary>
    public static void AssertDemonstrated(DebateDemoOutcome outcome, int participants, bool expectWaiver)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var capture = outcome.Capture;
        var debate = capture.Debate!;
        var why = $"{capture.Result} {debate.Stopped} Problemas: {string.Join(" | ", debate.Problems)}";
        Assert.True(capture.LogIntact, "The decision log does not verify. " + why);
        Assert.True(outcome.RoundOneAuthors.Count == participants, $"Round 1 has {outcome.RoundOneAuthors.Count} contributions of {participants} participants. " + why);
        Assert.True(outcome.Parallel, "The turns of round 1 were not launched together. " + why);
        Assert.True(outcome.Sealed, "A contribution of round 1 was published before its last turn ended. " + why);
        Assert.True(outcome.Independent, "A prompt of round 1 carries the contribution of another participant. " + why);
        Assert.True(capture.Completed, "The debate did not reach the verdicts of all its judges. " + why);
        Assert.Equal(nameof(DebatePhase.Closed), debate.Phase);
        Assert.NotEmpty(capture.Journal);
        Assert.All(capture.Journal, line => Assert.Contains("#debate", line, StringComparison.Ordinal));
        if (expectWaiver)
        {
            Assert.NotEmpty(debate.WaivedVendors);
        }

        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), File.ReadAllText(Path.Combine(DemoCaptures.Folder(DemoSettings.CaptureDirectory!), capture.Block + ".json")), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The checks of F5-2 on the log of the real debate (attempt 1 of each turn of round 1).</summary>
    private static async Task<(bool Parallel, bool Sealed, bool Independent, IReadOnlyList<string> Authors, IReadOnlyList<string> Notes)> RoundOneAsync(
        DemoApp app,
        IReadOnlyList<DecisionEntry> log,
        RunId debate,
        DemoDebateFacts facts)
    {
        var debateLog = DebateLog.Of(log, debate);
        var firsts = debateLog.Turns.Where(t => t.Attempt == 1 && DebateIds.TryReadTurn(t.Task?.Value, out var round, out _) && round == 1).ToList();
        var finished = firsts.Where(t => t.FinishedEntry is not null).Select(t => t.FinishedEntry!.Sequence).ToList();
        var parallel = firsts.Count >= 2 && finished.Count > 0 && firsts.All(t => t.StartedEntry.Sequence < finished.Min());
        var roundOne = debateLog.Contributions.Where(c => c.Payload.Round == 1).ToList();
        var lastFinished = debateLog.Turns.Where(t => DebateIds.TryReadTurn(t.Task?.Value, out var round, out _) && round == 1 && t.FinishedEntry is not null)
            .Select(t => t.FinishedEntry!.Sequence).DefaultIfEmpty(long.MaxValue).Max();
        var sealedRound = roundOne.Count > 0 && roundOne.All(c => c.Entry.Sequence > lastFinished);

        // No prompt of round 1 holds the evidence id or the hash of a contribution of another participant, nor its argument
        // (the summary is left out: a participant may quote the question, which every prompt carries).
        var independent = true;
        var notes = new List<string>();
        var texts = facts.Contributions.Where(c => c.Round == 1).ToList();
        foreach (var turn in debateLog.Turns.Where(t => DebateIds.TryReadTurn(t.Task?.Value, out var round, out _) && round == 1))
        {
            string prompt;
            try
            {
                await using var stream = await app.Get<IEvidenceStore>().OpenContentAsync(app.Project.ProjectId, turn.Started.PromptEvidenceId, CancellationToken.None);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                prompt = await reader.ReadToEndAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                notes.Add($"El prompt de {turn.Task} (intento {turn.Attempt}) no se pudo leer: {ex.Message}");
                independent = false;
                continue;
            }

            var label = facts.Participants.FirstOrDefault(p => p.Agent == turn.Started.Agent.Value)?.Label;
            foreach (var other in texts.Where(c => c.Author != label))
            {
                var leaked = (other.Argument is { Length: > 80 } argument && prompt.Contains(argument, StringComparison.Ordinal))
                    || prompt.Contains(other.Sha256, StringComparison.OrdinalIgnoreCase)
                    || roundOne.Any(c => c.Payload.Author.Value != turn.Started.Agent.Value && prompt.Contains(c.Payload.Evidence.Value, StringComparison.Ordinal));
                if (leaked)
                {
                    independent = false;
                    notes.Add($"El prompt de {turn.Task} (intento {turn.Attempt}) contiene la contribución {other.Label} de {other.Author}.");
                }
            }
        }

        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"Ronda 1 (F5-2): {firsts.Count} turnos lanzados {(parallel ? "a la vez (cada uno arrancó antes de que terminara ninguno)" : "sin solaparse")}; {roundOne.Count} contribuciones publicadas {(sealedRound ? "después del último final de la ronda" : "antes de que terminara la ronda")}; {(independent ? "ningún prompt lleva texto, id ni hash de otro participante" : "algún prompt lleva material de otro participante")}."));
        var authors = roundOne.Select(c => c.Payload.Author.Value).Distinct(StringComparer.Ordinal).ToList();
        return (parallel, sealedRound, independent, authors, notes);
    }

    private static string ResultLine(DemoDebateFacts facts, int verdicts)
    {
        var phase = Enum.TryParse<DebatePhase>(facts.Phase, out var p) ? DebateTexts.Phase(p) : facts.Phase;
        var outcome = Enum.TryParse<DebateOutcome>(facts.Outcome, out var o) ? DebateTexts.Outcome(o) : facts.Outcome;
        var text = new StringBuilder($"{phase}: {outcome}");
        if (facts.Decision is { } decision)
        {
            text.Append(", ").Append(decision);
        }

        if (facts.ChosenProposal is { } chosen)
        {
            text.Append(", propuesta ").Append(chosen);
        }

        text.Append(" (regla ").Append(DebateTexts.Rule(facts.Rule)).Append(string.Create(CultureInfo.InvariantCulture, $"); {verdicts} veredictos guardados"));
        if (facts.Stopped is not null)
        {
            text.Append(". ").Append(facts.Stopped);
        }

        return text.ToString();
    }
}
