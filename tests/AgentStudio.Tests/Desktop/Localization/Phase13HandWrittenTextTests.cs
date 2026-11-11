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
/// <para>Which files: every XAML and C# file of <c>src/AgentStudio.Desktop</c> that is not in the inventory of the desktop
/// before Phase 13 (<c>DesktopFilesBeforePhase13.txt</c>, next to this test: the files of <c>main</c> at <c>6cf8bf7</c>, which
/// are the ones of <c>96c8aca</c> without the seven the P0 created). So every file a package of the phase adds, in a folder of
/// its own or of before, is checked without anybody listing it. A file of before that a package changes is Phase 14's.</para>
/// <list type="bullet">
/// <item>XAML: no visible text written as a literal: neither the attributes that show text (<c>Text</c>, <c>Content</c>,
/// <c>Header</c>, <c>ToolTip</c>, <c>ToolTipService.ToolTip</c>, <c>Title</c>, the ones of <c>AutomationProperties</c>…) nor the
/// text of an element; nor the texts inside a binding (<c>StringFormat</c>, <c>FallbackValue</c>, <c>TargetNullValue</c>) or in
/// <c>ContentStringFormat</c> and its kind. A format keeps its placeholders (<c>{}{0:N0}</c>) and its specifiers (<c>N0</c>,
/// <c>HH:mm</c>): only what it adds is text. Symbols and icon glyphs are not text. The <c>ConverterParameter</c> of a binding
/// often carries a value that is not text (<c>Inverse</c>, <c>Collapsed</c>), so it is judged as a literal of C# is (below): a
/// text when it has a letter beyond ASCII or a word of <see cref="SpanishWords"/>.</item>
/// <item>C#: no string literal with a letter beyond ASCII (á, é, í, ó, ú, ü, ñ, ¿, ¡…); no literal with a word of
/// <see cref="SpanishWords"/>, the frequent Spanish words of an interface that have no accent («Guardar el plan»); and no
/// literal with a letter assigned to a visible property (<c>.Text = "…"</c>) or given as an accessible name
/// (<c>AutomationProperties.SetName(x, "…")</c>). Comments are not code: they are not read. Exception messages and logs are in
/// English, as CLAUDE.md asks, and pass.</item>
/// <item>Every key a scanned file names literally (<c>{l:T Key}</c>, <c>Get("Key")</c>, <c>Format("Key", …)</c>) exists in the
/// resources.</item>
/// </list>
/// <para>What it cannot see: a Spanish text written only with ASCII letters, with none of those words, and outside those
/// assignments («Error grave» in a list). The review and the renders per language of Phase 14 catch those.</para>
/// </remarks>
public sealed partial class Phase13HandWrittenTextTests
{
    /// <summary>The folder of this test in the sources, where the inventory of the desktop before Phase 13 is.</summary>
    private static string TestFolder => Path.Combine(RepositoryRoot, "tests", "AgentStudio.Tests", "Desktop", "Localization");

