using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Graph;
using AgentStudio.Memory.Diagrams;

namespace AgentStudio.Tests.Memory.Diagrams;

/// <summary>
/// F6-10 (docs/20 D-24, ADR-0045): the golden files of the three generators over fixed models, the single escaper under hostile names, and that the same model gives
/// the same bytes in any culture. The goldens are compared byte by byte; to rewrite them set <c>AGENTSTUDIO_UPDATE_GOLDEN=1</c> and read the result before committing it.
/// </summary>
public sealed partial class DiagramGrammarTests
{
    private const string Hostile = "x\";%%{init: {\"theme\":\"evil\"}}%% --> click n1 \"https://evil.test\" end Note over p1: `a` <b>&#35;\\ ";

    [Fact]
    public void The_solution_matches_its_golden_file()
    {
        var text = GraphDiagrams.Solution(DiagramFixtures.Graph(), DiagramFixtures.At);

        DiagramFixtures.AssertGolden("solution.golden.mmd", text.Mermaid);
        Assert.Equal(DiagramFixtures.Graph().ModelHash, text.ModelHash);
        Assert.Equal(DiagramKind.Solution, text.Kind);
        Assert.Equal(string.Empty, text.Subject);
    }

    [Fact]
    public void The_namespaces_of_a_project_match_their_golden_file()
    {
        var text = GraphDiagrams.ProjectNamespaces(DiagramFixtures.Graph(), DiagramFixtures.CoreProject, DiagramFixtures.At);

        DiagramFixtures.AssertGolden("project-namespaces.golden.mmd", text.Mermaid);
        Assert.Equal(DiagramFixtures.CoreProject, text.Subject);
    }

    [Fact]
    public void A_project_with_no_namespaces_is_one_node()
    {
        var text = GraphDiagrams.ProjectNamespaces(DiagramFixtures.Graph(), DiagramFixtures.TestsProject, DiagramFixtures.At);

        DiagramFixtures.AssertGolden("project-without-namespaces.golden.mmd", text.Mermaid);
    }

    [Fact]
    public void The_plan_of_a_run_matches_its_golden_file()
    {
        var plan = DiagramFixtures.Plan("Preparar \"x\"; %% y", "sí -->|z");
        var text = RunDiagrams.Plan(DiagramFixtures.Run, plan, DiagramFixtures.At);

        DiagramFixtures.AssertGolden("plan.golden.mmd", text.Mermaid);
        Assert.Equal(plan.ContentHash, text.ModelHash);
        Assert.Equal(DiagramFixtures.Run, text.Subject);
    }

    [Fact]
    public void The_sequence_of_a_run_matches_its_golden_file()
    {
        var logs = RunLogReader.Read(DiagramFixtures.Log(executor: "bob: \"B\"; ana"));
        var text = RunDiagrams.Sequence(Assert.Single(logs), DiagramFixtures.At);

        DiagramFixtures.AssertGolden("sequence.golden.mmd", text.Mermaid);
        Assert.Equal(Assert.Single(logs).LastHash, text.ModelHash);
    }

    [Fact]
    public void The_header_says_the_version_of_the_generator_the_commit_and_the_model()
    {
        var text = GraphDiagrams.Solution(DiagramFixtures.Graph(), DiagramFixtures.At).Mermaid.Split('\n');

        Assert.Equal("%% Generado por Agent Studio. Editarlo no cambia nada: se regenera.", text[0]);
        Assert.Equal("%% diagrama: solucion · generador: 1", text[1]);
        Assert.Equal($"%% commit: {DiagramFixtures.Commit} · modelo: sha256:{DiagramFixtures.Graph().ModelHash} · fecha: 2026-11-12T10:00:00Z", text[2]);
        Assert.Equal("flowchart LR", text[3]);
    }

    [Fact]
    public void A_relation_an_ai_proposed_never_comes_out_of_a_diagram()
    {
        var with = GraphDiagrams.Solution(DiagramFixtures.Graph(withInference: true), DiagramFixtures.At);
        var without = GraphDiagrams.Solution(DiagramFixtures.Graph(withInference: false), DiagramFixtures.At);

        Assert.Equal(without.Mermaid, with.Mermaid);
        Assert.Equal(without.ModelHash, with.ModelHash);
    }

