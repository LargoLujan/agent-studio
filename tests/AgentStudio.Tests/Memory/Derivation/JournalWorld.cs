using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Derivation;
using AgentStudio.Memory.Journal;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// A project whose journal is written as the app and the development hook write it (docs/17 D-02): the product's lines by
/// the real <see cref="MarkdownDailyJournal"/> (with its clock at +02:00), each launch announced in the log as the engine
/// announces it (<c>AgentRunStarted</c>, then <c>AgentRunFinished</c> with the receipt the journal gave), and the hook's lines
/// appended to the file as <c>.claude/hooks/bitacora.ps1</c> appends them. The memory, the log and the derivation are those
/// of <see cref="DerivationWorld"/>; <see cref="Trust"/> is the service of the decision about the journal.
/// </summary>
internal sealed class JournalWorld : IDisposable
{
    /// <summary>The first day the tests write, in the local zone of the journal (+02:00).</summary>
    public static readonly DateOnly Day1 = new(2026, 3, 10);

    public static readonly DateOnly Day2 = new(2026, 3, 11);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private JournalWorld(DerivationWorld world)
    {
        World = world;
        Clock = new FakeClock(At(Day1, 9, 0, 0));
        Writer = new MarkdownDailyJournal(world.World.Root, new ProjectStorageFiles(), Clock);
        Trust = new JournalTrust(world.World.Instance.Recorder, world.World.Memory, world.World.Activity, world.World.Redactor, world.World.Clock);
    }

    public DerivationWorld World { get; }

    /// <summary>The clock of the journal: its local zone is +02:00.</summary>
    public FakeClock Clock { get; }

    /// <summary>The journal of the product: one instance, as the app keeps one per project, so it can give its receipts.</summary>
    public MarkdownDailyJournal Writer { get; private set; }

    public JournalTrust Trust { get; private set; }

    public string JournalDirectory => Path.Combine(World.World.DataDirectory, "bitacora");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<JournalWorld> CreateAsync(bool anchored = true) => new(await DerivationWorld.CreateAsync(anchored));

    /// <summary>The instant of <paramref name="day"/> at that local time of the journal (+02:00).</summary>
    public static DateTimeOffset At(DateOnly day, int hour, int minute, int second) =>
        new(day.Year, day.Month, day.Day, hour, minute, second, TimeSpan.FromHours(2));

    /// <summary>Closes the app and opens it again: the journal keeps no receipt of what it wrote before.</summary>
    public async Task ReopenAsync(bool anchored = true)
    {
        await World.ReopenAsync(anchored);
        Writer = new MarkdownDailyJournal(World.World.Root, new ProjectStorageFiles(), Clock);
        Trust = new JournalTrust(World.World.Instance.Recorder, World.World.Memory, World.World.Activity, World.World.Redactor, World.World.Clock);
    }

    public string DayFile(DateOnly day) => Path.Combine(JournalDirectory, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".md");

    public string[] DayLines(DateOnly day) => File.ReadAllText(DayFile(day), Utf8).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    public Task<MemoryDerivationResult> DeriveAsync() => World.DeriveAsync();

    /// <summary>The journal entries of the memory, whatever their trust (not those a trusted version supersedes).</summary>
    public async Task<IReadOnlyList<MemoryEntry>> JournalAsync() => [.. (await World.MemoryAsync()).Where(e => e.Kind == MemoryKind.JournalEntry)];

    /// <summary>The journal entries a prompt with <c>ReadSelected</c> could get.</summary>
    public async Task<IReadOnlyList<MemoryEntry>> TrustedJournalAsync() => [.. (await World.TrustedAsync()).Where(e => e.Kind == MemoryKind.JournalEntry)];

