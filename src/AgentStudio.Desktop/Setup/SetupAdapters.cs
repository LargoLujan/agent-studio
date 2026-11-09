using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Git;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Security;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// What the repository says of itself for step 1 (docs/17 D-26): the index of the explorer (the names of its files, folder links
/// never followed) and, when the git layer is composed, the branch, HEAD and the changes not committed it reports. It reads
/// without network and runs nothing of the repository. The remotes stay empty: the hardened git layer does not expose them yet,
/// and a reader that does can replace this one without touching the configurator.
/// </summary>
public sealed class FileSetupRepositoryReader(IWorktreeManager? git = null) : ISetupRepositoryReader
{
    public async Task<SetupRepositoryFacts> ReadAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var index = await Task.Run(() => ProjectFileIndexer.Build(project.RootPath, ProjectFileIndexer.DefaultMaxFiles, ct), ct).ConfigureAwait(false);
        string? branch = null;
        string? head = null;
        var dirty = false;
        if (git is not null)
        {
            try
            {
                var state = await git.InspectAsync(project.RootPath, ct).ConfigureAwait(false);
                branch = state.HeadBranch;
                head = state.HeadCommit.Length > 7 ? state.HeadCommit[..7] : state.HeadCommit;
                dirty = state.HasUncommittedChanges;
            }
#pragma warning disable CA1031 // A folder that is not a repository, or no git: the step shows what the files say and no branch.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
            }
        }

        return new SetupRepositoryFacts(project.RootPath, branch, head, dirty, index.RelativePaths, index.IsTruncated, []);
    }
}

/// <summary>
/// The verification profiles of step 5 over the profiles of the stacks and the profile file of the project (ADR-0016): .NET, else
/// Node, else Python, by the names of the files. Reading and writing go through the same loader and the same storage policy
/// as the orchestrator uses (<see cref="VerificationProfileFile"/>, <see cref="IProjectStorageFiles"/>): never through a link.
/// </summary>
public sealed class StackSetupVerificationProfiles(IProjectStorageFiles files) : ISetupVerificationProfiles
{
    public VerificationProfile? Propose(IReadOnlyList<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        return SetupProposer.StackOf(AgentStudio.Domain.Skills.ProjectStackDetector.Detect(relativeFiles)) switch
        {
            SetupStacks.DotNet => DotNetVerificationProfile.Propose(),
            _ => NodeVerificationProfile.ProposeFor(relativeFiles) ?? PythonVerificationProfile.ProposeFor(relativeFiles),
        };
    }

    public async Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var load = await VerificationProfileFile.LoadAsync(project.RootPath, ct).ConfigureAwait(false);
        return load.Status switch
        {
            VerificationProfileStatus.Loaded => load.Profile,
            VerificationProfileStatus.NotFound => null,
            _ => throw new InvalidDataException($"The verification profile '{load.Path}' cannot be used: {string.Join(" ", load.Problems)}"),
        };
    }

    public Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        return VerificationProfileFile.SaveAsync(project.RootPath, profile, files, ct);
    }
}

/// <summary>
/// The options of a project (step 7), kept in the folder of the app of this computer, <c>&lt;folder&gt;\&lt;id of the project&gt;.json</c>
/// (by default under <c>%LOCALAPPDATA%\AgentStudio\setup</c>): they are the person's, not the repository's, so nothing is added
/// to the repository and its state does not change. A file that cannot be read is no options: the project goes on with the
/// values of always.
/// </summary>
public sealed class LocalProjectOptionsStore : IProjectOptionsStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _folder;

    public LocalProjectOptionsStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
    }

    /// <summary>The folder of the app of this computer where the options live by default: <c>%LOCALAPPDATA%\AgentStudio\setup</c>.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "setup");

    public async Task<ProjectRunOptions?> LoadAsync(ProjectContext project, CancellationToken ct)
    {
        var path = PathOf(project);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<OptionsDocument>(await File.ReadAllTextAsync(path, Utf8, ct).ConfigureAwait(false), Json);
            var options = document is null
                ? null
                : new ProjectRunOptions(document.MaxAgentRuns, document.MaxParallelTasks, document.TimePerAttemptSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
            return options is not null && options.Validate().Count == 0 ? options : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ProjectContext project, ProjectRunOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = PathOf(project);
        Directory.CreateDirectory(_folder);
        var json = JsonSerializer.Serialize(
            new OptionsDocument(1, options.MaxAgentRuns, options.MaxParallelTasks, options.TimePerAttempt is { } time ? (long)time.TotalSeconds : null),
            Json);

        // Written whole beside the file and moved over it, so a reader never sees half of it.
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllTextAsync(temporary, json, Utf8, ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(ProjectContext project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.ProjectId) || project.ProjectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The id of the project is not a file name.", nameof(project));
        }

        return Path.Combine(_folder, project.ProjectId + ".json");
    }

    private sealed record OptionsDocument(int Version, int MaxAgentRuns, int MaxParallelTasks, long? TimePerAttemptSeconds);
}

/// <summary>
/// The barrier of Windows Hello for the critical changes of the configurator (docs/17 D-37 to D-41, ADR-0053): the same
/// <see cref="ReinforcedConfirmation"/> the approvals and «Aprobar plan» use, so there is one rule. Whatever the setting or any
/// file says, the actions of the fixed list ask Windows Hello, or without it the verified lock of Windows. Only a confirmation
/// that <see cref="ReinforcedConfirmation.Approves"/> confirms; every other answer is a no, with its reason as a code.
/// </summary>
public sealed class HelloSetupPresenceGate(ReinforcedConfirmation confirmation, IUiTextProvider? texts = null) : ISetupPresenceGate
{
    public async Task<SetupPresenceResult> ConfirmAsync(PresenceAction action, string subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var plan = await confirmation.PlanAsync(action, ct).ConfigureAwait(false);
        if (!plan.ConfirmsAfterYes)
        {
            return ReinforcedConfirmation.Approves(plan, plan.AnswerAloneProof)
                ? new SetupPresenceResult(true)
                : new SetupPresenceResult(false, UserText.Of("setup.hello.not-confirmed"));
        }

        var message = (texts ?? Texts.Current).Format("Setup_Hello_Message", subject.Length > 12 ? subject[..12] : subject);
        var outcome = await confirmation.ConfirmAsync(plan, message, ct).ConfigureAwait(false);
        if (ReinforcedConfirmation.Approves(plan, outcome.Proof))
        {
            return new SetupPresenceResult(true);
        }

        return new SetupPresenceResult(
            false,
            UserText.Of(outcome.AnotherPending ? "setup.hello.another-pending" : outcome.Problem is not null ? "setup.hello.not-asked" : "setup.hello.not-confirmed"));
    }
}
