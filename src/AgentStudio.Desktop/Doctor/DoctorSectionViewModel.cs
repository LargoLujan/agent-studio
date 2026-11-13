using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Doctor;

/// <summary>
/// One of the folded sections of the panel (docs/20 D-04 and section 6.1): the worst state of its checks and how many are correct while folded, and
/// its checks when it is open. It opens by itself when something in it is not correct. The section of the providers keeps the rows of today
/// (<see cref="DoctorViewModel.Providers"/>); the declared environment is shown as a table of four columns (D-08).
/// </summary>
public sealed partial class DoctorSectionViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    internal DoctorSectionViewModel(DoctorSection section, IReadOnlyList<DoctorCheckViewModel> checks, DoctorStatus worst, IUiTextProvider texts, string? summary = null)
    {
        Section = section;
        Checks = checks;
        Status = worst;
        Title = texts.Get(DoctorTexts.SectionKey(section));
        InfoKey = "Info_Doctor_" + section;
        var counted = checks.Count(c => c.Status != DoctorStatus.NotApplicable);
        var correct = checks.Count(c => c.Status == DoctorStatus.Ok);
        Summary = summary ?? (counted == 0 ? texts.Get("Doctor_Section_NoneApply") : texts.Format("Doctor_Section_Count", correct, counted));
        AutomationName = texts.Format("Doctor_Section_AutomationName", Title, texts.Get(DoctorTexts.StatusKey(worst)));
        Glyph = worst switch
        {
            DoctorStatus.Ok => "✓",
            DoctorStatus.Warning => "!",
            DoctorStatus.Error => "✕",
            DoctorStatus.Unknown => "?",
            _ => "–",
        };

        // What is correct or does not apply stays folded: the panel is compact (P20); what needs a look opens.
        IsExpanded = worst is DoctorStatus.Warning or DoctorStatus.Error or DoctorStatus.Unknown;
    }

    /// <summary>What a screen reader says for the section: its title and its worst state, not the name of the type.</summary>
    public override string ToString() => AutomationName;

    public DoctorSection Section { get; }

    public string Title { get; }

    public string Summary { get; }

    public string AutomationName { get; }

    public DoctorStatus Status { get; }

    public string Glyph { get; }

    /// <summary>The key of the ⓘ of the section: <c>Info_Doctor_&lt;Section&gt;</c>.</summary>
    public string InfoKey { get; }

    public IReadOnlyList<DoctorCheckViewModel> Checks { get; }

    /// <summary>The checks that have a fix to offer: under the rows of the providers, where the guides of a missing program or a missing session appear.</summary>
    public IReadOnlyList<DoctorCheckViewModel> ActionChecks => [.. Checks.Where(c => c.HasFix)];

    public bool HasActionChecks => ActionChecks.Count > 0;

    public bool IsProviders => Section == DoctorSection.Providers;

    public bool IsTools => Section == DoctorSection.Tools;

    public bool IsProject => Section == DoctorSection.Project;

    public bool IsContainers => Section == DoctorSection.Containers;

    public bool IsTable => Section == DoctorSection.DeclaredEnvironment;

    public bool IsList => !IsProviders && !IsTable;
}
