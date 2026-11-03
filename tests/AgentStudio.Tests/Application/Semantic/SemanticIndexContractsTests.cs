using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Common;

namespace AgentStudio.Tests.Application.Semantic;

/// <summary>
/// The contracts of the semantic index that are values (docs/17 D-03, D-04 and D-06; ADR-0047): the pinned model, where
/// the model and the index live, and what a search by words only says.
/// </summary>
public sealed class SemanticIndexContractsTests
{
    private static readonly EmbeddingModelDescriptor E5 = EmbeddingModels.MultilingualE5Small;

    [Fact]
    public void The_model_is_pinned_to_the_revision_and_the_hashes_the_spike_downloaded()
    {
        Assert.Equal("intfloat/multilingual-e5-small", E5.Name);
        Assert.Equal("614241f622f53c4eeff9890bdc4f31cfecc418b3", E5.Revision);
        Assert.Equal("MIT", E5.License);
        Assert.Equal((384, 512, "query: ", "passage: "), (E5.Dimensions, E5.MaxTokens, E5.QueryPrefix, E5.PassagePrefix));
        Assert.Equal(new SentencePieceIds(0, 1, 2, 3, 1), E5.TokenIds);

        Assert.Equal("model_qint8_avx512_vnni.onnx", E5.Model.FileName);
        Assert.Equal(118_346_824, E5.Model.Bytes);
        Assert.Equal("dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88", E5.Model.Sha256);
        Assert.Equal("https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/onnx/model_qint8_avx512_vnni.onnx", E5.Model.Source.AbsoluteUri);

        Assert.Equal("sentencepiece.bpe.model", E5.Tokenizer.FileName);
        Assert.Equal(5_069_051, E5.Tokenizer.Bytes);
        Assert.Equal("cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", E5.Tokenizer.Sha256);
        Assert.Equal("https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/onnx/sentencepiece.bpe.model", E5.Tokenizer.Source.AbsoluteUri);

        Assert.All(new[] { E5.Model.Source, E5.Tokenizer.Source }, uri => Assert.Equal(("https", "huggingface.co"), (uri.Scheme, uri.Host)));
    }

    [Fact]
    public void The_fingerprint_of_the_model_is_stable_and_changes_with_any_part_of_it()
    {
        Assert.True(ContentHash.IsSha256Hex(E5.Fingerprint()));
        Assert.Equal(E5.Fingerprint(), (E5 with { }).Fingerprint());
        Assert.NotEqual(E5.Fingerprint(), (E5 with { Revision = "0000000000000000000000000000000000000000" }).Fingerprint());
        Assert.NotEqual(E5.Fingerprint(), (E5 with { Model = E5.Model with { Sha256 = ContentHash.Sha256Hex("otro") } }).Fingerprint());
        Assert.NotEqual(E5.Fingerprint(), (E5 with { QueryPrefix = "consulta: " }).Fingerprint());
        Assert.NotEqual(E5.Fingerprint(), (E5 with { TokenIds = E5.TokenIds with { PieceOffset = 0 } }).Fingerprint());
    }

    [Fact]
    public void The_model_and_the_index_live_in_the_local_app_data_of_the_user()
    {
        const string local = @"C:\Users\persona\AppData\Local";

        Assert.Equal(
            @"C:\Users\persona\AppData\Local\AgentStudio\models\multilingual-e5-small\614241f622f53c4eeff9890bdc4f31cfecc418b3",
            SemanticIndexLocation.ModelDirectory(local, E5));

        var key = SemanticIndexLocation.ProjectKey(@"C:\Repos\Mi Proyecto");
        Assert.Equal(Path.Combine(local, "AgentStudio", "index", key, "semantic.db"), SemanticIndexLocation.IndexFile(local, @"C:\Repos\Mi Proyecto"));
    }

    [Fact]
    public void The_key_of_an_index_is_the_one_of_the_anchor_of_the_log_of_the_project()
    {
        const string path = @"C:\Repos\Mi Proyecto";

        // FileDecisionLogAnchorStore: SHA-256 of the UTF-8 of the final root path in upper case, lowercase hex.
        var anchorKey = Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(path.ToUpperInvariant())));
        Assert.Equal(anchorKey, SemanticIndexLocation.ProjectKey(path));
        Assert.Equal(SemanticIndexLocation.ProjectKey(path), SemanticIndexLocation.ProjectKey(@"c:\repos\mi proyecto"));
        Assert.NotEqual(SemanticIndexLocation.ProjectKey(path), SemanticIndexLocation.ProjectKey(@"C:\Repos\Otro"));
        Assert.Throws<ArgumentException>(() => SemanticIndexLocation.ProjectKey(" "));
    }

    [Fact]
    public void A_search_by_words_only_says_why_in_Spanish_and_a_hybrid_one_says_nothing()
    {
        foreach (var reason in Enum.GetValues<WordsOnlyReason>())
        {
            var notice = new SemanticSearchResult([], SemanticSearchMode.WordsOnly, reason).Notice;
            Assert.NotNull(notice);
            Assert.StartsWith("Solo por palabras: ", notice, StringComparison.Ordinal);
        }

        Assert.Null(new SemanticSearchResult([], SemanticSearchMode.Hybrid).Notice);
        Assert.Throws<ArgumentOutOfRangeException>(() => SemanticSearchTexts.WordsOnly((WordsOnlyReason)99));
    }

    [Fact]
    public void A_query_asks_for_trusted_fragments_with_the_log_unless_it_says_otherwise()
    {
        var query = new SemanticQuery("pruebas que fallan");

        Assert.Equal((10, true, true), (query.Top, query.TrustedOnly, query.IncludeLog));
        Assert.Null(query.Kinds);
    }
}
