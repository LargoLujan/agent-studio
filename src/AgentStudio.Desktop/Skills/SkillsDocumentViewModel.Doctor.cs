using System.Globalization;
using System.IO;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Skills;

/// <summary>
/// The doctor of skills in the screen (docs/20 D-13 to D-15, F6-6 and F6-13, ADR-0063):
/// <list type="bullet">
/// <item>«Comprobar en GitHub» is the only way a request leaves, and only with the permission on (<see cref="IsNetworkAllowed"/>, off by default). Nothing
/// is asked when the screen opens, nor in the background. The section says where it connects and what it sends before the button is pressed.</item>
/// <item>The token is optional: it enters through <see cref="SaveTokenAsync"/> as an argument, never as a bound property, and it is never shown again
/// (only «hay una clave guardada»). Storing and deleting it ask for Windows Hello inside <see cref="ISkillsDoctorTokenManager"/>, and using it, inside the checker.</item>
/// <item>What a batch found (<c>upstream</c> and <c>archived</c>) is laid over the local health, so the same checks and the same dot show it. No text of
/// an error reaches the screen: a failure is a code with its own sentence.</item>
/// <item>«Marcar los scripts como revisados» keeps the fingerprint of <c>scripts/</c>; a changed script is «sin revisar» again. Nothing is ever run.</item>
/// </list>
/// </summary>
public sealed partial class SkillsDocumentViewModel
{
    private readonly Dictionary<SkillId, IReadOnlyList<SkillHealthCheck>> _remoteChecks = [];
    private readonly Dictionary<SkillId, SkillScriptsInfo> _scripts = [];
    private readonly Dictionary<SkillId, string> _reviewedScripts = [];
    private bool _loadingDoctor;
    private CancellationTokenSource? _checkCancellation;

