using System.Text.Json;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// The parsers of the closed list of files of <see cref="DeclaredFiles"/> (docs/20 D-05 and D-06): from the text of a file to its entries and issues, as
/// code that touches no disk and starts no process. The text came from a repository and is not trusted: each parser takes only the fields the design names,
/// nothing is followed (<c>extends</c>, <c>dockerComposeFile</c>, an image), and whatever it does not understand is an issue with its line, never a guess.
/// The reader of the infrastructure only opens the file, caps it and decodes it; every rule about what is inside lives here, where it is tested without a disk.
/// </summary>
public static class DeclaredFileParser
{
    /// <summary>The longest line of a text file; one that is longer cuts the reading (docs/20 D-06).</summary>
    public const int MaxLineLength = 4096;

    /// <summary>Reads <paramref name="text"/> (the decoded content of <paramref name="file"/>) into <paramref name="builder"/>. Never throws for a text.</summary>
    /// <param name="file">One of <see cref="DeclaredFiles.All"/>; a compose file has no parser (it is not interpreted) and is refused.</param>
    public static void Parse(string file, string text, DeclaredReadingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(builder);
        if (!DeclaredFiles.IsDeclared(file) || DeclaredFiles.ComposeFiles.Contains(file, StringComparer.Ordinal))
        {
            throw new ArgumentException("Only the files of the closed list that are interpreted have a parser.", nameof(file));
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        switch (file)
        {
            case DeclaredFiles.GlobalJson:
                ParseGlobalJson(text, builder);
                break;
            case DeclaredFiles.DotNetTools:
                ParseDotNetTools(text, builder);
                break;
            case DeclaredFiles.NvmRc:
                ParseFirstLine(file, text, DeclaredKind.NodeVersion, builder);
                break;
            case DeclaredFiles.NodeVersionFile:
                ParseFirstLine(file, text, DeclaredKind.NodeVersion, builder);
                break;
            case DeclaredFiles.PackageJson:
                ParsePackageJson(text, builder);
                break;
            case DeclaredFiles.PythonVersionFile:
                ParseFirstLine(file, text, DeclaredKind.PythonVersion, builder);
                break;
            case DeclaredFiles.PyProject:
                ParsePyProject(text, builder);
                break;
            case DeclaredFiles.PyVenvCfg:
                ParseVenv(text, builder);
                break;
            case DeclaredFiles.DevContainerFolder or DeclaredFiles.DevContainerRoot:
                ParseDevContainer(file, text, builder);
                break;
            default:
                ParseDockerfile(text, builder);
                break;
        }
    }

    // ---- JSON ----

    private static void ParseGlobalJson(string text, DeclaredReadingBuilder builder)
    {
        const string file = DeclaredFiles.GlobalJson;
        using var json = ManifestJson.Read(text, comments: true);
        if (!Accepted(json, file, builder))
        {
            return;
        }

        var root = json.Document!.RootElement;
        if (!root.TryGetProperty("sdk", out var sdk))
        {
            return;
        }

        if (sdk.ValueKind != JsonValueKind.Object)
        {
            builder.Issue(Source(file, json, "sdk"), DeclaredIssueKind.NotInterpreted);
            return;
        }

        Text(sdk, "version", "sdk/version", json, file, DeclaredKind.DotNetSdkVersion, builder);
        Text(sdk, "rollForward", "sdk/rollForward", json, file, DeclaredKind.DotNetRollForward, builder);
        if (sdk.TryGetProperty("allowPrerelease", out var allow))
        {
            if (allow.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                builder.Entry(DeclaredKind.DotNetAllowPrerelease, null, allow.ValueKind == JsonValueKind.True ? "true" : "false", Source(file, json, "sdk/allowPrerelease"));
            }
            else
            {
                builder.Issue(Source(file, json, "sdk/allowPrerelease"), DeclaredIssueKind.NotInterpreted);
            }
        }

        if (sdk.TryGetProperty("paths", out var paths))
        {
            if (paths.ValueKind != JsonValueKind.Array)
            {
                builder.Issue(Source(file, json, "sdk/paths"), DeclaredIssueKind.NotInterpreted);
                return;
            }

            foreach (var path in paths.EnumerateArray())
            {
                if (path.ValueKind == JsonValueKind.String)
                {
                    builder.Entry(DeclaredKind.DotNetSdkPath, null, path.GetString(), Source(file, json, "sdk/paths"));
                }
                else
                {
                    builder.Issue(Source(file, json, "sdk/paths"), DeclaredIssueKind.NotInterpreted);
                }
            }
        }
    }

    private static void ParseDotNetTools(string text, DeclaredReadingBuilder builder)
    {
        const string file = DeclaredFiles.DotNetTools;
        using var json = ManifestJson.Read(text, comments: false);
        if (!Accepted(json, file, builder))
        {
            return;
        }

        if (!json.Document!.RootElement.TryGetProperty("tools", out var tools))
        {
            return;
        }

        if (tools.ValueKind != JsonValueKind.Object)
        {
            builder.Issue(Source(file, json, "tools"), DeclaredIssueKind.NotInterpreted);
            return;
        }

        foreach (var tool in tools.EnumerateObject())
        {
            var source = Source(file, json, "tools/" + tool.Name);
            if (tool.Value.ValueKind == JsonValueKind.Object
                && tool.Value.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                builder.Entry(DeclaredKind.DotNetLocalTool, tool.Name, version.GetString(), source);
            }
            else
            {
                builder.Issue(source, DeclaredIssueKind.NotInterpreted, tool.Name);
            }
        }
    }

    private static void ParsePackageJson(string text, DeclaredReadingBuilder builder)
    {
        const string file = DeclaredFiles.PackageJson;
        using var json = ManifestJson.Read(text, comments: false);
        if (!Accepted(json, file, builder))
        {
            return;
        }

        if (!json.Document!.RootElement.TryGetProperty("engines", out var engines))
        {
            return;
        }

        if (engines.ValueKind != JsonValueKind.Object)
        {
            builder.Issue(Source(file, json, "engines"), DeclaredIssueKind.NotInterpreted);
            return;
        }

        Text(engines, "node", "engines/node", json, file, DeclaredKind.NodeEngine, builder);
        Text(engines, "npm", "engines/npm", json, file, DeclaredKind.NpmEngine, builder);
    }

    private static void ParseDevContainer(string file, string text, DeclaredReadingBuilder builder)
    {
        using var json = ManifestJson.Read(text, comments: true);
        if (!Accepted(json, file, builder))
        {
            return;
        }

        var root = json.Document!.RootElement;
        Text(root, "image", "image", json, file, DeclaredKind.ContainerImage, builder);
        Text(root, "dockerFile", "dockerFile", json, file, DeclaredKind.DevContainerDockerfile, builder);
        Text(root, "extends", "extends", json, file, DeclaredKind.Extends, builder);
        if (root.TryGetProperty("build", out var build))
        {
            if (build.ValueKind == JsonValueKind.Object)
            {
                Text(build, "dockerfile", "build/dockerfile", json, file, DeclaredKind.DevContainerDockerfile, builder);
            }
            else
            {
                builder.Issue(Source(file, json, "build"), DeclaredIssueKind.NotInterpreted);
            }
        }

        if (root.TryGetProperty("dockerComposeFile", out var compose))
        {
            var source = Source(file, json, "dockerComposeFile");
            if (compose.ValueKind == JsonValueKind.String)
            {
                builder.Entry(DeclaredKind.DevContainerComposeFile, null, compose.GetString(), source);
            }
            else if (compose.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in compose.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        builder.Entry(DeclaredKind.DevContainerComposeFile, null, item.GetString(), source);
                    }
                    else
                    {
                        builder.Issue(source, DeclaredIssueKind.NotInterpreted);
                    }
                }
            }
            else
            {
                builder.Issue(source, DeclaredIssueKind.NotInterpreted);
            }
        }

