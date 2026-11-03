using System.IO;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Providers.Live;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// The switches of the real demonstrations of Phase 5 (docs/15 §12, decision 2 a): what spends quota needs
/// <c>AGENTSTUDIO_PROVIDER_TESTS=1</c> and <c>AGENTSTUDIO_PHASE5_DEMOS=1</c>, and leaves its evidence in
/// <c>AGENTSTUDIO_CAPTURE_DIR</c>; what spends none (the preparation, the measurement of the suite at Low integrity and the
/// composition of the evidence document) needs only <c>AGENTSTUDIO_PHASE5_DEMOS=1</c>. Every other variable is optional.
/// </summary>
internal static class DemoSettings
{
    /// <summary>Opt-in switch of the demonstrations, on top of <see cref="LiveTestSupport.OptInVariable"/>.</summary>
    public const string DemosVariable = "AGENTSTUDIO_PHASE5_DEMOS";

    /// <summary>
    /// The folder the demonstrations keep between runs (the repository of the debates, the stand-in for
    /// <c>%LOCALAPPDATA%\AgentStudio</c> and the copy of the audited repository); <c>%TEMP%\as-f5</c> when not set.
    /// </summary>
    public const string WorkspaceVariable = "AGENTSTUDIO_PHASE5_WORKSPACE";

    /// <summary>The verdict the challenge revises; the last verdict of the latest implementation audit when not set.</summary>
    public const string ChallengeVerdictVariable = "AGENTSTUDIO_PHASE5_CHALLENGE_VERDICT";

    /// <summary>The repository the whole-project audit copies; the one this test was built from when not set.</summary>
    public const string AuditRepositoryVariable = "AGENTSTUDIO_PHASE5_AUDIT_REPO";

    /// <summary>
    /// What the test step of the audit leaves out besides <c>Category=ProviderLive</c>, separated by <c>;</c>: a trait
    /// (<c>Category=ConPty</c>, as <c>--filter-not-trait</c>) or a fully qualified class (as <c>--filter-not-class</c>).
    /// </summary>
    public const string AuditExcludeVariable = "AGENTSTUDIO_PHASE5_AUDIT_EXCLUDE";

    /// <summary>
    /// The model of <c>juez-revision</c>, the Codex judge with «otro modelo» of the challenges (D-09 and D-39), when it should
    /// not be the one of the default template (<see cref="DefaultAgentRoster.RevisionJudgeModel"/>).
    /// </summary>
    public const string RevisionModelVariable = "AGENTSTUDIO_PHASE5_REVISION_MODEL";

    /// <summary>The demonstrations that spend quota: both switches, the native CLIs and a folder for the evidence.</summary>
    public static void RequireLiveDemo()
    {
        LiveTestSupport.RequireOptIn();
        RequireDemos();
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(CaptureDirectory),
            $"Set {LiveTestSupport.CaptureVariable} as well: a demonstration that spends quota leaves its evidence there.");
        var claude = new ExecutableLocator().Find("claude");
        Assert.SkipUnless(claude is not null && IsNative(claude), "The demonstrations need the native claude executable (the inline --json-schema cannot go through a .cmd shim).");
        Assert.SkipUnless(new ExecutableLocator().Find("codex") is not null, "Codex CLI is not installed.");
    }

    /// <summary>What spends no quota (preparation, measurement, report): only the switch of the demonstrations.</summary>
    public static void RequireDemos() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(DemosVariable) == "1",
            $"Set {DemosVariable}=1 to run the Phase 5 demonstrations (the ones that spend quota also need {LiveTestSupport.OptInVariable}=1).");

    public static string? CaptureDirectory => Environment.GetEnvironmentVariable(LiveTestSupport.CaptureVariable) is { Length: > 0 } folder ? Path.GetFullPath(folder) : null;

    public static string? ChallengeVerdict => Environment.GetEnvironmentVariable(ChallengeVerdictVariable) is { Length: > 0 } verdict ? verdict.Trim() : null;

    public static string RevisionModel => Environment.GetEnvironmentVariable(RevisionModelVariable) is { Length: > 0 } model ? model.Trim() : DefaultAgentRoster.RevisionJudgeModel;

    /// <summary>The repository the audit copies: the variable, or the root of the source tree this test was compiled from.</summary>
    public static string AuditRepository => Environment.GetEnvironmentVariable(AuditRepositoryVariable) is { Length: > 0 } root ? Path.GetFullPath(root) : SourceTree.Root;

    /// <summary>The extra exclusions of the test step of the audit, as written in <see cref="AuditExcludeVariable"/>.</summary>
    public static IReadOnlyList<string> AuditExclusions =>
        (Environment.GetEnvironmentVariable(AuditExcludeVariable) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static bool IsNative(string executable)
    {
        var name = executable.TrimEnd('.', ' ');
        return !name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The source tree this test assembly was compiled from (the checkout the lead runs the demonstrations in).</summary>
internal static class SourceTree
{
    public static string Root { get; } = Find();

    private static string Find([CallerFilePath] string callerFile = "")
    {
        // tests/AgentStudio.Tests/Integration/Phase5/Live/<this file>
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "..", "..", "..", ".."));
        if (!File.Exists(Path.Combine(root, "AgentStudio.slnx")))
        {
            throw new InvalidOperationException($"The root of the repository was not found from {callerFile}.");
        }

        return root;
    }
}