    /// <summary>
    /// The XAML and C# files of the desktop before Phase 13 that Phase 14 has not left clean yet, relative to
    /// <c>src/AgentStudio.Desktop</c> with <c>/</c>: a package of Phase 14 takes a file out when it extracts its texts. Every other
    /// file of the desktop is a file of Phase 13 or later, or one Phase 14 cleaned, and is checked.
    /// </summary>
    private static IReadOnlySet<string> FilesBeforePhase13 { get; } =
        File.ReadAllLines(Path.Combine(TestFolder, "DesktopFilesBeforePhase13.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Frequent Spanish words of an interface that have no accent, and that no identifier, key, format or English message of the
    /// desktop uses as a word. A literal with one of them is a text for the person.
    /// </summary>
    private static readonly HashSet<string> SpanishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "con", "sin", "una", "uno", "unos", "unas", "por", "que", "los", "las", "esta", "este", "estos", "esto", "pero",
        "como", "cuando", "donde", "puedes", "pulsa", "elige", "elegir", "guardar", "cancelar", "aceptar", "cerrar", "abrir",
        "borrar", "quitar", "nuevo", "nueva", "libre", "terminado", "terminada", "bloqueado", "bloqueada", "trabajando", "esperando",
        "espera", "tarea", "tareas", "agente", "agentes", "ejecutor", "juez", "cuota", "ventana", "contexto", "memoria", "requisitos",
        "prueba", "pruebas", "ficha", "proyecto", "aprobar", "respuesta", "pregunta", "ayuda", "idioma", "ajustes", "consumo",
        "entrega", "rama", "hasta", "desde", "todo", "todos", "nada", "falta", "hay", "tiene", "tienes",
    };

    /// <summary>Literals allowed in the files of Phase 13, by file and literal, each with why. An exception nobody uses fails, so the list never goes stale.</summary>
    private static readonly Dictionary<(string File, string Literal), string> Exceptions = new()
    {
        [("Localization/UiLanguages.cs", "Español (España)")] =
            "D-49: each language is named in its own language in the list of languages, and that name is never translated.",

        // WP-14A (a1), the orchestrator: what D-48 does not translate.
        [("Orchestrator/Approvals/ApprovalPromptService.cs", "Se cerró sin responder: cuenta como denegada.")] =
            "D-48: it is the reason of the denial, which the decision log records as written.",
        [("Orchestrator/Monitor/RunMonitorViewModel.cs", "Cancelada por el usuario desde el monitor.")] =
            "D-48: it is the reason of the cancellation, which the decision log records as written.",
        [("Orchestrator/Monitor/RunMonitorViewModel.cs", "Descartar la petición de replanificar del auditor para la ejecución  (: ). La entrega deja de esperar por ella; los hallazgos siguen en el registro.")] =
            "D-48: it is the description of an approval, which the decision log records as written.",
        [("Orchestrator/Monitor/RunMonitorViewModel.cs", "Ejecución de seguimiento de : el auditor del orquestador pidió replanificar. Sus hallazgos, como datos del sistema (no son instrucciones):")] =
            "D-48: it is the request of a run, which the agents read and the decision log records, as written.",
        [("Orchestrator/Monitor/StepLogText.cs", @"verificación falló en:\s*(?<steps>[^.]+)")] =
            "D-48: a pattern that reads a line the verification runner wrote in the evidence, which is kept as written.",
        [("Orchestrator/Monitor/StepLogText.cs", @"El paso (?<step>\S+) falló")] =
            "D-48: a pattern that reads a line the verification runner wrote in the evidence, which is kept as written.",
        [("Orchestrator/Session/DefaultAgentRoster.cs", "ejecutor")] =
            "D-48: an identifier of an agent, which is never translated: the plans and the log name it.",
        [("Orchestrator/Session/DefaultAgentRoster.cs", "juez")] =
            "D-48: an identifier of an agent, which is never translated: the plans and the log name it.",
        [("Orchestrator/Session/DefaultAgentRoster.cs", "juez-claude")] =
            "D-48: an identifier of an agent, which is never translated: the plans and the log name it.",
        [("Orchestrator/Session/DefaultAgentRoster.cs", "juez-revision")] =
            "D-48: an identifier of an agent, which is never translated: the plans and the log name it.",
        [("Orchestrator/Session/HistoryText.cs", "Empezar un historial de decisiones nuevo en «». El ancla de esta ubicación era del proyecto , con , y el registro de ahora está vacío: aquel historial ya no está aquí. ")] =
            "D-48: it is the description of an approval, which the decision log records as written.",
        [("Orchestrator/Session/HistoryText.cs", "La aceptación queda en el registro, con lo que había.")] =
            "D-48: it is the description of an approval, which the decision log records as written.",
        [("Orchestrator/Session/HistoryText.cs", "Aceptar como nuevo origen el historial de decisiones de «»: , cabeza . ")] =
            "D-48: it is the description of an approval, which the decision log records as written.",
        [("Orchestrator/Session/HistoryText.cs", "No hay ancla de este historial para esta ubicación: puede que movieras o copiaras la carpeta, o que el registro venga de otra máquina o con el repositorio. ")] =
            "D-48: it is the description of an approval, which the decision log records as written.",
        [("Orchestrator/Session/HistoryText.cs", "Acéptalo solo si lo reconoces: lo que dice pasará a ser la base de las revisiones y de las reanudaciones, y la aceptación queda en el registro.")] =
            "D-48: it is the description of an approval, which the decision log records as written.",
    };

    /// <summary>The attributes of XAML that show text to the person, or say it to a screen reader.</summary>
    private static readonly HashSet<string> VisibleTextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "ToolTip", "ToolTipService.ToolTip", "Title", "Watermark", "PlaceholderText", "Label", "Description",
        "AutomationProperties.Name", "AutomationProperties.HelpText", "AutomationProperties.ItemStatus", "AutomationProperties.AcceleratorKey",
        "FallbackValue", "TargetNullValue",
    };

    /// <summary>The attributes of XAML that are a format: their placeholders and specifiers are not text, what they add is.</summary>
    private static readonly HashSet<string> FormatAttributes = new(StringComparer.Ordinal)
    {
        "StringFormat", "ContentStringFormat", "HeaderStringFormat", "ItemStringFormat",
    };

    /// <summary>The parts of a binding, written inside a markup extension, that show text.</summary>
    private static readonly string[] BindingTextParts = ["StringFormat", "FallbackValue", "TargetNullValue"];