    /// <summary>
    /// One launch as the engine makes it (<c>AgentRunner</c>): <c>AgentRunStarted</c> in the unit of <paramref name="run"/>, the
    /// line of the journal with the anchor, the result line when the agent ends, and <c>AgentRunFinished</c> with the receipt
    /// the journal gave (none when <paramref name="receipt"/> is false: a launch of before Phase 12). With
    /// <paramref name="finish"/> false its end is not recorded yet (<see cref="FinishAsync"/>): the result line is in the file
    /// and the receipt is still to come to the log.
    /// </summary>
    public async Task<Launch> LaunchAsync(
        string run,
        string? task,
        int attempt,
        DateTimeOffset at,
        TimeSpan? lasts = null,
        string agent = "ejecutor",
        ProviderId? provider = null,
        string summary = "Implementar la tarea",
        bool receipt = true,
        bool finish = true)
    {
        var via = provider ?? ProviderId.ClaudeCode;
        var anchor = new AgentRunAnchor(run, task, attempt);
        var taskId = task is null ? (TaskId?)null : Tid(task);
        await World.RecordAsync(
            RunWriter.System,
            DecisionKind.AgentRunStarted,
            $"Arranca {agent}",
            new AgentRunStartedPayload(taskId, attempt, Aid(agent), via, VendorId.Anthropic, "sonnet", @"C:\wt\" + run, Eid($"prompt-{run}-{task}-{attempt}"), HashA, [], AgentRunPurpose.Executor) { RunId = run, V = AgentRunStartedPayload.PurposeVersion });
        var id = await Writer.RecordPromptAsync(new PromptJournalEntry(at, agent, via, "sonnet", summary, ["tests"], task) { Anchor = anchor }, Ct);
        var duration = lasts ?? TimeSpan.FromSeconds(30);
        var given = await Writer.RecordReceiptedOutcomeAsync(
            id,
            new PromptOutcome(at + duration, AgentRunStatus.Succeeded, duration, MetricValue<long>.Exact(1200, "u"), MetricValue<long>.Exact(340, "u")) { Anchor = anchor },
            Ct);
        var launch = new Launch(id, given!, anchor);
        if (finish)
        {
            await FinishAsync(launch, receipt);
        }

        return launch;
    }

    /// <summary>The <c>AgentRunFinished</c> of <paramref name="launch"/>, with the receipt of its entry unless <paramref name="receipt"/> is false.</summary>
    public Task<DecisionEntry> FinishAsync(Launch launch, bool receipt = true) =>
        World.RecordAsync(
            RunWriter.System,
            DecisionKind.AgentRunFinished,
            "Termina el lanzamiento",
            new AgentRunFinishedPayload(launch.Anchor.Task is { } task ? Tid(task) : null, launch.Anchor.Attempt, AttemptOutcome.Succeeded, 0, "sesion", null, null, null)
            {
                RunId = launch.Anchor.Run,
                Journal = receipt ? launch.Receipt : null,
            });

    /// <summary>Appends <paramref name="lines"/> to the file of <paramref name="day"/> as the hook does, creating the day with its two lines.</summary>
    public void Append(DateOnly day, params string[] lines)
    {
        Directory.CreateDirectory(JournalDirectory);
        var file = DayFile(day);
        var text = new StringBuilder();
        if (!File.Exists(file))
        {
            text.Append("# Bitácora · ").Append(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n\r\n")
                .Append("Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.\r\n")
                .Append("Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.\r\n\r\n");
        }

        foreach (var line in lines)
        {
            text.Append(line).Append("\r\n");
        }

        File.AppendAllText(file, text.ToString(), Utf8);
    }

    /// <summary>Replaces <paramref name="old"/> by <paramref name="replacement"/> in the file of <paramref name="day"/> (a hand edit, or a pull).</summary>
    public void Edit(DateOnly day, string old, string replacement)
    {
        var text = File.ReadAllText(DayFile(day), Utf8);
        Assert.Contains(old, text, StringComparison.Ordinal);
        File.WriteAllText(DayFile(day), text.Replace(old, replacement, StringComparison.Ordinal), Utf8);
    }

    /// <summary>A line of the hook: <c>- **HH:mm:ss** · sesión `xxxxxxxx` · #prompt … · «extracto»</c>.</summary>
    public static string Hook(string time, string excerpt, string session = "eb1c6494") => $"- **{time}** · sesión `{session}` · #prompt #tests · «{excerpt}»";

    public void Dispose() => World.Dispose();

    /// <summary>A launch: its entry, the receipt the journal gave and the start it was announced with.</summary>
    internal sealed record Launch(JournalEntryId Entry, JournalReceipt Receipt, AgentRunAnchor Anchor);
}
