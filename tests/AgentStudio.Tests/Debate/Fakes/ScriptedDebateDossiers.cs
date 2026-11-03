using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>
/// The dossier the judgment of a debate builds (WP2's <see cref="DebateDossierBuilder"/>, the real one) over the stores of a
/// world, every time the scripted judgment is asked: its system checks say what of the debate the log does not back (a header
/// that is not the approved one, a row of evidence the log never announced, a contribution that does not read).
/// </summary>
internal sealed class ScriptedDebateDossiers
{
    private readonly Lock _gate = new();
    private readonly DebateDossierBuilder _builder;
    private readonly List<(RunId Debate, bool HeaderVerified, IReadOnlyList<string> Checks)> _built = [];

    public ScriptedDebateDossiers(ScriptedDebateWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var options = new JudgingOptions(Path.Combine(world.Root, "dossiers"));
        var runs = new JudgeDossierBuilder(world.Recorder, world.Runs, new FakePlanStore(), world.Requirements, world.Evidence, options);
        _builder = new DebateDossierBuilder(world.Recorder, world.Debates, world.Requirements, world.Evidence, world.Verdicts, runs, options, world.Worktrees);
        world.Judgment.Inspect = InspectAsync;
    }

    /// <summary>Every dossier built, in order: the debate, whether its header verified, and its system checks.</summary>
    public IReadOnlyList<(RunId Debate, bool HeaderVerified, IReadOnlyList<string> Checks)> Built
    {
        get
        {
            lock (_gate)
            {
                return [.. _built];
            }
        }
    }

    private async Task InspectAsync(ProjectContext project, RunId debate, CancellationToken cancellationToken)
    {
        var package = await _builder.BuildPackageAsync(project, debate, cancellationToken);
        lock (_gate)
        {
            _built.Add((debate, package.HeaderVerified, package.Files.SystemChecks));
        }
    }
}
