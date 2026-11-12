using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Application.Diagnostics;

// Phase 6 contracts (docs/20 section 4, ADR-0061). The implementations are WP-A (the door for tools, the sources, the fixes) and WP-E (the
// declared environment); this file only fixes what they and the screen agree on.

/// <summary>
/// The names of the facts that more than one package of Phase 6 writes or paints (docs/20 section 4.2): the table of the declared
/// environment (WP-A paints it, WP-E fills it) and the facts of a tool. A <see cref="DoctorFact"/> name is lowercase words joined by
/// <c>.</c> and <c>-</c>, so each of these is one.
/// </summary>
public static class DoctorFactNames
{
    /// <summary>What the project asks for (the table of the declared environment).</summary>
    public const string Declared = "declared";

    /// <summary>The file, and the line when there is one, where the project says it.</summary>
    public const string Source = "source";

    /// <summary>What is installed.</summary>
    public const string Installed = "installed";

    /// <summary>What would be used: for .NET, the SDK that the host would resolve for the <c>global.json</c> rule.</summary>
    public const string Resolved = "resolved";

    /// <summary>The version of a tool.</summary>
    public const string Version = "version";

    /// <summary>The path where a tool was found.</summary>
    public const string Path = "path";
}

/// <summary>One source of checks of Auto Doctor (docs/20 D-01): the providers, the tools, the project, the declared environment, the containers.</summary>
public interface IDoctorCheckSource
{
    /// <summary>The section the checks of this source belong to.</summary>
    DoctorSection Section { get; }

    /// <summary>The checks of the source. <paramref name="project"/> is null when no project is open: then only the sources that do not need one answer.</summary>
    Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct);
}

/// <summary>Runs every source and joins their checks. A source that throws becomes one check in error: the panel never fails whole.</summary>
public interface IDoctorService
{
    Task<DoctorReport> RunAsync(ProjectContext? project, CancellationToken ct);
}

/// <summary>
/// The closed list of what Auto Doctor may ask of a tool (docs/20 D-02). The caller names an entry; it cannot pass an executable or an argument,
/// so the guarantee of F6-7 is structural. Entries are appended.
/// </summary>
public enum DoctorTool
{
    /// <summary><c>dotnet --list-sdks</c>.</summary>
    DotNetListSdks,

    /// <summary><c>dotnet --list-runtimes</c>.</summary>
    DotNetListRuntimes,

    /// <summary><c>node --version</c>.</summary>
    NodeVersion,

    /// <summary><c>npm --version</c>.</summary>
    NpmVersion,

    /// <summary><c>python --version</c>.</summary>
    PythonVersion,

    /// <summary><c>py --version</c>.</summary>
    PyVersion,

    /// <summary><c>git --version</c>.</summary>
    GitVersion,

    /// <summary><c>docker version --format json</c>, always against the local engine.</summary>
    DockerVersionJson,

    /// <summary><c>wsl --status</c>, with its output in UTF-8.</summary>
    WslStatus,
}

/// <summary>How a question to a tool ended. A result is always one of these: the door never throws for a tool that is not there.</summary>
public enum DoctorToolOutcome
{
    /// <summary>The tool answered; the exit code and the output are the answer.</summary>
    Completed,

    /// <summary>The locator found no such tool outside the projects.</summary>
    NotInstalled,

    /// <summary>The tool was found only inside the folder of an open project, so it is not used (docs/20 D-02).</summary>
    InsideProject,

    /// <summary>The tool did not answer in time (10 seconds).</summary>
    TimedOut,

    /// <summary>The process could not be started.</summary>
    FailedToStart,
}

/// <param name="ExecutablePath">Where the tool was found, when it was (outside every project).</param>
/// <param name="Output">What it wrote, up to 64 000 characters; data, never text of the system.</param>
/// <param name="Truncated">The output was cut at the cap.</param>
public sealed record DoctorToolResult(DoctorToolOutcome Outcome, int? ExitCode, string Output, bool Truncated, string? ExecutablePath)
{
    /// <summary>The longest output kept (docs/20 D-02).</summary>
    public const int MaxOutputLength = 64_000;

    /// <summary>The answer of a tool that did not run.</summary>
    public static DoctorToolResult Without(DoctorToolOutcome outcome, string? executablePath = null) =>
        outcome == DoctorToolOutcome.Completed
            ? throw new ArgumentException("A tool that did not run has no answer.", nameof(outcome))
            : new DoctorToolResult(outcome, null, string.Empty, false, executablePath);
}

/// <summary>
/// The one door for launching system tools from Auto Doctor (docs/20 D-02): a closed list, the executable resolved outside the open
/// projects, a neutral working folder and an environment from a short list. There is no overload with an executable or with arguments.
/// </summary>
public interface IDoctorToolRunner
{
    /// <param name="projectRoot">The root of the open project, so the door refuses what resolves inside it; null with no project. It is never the working folder.</param>
    Task<DoctorToolResult> RunAsync(DoctorTool tool, string? projectRoot, CancellationToken ct);
}

/// <summary>What is installed on this computer (docs/20 D-03), read through <see cref="IDoctorToolRunner"/> and the registry. WP-E uses it through this port.</summary>
public interface IInstalledToolInventory
{
    Task<InstalledTools> ReadAsync(string? projectRoot, CancellationToken ct);
}

