using AgentStudio.Domain.Environments;

namespace AgentStudio.Application.Diagnostics;

/// <summary>
/// Reads what a project declares about its environment (docs/20 D-05 and D-06): the closed list of files of <see cref="DeclaredFiles"/>, from the
/// working folder, as untrusted data. One reader for every file and one set of rules (256 KB, no links, no referenced file opened, JSON with
/// a depth of 32 and no repeated key, lines of up to 4096 characters, values cut at 200 characters). It runs nothing: not <c>npm</c>, not
/// <c>dotnet tool</c>, not the <c>python.exe</c> of a <c>.venv</c>, not <c>docker compose config</c>. WP-E implements it.
/// </summary>
public interface IDeclaredEnvironmentReader
{
    /// <param name="projectRoot">The root of the open project. What lies under it is data; nothing under it is executed.</param>
    Task<DeclaredEnvironment> ReadAsync(string projectRoot, CancellationToken ct);
}
