using AgentStudio.Domain.Common;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// The closed list of files of a project that Auto Doctor reads to learn what it declares (docs/20 D-05): only these paths, relative to
/// the root of the project, never searched in subfolders. Whatever else a project holds is not read.
/// </summary>
public static class DeclaredFiles
{
    public const string GlobalJson = "global.json";
    public const string DotNetTools = ".config/dotnet-tools.json";
    public const string NvmRc = ".nvmrc";
    public const string NodeVersionFile = ".node-version";
    public const string PackageJson = "package.json";
    public const string PythonVersionFile = ".python-version";
    public const string PyProject = "pyproject.toml";
    public const string PyVenvCfg = ".venv/pyvenv.cfg";
    public const string DevContainerFolder = ".devcontainer/devcontainer.json";
    public const string DevContainerRoot = ".devcontainer.json";
    public const string ComposeYaml = "compose.yaml";
    public const string ComposeYml = "compose.yml";
    public const string DockerComposeYaml = "docker-compose.yaml";
    public const string DockerComposeYml = "docker-compose.yml";
    public const string Dockerfile = "Dockerfile";

    /// <summary>Every file that is read, in the order of the table of the design.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GlobalJson, DotNetTools, NvmRc, NodeVersionFile, PackageJson, PythonVersionFile, PyProject, PyVenvCfg, DevContainerFolder,
        DevContainerRoot, ComposeYaml, ComposeYml, DockerComposeYaml, DockerComposeYml, Dockerfile,
    ];

    /// <summary>The compose files: Phase 6 only says that one exists and its size; it does not interpret YAML (docs/20 decision 10).</summary>
    public static IReadOnlyList<string> ComposeFiles { get; } = [ComposeYaml, ComposeYml, DockerComposeYaml, DockerComposeYml];

    public static bool IsDeclared(string? path) => path is not null && All.Contains(path, StringComparer.Ordinal);
}

/// <summary>Where a declared thing was read: one of the files of <see cref="DeclaredFiles"/> and, when it has one, the line.</summary>
public sealed record DeclaredSource
{
    public DeclaredSource(string file, int? line = null)
    {
        if (!DeclaredFiles.IsDeclared(file))
        {
            throw new ArgumentException("Only the closed list of files of the design is read.", nameof(file));
        }

        if (line is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), line, "A line starts at 1.");
        }

        File = file;
        Line = line;
    }

    public string File { get; }

    public int? Line { get; }
}

/// <summary>What a declared entry is about. The names are written nowhere, but the desktop paints a text per kind: new kinds are appended.</summary>
public enum DeclaredKind
{
    /// <summary><c>sdk.version</c> of <c>global.json</c>.</summary>
    DotNetSdkVersion,

    /// <summary><c>sdk.rollForward</c>.</summary>
    DotNetRollForward,

    /// <summary><c>sdk.allowPrerelease</c>, as <c>true</c> or <c>false</c>.</summary>
    DotNetAllowPrerelease,

    /// <summary>One of <c>sdk.paths</c>: a folder where the host would look for the SDK. Data, never opened.</summary>
    DotNetSdkPath,

    /// <summary>A local tool of <c>dotnet-tools.json</c>: the name is its package id and the value its version.</summary>
    DotNetLocalTool,

    /// <summary>The first line of <c>.nvmrc</c> or <c>.node-version</c>.</summary>
    NodeVersion,

    /// <summary><c>engines.node</c> of <c>package.json</c>.</summary>
    NodeEngine,

    /// <summary><c>engines.npm</c> of <c>package.json</c>.</summary>
    NpmEngine,

    /// <summary>The first line of <c>.python-version</c>.</summary>
    PythonVersion,

    /// <summary><c>requires-python</c> of the <c>[project]</c> table of <c>pyproject.toml</c>.</summary>
    PythonRequires,

    /// <summary>The <c>version</c> line of <c>.venv/pyvenv.cfg</c>.</summary>
    VenvVersion,

    /// <summary>An image: <c>image</c> of a Dev Container or a <c>FROM</c> of a <c>Dockerfile</c>. Named, never pulled nor checked.</summary>
    ContainerImage,

    /// <summary><c>build.dockerfile</c> of a Dev Container. Named, never opened.</summary>
    DevContainerDockerfile,

    /// <summary>A <c>dockerComposeFile</c> of a Dev Container. Named, never opened.</summary>
    DevContainerComposeFile,

    /// <summary>One identifier of the <c>features</c> of a Dev Container.</summary>
    DevContainerFeature,

    /// <summary>A compose file exists; the value is its size in bytes. It is not interpreted.</summary>
    ComposeFile,

    /// <summary>The <c>extends</c> of a Dev Container (docs/20 D-06): the file it names. Named, never opened.</summary>
    Extends,
}

/// <summary>
/// One thing a project declares (docs/20 D-05 and D-06): what it is, its name when it has one (the package of a local tool), its value and
/// where it was read. The value came from a file of the repository and is data: one line, at most <see cref="MaxValueLength"/> characters,
/// and never an argument of a command line.
/// </summary>
public sealed record DeclaredEntry
{
    /// <summary>The longest value (docs/20 D-06).</summary>
    public const int MaxValueLength = DoctorFact.MaxValueLength;

