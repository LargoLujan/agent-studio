using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Doctor;

/// <summary>What a fix is for the person, as the button says it (docs/20 D-10, section 6.1): «Ir a…», «Cómo arreglarlo» or «Aplicar».</summary>
public enum DoctorFixKind
{
    None,
    Go,
    Guide,
    Apply,
}

/// <summary>A fact of a check, with the label in the language in use (the «Detalles» of a check).</summary>
public sealed record DoctorFactItem(string Label, string Value)
{
    /// <summary>A list item that is not a control is named by its text: the label and the value, as the row shows them.</summary>
    public override string ToString() => Label + ": " + Value;
}

/// <summary>One check of the panel: its state with a shape as well as a colour, its title and sentence in the language in use, its facts and its fix (docs/20 D-04).</summary>
public sealed partial class DoctorCheckViewModel : ObservableObject
{
    private readonly DoctorViewModel _owner;
    private readonly DoctorFix? _fix;

    [ObservableProperty]
    private bool _isGuideOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private long? _appliedSequence;

    [ObservableProperty]
    private string? _fixMessage;

    internal DoctorCheckViewModel(DoctorCheck check, DoctorFix? fix, DoctorViewModel owner, IUiTextProvider texts)
    {
        Check = check;
        _owner = owner;
        _fix = fix;
        Title = DoctorTexts.Title(texts, check);
        Explanation = DoctorTexts.Explanation(texts, check);
        StatusName = texts.Get(DoctorTexts.StatusKey(check.Status));
        Glyph = GlyphOf(check.Status);
        Facts = [.. check.Facts.Select(f => new DoctorFactItem(DoctorTexts.FactLabel(texts, f.Name), f.Value))];
        AutomationName = texts.Format("Doctor_Check_AutomationName", Title, StatusName);
        FixKind = fix switch
        {
            DoctorFix.Navigate navigate when owner.CanNavigate(navigate.Target) => DoctorFixKind.Go,
            DoctorFix.Guide => DoctorFixKind.Guide,
            DoctorFix.Reversible or DoctorFix.Approved => DoctorFixKind.Apply,
            _ => DoctorFixKind.None,
        };
        FixLabel = FixKind switch
        {
            DoctorFixKind.Go => texts.Get("Doctor_Fix_Go"),
            DoctorFixKind.Guide => texts.Get("Doctor_Fix_How"),
            DoctorFixKind.Apply => texts.Get("Doctor_Fix_Apply"),
            _ => string.Empty,
        };
        FixSentence = fix is null || FixKind == DoctorFixKind.None ? string.Empty : DoctorTexts.FixSentence(texts, fix.Code, check);
        if (fix is DoctorFix.Guide guide)
        {
            GuideCommand = guide.Command;
            GuidePage = guide.Page;
        }

        Declared = check.Fact(DoctorFactNames.Declared) ?? "—";
        Source = check.Fact(DoctorFactNames.Source) ?? "—";
        Installed = check.Fact(DoctorFactNames.Installed) ?? check.Fact(DoctorFactNames.Resolved) ?? "—";
    }

    public DoctorCheck Check { get; }

    public string Code => Check.Code;

    public DoctorStatus Status => Check.Status;

    public string StatusName { get; }

    /// <summary>The shape of the state, so it is not told by colour alone: a tick, an exclamation mark, a cross, a question mark and a dash.</summary>
    public string Glyph { get; }

    public string Title { get; }

    public string Explanation { get; }

    public string AutomationName { get; }

    public IReadOnlyList<DoctorFactItem> Facts { get; }

    public bool HasFacts => Facts.Count > 0;

    // The four columns of the table of the declared environment (docs/20 D-08): what the project asks for, where it says it, what is installed, and the result is the state.
    public string Declared { get; }

    public string Source { get; }

    public string Installed { get; }

    public DoctorFixKind FixKind { get; }

    public bool HasFix => FixKind != DoctorFixKind.None;

    public string FixLabel { get; }

    public string FixSentence { get; }

    /// <summary>The command a guide gives to copy. The app never runs it.</summary>
    public string? GuideCommand { get; }

    public bool HasGuideCommand => GuideCommand is not null;

    public Uri? GuidePage { get; }

    public bool HasGuidePage => GuidePage is not null;

    public bool CanUndo => AppliedSequence is not null && _fix is DoctorFix.Reversible;

    private static string GlyphOf(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "✓",
        DoctorStatus.Warning => "!",
        DoctorStatus.Error => "✕",
        DoctorStatus.Unknown => "?",
        _ => "–",
    };

    /// <summary>«Ir a…» opens the screen; a guide opens or closes its card; «Aplicar» goes through the runner of fixes.</summary>
    [RelayCommand]
    private async Task RunFixAsync()
    {
        switch (_fix)
        {
            case DoctorFix.Navigate navigate:
                _owner.NavigateTo(navigate.Target);
                break;
            case DoctorFix.Guide:
                IsGuideOpen = !IsGuideOpen;
                break;
            case not null:
                await ApplyAsync();
                break;
        }
    }

    [RelayCommand]
    private void CopyCommand()
    {
        if (GuideCommand is not null)
        {
            _owner.Copy(GuideCommand);
        }
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (GuidePage is not null)
        {
            _owner.OpenPage(GuidePage);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (AppliedSequence is { } sequence)
        {
            await _owner.UndoAsync(this, sequence);
        }
    }

    private async Task ApplyAsync()
    {
        if (_fix is null)
        {
            return;
        }

        var result = await _owner.ApplyAsync(_fix, Check.Code);
        AppliedSequence = result.AppliedSequence;
        FixMessage = Texts.Get(result.Outcome switch
        {
            DoctorFixOutcome.Done => "Doctor_Fix_Done",
            DoctorFixOutcome.Denied => "Doctor_Fix_Denied",
            _ => "Doctor_Fix_Failed",
        });
    }

    /// <summary>What a screen reader says for the row: the title and the state, not the name of the type.</summary>
    public override string ToString() => AutomationName;

    internal void Undone() => (AppliedSequence, FixMessage) = (null, Texts.Get("Doctor_Fix_Undone"));
}
