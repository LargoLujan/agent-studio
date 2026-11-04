using System.Globalization;
using System.Text;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Application.Semantic;

// Phase 12 contracts of the local semantic index (docs/17 section 3, D-01 to D-06; ADR-0047). Who does what:
// - WP2 (Infrastructure/Embeddings/Onnx and Infrastructure/Persistence/SemanticIndexStore.cs): IEmbedderSource and its
//   IEmbedder, from the verified local files of EmbeddingModels.MultilingualE5Small, and ISemanticIndexStore.
// - WP3 (Memory/Semantic and Memory/Context/ContextPacker.cs): ISemanticIndex (the fragments of what is anchored, the
//   refresh and the hybrid search with HybridRanking and LexicalTerms) and the packer's relevance.
// - WP4 (SkillsMcp/History and Desktop/Memory): the search of the «Memoria» document and the MCP tool over a
//   ProjectHistorySnapshot.
// - The lead, in the integration: IEmbeddingModelInstaller (the download with the user's OK) and the composition.

/// <summary>One file of an embedding model, pinned: its name in the model folder, where it is downloaded from, its size and its SHA-256.</summary>
/// <param name="FileName">The name of the file in <see cref="SemanticIndexLocation.ModelDirectory"/>: a plain name, no folder.</param>
/// <param name="Source">The URL of the file at the pinned revision; the only address the installer ever reads.</param>
/// <param name="Bytes">Its exact size.</param>
/// <param name="Sha256">Its SHA-256, 64 lowercase hex characters: a file that does not give it is never loaded (docs/17 decision 9).</param>
public sealed record EmbeddingModelFile(string FileName, Uri Source, long Bytes, string Sha256);

/// <summary>
/// How the ids of the SentencePiece model become the ids the network expects (the fairseq rule of XLM-RoBERTa): the
/// special tokens have fixed ids, an unknown piece is <see cref="Unknown"/> and every other piece is its SentencePiece id
/// plus <see cref="PieceOffset"/>. The spike checked that <c>SentencePieceTokenizer</c> of <c>Microsoft.ML.Tokenizers</c>
/// with this rule gives the ids of the reference on 56 of 56 sentences (docs/investigacion/2026-10-30-spike-embeddings.md).
/// </summary>
public sealed record SentencePieceIds(int BeginOfSentence, int Padding, int EndOfSentence, int Unknown, int PieceOffset);

/// <summary>
/// An embedding model pinned in the code (docs/17 D-03 and decision 9; ADR-0047): its name and revision, its license,
/// the size of its vectors, the most tokens of one input (<see cref="MaxTokens"/>, the special ones included: longer
/// texts are cut), the prefixes of a query and of a passage, the network and tokenizer files with their SHA-256, and the
/// rule of its token ids. The ONNX graph takes <c>input_ids</c>, <c>attention_mask</c> and <c>token_type_ids</c> (int64,
/// <c>[batch, tokens]</c>, the types all zero) and gives <c>last_hidden_state</c> (<c>[batch, tokens, dimensions]</c>); the
/// embedding is its mean over the attention mask, L2-normalised. <see cref="Fingerprint"/> names the model in every vector
/// the index keeps: a vector of another model is never compared.
/// </summary>
public sealed record EmbeddingModelDescriptor(
    string Name,
    string Revision,
    string License,
    int Dimensions,
    int MaxTokens,
    string QueryPrefix,
    string PassagePrefix,
    EmbeddingModelFile Model,
    EmbeddingModelFile Tokenizer,
    SentencePieceIds TokenIds)
{
    /// <summary>The input of the ids, the attention mask and the token types; the output of the hidden states.</summary>
    public const string InputIds = "input_ids";

    public const string AttentionMask = "attention_mask";

    public const string TokenTypeIds = "token_type_ids";

    public const string LastHiddenState = "last_hidden_state";

    /// <summary>
    /// The SHA-256 of the canonical description of the model (name, revision, files and their hashes, dimensions, limit,
    /// prefixes and id rule): the same model always gives the same fingerprint, and any change gives another one.
    /// </summary>
    public string Fingerprint() => ContentHash.Sha256Hex(ContentHash.Canonical(
        "agent-studio/embedding-model/1",
        Name,
        Revision,
        Model.FileName,
        Model.Sha256,
        Tokenizer.FileName,
        Tokenizer.Sha256,
        Dimensions.ToString(CultureInfo.InvariantCulture),
        MaxTokens.ToString(CultureInfo.InvariantCulture),
        QueryPrefix,
        PassagePrefix,
        string.Join(',', new[] { TokenIds.BeginOfSentence, TokenIds.Padding, TokenIds.EndOfSentence, TokenIds.Unknown, TokenIds.PieceOffset }.Select(i => i.ToString(CultureInfo.InvariantCulture)))));

    /// <summary>The folder of the model under the models folder: the last segment of its name, then its revision.</summary>
    public string RelativeFolder() => Path.Combine(Name[(Name.LastIndexOf('/') + 1)..], Revision);
}

