using System.Collections.ObjectModel;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Work;

/// <summary>A link in force around the node: both ends, who said so and why. Retiring it keeps it in the log.</summary>
public sealed class TraceLinkRowViewModel
{
    internal TraceLinkRowViewModel(TraceLink link, Func<TraceLinkRowViewModel, Task> retract)
    {
        Link = link;
        RetractCommand = new AsyncRelayCommand(() => retract(this));
    }

    public TraceLink Link { get; }

    public string Line => Texts.Format("Work_Link_Line", Describe(Link.From), Describe(Link.To));

    public string AssertedText => Texts.Format("Work_Link_By", WorkTexts.Asserted(Link.AssertedBy));

    public string Reason => Link.Reason;

    public IAsyncRelayCommand RetractCommand { get; }

    internal static string Describe(TraceRef node) => Texts.Format("Work_Chain_Node", WorkTexts.Node(node.Kind), WorkTexts.Short(node.Id));

    public override string ToString() => Line;
}

/// <summary>What the system infers and a person has not confirmed: it counts as nothing until they do (docs/16 D-30).</summary>
public sealed class TraceSuggestionRowViewModel
{
    internal TraceSuggestionRowViewModel(TraceSuggestion suggestion, Func<TraceSuggestionRowViewModel, Task> confirm)
    {
        Suggestion = suggestion;
        ConfirmCommand = new AsyncRelayCommand(() => confirm(this));
    }

    public TraceSuggestion Suggestion { get; }

    public string Line => Texts.Format("Work_Link_Line", TraceLinkRowViewModel.Describe(Suggestion.From), TraceLinkRowViewModel.Describe(Suggestion.To));

    public string SourceText => Texts.Format("Work_Link_Inferred", WorkTexts.Suggestion(Suggestion.Source), Suggestion.InferredFrom);

    public IAsyncRelayCommand ConfirmCommand { get; }

    public override string ToString() => Line;
}

/// <summary>
/// «Vínculos» (docs/16 D-29 and D-30, F9-3): the explicit links around the node the chain looks at, and the ones the system infers
/// (from commit messages, trailers, branch names and pull requests) as suggestions. A suggestion is never a fact: it becomes a link only
/// when the person confirms it, and a link is retired, never deleted. The retirement asks for its reason, which is the person's text.
/// </summary>
public sealed partial class LinksSection : WorkSection
{
    public LinksSection(WorkContext context)
        : base(context, "Info_Work_Links")
    {
        Context.FocusChanged += (_, _) => _ = ReloadAsync();
    }

    public ObservableCollection<TraceLinkRowViewModel> Links { get; } = [];

    public bool HasLinks => Links.Count > 0;

    public ObservableCollection<TraceSuggestionRowViewModel> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public string NodeText => Context.Focus is { } focus ? Texts.Format("Work_Chain_Start", WorkTexts.Node(focus.Kind), WorkTexts.Short(focus.Id)) : Texts.Get("Work_Chain_NoStart");

    /// <summary>Why a link is retired: the person's own words, required.</summary>
    [ObservableProperty]
    private string _retractReason = string.Empty;

    public override async Task ReloadAsync()
    {
        var read = BeginRead();
        Notice = Context.Backend.Links is null ? Texts.Get("Work_Notice_NoLinks") : null;
        OnPropertyChanged(nameof(NodeText));
        var links = new List<TraceLinkRowViewModel>();
        var suggestions = new List<TraceSuggestionRowViewModel>();
        if (Context.Project is { } project && Context.Focus is { } node)
        {
            await GuardAsync(async () =>
            {
                if (Context.Backend.Links is { } service)
                {
                    links.AddRange((await service.LinksAroundAsync(project, node, CancellationToken.None).ConfigureAwait(true)).Select(l => new TraceLinkRowViewModel(l, RetractAsync)));
                }

                if (Context.Backend.Suggestions is { } suggester)
                {
                    suggestions.AddRange((await suggester.SuggestAsync(project, node, CancellationToken.None).ConfigureAwait(true)).Select(s => new TraceSuggestionRowViewModel(s, ConfirmAsync)));
                }
            }).ConfigureAwait(true);
        }

        if (!IsLatest(read))
        {
            return;
        }

        Links.Clear();
        foreach (var link in links)
        {
            Links.Add(link);
        }

        Suggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            Suggestions.Add(suggestion);
        }

        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasSuggestions));
    }

    protected override void ProjectChanged()
    {
        Links.Clear();
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(NodeText));
    }

    private async Task ConfirmAsync(TraceSuggestionRowViewModel row)
    {
        if (Context.Backend.Links is not { } links || Context.Project is not { } project)
        {
            return;
        }

        if (await GuardAsync(() => links.ConfirmAsync(project, row.Suggestion, Context.Person, CancellationToken.None)).ConfigureAwait(true))
        {
            Message = Texts.Get("Work_Link_Confirmed");
            Context.NotifyWritten();
        }
    }

    private async Task RetractAsync(TraceLinkRowViewModel row)
    {
        if (Context.Backend.Links is not { } links || Context.Project is not { } project)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RetractReason))
        {
            Error = Texts.Get("Work_Link_NeedsReason");
            return;
        }

        if (await GuardAsync(() => links.RetractAsync(project, row.Link.Id, Context.Person, RetractReason.Trim(), CancellationToken.None)).ConfigureAwait(true))
        {
            RetractReason = string.Empty;
            Message = Texts.Get("Work_Link_Retracted");
            Context.NotifyWritten();
        }
    }
}
