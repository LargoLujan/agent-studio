namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Sets environment variables of the test process and restores the previous values. Only for tests of
/// <see cref="ProcessEnvironmentCollection"/>, which run alone: every process another test starts meanwhile would
/// inherit the variables.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = [];

    public EnvironmentScope(IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
