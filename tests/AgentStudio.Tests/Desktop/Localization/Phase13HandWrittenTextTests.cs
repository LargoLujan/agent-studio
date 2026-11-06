using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>
/// F13-19 (docs/17 D-51 and D-65): nothing written by hand in what Phase 13 adds to the desktop. Every text the person reads
/// comes from the resources, in Spanish of Spain and in English, so what is born in Phase 13 is translatable from the start;
/// Phase 14 extends the same check to the whole desktop.
/// </summary>
/// <remarks>
/// <para>It goes by convention over the files of Phase 13: every XAML and C# file in the folders the phase creates
/// (<see cref="Phase13Folders"/>, docs/17 §9.3) and the new files a package adds to a folder of before
/// (<see cref="Phase13Files"/>: each package lists its own there).</para>
/// <list type="bullet">
/// <item>XAML: no visible text written as a literal: neither the attributes that show text (<c>Text</c>, <c>Content</c>,
/// <c>Header</c>, <c>ToolTip</c>, <c>Title</c>, <c>AutomationProperties.Name</c>, <c>HelpText</c>, <c>ItemStatus</c>…) nor the
/// text of an element. A value with a letter must be a markup extension (<c>{l:T Key}</c>, a binding). Symbols and icon glyphs
/// are not text.</item>
/// <item>C#: no string literal with a letter of Spanish (á, é, í, ó, ú, ü, ñ, ¿, ¡) or of any other language beyond ASCII; and
/// no literal with a letter assigned to a visible property (<c>.Text = "…"</c>) or given as an accessible name
/// (<c>AutomationProperties.SetName(x, "…")</c>). Comments are not code: they are not read. Exception messages and logs are in
/// English, as CLAUDE.md asks, and pass.</item>
/// <item>Every key a scanned file names literally (<c>{l:T Key}</c>, <c>Get("Key")</c>, <c>Format("Key", …)</c>) exists in the
/// resources.</item>
/// </list>
/// <para>What it cannot see: a Spanish sentence written only with ASCII letters and built outside those assignments (in a
/// collection, a format). The review and the renders per language of Phase 14 catch those.</para>
/// </remarks>
public sealed partial class Phase13HandWrittenTextTests
{
    /// <summary>The folders of the desktop that are new in Phase 13 (docs/17 §9.3), relative to <c>src/AgentStudio.Desktop</c>. One that does not exist yet is skipped.</summary>
    private static readonly string[] Phase13Folders =
    [
        "Localization", "Controls/Info", "Orchestrator/Live", "Orchestrator/Team", "Orchestrator/Questions", "Setup", "Observability/Analysis",
        "Panels", "Tools", "Views/Setup", "Views/Orchestrator/Team", "Views/Orchestrator/Questions", "Views/Observability/Analysis",
        "Views/Panels", "Views/Tools",
    ];

    /// <summary>
    /// The new files of Phase 13 in folders of before, relative to <c>src/AgentStudio.Desktop</c>: each package of the phase
    /// adds the files it creates in a folder that existed (<c>Orchestrator/Canvas/…</c>, <c>Views/Orchestrator/…</c>).
    /// </summary>
    private static readonly string[] Phase13Files = [];

    /// <summary>Literals allowed in the files of Phase 13, by file and literal, each with why. An exception nobody uses fails, so the list never goes stale.</summary>
    private static readonly Dictionary<(string File, string Literal), string> Exceptions = new()
    {
        [("Localization/UiLanguages.cs", "Español (España)")] =
            "D-49: each language is named in its own language in the list of languages, and that name is never translated.",
    };

