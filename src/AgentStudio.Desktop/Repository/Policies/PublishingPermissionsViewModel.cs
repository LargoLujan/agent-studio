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

    // Counts the projects the section has been for: a grant or a withdrawal that was waiting for Windows Hello when the project changed must not repopulate the list of the new one.
    private int _epoch;

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

    /// <summary>
    /// Leaves the project: no permission of it stays on screen, and «Dar permiso…» and «Retirar» have nothing to act on until a project and its repository are loaded again. Every way out of
    /// a project (a change, none, not available, no remote, an error of reading) comes through here.
    /// </summary>
    public void Reset()
    {
        _generation++;
        _epoch++;
        _project = null;
        _repository = null;
        Grants.Clear();
        Note = null;
        IsBusy = false;
        OnPropertyChanged(nameof(CanGrant));
    }

    /// <summary>The permissions of the project for the repository, read again from the gate (it reads the log); null repository clears them.</summary>
    public async Task LoadAsync(ProjectContext? project, RemoteRepository? repository)
    {
        var generation = ++_generation;
        if (!Equals(_project, project) || !Equals(_repository, repository))
        {
            _epoch++;
        }

        _project = project;
        _repository = repository;
        OnPropertyChanged(nameof(CanGrant));
        Note = null;
        if (_gate is null || project is null || repository is null)
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

        var epoch = _epoch;
        IsBusy = true;
        Note = null;
        string? result;
        try
        {
            var granted = await _gate.GrantAsync(project, option.Class, repository, baseBranch, CancellationToken.None).ConfigureAwait(true);
            result = granted is null ? Texts.Get("Hosting_Permissions_NotGiven") : Texts.Get("Hosting_Permissions_Given");
        }
        catch (UserTextException refusal)
        {
            result = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The gate is behind a port: a failure is said in the section, never thrown into the UI.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            result = Texts.Get("Hosting_Card_Failed");
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBusy = false;
            }
        }

        if (epoch != _epoch)
        {
            // The project changed while the gate waited: neither the list nor the note of the old one belongs on screen.
            return;
        }

        await LoadAsync(project, repository).ConfigureAwait(true);
        Note = result;
    }

    /// <summary>«Retirar»: asks the gate to revoke the permission; the next publication asks again.</summary>
    [RelayCommand]
    private async Task RevokeAsync(GrantRow? row)
    {
        if (_gate is null || _project is not { } project || row is null || !Grants.Contains(row))
        {
            return;
        }

        var epoch = _epoch;
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
            if (epoch == _epoch)
            {
                IsBusy = false;
            }
        }

        if (epoch != _epoch)
        {
            return;
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
