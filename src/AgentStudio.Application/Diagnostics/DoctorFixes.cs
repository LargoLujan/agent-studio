using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Application.Diagnostics;

/// <summary>
/// The closed list of the fixes of Auto Doctor (docs/20 D-10). In Phase 6 it holds only <see cref="DoctorFix.Navigate"/> («Ir a…») and
/// <see cref="DoctorFix.Guide"/> («Cómo arreglarlo», with a command to copy or an official page): under decision 8 (a) no fix touches the system, so
/// nothing here installs, starts or changes anything, and the app never runs the command of a guide. A fix that applies something
/// (<see cref="DoctorFix.Reversible"/> and <see cref="DoctorFix.Approved"/>) is written where it acts on data of Agent Studio, with its undo or its
/// approval, and goes through <see cref="DoctorFixRunner"/>.
/// </summary>
public static class DoctorFixCatalog
{
    public const string OpenSetup = "doctor.fix.open-setup";
    public const string InstallDotNet = "doctor.fix.install-dotnet";
    public const string InstallNode = "doctor.fix.install-node";
    public const string InstallPython = "doctor.fix.install-python";
    public const string InstallGit = "doctor.fix.install-git";
    public const string InstallWebView2 = "doctor.fix.install-webview2";
    public const string InstallDocker = "doctor.fix.install-docker";
    public const string StartDocker = "doctor.fix.start-docker";
    public const string InstallWsl = "doctor.fix.install-wsl";
    public const string InstallWslDistribution = "doctor.fix.install-wsl-distribution";

    private const string ProviderInstallPrefix = "doctor.fix.install-";
    private const string ProviderLoginPrefix = "doctor.fix.login-";

    // The providers whose install and sign-in the guide knows, by the id of the probe: the command to copy and the page of the vendor.
    private static readonly IReadOnlyDictionary<string, (string Install, Uri Page, string? Login)> Providers = new Dictionary<string, (string, Uri, string?)>(StringComparer.Ordinal)
    {
        ["claude-code"] = ("npm install -g @anthropic-ai/claude-code", new Uri("https://docs.anthropic.com/en/docs/claude-code/setup"), "claude auth login"),
        ["codex-cli"] = ("npm install -g @openai/codex", new Uri("https://github.com/openai/codex"), "codex login"),
        ["gemini-cli"] = ("npm install -g @google/gemini-cli", new Uri("https://github.com/google-gemini/gemini-cli"), null),
    };

    private static readonly IReadOnlyDictionary<string, DoctorFix> Registered = Build();

    /// <summary>Every fix of the list, in a stable order: what the test of F6-2 walks.</summary>
    public static IReadOnlyList<DoctorFix> All { get; } = [.. Registered.Values.OrderBy(f => f.Code, StringComparer.Ordinal)];

    /// <summary>The fix with this code, or null: a check names its fix by code and nothing outside the list is ever applied.</summary>
    public static DoctorFix? Find(string? code) => code is not null && Registered.TryGetValue(code, out var fix) ? fix : null;

    /// <summary>The code of the guide that installs the provider <paramref name="providerId"/> (the id as <see cref="ProviderCheckSource.Id"/> writes it), or null when none is known.</summary>
    public static string? ProviderInstallFixCode(string providerId) => Providers.ContainsKey(providerId) ? ProviderInstallPrefix + providerId : null;

    /// <summary>The code of the guide that signs in to the provider, or null when none is known.</summary>
    public static string? ProviderLoginFixCode(string providerId) => Providers.TryGetValue(providerId, out var p) && p.Login is not null ? ProviderLoginPrefix + providerId : null;

    /// <summary>The code of the texts of a fix: the guides of every provider share one sentence (doctor.fix.install-provider, doctor.fix.login-provider), with the name of the program as an argument.</summary>
    public static string TextFamily(string fixCode)
    {
        ArgumentNullException.ThrowIfNull(fixCode);
        if (fixCode.StartsWith(ProviderInstallPrefix, StringComparison.Ordinal) && Providers.ContainsKey(fixCode[ProviderInstallPrefix.Length..]))
        {
            return ProviderInstallPrefix + "provider";
        }

        return fixCode.StartsWith(ProviderLoginPrefix, StringComparison.Ordinal) && Providers.ContainsKey(fixCode[ProviderLoginPrefix.Length..]) ? ProviderLoginPrefix + "provider" : fixCode;
    }