/// <summary>The embedding models Agent Studio knows, pinned (ADR-0047).</summary>
public static class EmbeddingModels
{
    private const string Repository = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/";
    private const string E5Revision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";

    /// <summary>
    /// <c>intfloat/multilingual-e5-small</c> (MIT), at the revision of 2026-04-02, in the int8 ONNX variant the repository
    /// publishes (<c>onnx/model_qint8_avx512_vnni.onnx</c>, 113 MB; it runs on AVX2 too, which is what the spike measured)
    /// and its SentencePiece model: 384 dimensions, 512 tokens, the prefixes <c>query: </c> and <c>passage: </c>. The spike
    /// chose it over the fp32 variant (449 MB): the same quality, a third of the memory and about half the time per query.
    /// </summary>
    public static EmbeddingModelDescriptor MultilingualE5Small { get; } = new(
        "intfloat/multilingual-e5-small",
        E5Revision,
        "MIT",
        384,
        512,
        "query: ",
        "passage: ",
        new EmbeddingModelFile(
            "model_qint8_avx512_vnni.onnx",
            new Uri(Repository + E5Revision + "/onnx/model_qint8_avx512_vnni.onnx"),
            118_346_824,
            "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88"),
        new EmbeddingModelFile(
            "sentencepiece.bpe.model",
            new Uri(Repository + E5Revision + "/onnx/sentencepiece.bpe.model"),
            5_069_051,
            "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
        new SentencePieceIds(BeginOfSentence: 0, Padding: 1, EndOfSentence: 2, Unknown: 3, PieceOffset: 1));
}

/// <summary>
/// Where the model and the index live (docs/17 D-04): in the user's machine, outside every project, so git never sees
/// them, a third-party repository cannot bring them and the check of the user's repository never trips on them.
/// <list type="bullet">
/// <item>The model: <c>%LOCALAPPDATA%\AgentStudio\models\multilingual-e5-small\&lt;revision&gt;\</c> (<see cref="ModelDirectory"/>).</item>
/// <item>The index of a project: <c>%LOCALAPPDATA%\AgentStudio\index\&lt;key&gt;\semantic.db</c>, where the key is
/// <see cref="ProjectKey"/> of the final path of the project root, the same key as the anchor of its decision log.
/// Deleting it loses nothing: the index is rebuilt from what is anchored.</item>
/// </list>
/// </summary>
public static class SemanticIndexLocation
{
    /// <summary>The folder of the app in <c>%LOCALAPPDATA%</c>.</summary>
    public const string AppFolder = "AgentStudio";

    /// <summary>The folder of the models, in <see cref="AppFolder"/>.</summary>
    public const string ModelsFolder = "models";

    /// <summary>The folder of the indexes, in <see cref="AppFolder"/>: one folder per project.</summary>
    public const string IndexFolder = "index";

    /// <summary>The database of the index of a project.</summary>
    public const string IndexFileName = "semantic.db";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The folder of <paramref name="model"/> under <paramref name="localAppData"/> (the folder <c>%LOCALAPPDATA%</c> resolves to).</summary>
    public static string ModelDirectory(string localAppData, EmbeddingModelDescriptor model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(localAppData, AppFolder, ModelsFolder, model.RelativeFolder());
    }

