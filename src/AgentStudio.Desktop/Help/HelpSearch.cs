using System.Text;
using AgentStudio.Application.Help;
using AgentStudio.Domain.Semantic;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Desktop.Help;

/// <summary>
/// The local search of the help (docs/17 D-55, ADR-0056): FTS5 over the articles of the corpus, in memory, free and without network.
/// It takes no project and no consent, so it cannot launch an agent nor spend quota. Each language has its own index, built the first
/// time it is asked for from <see cref="IHelpCorpus.Articles"/>. The terms of the query are the ones of the search of the memory
/// (<see cref="LexicalTerms"/>: without function words), each a quoted phrase with its quotes doubled and a prefix mark, joined by
/// <c>OR</c>, so no FTS5 operator gets through from what the person typed. The vectors of the semantic index (ADR-0047) are not used
/// by the help yet: every result is <see cref="HelpMatchKind.Lexical"/>.
/// </summary>
public sealed class HelpSearch : IHelpSearch, IDisposable
{
    private const int ExcerptWords = 14;

    private readonly IHelpCorpus _corpus;
    private readonly Dictionary<string, SqliteConnection> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Lock _gate = new();

    public HelpSearch(IHelpCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        _corpus = corpus;
    }

    public Task<IReadOnlyList<HelpSearchHit>> SearchAsync(string query, string language, int limit, CancellationToken ct)
    {
        var problems = HelpSearchRules.Validate(query, limit);
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The search breaks the rules of the help: {string.Join(", ", problems)}.", nameof(query));
        }

        ct.ThrowIfCancellationRequested();
        var match = MatchOf(query, IsEnglish(KeyOf(language)));
        if (match is null)
        {
            return Task.FromResult<IReadOnlyList<HelpSearchHit>>([]);
        }

        lock (_gate)
        {
            var connection = IndexFor(language);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, snippet(help, 2, '', '', '...', $words), bm25(help, 0.0, 4.0, 1.0)
                FROM help
                WHERE help MATCH $match
                ORDER BY bm25(help, 0.0, 4.0, 1.0)
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$words", ExcerptWords);
            var hits = new List<HelpSearchHit>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // bm25 is lower for a better match (and negative): the score is higher for a better one.
                hits.Add(new HelpSearchHit(reader.GetString(0), reader.GetString(1), reader.GetString(2).ReplaceLineEndings(" ").Trim(), -reader.GetDouble(3), HelpMatchKind.Lexical));
            }

            return Task.FromResult<IReadOnlyList<HelpSearchHit>>(hits);
        }
    }

    /// <summary>
    /// The FTS5 query of <paramref name="query"/>, or null when no term is left: each term a quoted phrase with a prefix mark, joined by <c>OR</c>,
    /// followed by the phrases the articles use for the technical words among them (<see cref="HelpSynonyms"/>), cleaned and quoted in the same way.
    /// </summary>
    public static string? MatchOf(string query, bool english = false)
    {
        var terms = LexicalTerms.From(query);
        if (terms.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var term in terms.Concat(terms.SelectMany(t => HelpSynonyms.For(t, english))))
        {
            var clean = new string([.. term.Where(c => !char.IsControl(c))]).Replace("\"", "\"\"", StringComparison.Ordinal);
            if (clean.Length == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(" OR ");
            }

            text.Append('"').Append(clean).Append('"').Append('*');
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private string KeyOf(string language) => _corpus.Articles(language).FirstOrDefault()?.Language ?? language;

    private static bool IsEnglish(string key) => key.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private SqliteConnection IndexFor(string language)
    {
        var key = KeyOf(language);
        if (_indexes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE help USING fts5(id UNINDEXED, title, body, tokenize = 'unicode61 remove_diacritics 2');";
            create.ExecuteNonQuery();
        }

        foreach (var article in _corpus.Articles(language))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO help (id, title, body) VALUES ($id, $title, $body);";
            insert.Parameters.AddWithValue("$id", article.Id);
            insert.Parameters.AddWithValue("$title", article.Title);
            insert.Parameters.AddWithValue("$body", PlainOf(article.Markdown));
            insert.ExecuteNonQuery();
        }

        _indexes[key] = connection;
        return connection;
    }

    /// <summary>The Markdown of an article as plain text, so the marks of the format are not indexed nor shown in an excerpt.</summary>
    internal static string PlainOf(string markdown)
    {
        var text = new StringBuilder();
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var plain = line.TrimStart('#', ' ', '-', '*', '>', '\t').Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
            text.Append(plain.Trim()).Append('\n');
        }

        return text.ToString().Trim();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var connection in _indexes.Values)
            {
                connection.Dispose();
            }

            _indexes.Clear();
        }
    }
}
