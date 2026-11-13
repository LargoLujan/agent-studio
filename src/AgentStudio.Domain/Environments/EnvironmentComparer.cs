using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// The codes of the checks of the declared environment (docs/20 D-07 and D-08) and of the fixes they name. The panel paints a title and an explanation from each
/// code (ADR-0054); the fix codes are the ones of the closed list of Auto Doctor (<c>DoctorFixCatalog</c>, which the domain cannot call, so a test keeps both in step): this package only names them, and a fix that is not on the list paints no button.
/// </summary>
public static class EnvironmentCheckCodes
{
    public const string None = "doctor.env.none";
    public const string DotNetSdk = "doctor.env.dotnet.sdk";
    public const string DotNetSdkInRepo = "doctor.env.dotnet.sdk-in-repo";
    public const string DotNetLocalTools = "doctor.env.dotnet.local-tools";
    public const string NodeNvmrc = "doctor.env.node.nvmrc";
    public const string NodeVersionFile = "doctor.env.node.version-file";
    public const string NodeEngine = "doctor.env.node.engine";
    public const string NpmEngine = "doctor.env.npm.engine";
    public const string PythonVersionFile = "doctor.env.python.version-file";
    public const string PythonRequires = "doctor.env.python.requires";
    public const string PythonVenv = "doctor.env.python.venv";
    public const string Containers = "doctor.env.containers";
    public const string NotRead = "doctor.env.not-read";

    public const string FixInstallDotNetSdk = "doctor.fix.install-dotnet";
    public const string FixInstallNode = "doctor.fix.install-node";
    public const string FixInstallPython = "doctor.fix.install-python";
    public const string FixInstallDocker = "doctor.fix.install-docker";
    public const string FixStartDocker = "doctor.fix.start-docker";
    public const string FixRestoreDotNetTools = "doctor.fix.restore-dotnet-tools";

    /// <summary>The checks, in the order the table of the declared environment shows them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        None, DotNetSdk, DotNetSdkInRepo, DotNetLocalTools, NodeNvmrc, NodeVersionFile, NodeEngine, NpmEngine, PythonVersionFile, PythonRequires,
        PythonVenv, Containers, NotRead,
    ];
}

/// <summary>
/// Sets what a project declares against what is installed (docs/20 D-07 and D-08), as pure code: no disk, no process. Each row of the table is a check of the
/// section «declared environment» with the facts <see cref="DoctorFactNames.Declared"/>, <c>source</c>, <c>installed</c> and <c>resolved</c> (the names are those of
/// <c>DoctorFactNames</c>, in the application layer, which the domain cannot call). What cannot be compared is «not checked» (<see cref="DoctorStatus.Unknown"/>),
/// never correct: a range outside the subset, a tag such as <c>lts/*</c>, a <c>global.json</c> with <c>sdk.paths</c> that the host would search before the installed SDKs.
/// A tool that the project asks for and is not installed is an error; a version that does not satisfy a range, a warning.
/// </summary>
public static class EnvironmentComparer
{
    private const string Declared = "declared";
    private const string Source = "source";
    private const string Installed = "installed";
    private const string Resolved = "resolved";
    private const string None = EnvironmentPhrases.None;

    /// <summary>The key of a local tool in the set the caller gives: <c>id@version</c> in lowercase.</summary>
    public static string LocalToolKey(string id, string version) => (id + "@" + version).ToLowerInvariant();

    /// <param name="declared">What the project says.</param>
    /// <param name="installed">What the computer has.</param>
    /// <param name="projectRoot">The root of the project, to tell which <c>sdk.paths</c> lie inside it; without it only a relative one does.</param>
    /// <param name="localToolsInCache">
    /// The local tools of <c>dotnet-tools.json</c> whose package is in the global NuGet cache, by <see cref="LocalToolKey"/>; null when it was not looked at, and the check is then «not checked».
    /// </param>
    public static IReadOnlyList<DoctorCheck> Compare(
        DeclaredEnvironment declared, InstalledTools installed, string? projectRoot, IReadOnlySet<string>? localToolsInCache)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(installed);
        if (declared.FilesFound.Count == 0)
        {
            return [new DoctorCheck(EnvironmentCheckCodes.None, DoctorSection.DeclaredEnvironment, DoctorStatus.NotApplicable, [])];
        }

