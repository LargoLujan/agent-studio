using System.IO;
using System.Xml.Linq;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>Where the sources of the desktop are, and the resource files of texts in them (docs/17 D-47 and D-51).</summary>
internal static class LocalizationTestSupport
{
    /// <summary>The root of the repository: the folder that holds <c>AgentStudio.slnx</c>, above the output of the tests.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>The folder of the sources of the desktop.</summary>
    public static string DesktopRoot => Path.Combine(RepositoryRoot, "src", "AgentStudio.Desktop");

    /// <summary>The folder of the resources of texts.</summary>
    public static string ResourcesFolder => Path.Combine(DesktopRoot, "Resources");

    /// <summary>
    /// The resource files of texts, by base name: <c>Strings</c> and the area files <c>&lt;Area&gt;Strings</c>, each with its
    /// neutral file (Spanish of Spain) and its satellites by culture.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Neutral, IReadOnlyDictionary<string, string> Satellites)> ResourceFiles()
    {
        var files = Directory.GetFiles(ResourcesFolder, "*Strings*.resx");
        var result = new Dictionary<string, (string, IReadOnlyDictionary<string, string>)>(StringComparer.Ordinal);
        foreach (var neutral in files.Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("Strings", StringComparison.Ordinal)))
        {
            var baseName = Path.GetFileNameWithoutExtension(neutral);
            var satellites = files
                .Where(f => Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.Ordinal) && f != neutral)
                .ToDictionary(f => Path.GetFileNameWithoutExtension(f)[(baseName.Length + 1)..], f => f, StringComparer.OrdinalIgnoreCase);
            result[baseName] = (neutral, satellites);
        }

        return result;
    }

    /// <summary>The texts of a resource file, by key, as it holds them.</summary>
    public static IReadOnlyDictionary<string, string> Read(string resx) =>
        XDocument.Load(resx).Root!.Elements("data")
            .Where(d => d.Attribute("type") is null)
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "AgentStudio.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("The tests run outside the repository: AgentStudio.slnx was not found above them.");
    }
}