    [Fact]
    public void The_same_model_gives_the_same_bytes_twice_and_in_any_culture()
    {
        var plan = DiagramFixtures.Plan();
        var log = Assert.Single(RunLogReader.Read(DiagramFixtures.Log()));
        string[] Render() =>
        [
            GraphDiagrams.Solution(DiagramFixtures.Graph(), DiagramFixtures.At).Mermaid,
            GraphDiagrams.ProjectNamespaces(DiagramFixtures.Graph(), DiagramFixtures.CoreProject, DiagramFixtures.At).Mermaid,
            RunDiagrams.Plan(DiagramFixtures.Run, plan, DiagramFixtures.At).Mermaid,
            RunDiagrams.Sequence(log, DiagramFixtures.At).Mermaid,
        ];

        var first = Render();
        Assert.Equal(first, Render());
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "tr-TR", "de-DE", "ar-SA", "th-TH", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(first, Render());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        // The bytes are UTF-8 without a byte-order mark, and \n is the only end of line.
        Assert.All(first, text =>
        {
            Assert.DoesNotContain('\r', text);
            Assert.True(text.EndsWith('\n'));
            Assert.NotEqual('\uFEFF', text[0]);
        });
    }

    [Fact]
    public void The_order_the_model_was_built_in_does_not_change_the_text()
    {
        var graph = DiagramFixtures.Graph();
        var shuffled = new CodeGraph(graph.Commit, graph.GeneratorVersion, [.. graph.Nodes.Reverse()], [.. graph.Edges.Reverse()], [.. graph.Gaps.Reverse()]);

        Assert.Equal(GraphDiagrams.Solution(graph, DiagramFixtures.At).Mermaid, GraphDiagrams.Solution(shuffled, DiagramFixtures.At).Mermaid);
    }

