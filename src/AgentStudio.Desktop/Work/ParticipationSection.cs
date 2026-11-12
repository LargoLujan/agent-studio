using System.Collections.ObjectModel;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Work;

/// <summary>
/// One of the six roles of a subject (docs/16 D-32): who took it and where that comes from. A role without a source says «No consta»:
/// it is never deduced from another role, because the producing agent, the author of the commit, the account that published, the
/// reviewer, the approver and the integrator are never the same thing.
/// </summary>
public sealed class ParticipationRowViewModel(ParticipationEntry entry)
{
    public ParticipationEntry Entry { get; } = entry;

    public string RoleText => WorkTexts.Role(Entry.Role);

    public bool IsRecorded => Entry.IsRecorded;

    /// <summary>Who took the role, or «No consta» when no source says.</summary>
    public string WhoText => Entry.Who ?? Texts.Get("Work_Participation_NotRecorded");

    public string? DetailText => Entry.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Entry.Detail);

    public string? SourceText => WorkTexts.Source(Entry.Source) is { } source ? Texts.Format("Work_Participation_Source", source) : null;

    public bool HasSource => SourceText is not null;

    public override string ToString() => $"{RoleText}: {WhoText}";
}

/// <summary>
/// «Participantes» (docs/16 D-32, F9-5): the six roles for a task, a run or a milestone, projected from the verified log. The subject is
/// the node the chain looks at, its task or its run, or the milestone selected; a role no source gives is «No consta».
/// </summary>
public sealed partial class ParticipationSection : WorkSection
{
    private bool _loading;

    public ParticipationSection(WorkContext context)
        : base(context, "Info_Work_Participation")
    {
        Context.FocusChanged += (_, _) => _ = ReloadAsync();
        Context.MilestoneSelected += (_, _) => _ = ReloadAsync();
    }

    /// <summary>What can be looked at: the task, the run and the milestone the node of the chain belongs to.</summary>
    public ObservableCollection<Choice<TraceRef>> Subjects { get; } = [];

    public bool HasSubjects => Subjects.Count > 0;

    [ObservableProperty]
    private Choice<TraceRef>? _selectedSubject;

    public ObservableCollection<ParticipationRowViewModel> Roles { get; } = [];

    public bool HasRoles => Roles.Count > 0;

    public override async Task ReloadAsync()
    {
        Notice = Context.Backend.Participation is null ? Texts.Get("Work_Notice_NoParticipation") : null;
        _loading = true;
        var keep = SelectedSubject?.Value;
        try
        {
            Subjects.Clear();
            foreach (var subject in SubjectsOf(Context.Focus, Context.SelectedMilestone is { } m ? TraceRef.Milestone(m.Value) : null))
            {
                Subjects.Add(new Choice<TraceRef>(subject, Texts.Format("Work_Chain_Node", WorkTexts.Node(subject.Kind), WorkTexts.Short(subject.Id))));
            }

            SelectedSubject = Subjects.FirstOrDefault(s => s.Value == keep) ?? Subjects.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasSubjects));
        await ProjectSelectedAsync().ConfigureAwait(true);
    }

    protected override void ProjectChanged()
    {
        Subjects.Clear();
        Roles.Clear();
        OnPropertyChanged(nameof(HasSubjects));
        OnPropertyChanged(nameof(HasRoles));
    }

    partial void OnSelectedSubjectChanged(Choice<TraceRef>? value)
    {
        if (!_loading)
        {
            _ = ProjectSelectedAsync();
        }
    }

    private async Task ProjectSelectedAsync()
    {
        var read = BeginRead();
        var roles = new List<ParticipationRowViewModel>();
        if (Context.Backend.Participation is { } participation && Context.Project is { } project && SelectedSubject is { } subject)
        {
            await GuardAsync(async () =>
            {
                var view = await participation.ProjectAsync(project, subject.Value, CancellationToken.None).ConfigureAwait(true);
                roles.AddRange(view.Entries.Select(entry => new ParticipationRowViewModel(entry)));
            }).ConfigureAwait(true);
        }

        if (!IsLatest(read))
        {
            return;
        }

        Roles.Clear();
        foreach (var role in roles)
        {
            Roles.Add(role);
        }

        OnPropertyChanged(nameof(HasRoles));
    }

    /// <summary>The subjects a node belongs to: its task, its run, and the milestone selected. Only a task, a run or a milestone has participation.</summary>
    internal static IEnumerable<TraceRef> SubjectsOf(TraceRef? focus, TraceRef? milestone)
    {
        var found = new List<TraceRef>();
        if (focus is { Kind: TraceNodeKind.Task or TraceNodeKind.Attempt } node)
        {
            var task = node.Id.Contains('#', StringComparison.Ordinal) ? node.Id[..node.Id.IndexOf('#', StringComparison.Ordinal)] : node.Id;
            var slash = task.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                found.Add(new TraceRef(TraceNodeKind.Task, task));
                found.Add(TraceRef.Run(task[..slash]));
            }
        }
        else if (focus is { Kind: TraceNodeKind.Run or TraceNodeKind.Milestone } direct)
        {
            found.Add(direct);
        }

        if (milestone is not null && !found.Contains(milestone))
        {
            found.Add(milestone);
        }

        return found;
    }
}