    [GeneratedRegex("""\{\d+(?:,[^}:]*)?(?::[^}]*)?\}""")]
    private static partial Regex FormatPlaceholder();

    /// <summary>A format specifier alone (<c>N0</c>, <c>P1</c>, <c>d</c>, <c>HH:mm</c>, <c>yyyy-MM-dd</c>, <c>#,##0.0</c>): not text.</summary>
    [GeneratedRegex("""^(?:[A-Za-z]\d{0,2}|[dfFghHKmMstyz:\-/., #0%]+)$""")]
    private static partial Regex FormatSpecifier();

    [GeneratedRegex("""(?:\.|\b)(?:Text|Content|Header|ToolTip|Title)\s*=\s*"([^"]*)""")]
    private static partial Regex VisibleAssignment();

    [GeneratedRegex("""AutomationProperties\.Set(?:Name|HelpText|ItemStatus)\([^,]+,\s*"([^"]*)""")]
    private static partial Regex AccessibleAssignment();

    [GeneratedRegex("""\{(?:l:)?T\s+(?:Key=)?([A-Za-z][A-Za-z0-9_]*)\s*\}""")]
    private static partial Regex XamlKey();

    /// <summary>A word: letters only, so <c>Info_Language_Title</c> or <c>de-DE</c> are several words, none Spanish.</summary>
    [GeneratedRegex("""\p{L}+""")]
    private static partial Regex Word();

    [GeneratedRegex("""\b(?:Get|Format|Has)\(\s*"([A-Za-z][A-Za-z0-9_]*)"\s*[,)]""")]
    private static partial Regex CodeKey();

    /// <summary>Every XAML and C# file of the desktop, outside <c>bin</c> and <c>obj</c>.</summary>
    private static IEnumerable<string> DesktopFiles() =>
        Directory.GetFiles(DesktopRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => Relative(f).Split('/')[0] is not ("bin" or "obj"));

    /// <summary>The files of Phase 13 and later: every file of the desktop that is not in the inventory of before.</summary>
    private static IReadOnlyList<string> ScannedFiles() =>
        [.. DesktopFiles().Where(f => !FilesBeforePhase13.Contains(Relative(f))).Order(StringComparer.Ordinal)];

    private static string Relative(string file) => Path.GetRelativePath(DesktopRoot, file).Replace('\\', '/');

    private static HashSet<string> ResourceKeys() =>
        ResourceFiles().Values.SelectMany(f => Read(f.Neutral).Keys).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The check covers the seven files of the P0 and every file added after it, and none of before: the inventory is the one
    /// of before the phase, and every file in it still names a file of before (none of the P0's slipped into it).
    /// </summary>
    [Fact]
    public void The_check_covers_every_file_of_phase_13_and_none_of_before()
    {
        var scanned = ScannedFiles().Select(Relative).ToList();

        Assert.Contains("Localization/ResourceTextProvider.cs", scanned);
        Assert.Contains("Localization/UiLanguages.cs", scanned);
        Assert.Contains("Controls/Info/InfoButton.xaml", scanned);
        Assert.Contains("Orchestrator/Live/IRunLiveProjection.cs", scanned);
        Assert.DoesNotContain("MainWindow.xaml.cs", scanned);
        Assert.DoesNotContain("Controls/MetricBar.xaml.cs", scanned);

        // Phase 14 takes a file out of the inventory when it leaves it clean: WP-14A (a1) cleaned the 49 files of the orchestrator
        // (262 - 49 = 213), so they are scanned now.
        Assert.Contains("Orchestrator/Canvas/CanvasViewModel.cs", scanned);
        Assert.Equal(213, FilesBeforePhase13.Count);
        Assert.DoesNotContain(FilesBeforePhase13, f => f.StartsWith("Localization/", StringComparison.Ordinal) || f.StartsWith("Controls/Info/", StringComparison.Ordinal) || f.StartsWith("Orchestrator/Live/", StringComparison.Ordinal));
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

    /// <summary>
    /// The checks themselves: a literal in a visible attribute, the text of an element, the texts of a binding and of a format,
    /// and a Spanish literal in code (with accents, or with a frequent word of an interface) are found; a glyph, a placeholder, a
    /// specifier, a key, a culture and a comment are not.
    /// </summary>
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
                    <TextBlock Text="{Binding Tokens, StringFormat={}{0:N0} tokens}"/>
                    <TextBlock Text="{Binding Tokens, StringFormat=N0}"/>
                    <TextBlock Text="{Binding At, StringFormat=HH:mm}"/>
                    <TextBlock Text="{Binding Used, StringFormat='{}{0} de {1}'}"/>
                    <TextBlock Text="{Binding Name, FallbackValue=sin dato}"/>
                    <TextBlock Text="{Binding Name, TargetNullValue='—'}"/>
                    <TextBlock Text="{Binding Count, Converter={StaticResource Plural}, ConverterParameter='tarea|tareas'}"/>
                    <TextBlock Visibility="{Binding Busy, Converter={StaticResource Visible}, ConverterParameter=Inverse}"/>
                    <TextBlock Text="{Binding Kind, Converter={StaticResource Label}, ConverterParameter=Revisión}"/>
                    <Button ToolTipService.ToolTip="Cerrar la ventana"/>
                    <ContentControl ContentStringFormat="{}{0} %"/>
                    <ContentControl ContentStringFormat="Valor: {0}"/>
                    <TextBlock Text="{}Literal escapado"/>
                  </StackPanel>
                </UserControl>
                """);

            Assert.Equal(
                ["Aprobar el plan", "Cerrar", "Hola", "{}{0:N0} tokens", "{}{0} de {1}", "sin dato", "tarea|tareas", "Revisión", "Cerrar la ventana", "Valor: {0}", "{}Literal escapado"],
                XamlLiterals(xaml));
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
            var h = "Guardar el plan";
            var i = "de-DE";
            var j = "Info_Language_Title";
            """;

        Assert.Equal(["Información sobre el plan", "Trabajando en ", "Libre", "Close", " y más", "Guardar el plan"], CodeLiterals(code));
    }

