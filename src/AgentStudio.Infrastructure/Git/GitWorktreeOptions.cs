namespace AgentStudio.Infrastructure.Git;

/// <param name="WorktreeRoot">
/// Folder that holds every worktree, outside any repository (docs/13 section 5): the default is
/// <c>%LOCALAPPDATA%\AgentStudio\wt</c>. Worktrees live at <c>&lt;root&gt;\&lt;repo8&gt;\&lt;run6&gt;\&lt;name&gt;</c>.
/// </param>
/// <param name="CommandTimeout">Upper bound for every git command; a hung git is killed with its process tree. Default: 5 minutes.</param>
/// <param name="GitExecutable">Full path of git.exe. When null it is located on the PATH at first use.</param>
/// <param name="DeliveryTimeout">
/// Upper bound for the commands of an approved delivery into the user's branch, which run the user's hooks. Default: 10
/// minutes.
/// </param>
/// <param name="CaptureBudget">How much of the user's repository a capture hashes by content before marking the rest indeterminate (N-07). Null uses <see cref="CaptureBudget.Default"/>.</param>
public sealed record GitWorktreeOptions(string WorktreeRoot, TimeSpan? CommandTimeout = null, string? GitExecutable = null, TimeSpan? DeliveryTimeout = null, CaptureBudget? CaptureBudget = null)
{
    public static TimeSpan DefaultCommandTimeout { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan DefaultDeliveryTimeout { get; } = TimeSpan.FromMinutes(10);

    public static string DefaultWorktreeRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "wt");
}

/// <summary>
/// The budget a capture of the user's repository (<c>CaptureUserRepositoryStateAsync</c>) spends on hashing the content
/// of the files its status lists, so a same-length swap with the timestamp restored is not invisible (N-07). A file over
/// <see cref="MaxFileBytes"/>, or everything past <see cref="MaxTotalBytes"/> or <see cref="MaxFiles"/> files, is marked
/// indeterminate instead of trusted by its size and time. The metadata of every entry of an untracked or ignored folder
/// is read as well, also past those limits, up to <see cref="MaxFolderEntries"/> entries (R3-02).
/// </summary>
/// <param name="MaxFileBytes">The largest file hashed by content; a larger one is indeterminate.</param>
/// <param name="MaxTotalBytes">How many bytes one capture hashes in all before the rest is indeterminate.</param>
/// <param name="MaxFiles">How many files one capture hashes in all before the rest is indeterminate.</param>
/// <param name="MaxFolderEntries">
/// How many entries (files, folders and links) of untracked or ignored folders one capture reads the metadata of, in
/// the sorted order of the folders. A folder past it has not even its metadata: it can never be compared and always
/// blocks.
/// </param>
public sealed record CaptureBudget(long MaxFileBytes, long MaxTotalBytes, int MaxFiles, int MaxFolderEntries = 200_000)
{
    /// <summary>
    /// 8 MiB per file, 64 MiB and 5 000 files in all, and the metadata of 200 000 folder entries: measured to bound the
    /// cost on this repository (ADR-0012).
    /// </summary>
    public static CaptureBudget Default { get; } = new(8L * 1024 * 1024, 64L * 1024 * 1024, 5_000, 200_000);
}
