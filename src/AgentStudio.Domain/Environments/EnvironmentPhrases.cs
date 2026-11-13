namespace AgentStudio.Domain.Environments;

/// <summary>
/// The fixed phrases that <see cref="EnvironmentComparer"/> writes in the value of a fact (<c>not in the NuGet cache</c>, <c>docker running (client 27.0, engine 27.0)</c>,
/// <c>image redis:7</c>). The evidence keeps them as English words; the panel recognises them to paint them in the language in use. Both sides use these constants and builders,
/// so a change of a phrase cannot leave the panel showing the English one.
/// </summary>
public static class EnvironmentPhrases
{
    public const string None = "none";

    public const string Absent = "absent";

    public const string StoreAlias = "store-alias";

    public const string InNuGetCache = "in the NuGet cache";

    public const string NotInNuGetCache = "not in the NuGet cache";

    public const string NotChecked = "not checked";

    public const string DockerNotInstalled = "docker not installed";

    /// <summary>The label of a path that a file of the project adds to the search path.</summary>
    public const string PathLabel = "path";

    public const string VenvLabel = "venv";

    public const string ImageLabel = "image";

    public const string DockerfileLabel = "dockerfile";

    public const string ComposeFileLabel = "compose file";

    public const string ExtendsLabel = "extends";

    public const string ComposeSizeLabel = "compose size";

    /// <summary>What follows the size of a compose file.</summary>
    public const string ComposeSizeSuffix = " bytes";

    /// <summary>The parts of «docker running (client …, engine …)»: the text before the client version, between the two versions and after the engine one.</summary>
    public const string DockerRunningBeforeClient = "docker running (client ";

    public const string DockerRunningBetween = ", engine ";

    public const string DockerRunningAfter = ")";

    /// <summary>The parts of «docker engine stopped (client …)».</summary>
    public const string DockerStoppedBeforeClient = "docker engine stopped (client ";

    public const string DockerStoppedAfter = ")";

    /// <summary>What separates a label from its datum.</summary>
    public const string LabelSeparator = " ";

    /// <summary>A label and its datum: <c>image redis:7</c>.</summary>
    public static string Labelled(string label, string value) => label + LabelSeparator + value;

    /// <summary>The size of a compose file: <c>compose size 120 bytes</c>.</summary>
    public static string ComposeSize(string bytes) => Labelled(ComposeSizeLabel, bytes + ComposeSizeSuffix);

    public static string DockerRunning(object? client, object? engine) =>
        $"{DockerRunningBeforeClient}{client}{DockerRunningBetween}{engine}{DockerRunningAfter}";

    public static string DockerStopped(object? client) => $"{DockerStoppedBeforeClient}{client}{DockerStoppedAfter}";
}