    public DeclaredEntry(DeclaredKind kind, string? name, string value, DeclaredSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of declared entry.");
        }

        if (name is not null && (name.Length is < 1 or > 214 || UntrustedText.HasHidden(name)))
        {
            throw new ArgumentException("The name of a declared entry is one line of up to 214 characters.", nameof(name));
        }

        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength || UntrustedText.HasHidden(value))
        {
            throw new ArgumentException($"The value of a declared entry is one line of 1 to {MaxValueLength} characters.", nameof(value));
        }

        Kind = kind;
        Name = name;
        Value = value;
        Source = source;
    }

    public DeclaredKind Kind { get; }

    public string? Name { get; }

    public string Value { get; }

    public DeclaredSource Source { get; }
}

/// <summary>Why something of a file was not read (docs/20 D-06): the rules of reading are the same for every file.</summary>
public enum DeclaredIssueKind
{
    /// <summary>Over 256 KB: not read.</summary>
    TooLarge,

    /// <summary>The file, or a folder between the root and it, is a symbolic link or a junction: not read.</summary>
    IsLink,

    /// <summary>Not understood (a range outside the subset, an alias, a line that is not <c>key = value</c>): shown as written, never decided.</summary>
    NotInterpreted,

    /// <summary>Not valid JSON, or not valid text in UTF-8.</summary>
    Malformed,

    /// <summary>JSON nested deeper than 32 levels.</summary>
    TooDeep,

    /// <summary>A repeated key in a JSON object.</summary>
    DuplicateKey,

    /// <summary>A line of more than 4096 characters cut the reading.</summary>
    LineTooLong,

    /// <summary>The file could not be opened or read.</summary>
    Unreadable,
}

/// <summary>A file, or a part of one, that was not read, and why. <paramref name="Text"/> is what was there, as data and cut at the cap, when there is something to show.</summary>
public sealed record DeclaredIssue
{
    public DeclaredIssue(DeclaredSource source, DeclaredIssueKind kind, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of issue.");
        }

        if (text is not null && (text.Length is < 1 or > DeclaredEntry.MaxValueLength || UntrustedText.HasHidden(text)))
        {
            throw new ArgumentException($"The text of an issue is one line of 1 to {DeclaredEntry.MaxValueLength} characters.", nameof(text));
        }

        Source = source;
        Kind = kind;
        Text = text;
    }

    public DeclaredSource Source { get; }

    public DeclaredIssueKind Kind { get; }

    public string? Text { get; }
}

/// <summary>
/// What a project declares about its environment (docs/20 D-05): the entries that were read, what was not read and why, and which of the
/// files of the closed list exist. It is what the reader of WP-E builds from the files and the comparer compares with what is installed.
/// Nothing in it was executed: a value is text.
/// </summary>
public sealed record DeclaredEnvironment
{
    /// <summary>The most entries and issues a project can declare: a file with more is cut by the reader.</summary>
    public const int MaxItems = 500;

    public DeclaredEnvironment(IReadOnlyList<DeclaredEntry> entries, IReadOnlyList<DeclaredIssue> issues, IReadOnlyList<string> filesFound)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(filesFound);
        if (entries.Count > MaxItems || issues.Count > MaxItems || entries.Any(e => e is null) || issues.Any(i => i is null))
        {
            throw new ArgumentException($"A project declares up to {MaxItems} entries and {MaxItems} issues, none of them null.");
        }

        if (filesFound.Any(f => !DeclaredFiles.IsDeclared(f)) || filesFound.Distinct(StringComparer.Ordinal).Count() != filesFound.Count)
        {
            throw new ArgumentException("The files found are some of the closed list of files, each once.", nameof(filesFound));
        }

        Entries = [.. entries];
        Issues = [.. issues];
        FilesFound = [.. filesFound];
    }

    /// <summary>A project that declares nothing and has none of the files.</summary>
    public static DeclaredEnvironment Empty { get; } = new([], [], []);

    public IReadOnlyList<DeclaredEntry> Entries { get; }

    public IReadOnlyList<DeclaredIssue> Issues { get; }

    public IReadOnlyList<string> FilesFound { get; }

    public IEnumerable<DeclaredEntry> Of(DeclaredKind kind) => Entries.Where(e => e.Kind == kind);

    /// <summary>Whether the project declares a container (a Dev Container, a compose file or a <c>Dockerfile</c>), which is when Docker matters (docs/20 D-09).</summary>
    public bool DeclaresContainers =>
        FilesFound.Any(f => f is DeclaredFiles.DevContainerFolder or DeclaredFiles.DevContainerRoot or DeclaredFiles.Dockerfile
            || DeclaredFiles.ComposeFiles.Contains(f, StringComparer.Ordinal));

    public bool Equals(DeclaredEnvironment? other) =>
        other is not null && Entries.SequenceEqual(other.Entries) && Issues.SequenceEqual(other.Issues) && FilesFound.SequenceEqual(other.FilesFound);

    public override int GetHashCode() => HashCode.Combine(Entries.Count, Issues.Count, FilesFound.Count);
}
