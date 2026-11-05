using AgentStudio.Domain.Semantic;

namespace AgentStudio.Tests.Domain.Semantic;

/// <summary>
/// The terms of a search text never fail for a lone surrogate (review of WP2, point 7): a pasted text or the
/// <c>\uD800</c> of a JSON can bring half of a pair, which normalization refuses. It is read as U+FFFD, which is neither a
/// letter nor a digit; a whole pair is kept as it is.
/// </summary>
public sealed class LexicalTermsSurrogateTests
{
    [Fact]
    public void A_lone_surrogate_never_makes_the_terms_throw()
    {
        Assert.Equal(["prueba", "x\uDC00y"], LexicalTerms.From("prueba \uD800 x\uDC00y"));
        Assert.Empty(LexicalTerms.From("\uD800"));
        Assert.Empty(LexicalTerms.From("\uDFFF\uD800"));
        Assert.False(LexicalTerms.IsFunctionWord("\uD800"));
        Assert.True(LexicalTerms.IsFunctionWord("de\uD800"));
    }

    [Fact]
    public void A_whole_surrogate_pair_is_kept_as_it_is()
    {
        Assert.Equal(["😀ok", "prueba"], LexicalTerms.From("😀ok la prueba"));
    }
}