/// <summary>What a fix does to the state of Agent Studio, or to nothing at all (<see cref="DoctorFix.Navigate"/> and <see cref="DoctorFix.Guide"/>).</summary>
public delegate Task DoctorAction(ProjectContext? project, CancellationToken ct);

/// <summary>
/// A fix of a check (docs/20 D-10): a closed hierarchy, because the rule is the type and not a convention. Its only constructor is private, so
/// the four forms below are the only ones there will ever be:
/// <list type="bullet">
/// <item><see cref="Navigate"/>: opens a screen of the app.</item>
/// <item><see cref="Guide"/>: shows what to do, with a command to copy or an official page. The app never runs the command.</item>
/// <item><see cref="Reversible"/>: changes the state of Agent Studio and carries the operation that undoes it.</item>
/// <item><see cref="Approved"/>: carries the request that <c>IApprovalGate</c> answers before anything is applied.</item>
/// </list>
/// There is no form that applies something with neither an undo nor an approval: it cannot be written. In Phase 6 every fix that applies is
/// reversible and acts on data of Agent Studio, never on the system, the repository of the person or the configuration of a CLI.
/// </summary>
public abstract class DoctorFix
{
    /// <summary>The longest command a guide carries.</summary>
    public const int MaxCommandLength = 300;

    private DoctorFix(string code)
    {
        Code = DoctorCodes.Require(code, nameof(code));
    }

    /// <summary>The stable code of the fix, the one a check names in <see cref="DoctorCheck.FixCode"/>.</summary>
    public string Code { get; }

    /// <summary>Opens a screen of the app: <paramref name="target"/> is the code of a screen the shell knows (<c>settings</c>, <c>setup</c>, <c>skills</c>…).</summary>
    public sealed class Navigate : DoctorFix
    {
        public Navigate(string code, string target)
            : base(code)
        {
            Target = DoctorCodes.Require(target, nameof(target));
        }

        public string Target { get; }
    }

    /// <summary>
    /// Says what to do. <paramref name="command"/> is shown to be copied, one line and never run by the app; <paramref name="page"/> is an
    /// official <c>https</c> page the person may open. At least one of them is there.
    /// </summary>
    public sealed class Guide : DoctorFix
    {
        public Guide(string code, string? command, Uri? page)
            : base(code)
        {
            if (command is null && page is null)
            {
                throw new ArgumentException("A guide gives a command to copy, a page to open, or both.", nameof(command));
            }

            if (command is not null && (command.Length is < 1 or > MaxCommandLength || command.Any(char.IsControl) || command != command.Trim()))
            {
                throw new ArgumentException($"A command to copy is one line of up to {MaxCommandLength} characters.", nameof(command));
            }

            if (page is not null && (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(page.UserInfo)))
            {
                throw new ArgumentException("A guide opens an https page with no credentials in it.", nameof(page));
            }

            Command = command;
            Page = page;
        }

        public string? Command { get; }

        public Uri? Page { get; }
    }

    /// <summary>A change of the state of Agent Studio that can be undone: it carries both operations, which the runner records and the tests apply and undo.</summary>
    public sealed class Reversible : DoctorFix
    {
        public Reversible(string code, DoctorAction apply, DoctorAction undo)
            : base(code)
        {
            Apply = apply ?? throw new ArgumentNullException(nameof(apply));
            Undo = undo ?? throw new ArgumentNullException(nameof(undo));
        }

        public DoctorAction Apply { get; }

        public DoctorAction Undo { get; }
    }

    /// <summary>A change that asks first: the runner gives <paramref name="approval"/> to <c>IApprovalGate</c> and applies only if it is granted (and, when the action is on the fixed list of ADR-0053, after Windows Hello).</summary>
    public sealed class Approved : DoctorFix
    {
        public Approved(string code, ApprovalRequest approval, DoctorAction apply)
            : base(code)
        {
            Approval = approval ?? throw new ArgumentNullException(nameof(approval));
            Apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        public ApprovalRequest Approval { get; }

        public DoctorAction Apply { get; }
    }
}

/// <summary>How the application of a fix ended.</summary>
public enum DoctorFixOutcome
{
    /// <summary>Applied (or opened or shown, for a navigation and a guide); an applied one left its entry in the log when a project is open.</summary>
    Done,

    /// <summary>The approval was denied: nothing was applied.</summary>
    Denied,

    /// <summary>Applying failed: the state was left as it was found.</summary>
    Failed,
}

/// <param name="AppliedSequence">The sequence of the <c>DoctorFixApplied</c> entry, when a project is open and the fix was applied; the one to hand to <c>UndoAsync</c>.</param>
public sealed record DoctorFixResult(DoctorFixOutcome Outcome, long? AppliedSequence = null);

/// <summary>
/// Applies the fixes (docs/20 D-10 and D-11): asks <c>IApprovalGate</c> for an <see cref="DoctorFix.Approved"/> one, records <c>DoctorFixApplied</c> and
/// undoes a reversible one by recording another entry that points to the first.
/// </summary>
public interface IDoctorFixRunner
{
    Task<DoctorFixResult> ApplyAsync(DoctorFix fix, ProjectContext? project, CancellationToken ct);

    /// <summary>Undoes the fix that left the entry <paramref name="appliedSequence"/>. Only a <see cref="DoctorFix.Reversible"/> one can be undone.</summary>
    Task UndoAsync(long appliedSequence, ProjectContext? project, CancellationToken ct);
}
