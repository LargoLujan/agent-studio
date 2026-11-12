using System.IO;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>Lists the files of a project folder. It runs on a background thread, so it may block.</summary>
public delegate ProjectFileIndex ProjectFileIndexFactory(string root, CancellationToken cancellationToken);

/// <summary>A file offered by "go to file".</summary>
/// <param name="RelativePath">Path inside the project with "/" separators.</param>
/// <param name="FullPath">Absolute path of the file.</param>
public sealed record QuickOpenItem(string RelativePath, string FullPath)
{
    /// <summary>The file name alone.</summary>
    public string FileName => Path.GetFileName(RelativePath);

    /// <summary>The folders above the file, for display; empty for a file in the project root.</summary>
    public string Directory => Path.GetDirectoryName(RelativePath) ?? string.Empty;

    /// <summary>What a screen reader says of the file in the list (A1-bis): its path in the project, never the members of the record.</summary>
    public override string ToString() => RelativePath;
}

/// <summary>
/// "Go to file" (Ctrl+P): a search box over the files of the open project. The owner opens it with a callback for the
/// chosen file, so the same list serves for opening a file and for picking the file to compare with.
/// </summary>
public sealed partial class QuickOpenViewModel : ObservableObject
{
    /// <summary>Most results shown at once; the rest are reached by typing more.</summary>
    public const int MaxResults = 100;

    private readonly ProjectFileIndexFactory _indexer;
    private string _projectRoot = string.Empty;
    private IReadOnlyList<string> _paths = [];
    private Func<string, Task>? _onChosen;
    private CancellationTokenSource? _session;

    public QuickOpenViewModel()
        : this(static (root, token) => ProjectFileIndexer.Build(root, ProjectFileIndexer.DefaultMaxFiles, token))
    {
    }

    /// <param name="indexer">Lists the project files; replaceable so tests can control the indexing.</param>
    public QuickOpenViewModel(ProjectFileIndexFactory indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        _indexer = indexer;
    }

    /// <summary>True while the list is shown.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>What the list is for («Go to file», «Compare … with…»).</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The text typed in the search box; every change filters the list.</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>True while the project is being walked; typing waits until the index arrives.</summary>
    [ObservableProperty]
    private bool _isIndexing;

    /// <summary>One line under the list: how many files matched, that nothing did, or why there is no list.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>The best matches (at most <see cref="MaxResults"/>), best first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedItem))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private IReadOnlyList<QuickOpenItem> _results = [];

    /// <summary>Position of the highlighted result; -1 when the list is empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedItem))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private int _selectedIndex = -1;

    /// <summary>True when the project has more files than were indexed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TruncationNotice))]
    private bool _isTruncated;

    public QuickOpenItem? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>Warns that some files are missing from the list; null when every file was indexed.</summary>
    public string? TruncationNotice => IsTruncated
        ? Texts.Format("QuickOpen_Truncated", FormatCount(ProjectFileIndexer.DefaultMaxFiles))
        : null;

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>
    /// Shows the list for <paramref name="projectRoot"/> and indexes its files in the background. Completes once the
    /// index is ready (or the list was closed meanwhile). Choosing a file calls <paramref name="onChosen"/>.
    /// </summary>
    public async Task OpenAsync(string projectRoot, string title, Func<string, Task> onChosen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(onChosen);

        ResetSession();
        var session = new CancellationTokenSource();
        _session = session;
        _projectRoot = projectRoot;
        _onChosen = onChosen;
        Title = title;
        IsTruncated = false;
        IsIndexing = true;
        Query = string.Empty;
        Results = [];
        SelectedIndex = -1;
        Notice = Texts.Get("QuickOpen_Indexing");
        IsOpen = true;

        ProjectFileIndex index;
        try
        {
            index = await Task.Run(() => _indexer(projectRoot, session.Token), session.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Closed, or replaced by another session, while indexing.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!session.IsCancellationRequested)
            {
                IsIndexing = false;
                Notice = Texts.Format("QuickOpen_ReadFailed", ex.Message);
            }

            return;
        }

        if (session.IsCancellationRequested)
        {
            return; // The index arrived after the list was closed or reopened for something else.
        }

        _paths = index.RelativePaths;
        IsTruncated = index.IsTruncated;

        // The first list is ranked while the index still counts as being read: whoever sees it ready (IsIndexing false) finds
        // the list in place, and a query typed at that moment is not overwritten by this ranking.
        Rank();
        IsIndexing = false;
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows, wrapping around the ends of the list.</summary>
    public void MoveSelection(int delta)
    {
        var count = Results.Count;
        if (count == 0)
        {
            return;
        }

        SelectedIndex = SelectedIndex < 0
            ? (delta >= 0 ? 0 : count - 1)
            : (((SelectedIndex + delta) % count) + count) % count;
    }

    /// <summary>Closes the list without choosing anything.</summary>
    [RelayCommand]
    public void Close()
    {
        ResetSession();
        IsOpen = false;
    }

    /// <summary>Closes the list and hands the highlighted file to the owner.</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private async Task AcceptAsync()
    {
        if (SelectedItem is not { } item || _onChosen is not { } onChosen)
        {
            return;
        }

        Close();
        await onChosen(item.FullPath);
    }

    private bool CanAccept() => SelectedItem is not null;

    /// <summary>Drops the current index and cancels the indexing in flight; the list may stay open.</summary>
    private void ResetSession()
    {
        _session?.Cancel();
        _session = null;
        _onChosen = null;
        _paths = [];
        IsIndexing = false;
        Results = [];
        SelectedIndex = -1;
        Notice = null;
    }

    private void Refresh()
    {
        if (IsIndexing)
        {
            return;
        }

        Rank();
    }

    private void Rank()
    {
        var ranked = FuzzyPathMatcher.Rank(_paths, Query, MaxResults);
        Results = ranked.Paths.Select(path => new QuickOpenItem(path, Path.GetFullPath(Path.Combine(_projectRoot, path)))).ToList();
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        Notice = BuildNotice(ranked.TotalMatches);
    }

    private string BuildNotice(int matches)
    {
        if (_paths.Count == 0)
        {
            return Texts.Get("QuickOpen_NoFiles");
        }

        if (matches == 0)
        {
            return Texts.Get("QuickOpen_NoMatches");
        }

        var filtering = !string.IsNullOrWhiteSpace(Query);
        if (matches > MaxResults)
        {
            return Texts.Format(filtering ? "QuickOpen_Showing_Matches" : "QuickOpen_Showing_Files", MaxResults, FormatCount(matches));
        }

        return matches == 1
            ? Texts.Get(filtering ? "QuickOpen_OneMatch" : "QuickOpen_OneFile")
            : Texts.Format(filtering ? "QuickOpen_Matches" : "QuickOpen_Files", FormatCount(matches));
    }

    /// <summary>A count with the thousands separator of the language of the interface ("20.000" in Spanish, "20,000" in English).</summary>
    private static string FormatCount(int count) => count.ToString("N0", Texts.Current.Culture);
}