    /// <summary>
    /// The key of the index of a project: the SHA-256 (lowercase hex) of the UTF-8 of its final root path in upper case,
    /// as Windows resolves it through every link (<c>ProjectDatabases.FinalRootPath</c>), exactly like the anchor of its
    /// decision log. Two paths that reach the same folder share an index; a copy of the project elsewhere has its own.
    /// </summary>
    public static string ProjectKey(string finalRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalRootPath);
        return ContentHash.Sha256Hex(Utf8.GetBytes(finalRootPath.ToUpperInvariant()));
    }

    /// <summary>The database of the index of the project whose final root path is <paramref name="finalRootPath"/>.</summary>
    public static string IndexFile(string localAppData, string finalRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return Path.Combine(localAppData, AppFolder, IndexFolder, ProjectKey(finalRootPath), IndexFileName);
    }
}

/// <summary>What there is of the local model (docs/17 D-03, F12-6).</summary>
public enum EmbeddingModelState
{
    /// <summary>A file of the model is not in its folder: the search goes by words only until the user downloads it.</summary>
    Missing,

    /// <summary>Every file is there with its size and SHA-256: the model may be loaded.</summary>
    Ready,

    /// <summary>A file is there but does not give the pinned size or SHA-256: it is never loaded.</summary>
    Mismatch,

    /// <summary>The files verify but ONNX Runtime or the tokenizer could not load them (a broken runtime, no memory).</summary>
    Unloadable,
}

/// <summary>The state of the local model, the folder it was looked for in and, unless it is ready, why, in Spanish.</summary>
public sealed record EmbeddingModelStatus(EmbeddingModelState State, string Directory, string? Problem = null)
{
    public bool IsReady => State == EmbeddingModelState.Ready;
}

/// <summary>What a text is to the model: a query or a passage (each with the prefix of the model).</summary>
public enum EmbeddingRole
{
    /// <summary>A search: the text goes with <see cref="EmbeddingModelDescriptor.QueryPrefix"/>.</summary>
    Query,

    /// <summary>A fragment of the index: the text goes with <see cref="EmbeddingModelDescriptor.PassagePrefix"/>.</summary>
    Passage,
}

/// <summary>
/// The embeddings of a local model, in the process and without network (docs/17 D-03; WP2). The text is trimmed, given the
/// prefix of its role, tokenized with the SentencePiece model and the id rule of the descriptor, cut at
/// <see cref="EmbeddingModelDescriptor.MaxTokens"/> and run through ONNX Runtime; each vector is the mean of the hidden
/// states over the attention mask, L2-normalised, so a dot product is its cosine. The same text always gives the same
/// vector on the same machine. Safe to call from several threads; the source that opened it owns it and disposes it.
/// </summary>
public interface IEmbedder
{
    EmbeddingModelDescriptor Model { get; }

    /// <summary>One vector of <see cref="EmbeddingModelDescriptor.Dimensions"/> per text, in the order of <paramref name="texts"/>.</summary>
    /// <exception cref="ArgumentException">A text is null or blank.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct);
}

/// <summary>An opening of the local model: the embedder when it loaded, and the state of the model either way.</summary>
public sealed record EmbedderOpening(IEmbedder? Embedder, EmbeddingModelStatus Status);

/// <summary>
/// The local model of <see cref="EmbeddingModels.MultilingualE5Small"/> (WP2): it only ever reads its folder
/// (<see cref="SemanticIndexLocation.ModelDirectory"/>), never the network, and it loads only files that give the pinned
/// size and SHA-256 (F12-6). It opens one embedder, once, and keeps it until it is disposed.
/// </summary>
public interface IEmbedderSource : IDisposable
{
    /// <summary>
    /// The state of the files (sizes and hashes); it loads nothing. Each file is hashed the first time in a run of the app,
    /// and again only when its path, its size or its time of last write (UTC) changes, or after the model is installed again.
    /// </summary>
    EmbeddingModelStatus Check();

    /// <summary>The embedder of the verified model, loaded the first time; null with the reason when there is none.</summary>
    Task<EmbedderOpening> OpenAsync(CancellationToken ct);
}

/// <summary>The user's OK to download the model, once (docs/17 decision 9): who gave it and when. Nothing is downloaded without one.</summary>
public sealed record EmbeddingModelDownloadConsent(string GrantedBy, DateTimeOffset GrantedAt);

