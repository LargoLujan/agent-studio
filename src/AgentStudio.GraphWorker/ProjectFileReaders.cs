using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AgentStudio.GraphWorker;

/// <summary>A project a solution lists: the raw path it says (not trusted yet), the line of it and the folder of the solution that holds it.</summary>
internal sealed record SolutionProject(string RawPath, int Line, string? Folder);

/// <summary>What a solution file says: its folders (full virtual paths such as <c>/src/tests/</c>) and its projects.</summary>
internal sealed record SolutionDescription(IReadOnlyList<string> Folders, IReadOnlyList<SolutionProject> Projects);

/// <summary>A reference of a project: the raw value and its line.</summary>
internal sealed record ProjectItem(string Value, int Line);

/// <summary>What the reading of a project file found. Nothing of it is evaluated: <paramref name="UnevaluatedLines"/> are the places MSBuild would have resolved.</summary>
internal sealed record ProjectDescription(
    IReadOnlyList<ProjectItem> ProjectReferences,
    IReadOnlyList<ProjectItem> PackageReferences,
    IReadOnlyList<int> UnevaluatedLines);

/// <summary>The XML of the repository, read as data (docs/20 D-16): no DTD, no entity expansion, no resolver, a cap on the size of the document.</summary>
internal static class SafeXml
{
    public const int MaxCharacters = 2_000_000;

    /// <exception cref="XmlException">The file is not well-formed XML, has a DTD, or is longer than the cap.</exception>
    public static XDocument Load(Stream stream)
    {
        using var text = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var reader = XmlReader.Create(
            text,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            });
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    public static int LineOf(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? Math.Max(1, info.LineNumber) : 1;

    /// <summary>The deepest nesting of elements a project file may have; MSBuild files are a few levels deep.</summary>
    public const int MaxDepth = 64;

    /// <summary>The elements below <paramref name="root"/>, document order, without recursion: a file nested a hundred thousand deep cannot exhaust the stack.</summary>
    public static IEnumerable<XElement> Descendants(XElement root) => root.Descendants();

    /// <summary>
    /// The elements below <paramref name="root"/> in document order with whether an ancestor below the root has a <c>Condition</c>, without recursion.
    /// </summary>
    /// <exception cref="XmlException">The elements are nested deeper than <see cref="MaxDepth"/>.</exception>
    public static IEnumerable<(XElement Element, bool InConditional)> Walk(XElement root)
    {
        var stack = new Stack<(XElement Element, int Depth, bool InConditional)>();
        foreach (var child in root.Elements().Reverse())
        {
            stack.Push((child, 1, false));
        }

        while (stack.Count > 0)
        {
            var (element, depth, inConditional) = stack.Pop();
            if (depth > MaxDepth)
            {
                throw new XmlException("The elements are nested too deep.");
            }

            yield return (element, inConditional);
            var below = inConditional || element.Attribute("Condition") is not null;
            foreach (var child in element.Elements().Reverse())
            {
                stack.Push((child, depth + 1, below));
            }
        }
    }
}

/// <summary>The <c>.slnx</c> of a solution: <c>&lt;Solution&gt;</c> with <c>&lt;Folder Name="/src/"&gt;</c> and <c>&lt;Project Path="…"/&gt;</c>.</summary>
internal static class SlnxReader
{
    /// <exception cref="XmlException">The file is not well-formed XML, or has a DTD.</exception>
    public static SolutionDescription Read(Stream stream)
    {
        var document = SafeXml.Load(stream);
        var folders = new List<string>();
        var projects = new List<SolutionProject>();
        if (document.Root is not { } root || root.Name.LocalName != "Solution")
        {
            throw new XmlException("The root of a solution file is Solution.");
        }

        foreach (var element in SafeXml.Descendants(root))
        {
            if (element.Name.LocalName == "Folder" && (string?)element.Attribute("Name") is { Length: > 0 } folder)
            {
                folders.Add(folder);
            }
            else if (element.Name.LocalName == "Project" && (string?)element.Attribute("Path") is { Length: > 0 } path)
            {
                var parent = element.Parent;
                string? holder = null;
                if (parent is { Name.LocalName: "Folder" })
                {
                    holder = (string?)parent.Attribute("Name");
                }

                projects.Add(new SolutionProject(path, SafeXml.LineOf(element), holder));
            }
        }

        return new SolutionDescription(folders, projects);
    }
}

/// <summary>The old <c>.sln</c>: not XML, read line by line. Only the <c>Project(…) = "name", "path", "{guid}"</c> lines and the nested-projects section.</summary>
internal static class SlnReader
{
    private const string SolutionFolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
    private const int MaxLineLength = 4096;