    /// <summary>«Permitir comprobar las skills en GitHub»: off until the person turns it on, and kept in the settings of the app.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckOnGitHubCommand))]
    private bool _isNetworkAllowed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckOnGitHubCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCheckCommand))]
    private bool _isChecking;

    /// <summary>What the last batch did, or that nothing was checked yet.</summary>
    [ObservableProperty]
    private string _doctorStatus = Texts.Get("Skills_Doctor_NotChecked");

    /// <summary>Whether a token is stored. Learned without decrypting anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenStoredText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTokenCommand))]
    private bool _hasToken;

    /// <summary>What the last change of the token did; never carries the token.</summary>
    [ObservableProperty]
    private string? _tokenStatus;

    public bool CanChangeNetwork => _backend.NetworkSetting is not null;

    public bool CanManageToken => _backend.Tokens is not null;

    public string TokenStoredText => Texts.Get(HasToken ? "Skills_Token_Stored" : "Skills_Token_None");

    /// <summary>Why the button cannot be pressed; null when it can.</summary>
    public string? DoctorUnavailable =>
        _backend.Doctor is null ? Texts.Get("Skills_Doctor_NoChecker") : !IsNetworkAllowed ? Texts.Get("Skills_Doctor_Off") : null;

    partial void OnIsNetworkAllowedChanged(bool value)
    {
        if (!_loadingDoctor)
        {
            _backend.NetworkSetting?.Set(value);
        }

        OnPropertyChanged(nameof(DoctorUnavailable));
    }

    /// <summary>The text of a check in the language of the person: its coded text painted from the resources, or the message it carries (docs/17 D-47, ADR-0054).</summary>
    internal static string CheckText(SkillHealthCheck check) =>
        check.TextCode is { } code ? Texts.Render(new UserText(code, check.TextArguments)) : check.Message;

    /// <summary>Stops the batch in progress (the button «Cancelar la comprobación» and closing the screen): the skills that are left stay unchecked.</summary>
    [RelayCommand(CanExecute = nameof(IsChecking))]
    private void CancelCheck() => _checkCancellation?.Cancel();

    /// <summary>Closing the screen cancels the batch: nothing keeps asking GitHub behind a tab that is gone.</summary>
    private void CancelRunningCheck() => _checkCancellation?.Cancel();

    private bool CanCheck() => IsNetworkAllowed && !IsChecking && _backend.Doctor is not null && _items.Count > 0;

    /// <summary>Asks GitHub about every skill of the catalog, once, in series, because the person pressed the button.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckOnGitHubAsync()
    {
        if (!CanCheck() || _backend.Doctor is not { } doctor)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        IsChecking = true;
        DoctorStatus = Texts.Get("Skills_Doctor_Checking");
        try
        {
            var result = await doctor.CheckAsync([.. _items.Select(item => item.Skill.Entry)], cancellation.Token).ConfigureAwait(true);
            _remoteChecks.Clear();
            foreach (var entry in result.Entries)
            {
                _remoteChecks[entry.Skill] = entry.Report.Checks;
            }

            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
            DoctorStatus = Summarize(result);
        }
        catch (OperationCanceledException)
        {
            DoctorStatus = Texts.Get("Skills_Doctor_Cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message of the exception is not shown: it could carry what was sent.
            DoctorStatus = Texts.Get("Skills_Doctor_Failed");
        }
        finally
        {
            _checkCancellation = null;
            IsChecking = false;
        }
    }

    private string Summarize(SkillHealthBatchResult result)
    {
        var notChecked = result.Entries.Count(e => e.Failure is not null);
        var toReview = result.Entries.Count(e => e.Failure is null && e.Report.Checks.Any(c => c.Status is SkillHealthStatus.Warning or SkillHealthStatus.Critical));
        var clean = result.Entries.Count - notChecked - toReview;
        var text = Texts.Format("Skills_Doctor_Summary", _time.GetLocalNow().ToString("HH:mm", CultureInfo.CurrentCulture), clean, toReview, notChecked);
        if (result.TokenState == SkillsDoctorTokenState.Granted)
        {
            text += " " + Texts.Get("Skills_Doctor_TokenUsed");
        }
        else if (result.TokenState == SkillsDoctorTokenState.PresenceDenied)
        {
            text += " " + Texts.Get("Skills_Doctor_TokenDenied");
        }

        if (result.Entries.Any(e => e.Failure == SkillHealthFailure.InvalidToken))
        {
            text += " " + Texts.Get("Skills_Doctor_BadToken");
        }

        if (result.Entries.Any(e => e.Failure == SkillHealthFailure.RateLimited))
        {
            text += " " + (result.RateLimitResetsAt is { } at
                ? Texts.Format("Skills_Doctor_Limit", at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture))
                : Texts.Get("Skills_Doctor_LimitSoon"));
        }

        return text;
    }

    /// <summary>Lays what the last batch found over the local health: the checks with the same code are replaced, the others are added.</summary>
    private SkillHealthReport OverlayRemote(SkillId skill, SkillHealthReport local)
    {
        if (!_remoteChecks.TryGetValue(skill, out var remote) || remote.Count == 0)
        {
            return local;
        }

        var codes = remote.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        return new SkillHealthReport([.. local.Checks.Where(c => !codes.Contains(c.Code)), .. remote]);
    }

    /// <summary>The state the doctor shows: the permission, whether there is a token, and the scripts of each skill with the review the person made.</summary>
    private async Task LoadDoctorStateAsync(IReadOnlyList<CatalogSkill> catalog, CancellationToken ct)
    {
        _loadingDoctor = true;
        try
        {
            IsNetworkAllowed = _backend.NetworkSetting?.NetworkAllowed == true;
        }
        finally
        {
            _loadingDoctor = false;
        }

        try
        {
            HasToken = _backend.Tokens is not null && await _backend.Tokens.HasTokenAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HasToken = false;
        }

        _scripts.Clear();
        _reviewedScripts.Clear();
        if (_backend.ScriptsInspector is { } inspector)
        {
            foreach (var skill in catalog.Where(c => c.LocalPath is not null))
            {
                try
                {
                    if (await inspector.InspectAsync(skill.LocalPath!, ct).ConfigureAwait(true) is { } info)
                    {
                        _scripts[skill.Entry.Id] = info;
                        if (_backend.ScriptReviews is { } reviews && await reviews.ReviewedSha256Async(skill.Entry.Id, ct).ConfigureAwait(true) is { } reviewed)
                        {
                            _reviewedScripts[skill.Entry.Id] = reviewed;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // The check of the reader already says that the scripts could not be read.
                }
            }
        }

        OnPropertyChanged(nameof(CanChangeNetwork));
        OnPropertyChanged(nameof(CanManageToken));
        OnPropertyChanged(nameof(DoctorUnavailable));
        CheckOnGitHubCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The scripts of the skill for its card: the list, whether they are reviewed, and the command that marks them as they are now.</summary>
    private (IReadOnlyList<string> Files, IReadOnlyList<SkillScriptLink> Links, string? ReviewedText, IAsyncRelayCommand? Mark) ScriptsOf(SkillItemViewModel item)
    {
        var id = item.Skill.Entry.Id;
        if (!_scripts.TryGetValue(id, out var info))
        {
            return ([], [], null, null);
        }

        // A link «abrir» exists only when the screen can open a file in the editor of the app; without it the list stays plain text, never a link that does nothing.
        IReadOnlyList<SkillScriptLink> links = _openFile is null
            ? []
            : [.. info.RelativePaths.Take(SkillDetailViewModel.MaxScriptLines).Select(path => new SkillScriptLink(path, Texts.Format("Skills_Scripts_OpenName", path), new AsyncRelayCommand(() => OpenScriptAsync(item, path))))];
        if (!info.Reviewable)
        {
            // Too big to be read: the list stays, there is nothing to mark, and the text says why.
            return (info.RelativePaths, links, Texts.Get("Skills_Scripts_TooBig"), null);
        }

        var reviewed = _reviewedScripts.TryGetValue(id, out var hash) && string.Equals(hash, info.Sha256, StringComparison.Ordinal);
        return (info.RelativePaths, links, reviewed ? Texts.Get("Skills_Scripts_Reviewed") : null, reviewed ? null : new AsyncRelayCommand(() => MarkScriptsReviewedAsync(item)));
    }

    /// <summary>
    /// Opens one script of the skill in the editor of the app, to read it. The path is rebuilt from the folder of the skill and a relative path that the reading of
    /// <c>scripts/</c> gave, and checked again before it is opened: it must still be a plain file inside <c>scripts/</c>, never a link.
    /// </summary>
    private async Task OpenScriptAsync(SkillItemViewModel item, string relativePath)
    {
        if (_openFile is not { } open || item.Skill.LocalPath is not { } folder)
        {
            return;
        }

        var scriptsRoot = Path.GetFullPath(Path.Combine(folder, "scripts"));
        var full = Path.GetFullPath(Path.Combine(scriptsRoot, relativePath));
        var file = new FileInfo(full);
        if (!full.StartsWith(scriptsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !file.Exists
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.LinkTarget is not null)
        {
            ActivationProblem = Texts.Get("Skills_Scripts_OpenFailed");
            return;
        }

        ActivationProblem = null;
        await open(full).ConfigureAwait(true);
    }

    private async Task MarkScriptsReviewedAsync(SkillItemViewModel item)
    {
        var id = item.Skill.Entry.Id;
        if (_backend.ScriptReviews is not { } reviews || !_scripts.TryGetValue(id, out var info))
        {
            return;
        }

        try
        {
            await reviews.MarkReviewedAsync(id, info.Sha256, CancellationToken.None).ConfigureAwait(true);
            ActivationProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ActivationProblem = Texts.Get("Skills_Scripts_SaveFailed");
            return;
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Stores or replaces the token of GitHub. The text comes as an argument of the click, from a password box that the view empties right after; it is not
    /// a property anybody can bind or read. Windows Hello is asked first, in <see cref="ISkillsDoctorTokenManager"/>.
    /// </summary>
    public async Task SaveTokenAsync(string? text)
    {
        if (_backend.Tokens is not { } tokens)
        {
            return;
        }

        GitHubToken token;
        try
        {
            token = new GitHubToken(text?.Trim() ?? string.Empty);
        }
        catch (ArgumentException)
        {
            TokenStatus = Texts.Get("Skills_Token_Invalid");
            return;
        }

        try
        {
            var change = await tokens.StoreAsync(token, CancellationToken.None).ConfigureAwait(true);
            TokenStatus = Texts.Get(change == SkillsDoctorTokenChange.Done ? "Skills_Token_SavedOk" : "Skills_Token_Denied");
            HasToken = await tokens.HasTokenAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TokenStatus = Texts.Get("Skills_Token_Failed");
        }
    }

    [RelayCommand(CanExecute = nameof(HasToken))]
    private async Task DeleteTokenAsync()
    {
        if (_backend.Tokens is not { } tokens)
        {
            return;
        }

        try
        {
            var change = await tokens.DeleteAsync(CancellationToken.None).ConfigureAwait(true);
            TokenStatus = Texts.Get(change switch
            {
                SkillsDoctorTokenChange.Done => "Skills_Token_DeletedOk",
                SkillsDoctorTokenChange.NothingStored => "Skills_Token_NothingStored",
                _ => "Skills_Token_Denied",
            });
            HasToken = await tokens.HasTokenAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TokenStatus = Texts.Get("Skills_Token_Failed");
        }
    }
}
