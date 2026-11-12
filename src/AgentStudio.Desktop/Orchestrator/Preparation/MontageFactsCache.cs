using AgentStudio.Application.Prompts;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Context;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>What the analyzer is given from the cache: the context of each task and the agent runs the plan is expected to take.</summary>
/// <param name="TaskContexts">One per task of agent work whose agent the template has, in the order of the plan.</param>
/// <param name="EstimatedAgentRuns">The 75th percentile of the agent runs of the plan; null when the estimator does not know (fewer than three references) or has not answered yet.</param>
public sealed record MontageFacts(IReadOnlyList<TaskContextFacts> TaskContexts, long? EstimatedAgentRuns)
{
    public static MontageFacts None { get; } = new([], null);
}

/// <summary>What the cache reads the draft from: the plan as it is now and what it is checked against.</summary>
public sealed record MontageFactsSubject(
    ProjectContext Project,
    PlanDefinition Plan,
    IReadOnlyList<AgentDefinition> Roster,
    FrozenRequirementSet Requirements,
    VerificationProfile? Profile,
    ProjectBrief? Brief,
    LearnedModels? Learned,
    ModelWindowCatalog Windows,
    RunOptions Options);

/// <summary>
/// The facts of the draft that cost something to know (docs/17 D-16 and D-18, P49): how much context each task starts with and
/// how many agent runs the plan is expected to take. The draft is built on the UI thread at every edit, so it only reads what is
/// here; what is missing is asked once, in the background, and when it arrives the analysis runs again.
/// <list type="bullet">
/// <item><b>The context of a task</b> is measured with the pieces of the engine (<see cref="ExecutorContextPreview"/>), quick and
/// pure, on every read: typing the title or the objective of a task changes its size at once. Only two things are asked, once and
/// in the background: the memory, to the packer of the engine, which verifies the log of the project (once for each agent, memory
/// policy and set of requirements covered), and what the CLI presents of the skills of each agent (once for each agent). Both are
/// kept until the context of the document is read again (<see cref="Reset"/>), so editing the text of a task never reads the
/// log nor the catalog.</item>
/// <item><b>The agent runs</b> come from the estimator of the system (<see cref="IUsageEstimator.EstimatePlanAsync"/>), which reads
/// the whole log: it is asked once for each shape of the plan (its tasks, their kinds and their agents, and the program and model
/// of each agent), not for each letter typed. Until the answer for the current shape arrives there is no estimate, never the one
/// of another shape.</item>
/// </list>
/// Every answer is kept under the key of the question it answers, so the order in which answers arrive decides nothing: going back
/// to an earlier shape of the plan (undo) finds its answer, and a late answer for a shape that is no longer on the screen is kept
/// for when it is. The ports are asked off the UI thread (<see cref="Task.Run(Func{Task})"/>: they read the log, a catalog, a disk) and
/// each answer is written back on the context that built the cache (the UI thread), so nothing is shared between threads and there
/// are no locks. What is asked goes with a token that <see cref="Reset"/> and <see cref="Dispose"/> cancel, so a document that is
/// closed, or read again, leaves nothing running. Nothing launches an agent or spends quota.
/// </summary>
public sealed class MontageFactsCache : IDisposable
{
    private readonly BuilderServices _services;
    private readonly Action _changed;
    private readonly Dictionary<string, ContextPack?> _packs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _skills = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skillsAsked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _runs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runsAsked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _runsFailures = new(StringComparer.Ordinal);

    /// <summary>How many times the estimate of one shape of the plan is asked when it fails, before the shape is kept as unknown.</summary>
    private const int MaxRunsAttempts = 3;
    private CancellationTokenSource _lifetime = new();
    private int _epoch;
    private bool _disposed;
    private Task _pending = Task.CompletedTask;