    public static SolutionDescription Read(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var entries = new Dictionary<string, (string Name, string Path, bool IsFolder, int Line)>(StringComparer.OrdinalIgnoreCase);
        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inNested = false;
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (line.Length > MaxLineLength)
            {
                continue;
            }

            var text = line.Trim();
            if (text.StartsWith("Project(\"", StringComparison.Ordinal))
            {
                var parts = QuotedParts(text);
                if (parts.Count == 4)
                {
                    entries[parts[3]] = (parts[1], parts[2], string.Equals(parts[0], SolutionFolderType, StringComparison.OrdinalIgnoreCase), number);
                }
            }
            else if (text.StartsWith("GlobalSection(NestedProjects)", StringComparison.Ordinal))
            {
                inNested = true;
            }
            else if (text.StartsWith("EndGlobalSection", StringComparison.Ordinal))
            {
                inNested = false;
            }
            else if (inNested)
            {
                var equals = text.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    parentOf[text[..equals].Trim()] = text[(equals + 1)..].Trim();
                }
            }
        }

        string FolderPath(string guid)
        {
            var names = new List<string>();
            var current = guid;
            for (var steps = 0; steps < 64 && entries.TryGetValue(current, out var entry) && entry.IsFolder; steps++)
            {
                names.Add(entry.Name);
                if (!parentOf.TryGetValue(current, out current!))
                {
                    break;
                }
            }

            names.Reverse();
            return "/" + string.Join('/', names) + "/";
        }

        var folders = entries.Where(e => e.Value.IsFolder).Select(e => FolderPath(e.Key)).Distinct(StringComparer.Ordinal).ToList();
        var projects = entries
            .Where(e => !e.Value.IsFolder)
            .OrderBy(e => e.Value.Line)
            .Select(e => new SolutionProject(e.Value.Path, e.Value.Line, parentOf.TryGetValue(e.Key, out var parent) && entries.ContainsKey(parent) ? FolderPath(parent) : null))
            .ToList();
        return new SolutionDescription(folders, projects);
    }

    /// <summary>The four quoted strings of a Project line: the type, the name, the path and the id.</summary>
    private static List<string> QuotedParts(string line)
    {
        var parts = new List<string>();
        var index = 0;
        while (parts.Count < 4 && (index = line.IndexOf('"', index)) >= 0)
        {
            var end = line.IndexOf('"', index + 1);
            if (end < 0)
            {
                break;
            }

            parts.Add(line[(index + 1)..end]);
            index = end + 1;
        }

        return parts;
    }
}

/// <summary>
/// The <c>.csproj</c> as XML, never evaluated (docs/20 D-16): the <c>ProjectReference</c> and <c>PackageReference</c> with a literal value, and the places
/// where MSBuild would have resolved something (an element with a <c>Condition</c>, a value with <c>$(…)</c>, <c>@(…)</c> or <c>%(…)</c>, a wildcard in an
/// <c>Include</c>, an <c>Import</c>, a <c>Compile</c> item, a target). Those are not followed: the project is partial, with the line.
/// </summary>
internal static class CsprojReader
{
    private static readonly HashSet<string> NotEvaluated = new(StringComparer.Ordinal)
    {
        "Import", "ImportGroup", "UsingTask", "Target", "Choose", "Compile", "Analyzer",
    };

    /// <exception cref="XmlException">The file is not well-formed XML, has a DTD, or its root is not Project.</exception>
    public static ProjectDescription Read(Stream stream)
    {
        var document = SafeXml.Load(stream);
        if (document.Root is not { } root || root.Name.LocalName != "Project")
        {
            throw new XmlException("The root of a project file is Project.");
        }

        var projectReferences = new List<ProjectItem>();
        var packageReferences = new List<ProjectItem>();
        var unevaluated = new SortedSet<int>();
        if (IsDynamic((string?)root.Attribute("Sdk")) || root.Attribute("Condition") is not null)
        {
            unevaluated.Add(SafeXml.LineOf(root));
        }

        foreach (var (element, inConditional) in SafeXml.Walk(root))
        {
            var name = element.Name.LocalName;
            var line = SafeXml.LineOf(element);
            if (element.Attribute("Condition") is not null || inConditional)
            {
                // What MSBuild would decide with a condition is not decided here: the element is not followed, the project says so.
                unevaluated.Add(line);
                continue;
            }

            if (NotEvaluated.Contains(name))
            {
                unevaluated.Add(line);
                continue;
            }

            switch (name)
            {
                case "ProjectReference" or "PackageReference":
                    {
                        var include = (string?)element.Attribute("Include");
                        if (include is null)
                        {
                            if (name == "ProjectReference")
                            {
                                unevaluated.Add(line);
                            }

                            break;
                        }

                        if (IsDynamic(include) || include.Contains(';', StringComparison.Ordinal) || include.Contains('*', StringComparison.Ordinal))
                        {
                            unevaluated.Add(line);
                            break;
                        }

                        (name == "ProjectReference" ? projectReferences : packageReferences).Add(new ProjectItem(include.Trim(), line));
                        break;
                    }

                case "TargetFramework" or "TargetFrameworks" or "OutputType" or "RootNamespace" or "AssemblyName":
                    if (IsDynamic(element.Value))
                    {
                        unevaluated.Add(line);
                    }

                    break;
            }
        }

        return new ProjectDescription(projectReferences, packageReferences, [.. unevaluated]);
    }

    /// <summary>Whether a value holds something MSBuild would evaluate: a property, an item list or a metadata reference.</summary>
    internal static bool IsDynamic(string? value) =>
        value is not null && (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal));
}