/// <summary>How a download goes: the file, the bytes received and its size.</summary>
public sealed record EmbeddingModelDownloadProgress(string File, long Received, long Total);

/// <summary>
/// Downloads the model of <see cref="EmbeddingModels.MultilingualE5Small"/> once, with the user's OK (docs/17 decision 9;
/// the integration). Each file comes from its pinned <see cref="EmbeddingModelFile.Source"/> to a temporary file in the
/// models folder, and is moved into place only when its size and SHA-256 are the pinned ones; a file that does not verify
/// is deleted and nothing is left half done. It is the only part of the index that reaches the network, and only here.
/// </summary>
public interface IEmbeddingModelInstaller
{
    /// <exception cref="ArgumentException">The consent is not one (no one, or no time).</exception>
    Task<EmbeddingModelStatus> InstallAsync(EmbeddingModelDownloadConsent consent, IProgress<EmbeddingModelDownloadProgress>? progress, CancellationToken ct);
}

/// <summary>
/// A fragment as the index keeps it: the fragment and, when the model embedded it, its vector and the fingerprint of that
/// vector (its model's, and for an entry of the journal also the version of the view its meaning is computed from).
/// </summary>
public sealed record IndexedFragment(SemanticFragment Fragment, float[]? Vector = null, string? ModelFingerprint = null);

/// <summary>What the index holds of a fragment: its id, its fingerprint and the fingerprint of its vector (null without one).</summary>
public sealed record IndexedFragmentState(string Id, string Sha256, string? ModelFingerprint);

/// <summary>
/// The index of one project in its own SQLite database (docs/17 D-04; WP2), in
/// <see cref="SemanticIndexLocation.IndexFile"/>: a table of its own with each fragment (text, source and its hash, part,
/// time, fingerprint) and its vector in float32 with the fingerprint of the model, and an FTS5 table over the texts with
/// the tokenizer of the memory (<c>unicode61</c>). No sqlite-vec and no native extension: the cosine is computed in memory.
/// It stores and searches; it decides nothing about trust, which a search checks against the log every time.
/// </summary>
public interface ISemanticIndexStore
{
    /// <summary>
    /// The id, fingerprint and model of every fragment held: what the refresh compares to know what to add, embed or remove.
    /// A row that is not a fragment (blank text, more than <see cref="SemanticFragment.MaxCharacters"/>, a source or a hash
    /// that is not one) is given with an empty <see cref="IndexedFragmentState.Sha256"/>, so the next refresh or search writes
    /// it again; <see cref="ReadAsync"/> and <see cref="ReadIndexedAsync"/> skip it, and no read ever fails for one row.
    /// </summary>
    Task<IReadOnlyList<IndexedFragmentState>> ReadStateAsync(ProjectContext project, CancellationToken ct);

    /// <summary>
    /// Adds or replaces each fragment by its id. A fragment whose fingerprint changed loses the vector it had unless a new
    /// one comes with it; a vector always comes with the fingerprint of its model and has the model's dimensions.
    /// </summary>
    Task UpsertAsync(ProjectContext project, IReadOnlyList<IndexedFragment> fragments, CancellationToken ct);

