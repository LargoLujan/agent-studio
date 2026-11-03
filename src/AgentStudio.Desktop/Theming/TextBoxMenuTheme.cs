using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AgentStudio.Desktop.Theming;

/// <summary>
/// The menu of a text box (Cortar, Copiar, Pegar) in the theme. WPF builds it from internal types that derive from
/// <see cref="ContextMenu"/> and <see cref="MenuItem"/>, and an implicit style is looked up by the exact type of the element,
/// so the ContextMenu and MenuItem styles of Themes/Dark.xaml never reach it and it stays light. This puts the same two styles
/// under those internal types, in the application resources, once at startup.
/// </summary>
public static class TextBoxMenuTheme
{
    /// <summary>The internal types of PresentationFramework that the menu of a text box is made of, with the type each derives from.</summary>
    public static IReadOnlyList<(string TypeName, Type BaseType)> InternalTypes { get; } =
    [
        ("System.Windows.Documents.TextEditorContextMenu+EditorContextMenu", typeof(ContextMenu)),
        ("System.Windows.Documents.TextEditorContextMenu+EditorMenuItem", typeof(MenuItem)),
    ];

    /// <summary>Puts the theme styles of ContextMenu and MenuItem found in <paramref name="resources"/> under the internal types.</summary>
    /// <returns>How many internal types got a style: all of them, unless WPF renamed one (then that menu stays light).</returns>
    public static int Apply(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var applied = 0;
        foreach (var (typeName, baseType) in InternalTypes)
        {
            if (typeof(TextBoxBase).Assembly.GetType(typeName) is { } type && baseType.IsAssignableFrom(type) && resources[baseType] is Style style)
            {
                resources[type] = style;
                applied++;
            }
        }

        return applied;
    }
}