    /// <summary>The attributes of XAML that show text to the person, or say it to a screen reader.</summary>
    private static readonly HashSet<string> VisibleTextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "ToolTip", "Title", "Watermark", "PlaceholderText", "Label", "Description",
        "AutomationProperties.Name", "AutomationProperties.HelpText", "AutomationProperties.ItemStatus", "AutomationProperties.AcceleratorKey",
    };

    [GeneratedRegex("""(?:\.|\b)(?:Text|Content|Header|ToolTip|Title)\s*=\s*"([^"]*)""")]
    private static partial Regex VisibleAssignment();

    [GeneratedRegex("""AutomationProperties\.Set(?:Name|HelpText|ItemStatus)\([^,]+,\s*"([^"]*)""")]
    private static partial Regex AccessibleAssignment();

    [GeneratedRegex("""\{(?:l:)?T\s+(?:Key=)?([A-Za-z][A-Za-z0-9_]*)\s*\}""")]
    private static partial Regex XamlKey();

    [GeneratedRegex("""\b(?:Get|Format|Has)\(\s*"([A-Za-z][A-Za-z0-9_]*)"\s*[,)]""")]
    private static partial Regex CodeKey();

    private static IReadOnlyList<string> ScannedFiles()
    {
        var files = new List<string>();
        foreach (var folder in Phase13Folders.Select(f => Path.Combine(DesktopRoot, f)).Where(Directory.Exists))
        {
            files.AddRange(Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)));
        }

        files.AddRange(Phase13Files.Select(f => Path.Combine(DesktopRoot, f)));
        return [.. files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
    }

    private static string Relative(string file) => Path.GetRelativePath(DesktopRoot, file).Replace('\\', '/');

    private static HashSet<string> ResourceKeys() =>
        ResourceFiles().Values.SelectMany(f => Read(f.Neutral).Keys).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void The_check_covers_the_foundations_of_phase_13_and_every_listed_file_exists()
    {
        var scanned = ScannedFiles().Select(Relative).ToList();

        Assert.Contains("Localization/ResourceTextProvider.cs", scanned);
        Assert.Contains("Controls/Info/InfoButton.xaml", scanned);
        Assert.Contains("Orchestrator/Live/IRunLiveProjection.cs", scanned);
        Assert.All(Phase13Files, file => Assert.True(File.Exists(Path.Combine(DesktopRoot, file)), $"{file} is listed as a file of Phase 13 and does not exist."));
    }

    [Fact]
    public void Nothing_the_person_reads_is_written_by_hand_in_the_files_of_phase_13()
    {
        var used = new HashSet<(string, string)>();
        var problems = new List<string>();
        foreach (var file in ScannedFiles())
        {
            var relative = Relative(file);
            var found = file.EndsWith(".xaml", StringComparison.Ordinal) ? XamlLiterals(file) : CodeLiterals(File.ReadAllText(file));
            foreach (var literal in found)
            {
                if (Exceptions.ContainsKey((relative, literal)))
                {
                    used.Add((relative, literal));
                    continue;
                }

                problems.Add($"{relative}: «{literal}»");
            }
        }

        Assert.True(problems.Count == 0, "Texts written by hand (put them in Resources/*Strings.resx):\n" + string.Join('\n', problems));
        Assert.All(Exceptions.Keys, exception => Assert.True(used.Contains(exception), $"The exception {exception} is not used any more: remove it."));
    }

    [Fact]
    public void Every_key_the_files_of_phase_13_name_exists_in_the_resources()
    {
        var keys = ResourceKeys();
        var missing = new List<string>();
        foreach (var file in ScannedFiles())
        {
            var text = File.ReadAllText(file);
            var named = file.EndsWith(".xaml", StringComparison.Ordinal)
                ? XamlKey().Matches(text).Select(m => m.Groups[1].Value)
                : CodeKey().Matches(StripComments(text)).Select(m => m.Groups[1].Value);
            missing.AddRange(named.Where(k => !keys.Contains(k)).Select(k => $"{Relative(file)}: {k}"));
        }

        Assert.True(missing.Count == 0, "Keys that are not in the resources:\n" + string.Join('\n', missing));
    }

    /// <summary>The checks themselves: a literal in a visible attribute, the text of an element and a Spanish literal in code are found; a glyph, a key and a comment are not.</summary>
    [Fact]
    public void The_checks_find_what_is_written_by_hand_and_nothing_else()
    {
        var folder = Path.Combine(Path.GetTempPath(), "as-f13-19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var xaml = Path.Combine(folder, "Sample.xaml");
            File.WriteAllText(xaml, """
                <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:l="clr-namespace:X">
                  <!-- Un comentario con texto. -->
                  <StackPanel>
                    <TextBlock Text="Aprobar el plan"/>
                    <TextBlock Text="{l:T Info_WhatIsThis}"/>
                    <TextBlock Text="&#xE946;"/>
                    <Button AutomationProperties.Name="Cerrar"/>
                    <TextBlock>Hola</TextBlock>
                    <TextBlock Text="%"/>
                  </StackPanel>
                </UserControl>
                """);

            Assert.Equal(["Aprobar el plan", "Cerrar", "Hola"], XamlLiterals(xaml));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        var code = """
            // «Información» in a comment is not code.
            /* Ni «esto». */
            var a = "Información sobre el plan";
            var b = 'á';
            var c = $"Trabajando en {task}";
            label.Text = "Libre";
            AutomationProperties.SetName(button, "Close");
            var d = "The run '{0}' is in use.";
            var e = @"C:\carpeta";
            var f = texts.Get("Info_WhatIsThis");
            var g = $"{(ok ? "sí" : "no")} y más";
            """;

        Assert.Equal(["Información sobre el plan", "Libre", "Close", " y más"], CodeLiterals(code));
    }

    // ---- XAML ----

    private static IReadOnlyList<string> XamlLiterals(string file)
    {
        var literals = new List<string>();
        foreach (var element in XDocument.Load(file).Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                if (VisibleTextAttributes.Contains(name) && !attribute.Value.StartsWith('{') && attribute.Value.Any(char.IsLetter))
                {
                    literals.Add(attribute.Value);
                }
            }

            foreach (var text in element.Nodes().OfType<XText>().Where(t => t is not XCData))
            {
                if (text.Value.Trim() is { Length: > 0 } value && value.Any(char.IsLetter))
                {
                    literals.Add(value);
                }
            }
        }

        return literals;
    }

    // ---- C# ----

    /// <summary>
    /// The literals of <paramref name="code"/> a person would read: those with a letter beyond ASCII, and those with a letter
    /// assigned to a visible property or given as an accessible name. In the order they are found, once each.
    /// </summary>
    private static IReadOnlyList<string> CodeLiterals(string code)
    {
        var withoutComments = StripComments(code);
        var found = new List<string>();
        foreach (var literal in StringLiterals(withoutComments))
        {
            if (literal.Any(c => c > 127 && char.IsLetter(c)) || literal.Any(c => c is '¿' or '¡'))
            {
                found.Add(literal);
            }
        }

        foreach (Match match in VisibleAssignment().Matches(withoutComments))
        {
            found.Add(match.Groups[1].Value);
        }

        foreach (Match match in AccessibleAssignment().Matches(withoutComments))
        {
            found.Add(match.Groups[1].Value);
        }

        return [.. found.Where(l => l.Any(char.IsLetter)).Distinct(StringComparer.Ordinal).OrderBy(l => withoutComments.IndexOf(l, StringComparison.Ordinal))];
    }

    /// <summary>The code without its comments; strings and characters are kept as they are, so a <c>//</c> inside one is not a comment.</summary>
    private static string StripComments(string code)
    {
        var result = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }
            }
            else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 2;
            }
            else if (code[i] is '"' or '\'')
            {
                var start = i;
                i = SkipLiteral(code, i);
                result.Append(code, start, i - start);
            }
            else
            {
                result.Append(code[i]);
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>The text of the string literals of code without comments: regular, verbatim, raw and the text parts of interpolated ones.</summary>
    private static IEnumerable<string> StringLiterals(string code)
    {
        var i = 0;
        while (i < code.Length)
        {
            if (code[i] == '\'')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (code[i] != '"')
            {
                i++;
                continue;
            }

            var end = SkipLiteral(code, i);
            var prefix = 0;
            while (i - prefix - 1 >= 0 && code[i - prefix - 1] is '@' or '$')
            {
                prefix++;
            }

            var text = code[i..end];
            yield return Unquote(text, code.Substring(i - prefix, prefix).Contains('$', StringComparison.Ordinal));
            i = end;
        }
    }

    /// <summary>Where the literal that starts at <paramref name="start"/> (a quote) ends, just after its closing quote.</summary>
    private static int SkipLiteral(string code, int start)
    {
        var quote = code[start];
        var verbatim = start > 0 && (code[start - 1] == '@' || (start > 1 && code[start - 1] == '$' && code[start - 2] == '@'));
        var run = 0;
        while (start + run < code.Length && code[start + run] == '"' && quote == '"')
        {
            run++;
        }

        if (run >= 3)
        {
            var close = code.IndexOf(new string('"', run), start + run, StringComparison.Ordinal);
            return close < 0 ? code.Length : close + run;
        }

        if (run == 2)
        {
            return start + 2;
        }

        var interpolated = quote == '"' && start > 0 && (code[start - 1] == '$' || (start > 1 && code[start - 1] == '@' && code[start - 2] == '$'));
        var depth = 0;
        var i = start + 1;
        while (i < code.Length)
        {
            if (interpolated && code[i] == '{')
            {
                if (depth == 0 && i + 1 < code.Length && code[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                depth++;
                i++;
                continue;
            }

            if (interpolated && depth > 0)
            {
                if (code[i] == '}')
                {
                    depth--;
                    i++;
                }
                else if (code[i] is '"' or '\'')
                {
                    i = SkipLiteral(code, i);
                }
                else
                {
                    i++;
                }

                continue;
            }

            if (!verbatim && code[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (code[i] == quote)
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (code[i] == '\n' && quote == '"' && !verbatim)
            {
                return i;
            }

            i++;
        }

        return code.Length;
    }

    /// <summary>The text of a literal without its quotes; for an interpolated one, without its holes.</summary>
    private static string Unquote(string literal, bool interpolated)
    {
        var run = 0;
        while (run < literal.Length && literal[run] == '"')
        {
            run++;
        }

        var body = run >= 3 ? literal[run..Math.Max(run, literal.Length - run)] : literal.Length >= 2 ? literal[1..^1] : string.Empty;
        if (!interpolated)
        {
            return body;
        }

        var text = new StringBuilder();
        var depth = 0;
        foreach (var c in body)
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
