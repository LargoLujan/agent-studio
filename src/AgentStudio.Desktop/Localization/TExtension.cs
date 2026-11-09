using System.Windows.Markup;

namespace AgentStudio.Desktop.Localization;

/// <summary>
/// A text of the resources in XAML (docs/17 D-47): <c>{l:T Info_WhatIsThis}</c>, with
/// <c>xmlns:l="clr-namespace:AgentStudio.Desktop.Localization"</c>. The value is read once, when the element is built: the
/// language only changes on restart (D-49), so nothing has to follow a change.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    /// <summary>The key of the text in the resources.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Texts.Get(Key);
}