    /// <param name="services">The ports it reads.</param>
    /// <param name="changed">Called when something that was missing arrived: the analysis of the draft has to run again.</param>
    public MontageFactsCache(BuilderServices services, Action changed)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(changed);
        _services = services;
        _changed = changed;
    }

    /// <summary>The background work asked so far, finished when every answer is in; for a test that has to wait for it.</summary>
    public Task Pending => _pending;

    /// <summary>
    /// Forgets what was read (the project opened again): what is still being asked is cancelled and, if an answer comes anyway, it
    /// is dropped; the memory, the skills and the estimate are asked again by the next draft.
    /// </summary>
    public void Reset()
    {
        if (_disposed)
        {
            return;
        }

        // What was known is forgotten before the questions are cancelled: a callback of the cancellation that throws leaves no half reset.
        _epoch++;
        _packs.Clear();
        _asked.Clear();
        _skills.Clear();
        _skillsAsked.Clear();
        _runs.Clear();
        _runsAsked.Clear();
        _runsFailures.Clear();
        var previous = _lifetime;
        _lifetime = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>The document was closed: what is being asked is cancelled and no answer is applied; nothing is asked again.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _epoch++;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>The facts for <paramref name="subject"/> as they are known now; what is not known yet is asked and left out.</summary>
    public MontageFacts Read(MontageFactsSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _disposed ? MontageFacts.None : new MontageFacts(TaskContexts(subject), EstimatedAgentRuns(subject));
    }

    private List<TaskContextFacts> TaskContexts(MontageFactsSubject subject)
    {
        var agents = subject.Roster.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var tasks = subject.Plan.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var facts = new List<TaskContextFacts>();
        List<(string Key, AgentDefinition Agent, TaskSpec Task)>? missing = null;
        List<AgentDefinition>? missingSkills = null;
        foreach (var task in subject.Plan.Tasks)
        {
            if (task.Kind != WorkTaskKind.AgentWork || task.Agent is not { } id || !agents.TryGetValue(id, out var agent))
            {
                continue;
            }

            ContextPack? pack = null;
            var reads = ExecutorContextPreview.ReadsMemory(agent);
            if (reads && _services.ContextPacker is not null)
            {
                var key = PackKey(agent, task, subject.Requirements);
                if (!_packs.TryGetValue(key, out pack) && _asked.Add(key))
                {
                    (missing ??= []).Add((key, agent, task));
                }
            }

            var skills = 0;
            if (_services.SkillCharacters is not null && !_skills.TryGetValue(agent.Id.Value, out skills) && _skillsAsked.Add(agent.Id.Value))
            {
                (missingSkills ??= []).Add(agent);
            }

            var size = ExecutorContextPreview.Measure(task, subject.Requirements, subject.Profile, agent, pack, subject.Brief, skills, ResponseLanguage.Resolve(_services.Language));
            facts.Add(new TaskContextFacts(
                task.Id,
                size.Tokens,
                WindowOf(agent, subject.Learned, subject.Windows),
                ExecutorContextPreview.MandatoryCharacters(task, subject.Requirements),

                // An agent that reads no memory gets no pack, so the engine checks no budget for it and the task starts however
                // long its requirements are: there is no limit to be over.
                reads ? ExecutorContextPreview.BudgetCharacters : int.MaxValue,
                HasMemory: reads,
                HasBrief: subject.Brief is not null && ExecutorContextPreview.ReceivesBrief(agent),
                HasUpstream: HasAgentWorkBefore(task, tasks)));
        }

        if (missing is not null)
        {
            Track(AskPacksAsync(_epoch, _lifetime.Token, subject.Project, subject.Requirements, missing));
        }

        if (missingSkills is not null)
        {
            Track(AskSkillsAsync(_epoch, _lifetime.Token, subject.Project, missingSkills));
        }

        return facts;
    }

    private long? EstimatedAgentRuns(MontageFactsSubject subject)
    {
        if (_services.Estimator is not { } estimator)
        {
            return null;
        }

        var key = RunsKey(subject);
        if (_runs.TryGetValue(key, out var known))
        {
            return known;
        }

        if (_runsAsked.Add(key))
        {
            Track(AskRunsAsync(_epoch, _lifetime.Token, estimator, key, subject));
        }

        return null;
    }

    private async Task AskPacksAsync(int epoch, CancellationToken token, ProjectContext project, FrozenRequirementSet requirements, IReadOnlyList<(string Key, AgentDefinition Agent, TaskSpec Task)> missing)
    {
        foreach (var (key, agent, task) in missing)
        {
            ContextPack? pack;
            try
            {
                pack = await Task.Run(() => ExecutorContextPreview.PackAsync(_services.ContextPacker, project, agent, task, requirements, token), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The document was closed or read again: nobody is waiting for this answer.
                return;
            }
#pragma warning disable CA1031 // The packer is behind a port: whatever it says (the requirements do not fit, the log does not verify, the store fails), the task is measured without memory, never thrown into the UI.
            catch (Exception)
#pragma warning restore CA1031
            {
                pack = null;
            }

            if (epoch != _epoch)
            {
                return;
            }

            _packs[key] = pack;
        }

        _changed();
    }

    private async Task AskSkillsAsync(int epoch, CancellationToken token, ProjectContext project, IReadOnlyList<AgentDefinition> agents)
    {
        foreach (var agent in agents)
        {
            int characters;
            try
            {
                // The catalog and the files are read off the UI thread, whatever the port does before its first await.
                characters = await Task.Run(() => _services.SkillCharacters!(project, agent, token), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The catalog and the files are behind a port: a failure counts no skills, never an error in the UI.
            catch (Exception)
#pragma warning restore CA1031
            {
                characters = 0;
            }

            if (epoch != _epoch)
            {
                return;
            }

            _skills[agent.Id.Value] = characters;
        }

        _changed();
    }

    private async Task AskRunsAsync(int epoch, CancellationToken token, IUsageEstimator estimator, string key, MontageFactsSubject subject)
    {
        long? runs;
        try
        {
            var request = new PlanEstimateRequest(subject.Project, subject.Plan, subject.Roster, subject.Options);
            var estimate = await Task.Run(() => estimator.EstimatePlanAsync(request, token), token).ConfigureAwait(true);

            // The high end: the budget has to cover the runs the plan is likely to take, and the fix raises it to this figure.
            runs = estimate.AgentRuns.IsKnown ? estimate.AgentRuns.High.Value : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // The estimator is behind a port: no estimate is said as such (the rule stays quiet), never thrown into the UI.
        catch (Exception)
#pragma warning restore CA1031
        {
            // A failure may pass (the log busy, say): the shape is asked again on a later read, a few times, before it is kept as unknown.
            var failures = _runsFailures.GetValueOrDefault(key) + 1;
            if (epoch == _epoch && failures < MaxRunsAttempts)
            {
                _runsFailures[key] = failures;
                _runsAsked.Remove(key);
                return;
            }

            runs = null;
        }

        if (epoch != _epoch)
        {
            return;
        }

        // Kept under the shape it answers: an answer that comes after the plan changed again is still the answer of its shape.
        _runs[key] = runs;
        _changed();
    }

    private void Track(Task work) => _pending = Task.WhenAll(_pending, work);

    /// <summary>The memory a task is packed with depends on its agent and on the requirements it covers, not on what its text says.</summary>
    private static string PackKey(AgentDefinition agent, TaskSpec task, FrozenRequirementSet requirements) =>
        string.Join('|', agent.Id.Value, agent.Role, agent.Memory, requirements.ContentHash, string.Join(',', task.Covers.Select(c => c.Value)));

    /// <summary>What the estimate of a plan depends on: which tasks of which kind go to which agent, and the program and model of each agent.</summary>
    private static string RunsKey(MontageFactsSubject subject) =>
        string.Join(
            '#',
            string.Join(';', subject.Plan.Tasks.Select(t => $"{t.Id.Value}:{t.Kind}:{t.Agent?.Value}")),
            string.Join(';', subject.Roster.OrderBy(a => a.Id.Value, StringComparer.Ordinal).Select(a => $"{a.Id.Value}:{a.Model.Provider.Value}:{a.Model.Model}")));

    /// <summary>The window of the model of <paramref name="agent"/>: what the log of the project taught of it, else what the monitor judges the context with (the log, then the base table).</summary>
    private static MetricValue<long> WindowOf(AgentDefinition agent, LearnedModels? learned, ModelWindowCatalog windows) =>
        learned?.Find(agent.Model.Provider, agent.Model.Model) is { } known && known.ContextWindow.HasValue
            ? known.ContextWindow
            : windows.WindowFor(agent.Model.Model);

    /// <summary>Whether a task of agent work comes before <paramref name="task"/> in the graph: the engine gives it the report of each one that ran.</summary>
    private static bool HasAgentWorkBefore(TaskSpec task, IReadOnlyDictionary<TaskId, TaskSpec> tasks)
    {
        var seen = new HashSet<TaskId>();
        var pending = new Stack<TaskId>(task.DependsOn.Select(d => d.Prerequisite));
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!seen.Add(id) || !tasks.TryGetValue(id, out var before))
            {
                continue;
            }

            if (before.Kind == WorkTaskKind.AgentWork)
            {
                return true;
            }

            foreach (var dependency in before.DependsOn)
            {
                pending.Push(dependency.Prerequisite);
            }
        }

        return false;
    }
}