        if (root.TryGetProperty("features", out var features))
        {
            if (features.ValueKind == JsonValueKind.Object)
            {
                foreach (var feature in features.EnumerateObject())
                {
                    builder.Entry(DeclaredKind.DevContainerFeature, null, feature.Name, Source(file, json, "features/" + feature.Name));
                }
            }
            else
            {
                builder.Issue(Source(file, json, "features"), DeclaredIssueKind.NotInterpreted);
            }
        }
    }

    private static bool Accepted(ManifestJson json, string file, DeclaredReadingBuilder builder)
    {
        if (json.Failure is { } failure)
        {
            builder.Issue(new DeclaredSource(file), failure);
            return false;
        }

        if (json.Document!.RootElement.ValueKind != JsonValueKind.Object)
        {
            builder.Issue(new DeclaredSource(file), DeclaredIssueKind.Malformed);
            return false;
        }

        return true;
    }

    private static DeclaredSource Source(string file, ManifestJson json, string path) => new(file, json.LineOf(path));

    /// <summary>The string property <paramref name="property"/> of <paramref name="parent"/> as an entry; absent is nothing, and another type is an issue.</summary>
    private static void Text(JsonElement parent, string property, string path, ManifestJson json, string file, DeclaredKind kind, DeclaredReadingBuilder builder)
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            return;
        }

        var source = Source(file, json, path);
        if (value.ValueKind == JsonValueKind.String)
        {
            builder.Entry(kind, null, value.GetString(), source);
        }
        else
        {
            builder.Issue(source, DeclaredIssueKind.NotInterpreted);
        }
    }

    // ---- text ----

    /// <summary>The lines of a text, from 1. A line over <see cref="MaxLineLength"/> characters ends the reading and is reported once.</summary>
    private static IEnumerable<(int Number, string Text)> Lines(string text, string file, DeclaredReadingBuilder builder)
    {
        var number = 0;
        var start = 0;
        while (start <= text.Length)
        {
            var end = text.AsSpan(start).IndexOfAny('\n', '\r');
            var length = end < 0 ? text.Length - start : end;
            number++;
            if (length > MaxLineLength)
            {
                builder.Issue(new DeclaredSource(file, number), DeclaredIssueKind.LineTooLong);
                yield break;
            }

            if (end < 0)
            {
                if (length > 0)
                {
                    yield return (number, text.Substring(start, length));
                }

                yield break;
            }

            yield return (number, text.Substring(start, length));
            start += length + (text[start + length] == '\r' && start + length + 1 < text.Length && text[start + length + 1] == '\n' ? 2 : 1);
        }
    }

    private static void ParseFirstLine(string file, string text, DeclaredKind kind, DeclaredReadingBuilder builder)
    {
        var issues = builder.Issues.Count;
        foreach (var (number, line) in Lines(text, file, builder))
        {
            builder.Entry(kind, null, line, new DeclaredSource(file, number));
            return;
        }

        // A line that was too long has already been reported; an empty file says nothing else.
        if (builder.Issues.Count == issues)
        {
            builder.Issue(new DeclaredSource(file), DeclaredIssueKind.NotInterpreted);
        }
    }

    private static void ParseVenv(string text, DeclaredReadingBuilder builder)
    {
        const string file = DeclaredFiles.PyVenvCfg;
        var found = false;
        foreach (var (number, raw) in Lines(text, file, builder))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                builder.Issue(new DeclaredSource(file, number), DeclaredIssueKind.NotInterpreted, line);
                continue;
            }

            if (!found && line[..equals].Trim() == "version")
            {
                found = true;
                builder.Entry(DeclaredKind.VenvVersion, null, line[(equals + 1)..], new DeclaredSource(file, number));
            }
        }

        if (!found)
        {
            builder.Issue(new DeclaredSource(file), DeclaredIssueKind.NotInterpreted);
        }
    }

    private static void ParseDockerfile(string text, DeclaredReadingBuilder builder)
    {
        const string file = DeclaredFiles.Dockerfile;
        var continued = false;
        foreach (var (number, raw) in Lines(text, file, builder))
        {
            var line = raw.Trim();
            var wasContinued = continued;
            continued = line.EndsWith('\\');
            if (wasContinued || line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!words[0].Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = words.Skip(1).Where(w => !w.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var source = new DeclaredSource(file, number);
            if (rest.Length == 0)
            {
                builder.Issue(source, DeclaredIssueKind.NotInterpreted, line);
                continue;
            }

            var alias = rest.Length >= 3 && rest[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? rest[2] : null;
            builder.Entry(DeclaredKind.ContainerImage, alias, rest[0], source);
        }
    }

    private static void ParsePyProject(string text, DeclaredReadingBuilder builder)
    {
        const string file = DeclaredFiles.PyProject;
        var inProject = false;
        string? multiline = null;
        var found = false;
        foreach (var (number, raw) in Lines(text, file, builder))
        {
            if (multiline is not null)
            {
                if (raw.Contains(multiline, StringComparison.Ordinal))
                {
                    multiline = null;
                }

                continue;
            }

            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (TableName(line) is { } table)
            {
                inProject = table == "project";
                continue;
            }

            if (inProject && !found && IsRequiresPythonKey(line, out var valueStart))
            {
                found = true;
                ReadRequiresPython(line[valueStart..], new DeclaredSource(file, number), builder);
            }

            multiline = OpensMultiline(line);
        }
    }

    private static void ReadRequiresPython(string value, DeclaredSource source, DeclaredReadingBuilder builder)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] is '"' or '\'' && !text.StartsWith("\"\"\"", StringComparison.Ordinal) && !text.StartsWith("'''", StringComparison.Ordinal))
        {
            var quote = text[0];
            var close = text.IndexOf(quote, 1);
            var rest = close < 0 ? "x" : text[(close + 1)..].Trim();
            var inner = close < 0 ? string.Empty : text[1..close];
            if (close > 0 && (rest.Length == 0 || rest[0] == '#') && !(quote == '"' && inner.Contains('\\', StringComparison.Ordinal)))
            {
                builder.Entry(DeclaredKind.PythonRequires, null, inner, source);
                return;
            }
        }

        builder.Issue(source, DeclaredIssueKind.NotInterpreted, text);
    }

    private static string? OpensMultiline(string line)
    {
        foreach (var delimiter in new[] { "\"\"\"", "'''" })
        {
            var count = 0;
            for (var at = line.IndexOf(delimiter, StringComparison.Ordinal); at >= 0; at = line.IndexOf(delimiter, at + 3, StringComparison.Ordinal))
            {
                count++;
            }

            if (count % 2 == 1)
            {
                return delimiter;
            }
        }

        return null;
    }

    /// <summary>
    /// The name of the table a line opens (<c>project</c>, <c>tool.black</c>), or null when the line is not a table header: <c>[name]</c> or <c>[[name]]</c> with
    /// only letters, digits, <c>_ . - " '</c> and spaces inside, and nothing after the closing bracket but a comment. A line of an array of arrays is not one.
    /// </summary>
    private static string? TableName(string line)
    {
        var close = line.LastIndexOf(']');
        if (line.Length < 3 || line[0] != '[' || close < 0)
        {
            return null;
        }

        var after = line.AsSpan(close + 1).Trim();
        if (after.Length > 0 && after[0] != '#')
        {
            return null;
        }

        var inner = line.AsSpan(0, close + 1);
        var isArrayTable = inner.StartsWith("[[", StringComparison.Ordinal);
        if (isArrayTable && (inner.Length < 5 || !inner.EndsWith("]]", StringComparison.Ordinal)))
        {
            return null;
        }

        var name = isArrayTable ? inner[2..^2] : inner[1..^1];

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '"' or '\'' or ' '))
            {
                return null;
            }
        }

        return isArrayTable ? "[[" + name.Trim().ToString() + "]]" : name.Trim().ToString();
    }

    private static bool IsRequiresPythonKey(string line, out int valueStart)
    {
        valueStart = 0;
        foreach (var key in new[] { "requires-python", "\"requires-python\"", "'requires-python'" })
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                var rest = line.AsSpan(key.Length).TrimStart();
                if (rest.Length > 0 && rest[0] == '=')
                {
                    valueStart = line.Length - rest.Length + 1;
                    return true;
                }
            }
        }

        return false;
    }
}
