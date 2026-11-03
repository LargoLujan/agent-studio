namespace AgentStudio.Observability.Quota;

/// <summary>
/// Where the user's record of quota observations lives and how it rotates (docs/14 D-14). The defaults are the
/// production settings: <c>%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl</c>, 1 MB per file and three older files.
/// </summary>
public sealed class QuotaLogOptions
{
    /// <summary>The current file of the record.</summary>
    public string FilePath { get; set; } = DefaultFilePath();

    /// <summary>When appending would take the current file over this size, it rotates first.</summary>
    public long MaxFileBytes { get; set; } = 1024 * 1024;

    /// <summary>How many older files are kept (<c>quota.1.jsonl</c> is the newest); older ones are deleted.</summary>
    public int RetainedFiles { get; set; } = 3;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FilePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(RetainedFiles);
    }

    private static string DefaultFilePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "AgentStudio", "usage", "quota.jsonl");
    }
}