    /// <summary>Removes these fragments (their source left the log's trust, or the text that made them is gone).</summary>
    Task RemoveAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct);

    /// <summary>The fragments with these ids, in that order; an id the index does not hold, or whose row is not a fragment, is skipped.</summary>
    Task<IReadOnlyList<SemanticFragment>> ReadAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct);

    /// <summary>
    /// The fragments with these ids as the index holds them, each with its vector and the fingerprint of its model when it
    /// has one (<see cref="IndexedFragment"/>), in that order; an id the index does not hold, or whose row is not a fragment,
    /// is skipped. It is what the snapshot of the planner's tool takes its vectors from
    /// (<see cref="ISemanticIndex.SnapshotAsync"/>, WP3).
    /// </summary>
    Task<IReadOnlyList<IndexedFragment>> ReadIndexedAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct);

    /// <summary>
    /// The lexical list: FTS5 over the texts for <paramref name="terms"/> (<see cref="LexicalTerms.From"/>), each quoted as a
    /// phrase with its quotes doubled and joined by <c>OR</c>, ranked by <c>bm25</c>, the best first, ties by time (newest
    /// first) and id; at most <paramref name="top"/>. No terms, no list.
    /// </summary>
    Task<IReadOnlyList<RankedCandidate>> SearchWordsAsync(ProjectContext project, IReadOnlyList<string> terms, int top, CancellationToken ct);

    /// <summary>
    /// The semantic list: the dot product of <paramref name="query"/> (normalised) with every vector of the model
    /// <paramref name="modelFingerprint"/>, the best first, ties by time (newest first) and id; at most <paramref name="top"/>.
    /// Each place carries that product, its similarity (<see cref="SimilarCandidate.Similarity"/>), which the search compares
    /// with the cut calibrated for the model (<see cref="MeaningOnlyCut"/>). Fragments without a vector of that model are not
    /// in it.
    /// </summary>
    Task<IReadOnlyList<SimilarCandidate>> SearchVectorsAsync(ProjectContext project, ReadOnlyMemory<float> query, string modelFingerprint, int top, CancellationToken ct);

    /// <summary>Deletes the whole index of the project (its folder): nothing is lost, the next refresh rebuilds it.</summary>
    Task DeleteAsync(ProjectContext project, CancellationToken ct);
}

/// <summary>How a search went (docs/17 D-05, F12-6).</summary>
public enum SemanticSearchMode
{
    /// <summary>Words and meaning: the lexical and the semantic lists, fused (<see cref="HybridRanking"/>).</summary>
    Hybrid,

    /// <summary>Only the lexical list, and the search says so (<see cref="WordsOnlyReason"/>).</summary>
    WordsOnly,
}

/// <summary>Why a search went by words only.</summary>
public enum WordsOnlyReason
{
    /// <summary>The model is not downloaded.</summary>
    ModelMissing,

    /// <summary>A file of the model does not give its pinned SHA-256: it was not loaded.</summary>
    ModelMismatch,

    /// <summary>The model verifies but could not be loaded.</summary>
    ModelUnloadable,

    /// <summary>The model is there, but the index has not embedded its fragments yet (it does so in the background).</summary>
    VectorsPending,

    /// <summary>
    /// The semantic index could not be used (it is not composed, or it failed): the context packer ranked its tiers by the
    /// full-text search of the memory, as before the index (<c>ContextPack.Relevance</c>).
    /// </summary>
    IndexUnavailable,
}

/// <summary>What a search by words only says to the person (F12-6: «sin modelo, busca por palabras y lo dice»), in Spanish.</summary>
public static class SemanticSearchTexts
{
    /// <summary>The notice of a search that went by words only, for <paramref name="reason"/>.</summary>
    public static string WordsOnly(WordsOnlyReason reason) => reason switch
    {
        WordsOnlyReason.ModelMissing => "Solo por palabras: el modelo de búsqueda por significado no está descargado.",
        WordsOnlyReason.ModelMismatch => "Solo por palabras: el modelo descargado no coincide con su huella y no se ha cargado.",
        WordsOnlyReason.ModelUnloadable => "Solo por palabras: el modelo de búsqueda por significado no se ha podido cargar.",
        WordsOnlyReason.VectorsPending => "Solo por palabras: el índice aún está calculando el significado de los fragmentos.",
        WordsOnlyReason.IndexUnavailable => "Solo por palabras: el índice semántico no se ha podido usar y se ha buscado en la memoria.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason for a search by words only."),
    };

    /// <summary>
    /// The notice of a search by meaning whose model has no cut calibrated (<see cref="MeaningOnlyCut"/>): everything the
    /// meaning finds may come, however little it resembles the search (ADR-0047, addendum of 2026-11-04).
    /// </summary>
    public const string NoCut = "Sin corte de relevancia para este modelo: lo que encuentra el significado entra aunque se parezca poco.";
}