/// <summary>
/// The folder the demonstrations keep between runs, so each one can run alone (by method) and still find what an earlier
/// one left: the run the review modes judge, the verdicts a challenge revises. Short and ASCII, far from MAX_PATH, under the
/// user's temporary folder unless <see cref="DemoSettings.WorkspaceVariable"/> says otherwise; never inside a repository.
/// </summary>
internal sealed record DemoWorkspace(string Root)
{
    /// <summary>The small .NET repository the debates are about (<see cref="DemoRepository"/>).</summary>
    public string Repository => Path.Combine(Root, "repo");

    /// <summary>The stand-in for <c>%LOCALAPPDATA%\AgentStudio</c>: worktrees, dossiers, debates, audits, anchors and the quota record.</summary>
    public Phase4Paths Paths => new(new Phase3Paths(Path.Combine(Root, "la", "AgentStudio")));

    /// <summary>The copy of the audited repository at <paramref name="head"/> (a clone, opened as its own project).</summary>
    public string AuditRepository(string head) => Path.Combine(Root, "aud-" + head[..Math.Min(10, head.Length)]);

    public static DemoWorkspace Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(DemoSettings.WorkspaceVariable);
        var root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "as-f5") : Path.GetFullPath(configured);
        Assert.False(
            LiveTestSupport.IsUnder(root, SourceTree.Root),
            $"The workspace of the demonstrations ({root}) cannot be inside the repository of Agent Studio.");
        Directory.CreateDirectory(root);
        return new DemoWorkspace(root);
    }
}

/// <summary>
/// The composition of the demonstrations, without the desktop: the one of Phase 4 (<see cref="Phase4Composition"/>, every
/// module of Phases 3 and 4 through its own extension, the real providers composed with the project's journal and the
/// user's quota record) plus, one line per module, what Phase 5 adds (docs/15 §9, «Integración»): the arena of debates and
/// the audits with the challenges; the judgment of debates already comes with <c>AddAgentStudioJudging</c>. The trusted
/// verification profile of a project is read from its <c>.agentstudio\verification.json</c>, as the desktop reads it.
/// </summary>
internal static class DemoComposition
{
    public static ServiceCollection Services(DemoWorkspace workspace, IApprovalPrompt person)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(person);
        var services = Phase4Composition.Services(workspace.Paths, person, providers: null, codexOptedIn: null);
        services.AddAgentStudioDebateArena().AddAgentStudioAudits();
        var profiles = new FileVerificationProfiles();
        services.AddSingleton<IAuditVerificationProfiles>(profiles);
        services.AddSingleton<IDebateTestProfiles>(profiles);
        return services;
    }
}

/// <summary>
/// The trusted verification profile of a project, read from its <c>.agentstudio\verification.json</c> as the desktop reads it
/// and with the behaviour of its adapter (<c>VerificationProfilePorts</c>, internal to the desktop): the audit gets the reason
/// of a file that cannot be used; the room, which then cannot run the tests, gets none.
/// </summary>
internal sealed class FileVerificationProfiles : IAuditVerificationProfiles, IDebateTestProfiles
{
    public async Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var load = await VerificationProfileFile.LoadAsync(project.RootPath, cancellationToken).ConfigureAwait(false);
        return load.Status switch
        {
            VerificationProfileStatus.Loaded => load.Profile,
            VerificationProfileStatus.NotFound => null,
            _ => throw new InvalidDataException($"The verification profile '{load.Path}' cannot be used: {string.Join(" ", load.Problems)}"),
        };
    }

    public async Task<VerificationProfile?> FindAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>
/// The person behind the approval dialog during a demonstration: the lead runs it with the user's OK, so it approves what
/// that OK covers (opening a debate, a challenge's revision debate, and an audit) and denies everything else, above all
/// what only the user decides: «Decidir yo» (<c>debate.human-resolution</c>), accepting a revision that turns towards an
/// approval and any delivery. Every question is kept for the evidence.
/// </summary>
internal sealed class DemoPerson : IApprovalPrompt
{
    public const string Name = "usuario";

    private static readonly HashSet<string> Approved = new(StringComparer.Ordinal) { DebateCoordinator.OpenAction, ProjectAuditService.ApprovalAction };