    [Fact]
    public void A_folder_of_the_solution_with_no_project_is_not_drawn_and_the_solution_node_is_not_either()
    {
        var text = GraphDiagrams.Solution(DiagramFixtures.Graph(), DiagramFixtures.At).Mermaid;

        Assert.DoesNotContain("empty", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sample", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n').Count(l => l.TrimStart().StartsWith("subgraph ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_project_that_is_not_in_the_model_is_refused()
    {
        Assert.Throws<ArgumentException>(() => GraphDiagrams.ProjectNamespaces(DiagramFixtures.Graph(), "src/Gone/Gone.csproj", DiagramFixtures.At));
    }

    // ---- the escaper ----

    public static TheoryData<string> HostileTexts() => new()
    {
        Hostile,
        "a;b",
        "100%% done",
        "%%{init: {\"securityLevel\":\"loose\"}}%%",
        "A -->> B",
        "end",
        "Note over p1: x",
        "click n1 \"https://evil.test\" _blank",
        "line one\nline two\r\nline three\u2028four\u2029five\ttab",
        "rtl \u202Eevil\u202C ltr \u200E mark \u200B zero \uFEFF bom \u2066iso\u2069",
        "`markdown string`",
        "back\\slash",
        "<script>alert(1)</script> &amp; #35; #quot;",
        "emoji 😀 ; 😀 \"",
        "colon: here",
        new string('a', 79) + "\";",
        new string(';', 200),
    };

    [Theory]
    [MemberData(nameof(HostileTexts))]
    public void The_escaper_leaves_nothing_that_the_grammar_could_read_as_syntax(string hostile)
    {
        foreach (var text in new[] { MermaidText.Label(hostile), MermaidText.Participant(hostile) })
        {
            // Every character of the list of the grammar is written as its entity, so what remains outside the entities has none of them.
            var outside = Entity().Replace(text, string.Empty);
            Assert.DoesNotContain(outside, c => "\"#&;<>%`\\|".Contains(c, StringComparison.Ordinal));
            Assert.All(text.EnumerateRunes(), rune => Assert.False(AgentStudio.Domain.Common.UntrustedText.IsHidden(rune)));
            Assert.DoesNotContain('\n', text);
        }

        Assert.DoesNotContain(':', MermaidText.Participant(hostile));
    }

    [Fact]
    public void The_escaper_cuts_before_escaping_so_an_entity_is_never_cut_in_half_and_says_so()
    {
        var text = MermaidText.Label(new string('a', 79) + "\";tail");

        Assert.EndsWith("#34;…", text, StringComparison.Ordinal);
        Assert.Equal(79 + "#34;".Length + 1, text.Length);
        Assert.Matches("^(a|#[0-9]+;|…)+$", text);
    }

    [Fact]
    public void An_empty_name_is_written_with_a_fixed_marker_and_white_space_is_one_space()
    {
        Assert.Equal(MermaidText.Unnamed, MermaidText.Label(null));
        Assert.Equal(MermaidText.Unnamed, MermaidText.Label("  \t\n  "));
        Assert.Equal(MermaidText.Unnamed, MermaidText.Label("\u200B\u202E"));
        Assert.Equal("a b c", MermaidText.Label("  a \n\n  b\t c "));
    }

    [Fact]
    public void A_pair_of_surrogates_is_never_split_by_the_cut()
    {
        var text = MermaidText.Label(string.Concat(Enumerable.Repeat("😀", 100)));

        Assert.EndsWith("…", text, StringComparison.Ordinal);

        // A lone surrogate cannot be written as UTF-8: the strict encoder would refuse the text.
        Assert.Null(Record.Exception(() => new UTF8Encoding(false, true).GetBytes(text)));
        Assert.Equal(80 * 2 + 1, text.Length);
    }

    // ---- hostile names do not change the statements of a diagram ----

    [Fact]
    public void Hostile_names_of_projects_give_the_same_statements_as_harmless_ones()
    {
        var harmless = GraphDiagrams.Solution(DiagramFixtures.Graph(appName: "App"), DiagramFixtures.At).Mermaid;
        var hostile = GraphDiagrams.Solution(DiagramFixtures.Graph(appName: Hostile.Trim()), DiagramFixtures.At).Mermaid;

        AssertSameStatements(harmless, hostile);
        Assert.DoesNotContain("click", Unquoted(hostile), StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_titles_of_tasks_and_names_of_branches_give_the_same_statements_in_the_plan()
    {
        var harmless = RunDiagrams.Plan(DiagramFixtures.Run, DiagramFixtures.Plan(), DiagramFixtures.At).Mermaid;
        var hostile = RunDiagrams.Plan(DiagramFixtures.Run, DiagramFixtures.Plan(Hostile + "\n%%{init}%%\r\nend", Hostile + "\u202E"), DiagramFixtures.At).Mermaid;

        AssertSameStatements(harmless, hostile);
        Assert.Equal(harmless.Split('\n').Length, hostile.Split('\n').Length);
    }

    [Fact]
    public void Hostile_agents_and_titles_give_the_same_statements_in_the_sequence_and_the_messages_are_a_closed_vocabulary()
    {
        var harmless = RunDiagrams.Sequence(Assert.Single(RunLogReader.Read(DiagramFixtures.Log(executor: "ana", taskTitle: "Preparar"))), DiagramFixtures.At).Mermaid;
        var hostile = RunDiagrams.Sequence(Assert.Single(RunLogReader.Read(DiagramFixtures.Log(executor: Hostile + "\nNote over p1: x\u202E", taskTitle: Hostile + "\n%%{init}%%\r\nend"))), DiagramFixtures.At).Mermaid;

        AssertSameStatements(harmless, hostile);
        Assert.Equal(harmless.Split('\n').Length, hostile.Split('\n').Length);

        // Every message line says one of the fixed phrases and nothing else.
        var vocabulary = typeof(DiagramGrammar.Words).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        foreach (var line in hostile.Split('\n'))
        {
            var statement = line.Trim();
            if (statement.Contains("->>", StringComparison.Ordinal) || statement.Contains("-->>", StringComparison.Ordinal))
            {
                Assert.Contains(statement[(statement.IndexOf(": ", StringComparison.Ordinal) + 2)..], vocabulary);
            }
        }
    }

    [Fact]
    public void A_diagram_over_the_caps_is_cut_and_says_how_much_was_left_out()
    {
        var nodes = new List<GraphNode> { new("Big.slnx", GraphNodeKind.Solution, "Big", "Big.slnx", false) };
        var edges = new List<GraphEdge>();
        for (var i = 0; i < 250; i++)
        {
            var path = $"p{i:D3}/P{i:D3}.csproj";
            nodes.Add(new GraphNode(path, GraphNodeKind.Project, $"P{i:D3}", path, false));
            edges.Add(new GraphEdge("Big.slnx", path, GraphEdgeKind.Contains, GraphEdgeOrigin.Static));
        }

        var graph = new CodeGraph(DiagramFixtures.Commit, 1, nodes, edges, []);
        var text = GraphDiagrams.Solution(graph, DiagramFixtures.At).Mermaid;

        Assert.Equal(DiagramGrammar.MaxNodes, text.Split('\n').Count(l => l.Contains("[\"P", StringComparison.Ordinal)));
        Assert.Contains("%% omitido: 50 nodos y 0 relaciones", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sequence_over_its_cap_is_cut_with_a_note()
    {
        var log = Assert.Single(RunLogReader.Read(DiagramFixtures.Log()));
        var events = new List<RunEvent>(log.Events);
        var approvals = log.Events.Where(e => e.Payload is AgentStudio.Application.Decisions.ApprovalRequestedPayload).ToList();
        for (var i = 0; i < DiagramGrammar.MaxMessages + 5; i++)
        {
            events.Insert(1, approvals[0]);
        }

        var text = RunDiagrams.Sequence(new RunLog(log.Run, log.PlanHash, log.Roster, events), DiagramFixtures.At).Mermaid;

        Assert.Equal(DiagramGrammar.MaxMessages, text.Split('\n').Count(l => l.Contains("->>", StringComparison.Ordinal)));
        Assert.Matches("Note over p2: se omiten [0-9]+ mensajes", text);
    }

    [Fact]
    public void Nothing_of_the_text_of_a_diagram_is_a_directive_a_click_or_a_link_unless_the_generator_wrote_it()
    {
        var all = new[]
        {
            GraphDiagrams.Solution(DiagramFixtures.Graph(appName: Hostile.Trim()), DiagramFixtures.At).Mermaid,
            RunDiagrams.Plan(DiagramFixtures.Run, DiagramFixtures.Plan(Hostile, Hostile), DiagramFixtures.At).Mermaid,
            RunDiagrams.Sequence(Assert.Single(RunLogReader.Read(DiagramFixtures.Log(executor: Hostile, taskTitle: Hostile))), DiagramFixtures.At).Mermaid,
        };

        foreach (var text in all)
        {
            Assert.DoesNotContain("%%{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("click ", Unquoted(text), StringComparison.Ordinal);
            Assert.DoesNotContain("://", Unquoted(text), StringComparison.Ordinal);
            Assert.DoesNotContain("style ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("classDef", text, StringComparison.Ordinal);

            // The only comment lines are the header and the note of what was left out: `%%` that came from data is escaped as an entity.
            Assert.All(text.Split('\n').Where(l => l.TrimStart().StartsWith("%%", StringComparison.Ordinal)), line => Assert.True(
                line.StartsWith("%% Generado por Agent Studio", StringComparison.Ordinal)
                || line.StartsWith("%% diagrama: ", StringComparison.Ordinal)
                || line.StartsWith("%% commit: ", StringComparison.Ordinal)
                || line.StartsWith("%% ejecución: ", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("%% omitido: ", StringComparison.Ordinal),
                line));
        }
    }

    /// <summary>The statements of a diagram: each line with the text between its quotes, and the text of a note, taken out.</summary>
    private static string Skeleton(string text)
    {
        var builder = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var statement = Quoted().Replace(line, "\"_\"");
            var note = statement.IndexOf("Note over ", StringComparison.Ordinal);
            if (note >= 0 && statement.IndexOf(": ", note, StringComparison.Ordinal) is > 0 and var colon)
            {
                statement = statement[..(colon + 2)] + "_";
            }

            builder.Append(statement).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The text with what is between quotes and the text of the notes taken out: what is left is only what the generator wrote.</summary>
    private static string Unquoted(string text) => Skeleton(text);

    /// <summary>The three lines of the header say the fingerprint of the model, which is of course not the same for another name: the statements are what follows.</summary>
    private static string Body(string text) => string.Join('\n', text.Split('\n').Skip(3));

    private static void AssertSameStatements(string harmless, string hostile)
    {
        Assert.StartsWith("%% Generado por Agent Studio.", hostile, StringComparison.Ordinal);
        harmless = Body(harmless);
        hostile = Body(hostile);
        Assert.Equal(Skeleton(harmless), Skeleton(hostile));

        // The same number of lines, and each one starts with the same word of the grammar (the skeleton has the text between quotes and of the notes taken out).
        var lines = Skeleton(hostile).Split('\n');
        var expected = Skeleton(harmless).Split('\n');
        Assert.Equal(expected.Length, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal(expected[i].TrimStart().Split(' ')[0], lines[i].TrimStart().Split(' ')[0]);
        }
    }

    [GeneratedRegex("#[0-9]+;")]
    private static partial Regex Entity();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex Quoted();
}