/// <summary>
/// A search of the index (docs/17 D-05 and D-06): the text, how many results at most, and what may come.
/// <list type="bullet">
/// <item><see cref="Kinds"/>: the categories of memory (null, all); <see cref="IncludeLog"/>: whether the fragments of the
/// log's entries may come too.</item>
/// <item><see cref="TrustedOnly"/>: only what may reach a prompt (<see cref="MayReachAPrompt"/>: trusted, not superseded,
/// and never a daily summary). The packer and the MCP snapshot never ask for anything else; the «Memoria» document may, to
/// show the rest with its trust.</item>
/// </list>
/// </summary>
public sealed record SemanticQuery(string Text, int Top = 10)
{
    /// <summary>
    /// Whether <paramref name="entry"/> may reach a prompt through the index, which is all a <see cref="TrustedOnly"/> search
    /// and the snapshot of the planner's tool ever give (<see cref="ISemanticIndex.SnapshotAsync"/>): trusted, not
    /// superseded, and never a daily summary (<see cref="MemoryKind.DailySummary"/>) whatever its origin, since it quotes the
    /// «Hecho» lines of the journal and ADR-0023 keeps it out of the prompts. The journal reaches them only as its anchored
    /// entries (<see cref="MemoryKind.JournalEntry"/>), with their own trust.
    /// </summary>
    public static bool MayReachAPrompt(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Trust == MemoryTrust.Trusted && entry.SupersededBy is null && entry.Kind != MemoryKind.DailySummary;
    }

    public IReadOnlyList<MemoryKind>? Kinds { get; init; }

    public bool IncludeLog { get; init; } = true;

    public bool TrustedOnly { get; init; } = true;
}

/// <summary>
/// One result of a search: the fragment that ranked, a label of what it is in Spanish («decisión», «bitácora»,
/// «registro · veredicto»…), the memory entry it cites as recall gives it now (anchored, with its trust; null for a
/// fragment of the log) and its place in the fusion.
/// </summary>
public sealed record SemanticHit(SemanticFragment Fragment, string Label, MemoryEntry? Entry, FusedCandidate Rank)
{
    /// <summary>
    /// The identifier of the search this hit contains exactly (a run, a number of the journal, a day, a memory id, as the
    /// search wrote it), when it went first for it, before the fusion; null for the rest.
    /// </summary>
    public string? Identifier { get; init; }
}

/// <summary>
/// The cut of the semantic list of a search (ADR-0047, addenda of 2026-11-03 and 2026-11-04): a fragment has a place by
/// meaning only when its similarity reaches <paramref name="Minimum"/> and, with a <paramref name="Margin"/>, comes within it
/// of the best similarity of the search, whether the lexical list has it too or not. So what only the meaning finds enters
/// the fusion only above the cut, and what the words find enters by its lexical place whatever its similarity. It is
/// calibrated for each model and kept by the fingerprint of the model, never in its descriptor, so a descriptor built anew
/// keeps its cut.
/// </summary>
public sealed record MeaningOnlyCut(float Minimum, float? Margin = null);

/// <summary>
/// The results of a search, best first, how it went and, when it went by words only, why. <see cref="Cut"/> is the cut its
/// semantic list had: null when there was no semantic list, or when the model has none calibrated (then everything the
/// meaning found could enter, and <see cref="CutNotice"/> says so).
/// </summary>
public sealed record SemanticSearchResult(IReadOnlyList<SemanticHit> Hits, SemanticSearchMode Mode, WordsOnlyReason? Reason = null)
{
    public MeaningOnlyCut? Cut { get; init; }

    /// <summary>What to tell the person about how the search went: null for a hybrid search.</summary>
    public string? Notice => Reason is { } reason ? SemanticSearchTexts.WordsOnly(reason) : null;

    /// <summary>What to tell the person about the cut: <see cref="SemanticSearchTexts.NoCut"/> for a hybrid search without one; null otherwise.</summary>
    public string? CutNotice => Mode == SemanticSearchMode.Hybrid && Cut is null ? SemanticSearchTexts.NoCut : null;
}

/// <summary>What a refresh did: fragments added, removed, embedded now and left as they were; and why it stopped, in Spanish, if it did.</summary>
public sealed record SemanticIndexRefresh(int Added, int Removed, int Embedded, int Unchanged, string? Reason = null)
{
    public bool Completed => Reason is null;
}

