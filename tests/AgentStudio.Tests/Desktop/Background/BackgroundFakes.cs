using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>Synthetic projects, runs and clocks of the background tests: nothing real behind them.</summary>
internal static class BackgroundSamples
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public const string RootA = @"C:\proyectos\monorepo";

    public const string RootB = @"C:\proyectos\tienda";

    public const string RootC = @"C:\proyectos\docs-internas";

    public static ProjectContext ProjectA { get; } = new("p-a", RootA);

    public static ProjectContext ProjectB { get; } = new("p-b", RootB);

    public static ProjectContext ProjectC { get; } = new("p-c", RootC);

    /// <summary>A registry that never polls by itself and raises its events inline: the test drives it.</summary>
    public static BackgroundWorkRegistry Registry(IProjectActivity? activity = null) =>
        new(new FakeTimeProvider(T0), activity, new InlineSynchronizationContext(), Timeout.InfiniteTimeSpan);

    public static TaskView TaskOf(string id, WorkTaskStatus status, string? agent = null, string? blockReason = null, WorkTaskKind kind = WorkTaskKind.AgentWork) =>
        new(new TaskId(id), $"Tarea {id}", kind, status, agent is null ? null : new AgentId(agent), Attempt: 1, RetryCount: 0, WorktreePath: null, Ref: null, blockReason, []);

    public static RunSnapshot Snapshot(string run, RunPhase phase, int agentRunsUsed, params TaskView[] tasks) =>
        new(new RunId(run), phase, tasks, FakeRun.Healthy, agentRunsUsed);

    /// <summary>A monitor that follows <paramref name="run"/>; its snapshot is applied at once, events as they are emitted.</summary>
    public static RunMonitorViewModel Follow(FakeRun run, ProjectContext project, TimeProvider? time = null)
    {
        var monitor = new RunMonitorViewModel(time ?? new FakeTimeProvider(T0), new InlineSynchronizationContext());
        monitor.Attach(run, project);
        return monitor;
    }

    /// <summary>Waits until <paramref name="condition"/> holds: the monitor reads the events of its run on a task of its own.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await System.Threading.Tasks.Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), because);
    }
}

/// <summary>A piece of work the test moves by hand: active or not, attempts running, and whether it can be paused.</summary>
internal sealed class FakeWork(BackgroundWorkKind kind, ProjectContext? project, string? workId, bool pausable = true, string? root = null) : IBackgroundWork
{
    public BackgroundWorkKind Kind => kind;

    public string ProjectRoot { get; } = root ?? project?.RootPath ?? @"C:\proyectos\sin-abrir";

    public ProjectContext? Project => project;

    /// <summary>The id of the work; a test changes it to stand for a document that follows another run now.</summary>
    public string? WorkId { get; set; } = workId;

    public bool IsActive { get; set; } = true;

    public int? AttemptsInFlight { get; set; }

    public bool IsPausable => pausable;

    public string Title => WorkId is null ? $"{kind}" : $"{kind} {WorkId}";

    public string StateText { get; set; } = "En ejecución";

    public Tone Tone => Tone.Active;

    public int Pauses { get; private set; }

    public Exception? PauseFailure { get; set; }

    public event EventHandler? Changed;

    public Task PauseAsync()
    {
        Pauses++;
        return PauseFailure is null ? Task.CompletedTask : Task.FromException(PauseFailure);
    }

    /// <summary>Sets what changes and says so, as a monitor does.</summary>
    public void Move(bool? active = null, int? attempts = null)
    {
        if (active is { } isActive)
        {
            IsActive = isActive;
        }

        if (attempts is { } running)
        {
            AttemptsInFlight = running;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The question of the exit, answered with what the test says; it records every question it was asked.</summary>
internal sealed class FakeExitPrompt(ExitChoice answer = ExitChoice.Cancel) : IExitPrompt
{
    public ExitChoice Answer { get; set; } = answer;

    public List<ExitQuestion> Asked { get; } = [];

    public Task<ExitChoice> AskAsync(ExitQuestion question, CancellationToken cancellationToken)
    {
        Asked.Add(question);
        return Task.FromResult(Answer);
    }
}

/// <summary>The main window, counted: shown and hidden, never on screen.</summary>
internal sealed class FakeShellWindow : IShellWindow
{
    public int Shown { get; private set; }

    public int Hidden { get; private set; }

    public bool IsVisible { get; private set; } = true;

    public void Show()
    {
        Shown++;
        IsVisible = true;
    }

    public void Hide()
    {
        Hidden++;
        IsVisible = false;
    }
}

/// <summary>Counts the times the app was asked to end.</summary>
internal sealed class FakeAppShutdown : IAppShutdown
{
    public int Calls { get; private set; }

    public void Shutdown() => Calls++;
}

/// <summary>What the exit wrote, one line per piece of work; a project can be made to fail, or every write to hang.</summary>
internal sealed class FakeRunExitRecorder : IRunExitRecorder
{
    public ConcurrentQueue<(string ProjectId, string WorkId, RunExitChoice Choice)> Recorded { get; } = new();

    public HashSet<string> FailingProjects { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every write waits for it (a disk that does not answer).</summary>
    public TaskCompletionSource? Hang { get; set; }

    public async Task RecordAsync(ProjectContext project, string workId, RunExitChoice choice, CancellationToken cancellationToken)
    {
        if (Hang is { } hang)
        {
            await hang.Task;
        }

        if (FailingProjects.Contains(project.ProjectId))
        {
            throw new DecisionLogIntegrityException($"The decision log of project '{project.ProjectId}' is not intact (simulated).");
        }

        Recorded.Enqueue((project.ProjectId, workId, choice));
    }
}

/// <summary>A log that keeps what it was told, with its level and its formatted message.</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>What holds each project, set by the test (the orchestrator's activity, without an orchestrator).</summary>
internal sealed class FakeProjectActivity : IProjectActivity
{
    public Dictionary<string, ProjectActivityKind> Holding { get; } = new(StringComparer.Ordinal);

    public bool IsBusy(string projectId) => Holding.ContainsKey(projectId);

    public ProjectActivityKind? Current(string projectId) => Holding.TryGetValue(projectId, out var kind) ? kind : null;

    public IDisposable? TryEnter(string projectId, ProjectActivityKind kind) => throw new NotSupportedException("The background only reads the activity.");
}