    private readonly Lock _gate = new();
    private readonly List<(ApprovalRequest Request, bool Approved)> _asked = [];

    public IReadOnlyList<(ApprovalRequest Request, bool Approved)> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approved = Approved.Contains(request.Action);
        lock (_gate)
        {
            _asked.Add((request, approved));
        }

        return Task.FromResult(new ApprovalDecision(
            approved,
            Name,
            DateTimeOffset.UtcNow,
            approved ? "Aprobado para la demostración, con el OK del usuario." : "Denegado: esta decisión es del usuario, no de la demostración."));
    }
}

/// <summary>
/// The agent template of the demonstrations: the app's default template (<see cref="DefaultAgentRoster.Create"/>, D-09),
/// with the agents of Claude that are not judges on haiku (decision 2 a: haiku for the participants). The judges keep the
/// default model of the account: the first run on haiku showed that <c>juez-claude</c> may exhaust its structured-output
/// retries on the verdict of a debate (<c>error_max_structured_output_retries</c>), which asks a person instead of judging.
/// <c>orquestador</c> and <c>ejecutor</c> (Claude) produce the run the
/// review modes judge and <c>juez</c> (Codex) reviews it; <c>arquitecto</c> (Claude) and <c>critico</c> (Codex with its default
/// model) take part in the debates; <c>juez-claude</c> (Claude) and <c>juez</c> (Codex) sit on the cross-vendor panels; and
/// <c>juez-revision</c> (Codex with another model) judges a challenge.
/// </summary>
internal static class DemoRoster
{
    /// <summary>The model of every agent of Claude in the demonstrations but the judges (decision 2 a).</summary>
    public const string ClaudeModel = "haiku";

    /// <summary>How long the judge of the audit may read the repository: decision 4 estimates 5 to 15 minutes, and the template gives 15.</summary>
    public static readonly TimeSpan AuditJudgeDuration = TimeSpan.FromMinutes(25);

    public static AgentId Orchestrator => DefaultAgentRoster.Orchestrator;

    public static AgentId Executor => DefaultAgentRoster.Executor;

    public static AgentId Judge => DefaultAgentRoster.Judge;

    public static AgentId Architect => DefaultAgentRoster.Architect;

    public static AgentId Critic => DefaultAgentRoster.Critic;

    public static AgentId ClaudeJudge => DefaultAgentRoster.ClaudeJudge;

    public static AgentId RevisionJudge => DefaultAgentRoster.RevisionJudge;

    public static IReadOnlyList<AgentDefinition> Debates() => [.. DefaultAgentRoster.Create().Select(Adjusted)];

    /// <summary>The template of the copy of Agent Studio the audit judges: the same, with the time of <c>juez</c> widened to read the repository.</summary>
    public static IReadOnlyList<AgentDefinition> Audit() =>
        [.. Debates().Select(a => a.Id == Judge ? a with { Budget = a.Budget with { MaxDuration = AuditJudgeDuration } } : a)];

    /// <summary>The agents the orchestrator plans the run of F3-8 with: <c>orquestador</c>, <c>ejecutor</c> and <c>juez</c>, as the project's template defines them.</summary>
    public static IReadOnlyList<AgentDefinition> Planning(IReadOnlyList<AgentDefinition> roster) =>
        [.. roster.Where(a => a.Id == Orchestrator || a.Id == Executor || a.Id == Judge)];

    private static AgentDefinition Adjusted(AgentDefinition agent)
    {
        if (agent.Model.Provider == ProviderId.ClaudeCode && agent.Role != AgentRole.Judge)
        {
            return agent with { Model = agent.Model with { Model = ClaudeModel } };
        }

        return agent.Id == RevisionJudge ? agent with { Model = agent.Model with { Model = DemoSettings.RevisionModel } } : agent;
    }
}

