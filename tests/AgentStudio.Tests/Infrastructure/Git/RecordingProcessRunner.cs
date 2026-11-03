using AgentStudio.Application.Execution;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Wraps a real <see cref="IProcessRunner"/>, records every <see cref="ProcessSpec"/> and can run code right after a
/// command finishes, which lets a test inject a race at an exact point (for example a competitor moving a ref between
/// <c>commit-tree</c> and <c>update-ref</c>).
/// </summary>
public sealed class RecordingProcessRunner(IProcessRunner inner) : IProcessRunner
{
    private readonly List<ProcessSpec> _specs = [];

    public IReadOnlyList<ProcessSpec> Specs
    {
        get
        {
            lock (_specs)
            {
                return _specs.ToArray();
            }
        }
    }

    /// <summary>Called after the real process finished, before its result is returned.</summary>
    public Action<ProcessSpec>? AfterRun { get; set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        lock (_specs)
        {
            _specs.Add(spec);
        }

        var result = await inner.RunAsync(spec, cancellationToken);
        AfterRun?.Invoke(spec);
        return result;
    }

    /// <summary>The git sub-command of a spec, without the leading global options (<c>-c key=value</c>, <c>--git-dir=</c>, <c>--work-tree=</c>).</summary>
    public static string SubCommand(ProcessSpec spec)
    {
        var index = 0;
        while (index < spec.Arguments.Count)
        {
            if (index + 1 < spec.Arguments.Count && spec.Arguments[index] == "-c")
            {
                index += 2;
            }
            else if (spec.Arguments[index].StartsWith("--git-dir=", StringComparison.Ordinal) || spec.Arguments[index].StartsWith("--work-tree=", StringComparison.Ordinal))
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return index < spec.Arguments.Count ? spec.Arguments[index] : string.Empty;
    }
}
