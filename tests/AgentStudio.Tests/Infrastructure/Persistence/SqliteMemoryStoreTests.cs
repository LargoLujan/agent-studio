using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The rows and the full-text index of the memory (docs/14 D-19 and D-21): immutable rows, ranked by <c>bm25</c> over
/// topic and content, ties newest first and then by id, accents folded, and a query that can never carry an FTS5
/// operator from what a person or an agent wrote.
/// </summary>
public sealed class SqliteMemoryStoreTests : IAsyncLifetime
{
    private readonly TempProjectFolder _folder = new();
    private ProjectInstance _instance = null!;
    private SqliteMemoryStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ProjectId => _instance.ProjectId;

    public async ValueTask InitializeAsync()
    {
        _instance = await ProjectInstance.OpenAsync(_folder.Root);
        _store = new SqliteMemoryStore(_instance.Databases);
    }

    public ValueTask DisposeAsync()
    {
        _instance.Dispose();
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }

    private static MemoryRow Row(
        string id,
        string topic,
        string content,
        MemoryKind kind = MemoryKind.Decision,
        int minutes = 0,
        MemoryOrigin origin = MemoryOrigin.System,
        string? supersedes = null) =>
        new(id, kind, topic, content, origin, "registro:20261015-a1b2c3#" + id, T0.AddMinutes(minutes), MemoryContent.Sha256(kind, topic, content), supersedes);

    private async Task AppendAsync(params MemoryRow[] rows)
    {
        foreach (var row in rows)
        {
            await _store.AppendAsync(ProjectId, row, Ct);
        }
    }

    private async Task<string[]> IdsAsync(MemorySearch search) => [.. (await _store.SearchAsync(ProjectId, search, Ct)).Select(r => r.Id)];

    /// <summary>Three entries about deploying, testing and nothing in particular.</summary>
    private Task SeedAsync() => AppendAsync(
        Row("mem-a", "Despliegue", "El despliegue es nocturno y usa blue green.", minutes: 1),
        Row("mem-b", "Pruebas", "Antes del despliegue se pasan las pruebas.", MemoryKind.Convention, minutes: 2),
        Row("mem-c", "Otro", "Nada que ver con lo demás.", MemoryKind.KnownError, minutes: 3));

    [Fact]
    public async Task An_appended_row_is_found_again_exactly_as_it_was_written()
    {
        var first = Row("mem-1", "Despliegue", "Se usa blue green.", origin: MemoryOrigin.User);
        var second = Row("mem-2", "Despliegue", "Ahora canary.", minutes: 5, supersedes: "mem-1");

        await AppendAsync(first, second);

        Assert.Equal([second, first], await _store.SearchAsync(ProjectId, new MemorySearch(), Ct));
    }

    [Fact]
    public async Task Rows_are_ranked_by_relevance_over_topic_and_content()
    {
        await SeedAsync();

        Assert.Equal(["mem-a", "mem-b"], await IdsAsync(new MemorySearch("despliegue")));
    }

    [Fact]
    public async Task Without_text_rows_come_newest_first_and_a_tie_by_id()
    {
        await AppendAsync(
            Row("mem-b", "Uno", "Primero.", minutes: 1),
            Row("mem-a", "Dos", "Segundo.", minutes: 1),
            Row("mem-c", "Tres", "Tercero.", minutes: 2));

        Assert.Equal(["mem-c", "mem-a", "mem-b"], await IdsAsync(new MemorySearch()));
    }

    [Fact]
    public async Task Equally_relevant_rows_come_newest_first_and_a_tie_by_id()
    {
        await AppendAsync(
            Row("mem-b", "Despliegue", "Igual.", minutes: 1),
            Row("mem-a", "Despliegue", "Igual.", minutes: 1),
            Row("mem-c", "Despliegue", "Igual.", minutes: 2));

        Assert.Equal(["mem-c", "mem-a", "mem-b"], await IdsAsync(new MemorySearch("despliegue")));
    }

    [Fact]
    public async Task Any_term_of_the_text_finds_a_row_and_more_terms_rank_it_higher()
    {
        await SeedAsync();

        Assert.Equal(["mem-a", "mem-b"], await IdsAsync(new MemorySearch("nocturno despliegue")));
    }

    [Theory]
    [InlineData("CONFIGURACIÓN")]
    [InlineData("configuracion")]
    [InlineData("Configuración")]
    public async Task Accents_and_case_are_folded(string text)
    {
        await AppendAsync(Row("mem-1", "Configuración", "La configuración vive en el perfil."));

        Assert.Equal(["mem-1"], await IdsAsync(new MemorySearch(text)));
    }