/// <summary>
/// The repository the debates are about: the minimal .NET solution of F3-8 (<see cref="Phase3Repos.DotNet"/>, the library
/// <c>src/Calc</c> with <c>Add</c> and its xUnit v3 test), created once in the workspace with a local identity and kept, so
/// the run the review modes judge and the debates about HEAD share it.
/// </summary>
internal static class DemoRepository
{
    /// <summary>Creates the repository at <paramref name="root"/> unless it is there, and returns its HEAD.</summary>
    public static string EnsureCalc(string root)
    {
        if (Directory.Exists(Path.Combine(root, ".git")))
        {
            return TempGitRepo.Git(root, "rev-parse", "HEAD").Trim();
        }

        Assert.False(
            Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(),
            $"'{root}' exists and is not the repository of the demonstrations: delete the workspace or choose another with {DemoSettings.WorkspaceVariable}.");
        Directory.CreateDirectory(root);
        TempGitRepo.Git(root, "init", "-q", "-b", "main");
        TempGitRepo.Git(root, "config", "user.name", "Agent Studio Demos");
        TempGitRepo.Git(root, "config", "user.email", "demos@localhost");
        TempGitRepo.Git(root, "config", "commit.gpgsign", "false");
        TempGitRepo.Git(root, "config", "core.autocrlf", "false");

        var versions = System.Xml.Linq.XDocument.Load(Path.Combine(SourceTree.Root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.Ordinal);
        Write(root, "global.json", File.ReadAllText(Path.Combine(SourceTree.Root, "global.json")));
        Write(root, ".gitignore", "bin/\nobj/\n*.log\n");
        Write(root, "Directory.Build.props", "<Project />\n");
        Write(root, "Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        Write(root, "Calc.slnx", "<Solution>\n  <Project Path=\"src/Calc/Calc.csproj\" />\n  <Project Path=\"tests/Calc.Tests/Calc.Tests.csproj\" />\n</Solution>\n");
        Write(root, "src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <RootNamespace>CalcLib</RootNamespace>
              </PropertyGroup>
            </Project>

            """);
        Write(root, "src/Calc/Calc.cs", """
            namespace CalcLib;

            /// <summary>Operaciones de la calculadora.</summary>
            public static partial class Calc
            {
                public static int Add(int a, int b) => a + b;
            }

            """);
        Write(root, "tests/Calc.Tests/Calc.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
                <RootNamespace>CalcLib.Tests</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{versions["Microsoft.NET.Test.Sdk"]}" />
                <PackageReference Include="xunit.v3" Version="{versions["xunit.v3"]}" />
                <PackageReference Include="xunit.runner.visualstudio" Version="{versions["xunit.runner.visualstudio"]}" />
              </ItemGroup>
              <ItemGroup>
                <Using Include="Xunit" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\..\src\Calc\Calc.csproj" />
              </ItemGroup>
            </Project>

            """);
        Write(root, "tests/Calc.Tests/CalcTests.cs", """
            namespace CalcLib.Tests;

            public sealed class CalcTests
            {
                [Fact]
                public void Add_suma() => Assert.Equal(5, Calc.Add(2, 3));
            }

            """);
        TempGitRepo.Git(root, "add", "-A");
        TempGitRepo.Git(root, "commit", "-q", "-m", "Proyecto .NET de las demostraciones de la Fase 5");
        return TempGitRepo.Git(root, "rev-parse", "HEAD").Trim();
    }

    private static void Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>
/// One instance of the app over one project of the demonstrations, composed by <see cref="DemoComposition"/>: the container
/// built with validation, the project opened (database, lock file and <c>info/exclude</c>), its template saved and, when it
/// has none, its trusted verification profile. Disposing it is closing the app.
/// </summary>
internal sealed class DemoApp : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private DemoApp(ServiceProvider services, ProjectContext project, DemoWorkspace workspace, DemoPerson person)
    {
        _services = services;
        Project = project;
        Workspace = workspace;
        Person = person;
    }

    public ProjectContext Project { get; }

    public DemoWorkspace Workspace { get; }

    public DemoPerson Person { get; }

    public IServiceProvider Services => _services;

    public static async Task<DemoApp> OpenAsync(string projectRoot, DemoWorkspace workspace, IReadOnlyList<AgentDefinition> roster, VerificationProfile? profile, CancellationToken ct)
    {
        var person = new DemoPerson();
        var services = DemoComposition.Services(workspace, person)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            var project = await services.GetRequiredService<IProjectWorkspace>().OpenAsync(projectRoot, ct);
            foreach (var agent in roster)
            {
                await services.GetRequiredService<IAgentRoster>().SaveAsync(project.ProjectId, agent, ct);
            }

            if (profile is not null)
            {
                var current = await VerificationProfileFile.LoadAsync(project.RootPath, ct);
                if (current.Status != VerificationProfileStatus.Loaded || current.Profile?.ContentHash != profile.ContentHash)
                {
                    await VerificationProfileFile.SaveAsync(project.RootPath, profile, services.GetRequiredService<IProjectStorageFiles>(), ct);
                }
            }

            return new DemoApp(services, project, workspace, person);
        }
        catch
        {
            await services.DisposeAsync();
            throw;
        }
    }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    /// <summary>The verified log of the project, read back from its store.</summary>
    public Task<DecisionLogState> LoadLogAsync(CancellationToken ct) => Get<IDecisionRecorder>().LoadAsync(Project.ProjectId, ct);

    /// <summary>The probes the app runs at start (<c>--version</c>, <c>claude auth status</c>, <c>codex login status</c>): no prompt, no quota.</summary>
    public Task<IReadOnlyList<ProviderProbeResult>> ProbeAsync(CancellationToken ct) => Get<ProviderDiagnostics>().RunAsync(ct);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