/// <summary>
/// The state of the index of a project: its fragments, how many have a vector of the current model, the model, and the cut
/// calibrated for it (<see cref="Cut"/>; null when the table has none, and then <see cref="CutNotice"/> says so while the
/// model is ready).
/// </summary>
public sealed record SemanticIndexStatus(int Fragments, int WithVectors, EmbeddingModelStatus Model)
{
    public MeaningOnlyCut? Cut { get; init; }

    /// <summary>What «Memoria» tells the person about the cut: <see cref="SemanticSearchTexts.NoCut"/> when the model is ready and has none; null otherwise.</summary>
    public string? CutNotice => Model.IsReady && Cut is null ? SemanticSearchTexts.NoCut : null;
}

/// <summary>
/// The semantic index of the projects (docs/17 D-01 to D-06; WP3): a local projection of what the decision log anchors,
/// rebuilt at will.
/// <list type="bullet">
/// <item><b>What goes in</b> (D-01): the anchored memory (the journal entries, <see cref="MemoryKind.JournalEntry"/>,
/// included) and the texts the system writes from entries of the log, each cut in fragments of at most
/// <see cref="SemanticFragment.MaxCharacters"/>, redacted. Never credentials, the folders of the CLIs, the source code,
/// the transcripts of the agents or raw evidence. Rejected, retracted and superseded entries leave it.</item>
/// <item><b>The search</b> (D-05): the lexical list (<see cref="LexicalTerms"/>) and, with a model, the semantic one
/// (<see cref="EmbeddingRole.Query"/>), fused by <see cref="HybridRanking.Fuse"/>; without one, words only, and the result
/// says why (<see cref="SemanticSearchResult.Notice"/>). Every hit is checked against the log now: a fragment whose source
/// no longer verifies, or whose fingerprint is not the one its source gives, never comes out (F12-2).</item>
/// <item><b>Who uses it</b> (D-06): the packer of every agent with memory (<c>ReadSelected</c>), in its tiers by relevance;
/// the «Memoria» document; and the snapshot of the planner's MCP tool. Never a judge nor an auditor (F12-8).</item>
/// </list>
/// </summary>
public interface ISemanticIndex
{
    /// <summary>
    /// Brings the index of the project up to date with what is anchored: new fragments in, gone ones out, and the vectors
    /// that are missing or of another model computed with the local model when there is one. Idempotent; meant to run in
    /// the background and at low priority after each derivation of the memory. The index lives outside the project, so it
    /// does not wait for a run to end.
    /// </summary>
    Task<SemanticIndexRefresh> RefreshAsync(ProjectContext project, CancellationToken ct);

    Task<SemanticSearchResult> SearchAsync(ProjectContext project, SemanticQuery query, CancellationToken ct);

    /// <summary>
    /// Several searches, in their order, as if each were one <see cref="SearchAsync"/>, but over one reading of what is
    /// anchored and of the index, with each text embedded once: what the context packer asks for its tiers by relevance,
    /// which share their focus. An implementation that cannot share anything may answer one search at a time, which is what
    /// this default does.
    /// </summary>
    async Task<IReadOnlyList<SemanticSearchResult>> SearchManyAsync(ProjectContext project, IReadOnlyList<SemanticQuery> queries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var results = new List<SemanticSearchResult>(queries.Count);
        foreach (var query in queries)
        {
            results.Add(await SearchAsync(project, query, ct).ConfigureAwait(false));
        }

        return results;
    }

    Task<SemanticIndexStatus> StatusAsync(ProjectContext project, CancellationToken ct);

    /// <summary>
    /// The fragments of the project that may reach a prompt (<see cref="SemanticQuery.MayReachAPrompt"/>: trusted, not
    /// superseded and never a daily summary) for the planner's MCP tool (D-06), newest first and at most
    /// <paramref name="maxFragments"/>, with their vectors when they have one of the current model.
    /// </summary>
    Task<ProjectHistorySnapshot> SnapshotAsync(ProjectContext project, int maxFragments, CancellationToken ct);

    /// <summary>Deletes the index of the project; the next refresh rebuilds the same fragments with the same fingerprints (F12-3).</summary>
    Task DeleteAsync(ProjectContext project, CancellationToken ct);
}
