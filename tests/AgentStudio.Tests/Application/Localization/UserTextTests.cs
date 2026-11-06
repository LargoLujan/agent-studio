using AgentStudio.Application.Localization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Tests.Application.Localization;

/// <summary>
/// The coded text of the layers below the desktop (docs/17 D-47, ADR-0054): a code and its arguments, never a sentence. The
/// code names one resource key for every language, and only arguments the desktop can format with a culture are allowed.
/// </summary>
public sealed class UserTextTests
{
    [Theory]
    [InlineData("delivery.no-integration", "Text_delivery_no_integration")]
    [InlineData("quota.window-near", "Text_quota_window_near")]
    [InlineData("a", "Text_a")]
    [InlineData("model.reason.many-mandatory-requirements", "Text_model_reason_many_mandatory_requirements")]
    public void A_code_names_one_resource_key(string code, string key)
    {
        Assert.Equal(key, UserText.KeyOf(code));
        Assert.Equal(key, UserText.Of(code).ResourceKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Delivery.no-integration")]
    [InlineData("delivery..no")]
    [InlineData("delivery.")]
    [InlineData(".delivery")]
    [InlineData("1delivery")]
    [InlineData("entrega.sin-integración")]
    [InlineData("delivery no")]
    public void Anything_but_lowercase_words_is_not_a_code(string code)
    {
        Assert.False(UserText.IsCode(code));
        Assert.Throws<ArgumentException>(() => UserText.Of(code));
    }

    [Fact]
    public void The_arguments_are_text_numbers_instants_and_durations_and_nothing_else()
    {
        var text = UserText.Of("quota.window-near", "Claude", 47, 12L, 0.47m, 1.5d, new DateTimeOffset(2026, 11, 6, 14, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(3));

        Assert.Equal(7, text.Arguments.Count);
        Assert.Equal(text, UserText.Of("quota.window-near", "Claude", 47, 12L, 0.47m, 1.5d, new DateTimeOffset(2026, 11, 6, 14, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(3)));
        Assert.NotEqual(text, UserText.Of("quota.window-near", "Codex"));
        Assert.Throws<ArgumentException>(() => UserText.Of("quota.window-near", new object()));
        Assert.Throws<ArgumentException>(() => UserText.Of("quota.window-near", DateTime.Now));
        Assert.False(TextArguments.IsAllowed(null));
    }
}
