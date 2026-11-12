using System.Globalization;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// A provider that remembers each template it hands out (the text of a key before its placeholders are filled), for the check of
/// the language of the inventory: every text of the interface comes through the provider, and the synthetic data does not.
/// </summary>
internal sealed class RecordingTextProvider(IUiTextProvider inner) : IUiTextProvider
{
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);

    public IReadOnlyList<(string Where, string Text)> Templates => [.. _templates.Select(t => ("key " + t.Key, t.Value))];

    public CultureInfo Culture => inner.Culture;

    public bool Has(string key) => inner.Has(key);

    public string Get(string key)
    {
        var text = inner.Get(key);
        _templates[key] = text;
        return text;
    }

    public string Format(string key, params object?[] arguments)
    {
        _templates[key] = inner.Get(key);
        return inner.Format(key, arguments);
    }

    public string Render(UserText text)
    {
        _templates[text.ResourceKey] = inner.Get(text.ResourceKey);
        return inner.Render(text);
    }
}