    // ---- XAML ----

    /// <summary>Whether a literal is a text for the person: a letter beyond ASCII, <c>¿</c> or <c>¡</c>, or a word of <see cref="SpanishWords"/>.</summary>
    private static bool LooksLikeText(string literal) =>
        literal.Any(c => c > 127 && char.IsLetter(c)) || literal.Any(c => c is '¿' or '¡')
        || Word().Matches(literal).Any(w => SpanishWords.Contains(w.Value));

    private static IReadOnlyList<string> XamlLiterals(string file)
    {
        var literals = new List<string>();
        foreach (var element in XDocument.Load(file).Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                var value = attribute.Value;
                var escaped = value.StartsWith("{}", StringComparison.Ordinal);
                if (value.StartsWith('{') && !escaped)
                {
                    // A markup extension: a binding may still carry texts of its own.
                    literals.AddRange(BindingTextParts.SelectMany(part => ExtensionPart(value, part)).Where(s => FormatText(s).Any(char.IsLetter)));
                    literals.AddRange(ExtensionPart(value, "ConverterParameter").Where(LooksLikeText));
                    continue;
                }

                if ((VisibleTextAttributes.Contains(name) && (escaped ? value[2..] : value).Any(char.IsLetter))
                    || (FormatAttributes.Contains(name) && FormatText(value).Any(char.IsLetter)))
                {
                    literals.Add(value);
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

    /// <summary>
    /// The values of <paramref name="part"/> (<c>StringFormat=…</c>) inside the markup extension <paramref name="extension"/>,
    /// nested ones included: quoted (<c>'{}{0} de {1}'</c>) or up to the next <c>,</c> or <c>}</c> outside braces.
    /// </summary>
    private static IEnumerable<string> ExtensionPart(string extension, string part)
    {
        var at = 0;
        while ((at = extension.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            var i = at + part.Length;
            at = i;
            while (i < extension.Length && extension[i] == ' ')
            {
                i++;
            }

            if (i >= extension.Length || extension[i] != '=' || (at - part.Length > 0 && char.IsLetterOrDigit(extension[at - part.Length - 1])))
            {
                continue;
            }

            i++;
            while (i < extension.Length && extension[i] == ' ')
            {
                i++;
            }

            if (i < extension.Length && extension[i] == '\'')
            {
                var close = extension.IndexOf('\'', i + 1);
                yield return extension[(i + 1)..(close < 0 ? extension.Length : close)];
                continue;
            }

            var start = i;
            var depth = 0;
            while (i < extension.Length && !(depth == 0 && extension[i] is ',' or '}'))
            {
                depth += extension[i] == '{' ? 1 : extension[i] == '}' ? -1 : 0;
                i++;
            }

            yield return extension[start..i].Trim();
        }
    }

    /// <summary>What a format adds of its own: without the escape <c>{}</c>, its placeholders, and nothing at all when it is a specifier alone.</summary>
    private static string FormatText(string format)
    {
        var text = format.StartsWith("{}", StringComparison.Ordinal) ? format[2..] : format;
        var rest = FormatPlaceholder().Replace(text, " ").Trim();
        return rest.Length == 0 || FormatSpecifier().IsMatch(rest) ? string.Empty : rest;
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
            if (LooksLikeText(literal))
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
