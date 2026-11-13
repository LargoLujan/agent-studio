using AgentStudio.Application.Diagnostics;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>One entry of the closed list of the door (docs/20 D-02): the program to look for and the exact arguments, nothing else.</summary>
/// <param name="Command">The name looked up on the PATH, outside the open projects (never a path).</param>
/// <param name="StderrWhenBlank">The version of some tools goes to the error output (Python 2): it is read from there when the output is blank. Never for Docker and WSL, whose JSON and text are read from the output alone.</param>
/// <param name="RefusesStoreAlias">A <c>python</c> that is only the alias of the Microsoft Store is not started: it may open the Store, and it is not an interpreter.</param>
public sealed record DoctorToolEntry(DoctorTool Tool, string Command, IReadOnlyList<string> Arguments, bool StderrWhenBlank, bool RefusesStoreAlias);

/// <summary>
/// The closed list of what Auto Doctor may ask of a tool (docs/20 D-02). The caller names an entry of <see cref="DoctorTool"/>; here is what
/// it means. Tests walk <see cref="All"/>: it holds one entry per value of the enum, no argument comes from outside and none of them is free text.
/// </summary>
public static class DoctorToolCatalog
{
    private static readonly string[] Version = ["--version"];

    /// <summary>The whole catalog, in the order of <see cref="DoctorTool"/>.</summary>
    public static IReadOnlyList<DoctorToolEntry> All { get; } =
    [
        new(DoctorTool.DotNetListSdks, "dotnet", ["--list-sdks"], false, false),
        new(DoctorTool.DotNetListRuntimes, "dotnet", ["--list-runtimes"], false, false),
        new(DoctorTool.NodeVersion, "node", Version, true, false),
        new(DoctorTool.NpmVersion, "npm", Version, true, false),
        new(DoctorTool.PythonVersion, "python", Version, true, true),
        new(DoctorTool.PyVersion, "py", Version, true, false),
        new(DoctorTool.GitVersion, "git", Version, true, false),
        new(DoctorTool.DockerVersionJson, "docker", ["version", "--format", "json"], false, false),
        // WSL: its message when it is not installed may go to the error output.
        new(DoctorTool.WslStatus, "wsl", ["--status"], true, false),
        new(DoctorTool.DockerVersionDesktopPipe, "docker", ["--host", "npipe:////./pipe/dockerDesktopLinuxEngine", "version", "--format", "json"], false, false),
        // The CLI of GitHub (Phase 10, docs/21 D-31): only its version, with the environment of DoctorEnvironment that keeps it from reaching the network.
        new(DoctorTool.GhVersion, "gh", Version, false, false),
    ];

    /// <exception cref="ArgumentOutOfRangeException">A value that is not in the list.</exception>
    public static DoctorToolEntry Of(DoctorTool tool) =>
        All.FirstOrDefault(e => e.Tool == tool) ?? throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown tool of Auto Doctor.");
}
