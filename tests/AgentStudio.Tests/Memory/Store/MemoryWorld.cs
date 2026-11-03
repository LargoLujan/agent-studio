using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Store;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>Whether a run or a planning goes on, as a test decides it; it counts how often it was asked.</summary>
internal sealed class FakeRunActivity : IRunActivity
{
    private int _checks;

    public volatile bool Busy;

    public int Checks => Volatile.Read(ref _checks);

    public bool IsBusy(string projectId)
    {
        Interlocked.Increment(ref _checks);
        return Busy;
    }

    /// <summary>Waits (in real time) until the activity was asked at least <paramref name="count"/> times.</summary>
    public async Task WaitForChecksAsync(int count = 1)
    {
        for (var i = 0; i < 500 && Checks < count; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(Checks >= count, $"The activity was asked {Checks} times, not {count}.");
    }
}

/// <summary>A memory store that lets a test see each append before it happens, or make the next one fail.</summary>
internal sealed class SpyMemoryStore(IMemoryStore inner) : IMemoryStore
{
    public Func<MemoryRow, Task>? BeforeAppend { get; set; }

    public bool FailNextAppend { get; set; }

    public async Task AppendAsync(string projectId, MemoryRow row, CancellationToken ct)
    {
        if (BeforeAppend is not null)
        {
            await BeforeAppend(row);
        }

        if (FailNextAppend)
        {
            FailNextAppend = false;
            throw new IOException("The disk went away just before the row was stored.");
        }

        await inner.AppendAsync(projectId, row, ct);
    }

    public Task<IReadOnlyList<MemoryRow>> SearchAsync(string projectId, MemorySearch search, CancellationToken ct) => inner.SearchAsync(projectId, search, ct);
}

/// <summary>
/// A project opened as the app opens it, with its memory over the real SQLite store and decision log, the anchors in a
/// folder of the test's own, a clock the test moves and an activity the test sets. Reopening is closing the app and
/// opening it again over the same folder, with or without the anchor store.
/// </summary>
internal sealed class MemoryWorld : IDisposable
{
    private readonly TempProjectFolder _folder;
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    private MemoryWorld(TempProjectFolder folder)
    {
        _folder = folder;
    }

    public FakeTimeProvider Clock { get; } = new(T0);

    public FakeRunActivity Activity { get; } = new();

    public ISecretRedactor Redactor { get; } = new SecretRedactor(@"C:\Users\persona");

    public ProjectInstance Instance { get; private set; } = null!;

    public SqliteMemoryStore Store { get; private set; } = null!;

    public SpyMemoryStore Spy { get; private set; } = null!;

    public ProjectMemory Memory { get; private set; } = null!;

    public ProjectContext Project => Instance.Project;

    public string Root => _folder.Root;

    public string DataDirectory => _folder.DataDirectory;

    public string AnchorFolder => _anchors;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<MemoryWorld> CreateAsync(bool anchored = true)
    {
        var world = new MemoryWorld(new TempProjectFolder());
        try
        {
            await world.OpenAsync(anchored);
            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    /// <summary>Closes the app and opens the project again, with (or without) the anchor store outside the project.</summary>
    public async Task ReopenAsync(bool anchored = true)
    {
        Instance.Dispose();
        await OpenAsync(anchored);
    }

    public async Task<IReadOnlyList<DecisionEntry>> LogAsync() => await Instance.DecisionLog.ReadAllAsync(Instance.ProjectId, Ct);

    public async Task<IReadOnlyList<DecisionEntry>> EntriesAsync(DecisionKind kind) => [.. (await LogAsync()).Where(e => e.Kind == kind)];

    public async Task<IReadOnlyList<AuditAlertPayload>> AlertsAsync() =>
        [.. (await EntriesAsync(DecisionKind.AuditAlertRaised)).Select(e => DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(e.PayloadJson))];

    public async Task<IReadOnlyList<MemoryRow>> RowsAsync() => await Store.SearchAsync(Instance.ProjectId, new MemorySearch(), Ct);

    /// <summary>Moves the clock a second at a time until <paramref name="task"/> ends (it waits on the clock, not on real time).</summary>
    public async Task AdvanceUntilAsync(Task task)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(task, Task.Delay(20, Ct));
        }

        Assert.True(task.IsCompleted, "The operation did not end once the project was no longer busy.");
        await task;
    }

    public async Task<T> AdvanceUntilAsync<T>(Task<T> task)
    {
        await AdvanceUntilAsync((Task)task);
        return await task;
    }

    public void Dispose()
    {
        Instance?.Dispose();
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private async Task OpenAsync(bool anchored)
    {
        Instance = await ProjectInstance.OpenAsync(_folder.Root, Clock, anchored ? _anchors : null);
        Store = new SqliteMemoryStore(Instance.Databases);
        Spy = new SpyMemoryStore(Store);
        Memory = new ProjectMemory(Instance.Recorder, Spy, Activity, Redactor, Clock);
    }
}
