using System.Collections.ObjectModel;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Domain.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Repository.Policies;

/// <summary>A class of operation the person may give a standing permission for. Only those that admit one are offered (docs/21 D-15): merging and the destructive ones never are.</summary>
public sealed record PermissionClassOption(HostingOperationClass Class, string Label, bool AsksForBase)
{
    public override string ToString() => Label;
}

/// <summary>
/// «Permisos de publicación» (docs/21 section 7.1, D-15 and D-16): the standing permissions of this project, with who gave them and when, «Dar permiso…» and «Retirar». The screen
/// never decides what a permission covers: it lists what <see cref="IHostingPolicyGate"/> holds and asks it to grant or revoke; the gate asks for Windows Hello when it must.
/// </summary>
public sealed partial class PublishingPermissionsViewModel : ObservableObject
{
    private readonly IHostingPolicyGate? _gate;
    private readonly TimeProvider _time;
    private ProjectContext? _project;
    private RemoteRepository? _repository;
    private int _generation;

    public PublishingPermissionsViewModel(IHostingPolicyGate? gate, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _gate = gate;
        _time = time;
        Classes =
        [
            .. new[] { HostingOperationClass.PublishRunBranch, HostingOperationClass.PullRequestWrite }
                .Where(HostingPolicy.ClassAdmitsGrant)
                .Select(c => new PermissionClassOption(c, Texts.Get("Hosting_Class_" + c), c == HostingOperationClass.PullRequestWrite)),
        ];
        _selectedClass = Classes.FirstOrDefault();
    }

    /// <summary>Whether the gate is composed: without it the section says permissions are not available.</summary>
    public bool IsAvailable => _gate is not null;

    public ObservableCollection<GrantRow> Grants { get; } = [];

    public IReadOnlyList<PermissionClassOption> Classes { get; }

    [ObservableProperty]
    private PermissionClassOption? _selectedClass;

    /// <summary>The base branch a permission to write on the pull request is limited to; empty covers any base.</summary>
    [ObservableProperty]
    private string _baseBranchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    public bool HasNote => Note is not null;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether a permission can be given: there is a project, a recognised repository and a gate.</summary>
    public bool CanGrant => _gate is not null && _project is not null && _repository is not null;

    /// <summary>The permissions of the project for the repository, read again from the gate (it reads the log); null repository clears them.</summary>
    public async Task LoadAsync(ProjectContext? project, RemoteRepository? repository)
    {
        var generation = ++_generation;
        _project = project;
        _repository = repository;
        OnPropertyChanged(nameof(CanGrant));
        Note = null;
        if (_gate is null || project is null)
        {
            Grants.Clear();
            return;
        }

        try
        {
            var grants = await _gate.GrantsAsync(project, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            Grants.Clear();
            foreach (var grant in grants.OrderBy(g => g.At))
            {
                Grants.Add(RowOf(grant));
            }
        }
#pragma warning disable CA1031 // The gate is behind a port: a failure is said in the section, never thrown into the UI.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                Grants.Clear();
                Note = Texts.Get("Hosting_Permissions_Unreadable");
            }
        }
    }

    /// <summary>«Dar permiso…»: asks the gate; a refusal or a no is said, and the list is read again.</summary>
    [RelayCommand]
    private async Task GrantAsync()
    {
        if (_gate is null || _project is not { } project || _repository is not { } repository || SelectedClass is not { } option)
        {
            return;
        }

        RemoteBranch? baseBranch = null;
        if (option.AsksForBase && !string.IsNullOrWhiteSpace(BaseBranchText))
        {
            baseBranch = RemoteBranch.TryParse(BaseBranchText.Trim(), out var refusal);
            if (baseBranch is null)
            {
                Note = HostingTexts.Code(refusal);
                return;
            }
        }

        IsBusy = true;
        Note = null;
        try
        {
            var granted = await _gate.GrantAsync(project, option.Class, repository, baseBranch, CancellationToken.None).ConfigureAwait(true);
            Note = granted is null ? Texts.Get("Hosting_Permissions_NotGiven") : Texts.Get("Hosting_Permissions_Given");
        }
        catch (UserTextException refusal)
        {
            Note = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The gate is behind a port: a failure is said in the section, never thrown into the UI.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Note = Texts.Get("Hosting_Card_Failed");
        }
        finally
        {
            IsBusy = false;
        }

        var message = Note;
        await LoadAsync(project, repository).ConfigureAwait(true);
        Note ??= message;
        if (message is not null)
        {
            Note = message;
        }
    }

    /// <summary>«Retirar»: asks the gate to revoke the permission; the next publication asks again.</summary>
    [RelayCommand]
    private async Task RevokeAsync(GrantRow? row)
    {
        if (_gate is null || _project is not { } project || row is null)
        {
            return;
        }

        IsBusy = true;
        string? message;
        try
        {
            await _gate.RevokeAsync(project, row.GrantId, CancellationToken.None).ConfigureAwait(true);
            message = Texts.Get("Hosting_Permissions_Revoked");
        }
        catch (UserTextException refusal)
        {
            message = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The gate is behind a port: a failure is said in the section, never thrown into the UI.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            message = Texts.Get("Hosting_Card_Failed");
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(project, _repository).ConfigureAwait(true);
        Note = message;
    }

    private GrantRow RowOf(HostingPolicyGrant grant) => new(
        grant.GrantId,
        Texts.Get("Hosting_Class_" + grant.Class),
        grant.BaseBranch is { } baseBranch
            ? Texts.Format("Hosting_Permissions_WhereBase", $"{grant.Repository.Owner}/{grant.Repository.Name}", baseBranch.Name)
            : Texts.Format("Hosting_Permissions_Where", $"{grant.Repository.Owner}/{grant.Repository.Name}"),
        Texts.Format("Hosting_Permissions_Who", HostingTexts.Data(grant.GrantedBy) ?? "?"),
        HostingTexts.When(grant.At, _time));
}