    private static Dictionary<string, DoctorFix> Build()
    {
        var fixes = new List<DoctorFix>
        {
            new DoctorFix.Navigate(OpenSetup, "setup"),
            new DoctorFix.Guide(InstallDotNet, "winget install Microsoft.DotNet.SDK.10", new Uri("https://dotnet.microsoft.com/download")),
            new DoctorFix.Guide(InstallNode, "winget install OpenJS.NodeJS.LTS", new Uri("https://nodejs.org/en/download")),
            new DoctorFix.Guide(InstallPython, "winget install Python.Python.3.13", new Uri("https://www.python.org/downloads/")),
            new DoctorFix.Guide(InstallGit, "winget install Git.Git", new Uri("https://git-scm.com/downloads")),
            new DoctorFix.Guide(InstallWebView2, "winget install Microsoft.EdgeWebView2Runtime", new Uri("https://developer.microsoft.com/microsoft-edge/webview2/")),
            new DoctorFix.Guide(InstallDocker, "winget install Docker.DockerDesktop", new Uri("https://www.docker.com/products/docker-desktop/")),
            new DoctorFix.Guide(StartDocker, null, new Uri("https://docs.docker.com/desktop/use-desktop/")),
            new DoctorFix.Guide(InstallWsl, "wsl --install", new Uri("https://learn.microsoft.com/windows/wsl/install")),
            new DoctorFix.Guide(InstallWslDistribution, "wsl --install -d Ubuntu", new Uri("https://learn.microsoft.com/windows/wsl/install")),
        };
        foreach (var (id, provider) in Providers)
        {
            fixes.Add(new DoctorFix.Guide(ProviderInstallPrefix + id, provider.Install, provider.Page));
            if (provider.Login is not null)
            {
                fixes.Add(new DoctorFix.Guide(ProviderLoginPrefix + id, provider.Login, null));
            }
        }

        return fixes.ToDictionary(f => f.Code, StringComparer.Ordinal);
    }
}

/// <summary>
/// Applies the fixes (docs/20 D-10 and D-11). A navigation and a guide change nothing (the screen opens or shows them): the result is «done» and
/// nothing is recorded. A <see cref="DoctorFix.Approved"/> fix goes first to <see cref="IApprovalGate"/> (which asks Windows Hello when its action is on the fixed
/// list of ADR-0053): denied, nothing is applied. A fix that applies leaves <c>DoctorFixApplied</c> in the log of the open project, with the two codes and
/// no text; undoing a reversible fix is another entry that points to the first. If applying fails, a reversible fix is undone so the state is as it was.
/// </summary>
public sealed class DoctorFixRunner(IDecisionRecorder? decisions = null, IApprovalGate? gate = null) : IDoctorFixRunner
{
    private static readonly Actor PersonActor = new(ActorKind.User, "person");

    // What can be undone in this session: the closure of the undo lives in memory, so an entry of an earlier session cannot be undone.
    private readonly ConcurrentDictionary<long, Applied> _undoable = new();

    private sealed record Applied(DoctorFix.Reversible Fix, string CheckCode);

    public async Task<DoctorFixResult> ApplyAsync(DoctorFix fix, string checkCode, ProjectContext? project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fix);
        Domain.Diagnostics.DoctorCodes.Require(checkCode, nameof(checkCode));

        switch (fix)
        {
            case DoctorFix.Navigate or DoctorFix.Guide:
                return new DoctorFixResult(DoctorFixOutcome.Done);

            case DoctorFix.Approved approved:
                if (gate is null || !(await gate.RequestAsync(approved.Approval, ct).ConfigureAwait(false)).Approved)
                {
                    return new DoctorFixResult(DoctorFixOutcome.Denied);
                }

                return await ApplyAsync(approved.Apply, undo: null, approved, checkCode, project, ct).ConfigureAwait(false);

            case DoctorFix.Reversible reversible:
                return await ApplyAsync(reversible.Apply, reversible.Undo, reversible, checkCode, project, ct).ConfigureAwait(false);

            default:
                throw new ArgumentException("A fix is one of the four forms of the closed hierarchy.", nameof(fix));
        }
    }

    public async Task UndoAsync(long appliedSequence, ProjectContext? project, CancellationToken ct)
    {
        if (!_undoable.TryRemove(appliedSequence, out var applied))
        {
            throw new InvalidOperationException("There is nothing to undo for that entry: it was not applied in this session, was already undone, or its fix cannot be undone.");
        }

        await applied.Fix.Undo(project, ct).ConfigureAwait(false);
        if (project is not null && decisions is not null)
        {
            await RecordAsync(project, DoctorFixAppliedPayload.Undoing(applied.Fix.Code, applied.CheckCode, appliedSequence), ct).ConfigureAwait(false);
        }
    }

    private async Task<DoctorFixResult> ApplyAsync(DoctorAction apply, DoctorAction? undo, DoctorFix fix, string checkCode, ProjectContext? project, CancellationToken ct)
    {
        try
        {
            await apply(project, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A fix that fails must leave the state as it was and say so, not break the panel.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (undo is not null)
            {
                try
                {
                    await undo(project, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Best effort: the failure of the fix is what the person is told.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }

            return new DoctorFixResult(DoctorFixOutcome.Failed);
        }

        long? sequence = null;
        if (project is not null && decisions is not null)
        {
            sequence = (await RecordAsync(project, DoctorFixAppliedPayload.Applying(fix.Code, checkCode), ct).ConfigureAwait(false)).Sequence;
            if (fix is DoctorFix.Reversible reversible)
            {
                _undoable[sequence.Value] = new Applied(reversible, checkCode);
            }
        }

        return new DoctorFixResult(DoctorFixOutcome.Done, sequence);
    }

    private Task<DecisionEntry> RecordAsync(ProjectContext project, DoctorFixAppliedPayload payload, CancellationToken ct) =>
        decisions!.RecordAsync(
            project.ProjectId,
            PersonActor,
            DecisionKind.DoctorFixApplied,
            payload.Undoes is null ? "Doctor fix applied" : "Doctor fix undone",
            payload,
            corrects: null,
            ct);
}
