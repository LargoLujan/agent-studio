namespace AgentStudio.Domain.Common;

/// <summary>
/// A coded text that another coded text carries as an argument (Phase 14, ADR-0054 addendum): a sentence of the system is made of
/// pieces (who, when, how many, in which state), and each piece is a coded text of its own, so every language puts the pieces in its
/// own order. The desktop paints the pieces first and the sentence with them (<c>NestedTextRendering</c>). <c>UserText</c> is the
/// only one; the interface has no member because a piece is nothing more than a code and its arguments.
/// </summary>
public interface INestedText
{
}

/// <summary>
/// The arguments a coded text may carry (docs/17 D-47): the layers below the desktop never write a sentence for the screen,
/// they return a code and its arguments, and the desktop paints them in the language of the person. The desktop formats
/// numbers, instants and durations with the culture of that language, so only these types are allowed: text (ids, names,
/// paths, as they are), whole and decimal numbers, an instant and a duration. Anything else is refused, so an argument never
/// reaches the screen as the name of a type.
/// </summary>
public static class TextArguments
{
    /// <summary>Whether <paramref name="value"/> is an argument a coded text may carry.</summary>
    public static bool IsAllowed(object? value) =>
        value is string or int or long or decimal or double or DateTimeOffset or TimeSpan or INestedText;

    /// <summary>The arguments, checked: a copy the caller cannot change afterwards.</summary>
    /// <exception cref="ArgumentException">An argument is null or of a type a coded text does not carry.</exception>
    public static IReadOnlyList<object> Checked(IEnumerable<object>? arguments, string parameterName)
    {
        var list = arguments?.ToList() ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            if (!IsAllowed(list[i]))
            {
                throw new ArgumentException(
                    $"Argument {i} of a coded text is {(list[i] is null ? "null" : list[i].GetType().Name)}; only text, numbers, instants, durations and nested coded texts are allowed.",
                    parameterName);
            }
        }

        return list.AsReadOnly();
    }
}
