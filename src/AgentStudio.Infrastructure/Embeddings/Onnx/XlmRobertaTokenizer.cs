using AgentStudio.Application.Semantic;
using Microsoft.ML.Tokenizers;

namespace AgentStudio.Infrastructure.Embeddings.Onnx;

/// <summary>
/// The token ids an XLM-RoBERTa network expects (multilingual-e5-small; ADR-0047), from the SentencePiece model of the
/// pinned files and the id rule of the descriptor (<see cref="SentencePieceIds"/>, the fairseq rule): the text is cut in
/// pieces by <see cref="SentencePieceTokenizer"/> of <c>Microsoft.ML.Tokenizers</c>, an unknown piece becomes
/// <see cref="SentencePieceIds.Unknown"/> and every other piece its id plus <see cref="SentencePieceIds.PieceOffset"/>;
/// the pieces are cut so that, with <see cref="SentencePieceIds.BeginOfSentence"/> before and
/// <see cref="SentencePieceIds.EndOfSentence"/> after, there are at most <see cref="EmbeddingModelDescriptor.MaxTokens"/>.
/// The spike checked this gives the ids of the <c>sentencepiece</c> reference on 56 of 56 sentences
/// (docs/investigacion/2026-10-30-spike-embeddings.md). The text goes as it comes: trimming it is the embedder's.
/// </summary>
/// <remarks>
/// Safe to call from several threads: nothing says the SentencePiece tokenizer is, so its calls take turns (a query is
/// about 0.1 ms of it).
/// </remarks>
public sealed class XlmRobertaTokenizer
{
    private readonly SentencePieceTokenizer _pieces;
    private readonly SentencePieceIds _ids;
    private readonly Lock _gate = new();

    private XlmRobertaTokenizer(SentencePieceTokenizer pieces, SentencePieceIds ids, int maxTokens)
    {
        _pieces = pieces;
        _ids = ids;
        MaxTokens = maxTokens;
    }

    /// <summary>The most ids <see cref="Encode"/> gives, the two special ones included.</summary>
    public int MaxTokens { get; }

    /// <summary>The tokenizer of <paramref name="model"/> from the bytes of its SentencePiece model, which the caller has verified.</summary>
    /// <exception cref="ArgumentException">The descriptor allows fewer than three tokens.</exception>
    public static XlmRobertaTokenizer Create(byte[] sentencePieceModel, EmbeddingModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(sentencePieceModel);
        ArgumentNullException.ThrowIfNull(model);
        if (model.MaxTokens < 3)
        {
            throw new ArgumentException($"A model input holds at least one piece and the two special tokens; '{model.Name}' allows {model.MaxTokens}.", nameof(model));
        }

        using var stream = new MemoryStream(sentencePieceModel, writable: false);
        var pieces = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        return new XlmRobertaTokenizer(pieces, model.TokenIds, model.MaxTokens);
    }

    /// <summary>The ids of <paramref name="text"/> for the network: the begin of sentence, the pieces (cut) and the end of sentence.</summary>
    public long[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        IReadOnlyList<int> pieces;
        lock (_gate)
        {
            pieces = _pieces.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        }

        var count = Math.Min(pieces.Count, MaxTokens - 2);
        var ids = new long[count + 2];
        ids[0] = _ids.BeginOfSentence;
        for (var i = 0; i < count; i++)
        {
            var piece = pieces[i];
            ids[i + 1] = piece == _pieces.UnknownId ? _ids.Unknown : (long)piece + _ids.PieceOffset;
        }

        ids[^1] = _ids.EndOfSentence;
        return ids;
    }
}