        var checks = new List<DoctorCheck>();
        DotNet(declared, installed, projectRoot, localToolsInCache, checks);
        Node(declared, installed, checks);
        Python(declared, installed, checks);
        Containers(declared, installed, checks);
        NotRead(declared, checks);
        return checks;
    }

    // ---- .NET ----

    private static void DotNet(DeclaredEnvironment declared, InstalledTools installed, string? projectRoot, IReadOnlySet<string>? cache, List<DoctorCheck> checks)
    {
        var version = declared.Of(DeclaredKind.DotNetSdkVersion).FirstOrDefault();
        var rollForward = declared.Of(DeclaredKind.DotNetRollForward).FirstOrDefault();
        var allow = declared.Of(DeclaredKind.DotNetAllowPrerelease).FirstOrDefault();
        var paths = declared.Of(DeclaredKind.DotNetSdkPath).ToList();
        if (version is not null || rollForward is not null || allow is not null || paths.Count > 0)
        {
            var resolution = SdkResolver.Resolve(
                version?.Value, rollForward?.Value, allow is null ? null : allow.Value == "true", [.. installed.DotNetSdks.Select(s => s.Version)], [.. paths.Select(p => p.Value)]);
            var facts = new List<DoctorFact>();
            if (version is not null)
            {
                facts.Add(F(Declared, "sdk " + version.Value));
            }

            if (rollForward is not null)
            {
                facts.Add(F(Declared, "rollForward " + rollForward.Value));
            }

            if (allow is not null)
            {
                facts.Add(F(Declared, "allowPrerelease " + allow.Value));
            }

            facts.AddRange(paths.Take(5).Select(p => F(Declared, EnvironmentPhrases.Labelled(EnvironmentPhrases.PathLabel, p.Value))));
            facts.Add(F(Source, SourceOf(version ?? rollForward ?? allow ?? paths[0])));
            facts.Add(F(Installed, installed.DotNetSdks.Count == 0 ? None : string.Join(", ", installed.DotNetSdks.Reverse().Take(8).Select(s => s.Version.Text))));
            if (resolution.Selected is not null)
            {
                facts.Add(F(Resolved, resolution.Selected.Text));
            }

            var (status, fix) = resolution.Kind switch
            {
                SdkResolutionKind.Selected => (DoctorStatus.Ok, null),
                SdkResolutionKind.NoMatch => (DoctorStatus.Error, EnvironmentCheckCodes.FixInstallDotNetSdk),
                _ => (DoctorStatus.Unknown, null),
            };
            checks.Add(new DoctorCheck(EnvironmentCheckCodes.DotNetSdk, DoctorSection.DeclaredEnvironment, status, facts, fix));
        }

        var inside = paths.Where(p => IsInsideProject(p.Value, projectRoot)).ToList();
        if (inside.Count > 0)
        {
            var facts = inside.Take(10).Select(p => F(Declared, EnvironmentPhrases.Labelled(EnvironmentPhrases.PathLabel, p.Value))).Append(F(Source, SourceOf(inside[0]))).ToList();
            checks.Add(new DoctorCheck(EnvironmentCheckCodes.DotNetSdkInRepo, DoctorSection.DeclaredEnvironment, DoctorStatus.Warning, facts));
        }

        var tools = declared.Of(DeclaredKind.DotNetLocalTool).ToList();
        if (tools.Count > 0)
        {
            var facts = new List<DoctorFact>();
            var status = DoctorStatus.Ok;
            foreach (var tool in tools.Take(19))
            {
                var present = cache?.Contains(LocalToolKey(tool.Name ?? string.Empty, tool.Value));
                facts.Add(F(Declared, (tool.Name ?? "?") + " " + tool.Value));
                facts.Add(F(Installed, present switch { true => EnvironmentPhrases.InNuGetCache, false => EnvironmentPhrases.NotInNuGetCache, _ => EnvironmentPhrases.NotChecked }));
                status = present switch
                {
                    false => DoctorStatus.Warning,
                    null when status == DoctorStatus.Ok => DoctorStatus.Unknown,
                    _ => status,
                };
            }

            facts.Add(F(Source, SourceOf(tools[0])));
            checks.Add(new DoctorCheck(
                EnvironmentCheckCodes.DotNetLocalTools, DoctorSection.DeclaredEnvironment, status, facts,
                status == DoctorStatus.Warning ? EnvironmentCheckCodes.FixRestoreDotNetTools : null));
        }
    }

    /// <summary>Whether a path of <c>sdk.paths</c> lies inside the project: a relative path does by definition, an absolute one when it is under the root. Lexical only; no link is resolved.</summary>
    public static bool IsInsideProject(string path, string? projectRoot)
    {
        if (path == SdkResolver.HostPath)
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return projectRoot is null || IsUnder(Path.GetFullPath(path, projectRoot), projectRoot);
            }

            return projectRoot is not null && IsUnder(Path.GetFullPath(path), projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be read as one is not trusted to lie outside.
            return true;
        }
    }

    private static bool IsUnder(string full, string root)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.TrimEndingDirectorySeparator(full);
        return target.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Node ----

    private static void Node(DeclaredEnvironment declared, InstalledTools installed, List<DoctorCheck> checks)
    {
        foreach (var entry in declared.Of(DeclaredKind.NodeVersion))
        {
            var code = entry.Source.File == DeclaredFiles.NvmRc ? EnvironmentCheckCodes.NodeNvmrc : EnvironmentCheckCodes.NodeVersionFile;
            checks.Add(RangeCheck(code, entry, installed.Node?.Version, installed.Node is null, SemverRange.Evaluate, EnvironmentCheckCodes.FixInstallNode));
        }

        foreach (var entry in declared.Of(DeclaredKind.NodeEngine).Take(1))
        {
            checks.Add(RangeCheck(EnvironmentCheckCodes.NodeEngine, entry, installed.Node?.Version, installed.Node is null, SemverRange.Evaluate, EnvironmentCheckCodes.FixInstallNode));
        }

        foreach (var entry in declared.Of(DeclaredKind.NpmEngine).Take(1))
        {
            checks.Add(RangeCheck(EnvironmentCheckCodes.NpmEngine, entry, installed.Npm?.Version, installed.Npm is null, SemverRange.Evaluate, EnvironmentCheckCodes.FixInstallNode));
        }
    }

    // ---- Python ----

    private static void Python(DeclaredEnvironment declared, InstalledTools installed, List<DoctorCheck> checks)
    {
        var missing = installed.Python is null;
        var pythonRequired = declared.Of(DeclaredKind.PythonVersion).Any() || declared.Of(DeclaredKind.PythonRequires).Any() || declared.Of(DeclaredKind.VenvVersion).Any();
        foreach (var entry in declared.Of(DeclaredKind.PythonVersion).Take(1))
        {
            checks.Add(RangeCheck(EnvironmentCheckCodes.PythonVersionFile, entry, installed.Python?.Version, missing, SemverRange.Evaluate, EnvironmentCheckCodes.FixInstallPython, installed.PythonIsStoreAlias));
        }

        var requires = declared.Of(DeclaredKind.PythonRequires).FirstOrDefault();
        if (requires is not null)
        {
            checks.Add(RangeCheck(EnvironmentCheckCodes.PythonRequires, requires, installed.Python?.Version, missing, Pep440Specifier.Evaluate, EnvironmentCheckCodes.FixInstallPython, installed.PythonIsStoreAlias));
        }

        var venv = declared.Of(DeclaredKind.VenvVersion).FirstOrDefault();
        if (venv is not null)
        {
            var facts = new List<DoctorFact> { F(Declared, EnvironmentPhrases.Labelled(EnvironmentPhrases.VenvLabel, venv.Value)), F(Source, SourceOf(venv)), F(Installed, installed.Python?.Version.Text ?? None) };
            var status = DoctorStatus.Ok;
            if (!ToolVersion.TryParse(venv.Value, out var venvVersion))
            {
                status = DoctorStatus.Unknown;
            }
            else if (requires is not null)
            {
                status = Status(Pep440Specifier.Evaluate(requires.Value, venvVersion), DoctorStatus.Warning);
                facts.Add(F(Declared, "requires-python " + requires.Value));
            }

            checks.Add(new DoctorCheck(EnvironmentCheckCodes.PythonVenv, DoctorSection.DeclaredEnvironment, status, facts));
        }
        else if (pythonRequired)
        {
            checks.Add(new DoctorCheck(
                EnvironmentCheckCodes.PythonVenv, DoctorSection.DeclaredEnvironment, DoctorStatus.NotApplicable,
                [F(Source, DeclaredFiles.PyVenvCfg), F(Installed, EnvironmentPhrases.Absent)]));
        }
    }

    // ---- containers ----

    private static void Containers(DeclaredEnvironment declared, InstalledTools installed, List<DoctorCheck> checks)
    {
        if (!declared.DeclaresContainers)
        {
            return;
        }

        var facts = new List<DoctorFact>();
        foreach (var file in declared.FilesFound.Where(f => f is DeclaredFiles.DevContainerFolder or DeclaredFiles.DevContainerRoot or DeclaredFiles.Dockerfile || DeclaredFiles.ComposeFiles.Contains(f, StringComparer.Ordinal)))
        {
            facts.Add(F(Source, file));
        }

        foreach (var entry in declared.Entries.Where(e => e.Kind is DeclaredKind.ContainerImage or DeclaredKind.DevContainerDockerfile or DeclaredKind.DevContainerComposeFile or DeclaredKind.ComposeFile or DeclaredKind.Extends))
        {
            if (facts.Count >= DoctorCheck.MaxFacts - 2)
            {
                break;
            }

            facts.Add(F(Declared, entry.Kind switch
            {
                DeclaredKind.ContainerImage => EnvironmentPhrases.Labelled(EnvironmentPhrases.ImageLabel, entry.Value),
                DeclaredKind.DevContainerDockerfile => EnvironmentPhrases.Labelled(EnvironmentPhrases.DockerfileLabel, entry.Value),
                DeclaredKind.DevContainerComposeFile => EnvironmentPhrases.Labelled(EnvironmentPhrases.ComposeFileLabel, entry.Value),
                DeclaredKind.Extends => EnvironmentPhrases.Labelled(EnvironmentPhrases.ExtendsLabel, entry.Value),
                _ => EnvironmentPhrases.ComposeSize(entry.Value),
            }));
        }

        var docker = installed.Docker;
        facts.Add(F(Installed, docker.State switch
        {
            DockerState.Running => EnvironmentPhrases.DockerRunning(docker.ClientVersion, docker.ServerVersion),
            DockerState.EngineStopped => EnvironmentPhrases.DockerStopped(docker.ClientVersion),
            _ => EnvironmentPhrases.DockerNotInstalled,
        }));
        var (status, fix) = docker.State switch
        {
            DockerState.Running => (DoctorStatus.Ok, null),
            DockerState.EngineStopped => (DoctorStatus.Warning, EnvironmentCheckCodes.FixStartDocker),
            _ => (DoctorStatus.Error, EnvironmentCheckCodes.FixInstallDocker),
        };
        checks.Add(new DoctorCheck(EnvironmentCheckCodes.Containers, DoctorSection.DeclaredEnvironment, status, facts, fix));
    }

    // ---- what was not read ----

    private static void NotRead(DeclaredEnvironment declared, List<DoctorCheck> checks)
    {
        if (declared.Issues.Count == 0)
        {
            return;
        }

        var facts = declared.Issues.Take(DoctorCheck.MaxFacts).Select(issue =>
            DoctorFact.Of(IssueName(issue.Kind), SourceOf(issue.Source) + (issue.Text is null ? string.Empty : " " + issue.Text))).ToList();
        checks.Add(new DoctorCheck(EnvironmentCheckCodes.NotRead, DoctorSection.DeclaredEnvironment, DoctorStatus.Warning, facts));
    }

    private static string IssueName(DeclaredIssueKind kind) => kind switch
    {
        DeclaredIssueKind.TooLarge => "too-large",
        DeclaredIssueKind.IsLink => "is-link",
        DeclaredIssueKind.NotInterpreted => "not-interpreted",
        DeclaredIssueKind.Malformed => "malformed",
        DeclaredIssueKind.TooDeep => "too-deep",
        DeclaredIssueKind.DuplicateKey => "duplicate-key",
        DeclaredIssueKind.LineTooLong => "line-too-long",
        _ => "unreadable",
    };

    // ---- shared ----

    private static DoctorCheck RangeCheck(
        string code, DeclaredEntry entry, ToolVersion? installedVersion, bool toolMissing, Func<string?, ToolVersion?, RangeVerdict> evaluate, string fix, bool storeAlias = false)
    {
        var facts = new List<DoctorFact>
        {
            F(Declared, entry.Value),
            F(Source, SourceOf(entry)),
            F(Installed, storeAlias ? EnvironmentPhrases.StoreAlias : installedVersion?.Text ?? None),
        };
        if (toolMissing)
        {
            return new DoctorCheck(code, DoctorSection.DeclaredEnvironment, DoctorStatus.Error, facts, fix);
        }

        return new DoctorCheck(code, DoctorSection.DeclaredEnvironment, Status(evaluate(entry.Value, installedVersion), DoctorStatus.Warning), facts);
    }

    private static DoctorStatus Status(RangeVerdict verdict, DoctorStatus whenNotSatisfied) => verdict switch
    {
        RangeVerdict.Satisfied => DoctorStatus.Ok,
        RangeVerdict.NotSatisfied => whenNotSatisfied,
        _ => DoctorStatus.Unknown,
    };

    private static string SourceOf(DeclaredEntry entry) => SourceOf(entry.Source);

    private static string SourceOf(DeclaredSource source) => source.Line is { } line ? $"{source.File}:{line}" : source.File;

    private static DoctorFact F(string name, string value) => DoctorFact.Of(name, value);
}