    [Theory]
    [InlineData("desp*", "")] // as a prefix it would find mem-a and mem-b
    [InlineData("topic:Pruebas", "")] // as a column filter it would find mem-b
    [InlineData("^despliegue", "mem-a mem-b")] // as an initial-token marker it would find mem-a only
    [InlineData("NEAR(despliegue", "")] // as an operator it would not even parse
    public async Task An_fts_operator_in_the_text_is_only_text(string text, string expected)
    {
        await SeedAsync();

        Assert.Equal(expected.Split(' ', StringSplitOptions.RemoveEmptyEntries), await IdsAsync(new MemorySearch(text)));
    }

    [Theory]
    [InlineData("\"despliegue")]
    [InlineData("despliegue\"")]
    [InlineData("\"despliegue nocturno\"")]
    [InlineData("-despliegue")]
    [InlineData("despliegue AND")]
    [InlineData("despliegue OR")]
    [InlineData("NOT despliegue")]
    public async Task Quotes_and_operator_words_never_break_the_query(string text)
    {
        await SeedAsync();

        Assert.Contains("mem-a", await IdsAsync(new MemorySearch(text)));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("\"")]
    [InlineData("\"\" ** ::")]
    public async Task A_text_with_nothing_to_search_for_finds_nothing(string text)
    {
        await SeedAsync();

        Assert.Empty(await IdsAsync(new MemorySearch(text)));
    }

    [Fact]
    public async Task Rows_are_filtered_by_kind_and_topic()
    {
        await SeedAsync();

        Assert.Equal(["mem-c", "mem-b"], await IdsAsync(new MemorySearch(Kinds: [MemoryKind.Convention, MemoryKind.KnownError])));
        Assert.Equal(["mem-b"], await IdsAsync(new MemorySearch(Topic: "pruebas")));
        Assert.Equal(["mem-b"], await IdsAsync(new MemorySearch("despliegue", [MemoryKind.Convention])));
    }

    [Fact]
    public async Task A_limit_returns_the_first_rows_only()
    {
        await SeedAsync();

        Assert.Equal(["mem-c", "mem-b"], await IdsAsync(new MemorySearch(Limit: 2)));
    }

    [Fact]
    public async Task A_row_is_never_replaced_updated_or_deleted()
    {
        var original = Row("mem-1", "Despliegue", "Se usa blue green.");
        await AppendAsync(original);

        await Assert.ThrowsAsync<SqliteException>(() => _store.AppendAsync(ProjectId, Row("mem-1", "Despliegue", "Reescrita."), Ct));
        using (var connection = _instance.Connect())
        {
            Assert.Throws<SqliteException>(() => Exec(connection, "UPDATE memory_entries SET content = 'reescrita' WHERE id = 'mem-1';"));
            Assert.Throws<SqliteException>(() => Exec(connection, "DELETE FROM memory_entries WHERE id = 'mem-1';"));
        }

        Assert.Equal([original], await _store.SearchAsync(ProjectId, new MemorySearch(), Ct));
    }

    [Fact]
    public async Task A_row_whose_hash_is_not_that_of_its_content_is_refused_and_not_stored()
    {
        var forged = Row("mem-1", "Despliegue", "Se usa blue green.") with { Content = "Otro contenido." };

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(ProjectId, forged, Ct));

        Assert.Empty(await _store.SearchAsync(ProjectId, new MemorySearch(), Ct));
    }

    [Fact]
    public async Task A_row_longer_than_an_entry_may_be_is_refused()
    {
        var content = new string('x', MemoryContent.MaxCharacters + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AppendAsync(ProjectId, Row("mem-1", "Largo", content), Ct));
    }

    [Fact]
    public async Task The_persistence_of_the_app_keeps_the_memory_in_the_database_of_each_open_project()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_instance.Databases);
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(Path.Combine(_folder.Root, "..", "anclas")));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var row = Row("mem-1", "Despliegue", "Se usa blue green.");

        await provider.GetRequiredService<IMemoryStore>().AppendAsync(ProjectId, row, Ct);

        Assert.Equal([row], await _store.SearchAsync(ProjectId, new MemorySearch(), Ct));
    }

    [Fact]
    public async Task A_row_written_behind_the_store_that_no_writer_of_agent_studio_could_have_written_is_reported()
    {
        using (var connection = _instance.Connect())
        {
            Exec(connection, $"INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256) VALUES ('mem-x', '{ProjectId}', 'Rumor', 'Tema', 'Contenido', 's', '2026-10-15T10:00:00.0000000+00:00', 'User', '{HashA}');");
        }

        var problem = await Assert.ThrowsAsync<InvalidDataException>(() => _store.SearchAsync(ProjectId, new MemorySearch(), Ct));

        Assert.Contains("mem-x", problem.Message, StringComparison.Ordinal);
    }
}
