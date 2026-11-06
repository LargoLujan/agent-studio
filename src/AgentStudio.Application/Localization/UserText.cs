using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Localization;

/// <summary>
/// The codes of the texts that more than one package of Phase 13 writes or paints. A package with codes of its own keeps them
/// next to its code; every code has its resource in the desktop (<see cref="UserText.ResourceKey"/>).
/// </summary>
public static class UserTextCodes
{
    /// <summary>Why a task whose agent asked a question waits (docs/17 D-29): «Espera tu respuesta».</summary>
    public const string QuestionAwaitingAnswer = "question.awaiting-answer";

    /// <summary>A task or a run passed its cap of questions (docs/17 D-29): «Demasiadas preguntas: revisa los requisitos.».</summary>
    public const string QuestionTooMany = "question.too-many";
}

/// <summary>
/// A text for the person, as the layers below the desktop give it (docs/17 D-47, ADR-0054): a stable code and its arguments,
/// never a sentence. The desktop paints it in the language of the person from its resources, with numbers, instants and
/// durations in the culture of that language. The validator already works this way (<c>PlanViolation</c> and its
/// <c>PlanViolationText</c>); Phase 14 converts what Orchestration, Debate and Memory still write as sentences.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A code is lowercase words joined by <c>.</c> and <c>-</c>, such as <c>delivery.no-integration</c>: the resource it is
/// painted with is <see cref="ResourceKey"/>, the same for every language.</item>
/// <item>The arguments are text (ids, names and paths as they are), numbers, instants and durations
/// (<see cref="TextArguments"/>). Text of an agent is never an argument of a sentence of the system: it is shown apart, framed
/// as written by an agent.</item>
/// <item>What is anchored (the log, the evidence, the memory, the journal) is never translated, and neither are the prompts:
/// they are not texts of this kind (D-48). Exception messages and technical logs stay in English.</item>
/// </list>
/// </remarks>
public sealed record UserText
{
    /// <summary>The prefix of the resource key of every coded text.</summary>
    public const string KeyPrefix = "Text_";

    /// <exception cref="ArgumentException">A code that is not lowercase words joined by <c>.</c> and <c>-</c>, or an argument of a type a coded text does not carry.</exception>
    public UserText(string code, IReadOnlyList<object>? arguments = null)
    {
        if (!IsCode(code))
        {
            throw new ArgumentException($"'{code}' is not a code: lowercase letters and digits in words joined by '.' and '-'.", nameof(code));
        }

        Code = code;
        Arguments = TextArguments.Checked(arguments, nameof(arguments));
    }

    /// <summary>The stable code: tests, logs and the desktop refer to it, never to a sentence.</summary>
    public string Code { get; }

    /// <summary>The arguments of the sentence, in the order of its placeholders (<c>{0}</c>, <c>{1}</c>…).</summary>
    public IReadOnlyList<object> Arguments { get; }

    /// <summary>The key of the resource the desktop paints it with: <c>Text_</c> and the code with <c>.</c> and <c>-</c> as <c>_</c>.</summary>
    public string ResourceKey => KeyOf(Code);

    /// <summary>A coded text with its arguments.</summary>
    public static UserText Of(string code, params object[] arguments) => new(code, arguments);

    /// <summary>The resource key of <paramref name="code"/>: <c>delivery.no-integration</c> is <c>Text_delivery_no_integration</c>.</summary>
    /// <exception cref="ArgumentException">It is not a code.</exception>
    public static string KeyOf(string code)
    {
        if (!IsCode(code))
        {
            throw new ArgumentException($"'{code}' is not a code.", nameof(code));
        }

        return KeyPrefix + code.Replace('.', '_').Replace('-', '_');
    }

    /// <summary>Whether <paramref name="code"/> is a code: words of lowercase letters and digits, joined by single <c>.</c> or <c>-</c>.</summary>
    public static bool IsCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || !char.IsAsciiLetterLower(code[0]))
        {
            return false;
        }

        var previousWasSeparator = false;
        foreach (var c in code)
        {
            if (c is '.' or '-')
            {
                if (previousWasSeparator)
                {
                    return false;
                }

                previousWasSeparator = true;
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                previousWasSeparator = false;
            }
            else
            {
                return false;
            }
        }

        return !previousWasSeparator;
    }

    /// <summary>Two coded texts are equal when they have the same code and the same arguments, in order.</summary>
    public bool Equals(UserText? other) => other is not null && Code == other.Code && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => HashCode.Combine(Code, Arguments.Count);

    /// <summary>The code and its arguments, for logs and tests: never what the person reads.</summary>
    public override string ToString() => Arguments.Count == 0 ? Code : $"{Code}({string.Join(", ", Arguments)})";
}
