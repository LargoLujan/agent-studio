using System.IO;
using System.Text;
using AgentStudio.Debate.Arena;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// Citations anchored to the copy of the commit (docs/15 D-10 and section 7): only the exact lines of a regular text file
/// inside the copy are read, and anything else comes back unresolved with a stable reason instead of failing the turn.
/// </summary>
public sealed class CitationResolverTests : IDisposable
{
    private static readonly string Calc = string.Concat(Enumerable.Range(1, 12).Select(i => $"línea {i}\n"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentstudio-test-" + Guid.NewGuid().ToString("N"));

    public CitationResolverTests()
    {
        Snapshot = Path.Combine(_root, "snapshot");
        Outside = Path.Combine(_root, "fuera");
        Directory.CreateDirectory(Path.Combine(Snapshot, "src", "Calc"));
        Directory.CreateDirectory(Path.Combine(Snapshot, "docs"));
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secreto.txt"), "SECRETO-DE-FUERA\n");
        Write("src/Calc/Calc.cs", Calc);
        Write("src/Calc/Crlf.cs", "uno\r\ndos\r\ntres\r\n");
        Write("README.md", "primera\nsegunda\núltima sin salto");
        Write("docs/vacío.md", string.Empty);
    }

    private string Snapshot { get; }

    private string Outside { get; }

    public void Dispose() => TempFolders.Delete(_root);

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(Snapshot, relative), text, new UTF8Encoding(false));

    private void WriteBytes(string relative, byte[] bytes) => File.WriteAllBytes(Path.Combine(Snapshot, relative), bytes);

    private static ContributionCitation Cite(string path, int startLine = 1, int endLine = 1, string id = "CITA-1") =>
        new(id, path, startLine, endLine, "Motivo.");

    private Task<CitationResolution> ResolveAsync(ContributionCitation citation) =>
        CitationResolver.ResolveAsync(Snapshot, citation, TestContext.Current.CancellationToken);

    private async Task<CitationResolution> UnresolvedAsync(ContributionCitation citation, CitationProblem problem)
    {
        var resolution = await ResolveAsync(citation);

        Assert.False(resolution.Resolved);
        Assert.Equal(problem, resolution.Problem);
        Assert.Null(resolution.Text);
        Assert.Same(citation, resolution.Citation);
        Assert.Contains(citation.Locator, resolution.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO", resolution.Detail, StringComparison.Ordinal);
        return resolution;
    }

    // ---- what resolves ----

    [Fact]
    public async Task The_exact_lines_are_read_with_their_line_ends()
    {
        var citation = Cite("src/Calc/Calc.cs", 3, 5);

        var resolution = await ResolveAsync(citation);

        Assert.True(resolution.Resolved);
        Assert.Equal(CitationProblem.None, resolution.Problem);
        Assert.Equal("línea 3\nlínea 4\nlínea 5\n", resolution.Text);
        Assert.Same(citation, resolution.Citation);
    }

    [Fact]
    public async Task Windows_line_ends_are_kept_as_they_are_in_the_copy()
    {
        var resolution = await ResolveAsync(Cite("src/Calc/Crlf.cs", 2, 3));

        Assert.Equal("dos\r\ntres\r\n", resolution.Text);
    }

    [Fact]
    public async Task The_whole_file_and_a_last_line_without_line_end_can_be_cited()
    {
        Assert.Equal(Calc, (await ResolveAsync(Cite("src/Calc/Calc.cs", 1, 12))).Text);
        Assert.Equal("última sin salto", (await ResolveAsync(Cite("README.md", 3, 3))).Text);
        Assert.Equal("primera\nsegunda\núltima sin salto", (await ResolveAsync(Cite("README.md", 1, 3))).Text);
    }

    [Fact]
    public async Task Backslashes_name_the_same_file()
    {
        var resolution = await ResolveAsync(Cite(@"src\Calc\Calc.cs", 12, 12));

        Assert.Equal("línea 12\n", resolution.Text);
    }

    [Fact]
    public async Task A_file_of_exactly_one_mebibyte_can_be_cited()
    {
        var bytes = Enumerable.Repeat((byte)'a', (int)CitationResolver.MaxFileBytes).ToArray();
        bytes[^1] = (byte)'\n';
        WriteBytes("grande.txt", bytes);

        var resolution = await ResolveAsync(Cite("grande.txt"));

        Assert.True(resolution.Resolved, resolution.Detail);
        Assert.Equal(CitationResolver.MaxFileBytes, Encoding.UTF8.GetByteCount(resolution.Text!));
    }

    [Fact]
    public async Task Any_range_of_any_text_file_resolves_to_exactly_its_lines()
    {
        // Seeded property: random files (LF and CRLF ends, empty lines, a lone '\r' or a U+2028 inside a line, a last line
        // with or without its end) against an oracle that builds the expected lines while it writes the file.
        var random = new Random(20261019);
        string[] alphabet = ["a", "b", "x", "z", " ", "ñ", "á", "ü", "\t", "<", ">", "&", "\"", "'", "\\", "\r", ((char)0x2028).ToString(), "😀"];
        for (var round = 0; round < 200; round++)
        {
            var expected = new List<string>();
            var text = new StringBuilder();
            var count = random.Next(0, 25);
            for (var i = 0; i < count; i++)
            {
                var last = i == count - 1;
                var end = last && random.Next(2) == 0 ? string.Empty : random.Next(3) == 0 ? "\r\n" : "\n";
                var length = random.Next(end.Length == 0 ? 1 : 0, 12); // a last line without its end is never empty: then it is no line
                var line = new StringBuilder();
                for (var c = 0; c < length; c++)
                {
                    line.Append(alphabet[random.Next(alphabet.Length)]);
                }

                line.Append(end);
                expected.Add(line.ToString());
                text.Append(line);
            }

            var name = $"propiedad-{round}.txt";
            Write(name, text.ToString());
            var start = random.Next(1, count + 3);
            var stop = start + random.Next(0, 6);

            var resolution = await ResolveAsync(Cite(name, start, stop));

            if (stop <= expected.Count)
            {
                Assert.True(resolution.Resolved, $"Ronda {round}: {resolution.Detail}");
                Assert.Equal(string.Concat(expected.Skip(start - 1).Take(stop - start + 1)), resolution.Text);
            }
            else
            {
                Assert.Equal(CitationProblem.LinesOutOfRange, resolution.Problem);
            }
        }
    }

    [Fact]
    public async Task Several_citations_resolve_each_on_its_own_in_their_order()
    {
        var citations = new[] { Cite("src/Calc/Calc.cs", 1, 1, "CITA-1"), Cite("src/Calc/Falta.cs", 1, 1, "CITA-2"), Cite("README.md", 2, 2, "CITA-3") };

        var resolutions = await CitationResolver.ResolveAsync(Snapshot, citations, TestContext.Current.CancellationToken);

        Assert.Equal(["CITA-1", "CITA-2", "CITA-3"], resolutions.Select(r => r.Citation.Id));
        Assert.Equal([CitationProblem.None, CitationProblem.NotFound, CitationProblem.None], resolutions.Select(r => r.Problem));
        Assert.Equal("segunda\n", resolutions[2].Text);
    }

    // ---- what does not resolve, and why ----

    [Theory]
    [InlineData("../fuera/secreto.txt")]
    [InlineData("src/../../fuera/secreto.txt")]
    [InlineData(@"..\fuera\secreto.txt")]
    [InlineData("./src/Calc/Calc.cs")]
    [InlineData("/src/Calc/Calc.cs")]
    [InlineData(@"\src\Calc\Calc.cs")]
    [InlineData(@"\\localhost\C$\Windows\win.ini")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("src/Calc/Calc.cs:flujo")]
    [InlineData("src//Calc/Calc.cs")]
    [InlineData("src/Calc/")]
    [InlineData("src/Calc/Calc.cs.")]
    [InlineData("src/Calc/Calc.cs ")]
    [InlineData("NUL")]
    [InlineData("src/con.txt")]
    [InlineData("src/COM1")]
    [InlineData("src/Calc/C?lc.cs")]
    [InlineData("src/Calc/\u0000.cs")]
    [InlineData("")]
    public async Task A_path_that_is_not_relative_to_the_copy_is_never_opened(string path)
    {
        var resolution = await UnresolvedAsync(Cite(path), CitationProblem.InvalidPath);

        Assert.NotNull(CitationResolver.PathProblem(path));
        Assert.Contains("relative", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_relative_to_the_copy_has_no_problem()
    {
        Assert.Null(CitationResolver.PathProblem("src/Calc/Calc.cs"));
        Assert.Null(CitationResolver.PathProblem(@"src\Calc\Calc.cs"));
        Assert.Null(CitationResolver.PathProblem(".github/workflows/ci.yml"));
        Assert.Null(CitationResolver.PathProblem("docs/console.md"));
        Assert.Null(CitationResolver.PathProblem("src/Null/Nullable.cs"));
        Assert.Null(CitationResolver.PathProblem("tests/Ca lc/a b.cs"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 4)]
    [InlineData(1, 81)]
    public async Task A_line_range_that_is_not_one_to_eighty_lines_is_never_read(int startLine, int endLine)
    {
        await UnresolvedAsync(Cite("src/Calc/Calc.cs", startLine, endLine), CitationProblem.InvalidLines);

        Assert.NotNull(CitationResolver.LinesProblem(startLine, endLine));
    }

    [Theory]
    [InlineData("src/Calc/Falta.cs")]
    [InlineData("falta/Calc.cs")]
    [InlineData("src/Calc/Calc.cs/dentro.cs")]
    public async Task A_file_that_is_not_in_the_commit_is_not_found(string path)
    {
        var resolution = await UnresolvedAsync(Cite(path), CitationProblem.NotFound);

        Assert.Contains("does not exist in the commit", resolution.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 13)]
    [InlineData(13, 13)]
    [InlineData(200, 210)]
    public async Task Lines_beyond_the_end_of_the_file_do_not_exist(int startLine, int endLine)
    {
        var resolution = await UnresolvedAsync(Cite("src/Calc/Calc.cs", startLine, endLine), CitationProblem.LinesOutOfRange);

        Assert.Contains("12 lines", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_file_has_no_line_to_cite()
    {
        var resolution = await UnresolvedAsync(Cite("docs/vacío.md"), CitationProblem.LinesOutOfRange);

        Assert.Contains("0 lines", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_is_not_a_file()
    {
        await UnresolvedAsync(Cite("src/Calc"), CitationProblem.NotAFile);
    }

    [Fact]
    public async Task A_file_larger_than_one_mebibyte_is_never_read()
    {
        WriteBytes("enorme.txt", Enumerable.Repeat((byte)'a', (int)CitationResolver.MaxFileBytes + 1).ToArray());

        await UnresolvedAsync(Cite("enorme.txt"), CitationProblem.TooLarge);
    }

    [Fact]
    public async Task A_file_that_is_not_utf8_text_is_not_cited()
    {
        WriteBytes("latin1.txt", [0x63, 0x61, 0xF1, 0x61, 0x0A]); // "caña" in Latin-1
        WriteBytes("binario.bin", [0x4D, 0x5A, 0x00, 0x00, 0x0A]);
        WriteBytes("utf16.txt", [0xFF, 0xFE, 0x61, 0x00, 0x0A, 0x00]);

        await UnresolvedAsync(Cite("latin1.txt"), CitationProblem.NotText);
        await UnresolvedAsync(Cite("binario.bin"), CitationProblem.NotText);
        await UnresolvedAsync(Cite("utf16.txt"), CitationProblem.NotText);
    }

    [Fact]
    public async Task A_file_another_process_holds_exclusively_is_unreadable_and_the_turn_goes_on()
    {
        using var held = new FileStream(Path.Combine(Snapshot, "README.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await UnresolvedAsync(Cite("README.md"), CitationProblem.Unreadable);
    }

    [Fact]
    public async Task A_cancelled_resolution_throws_instead_of_reporting_a_problem()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CitationResolver.ResolveAsync(Snapshot, Cite("README.md"), cancelled.Token));
    }

    // ---- links: never read through one ----

    [Fact]
    public async Task A_junction_inside_the_copy_is_never_followed()
    {
        using var links = new PlantedLinks();
        links.Junction(Path.Combine(Snapshot, "enlace"), Outside);

        var resolution = await UnresolvedAsync(Cite("enlace/secreto.txt"), CitationProblem.Link);

        Assert.Contains("enlace", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_junction_deeper_on_the_way_is_never_followed()
    {
        using var links = new PlantedLinks();
        links.Junction(Path.Combine(Snapshot, "src", "Calc", "otro"), Outside);

        await UnresolvedAsync(Cite("src/Calc/otro/secreto.txt"), CitationProblem.Link);
        await UnresolvedAsync(Cite("src/Calc/otro"), CitationProblem.Link);
    }

    [Fact]
    public async Task A_copy_that_is_itself_a_link_or_missing_is_a_system_failure_not_an_unresolved_citation()
    {
        var link = Path.Combine(_root, "copia-enlazada");
        using var links = new PlantedLinks();
        links.Junction(link, Outside);
        var token = TestContext.Current.CancellationToken;

        var linked = await Assert.ThrowsAsync<InvalidOperationException>(() => CitationResolver.ResolveAsync(link, Cite("secreto.txt"), token));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => CitationResolver.ResolveAsync(Path.Combine(_root, "no-existe"), [Cite("README.md")], token));

        Assert.Contains("link", linked.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);
    }
}
