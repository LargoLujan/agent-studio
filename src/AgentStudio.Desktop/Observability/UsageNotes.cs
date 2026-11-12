using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Observability;

/// <summary>
/// The notes the usage projection writes in the language of the source (why a project was left out of the report, what could not be read,
/// how the journal differs from the log) said in the language in use. The projection keeps them as they are, because the export keeps them
/// too; the screen reads each one against the texts of the neutral resources (the language of the source) and says it again from the
/// resources of the language in use. A note that matches none (a message of the system, a text appended later) is shown as it is.
/// </summary>
internal static class UsageNotes
{
    private static readonly string[] Keys =
    [
        "Usage_Note_NoJournal",
        "Usage_Note_NoPhase4",
        "Usage_Note_JournalUnreadable",
        "Usage_Note_Difference",
        "Usage_Note_NoStart",
        "Usage_Note_EntryUnreadableActivity",
        "Usage_Note_EntryUnreadable",
        "Usage_Note_FigureUnreadable",
        "Usage_Note_LogUnreadable",
        "Usage_Note_LogInvalidAt",
        "Usage_Note_LogInvalid",
        "Usage_Note_Unanchored",
        "Usage_Note_AnotherProject",
        "Usage_Note_AnchorUnreadable",
        "Usage_Note_Untrusted",
        "Usage_Note_PathInvalid",
    ];

    private static readonly Lazy<ResourceTextProvider> Source = new(() => new ResourceTextProvider(CultureInfo.GetCultureInfo("es")));

    private static readonly Lazy<IReadOnlyList<(string Key, Regex Pattern, int Arguments)>> Templates = new(Build);

    /// <summary>The note in the language in use; the note itself when it is not one the projection is known to write.</summary>
    public static string Translate(string note)
    {
        foreach (var (key, pattern, count) in Templates.Value)
        {
            if (pattern.Match(note) is not { Success: true } match)
            {
                continue;
            }

            // Some notes start with the root of the project they are about.
            var prefix = match.Groups["prefix"].Value;
            var arguments = new object?[count];
            for (var i = 0; i < count; i++)
            {
                arguments[i] = match.Groups["g" + i.ToString(CultureInfo.InvariantCulture)].Value;
            }

            return prefix + Say(key, arguments);
        }

        return note;
    }

    private static string Say(string key, object?[] arguments)
    {
        switch (key)
        {
            case "Usage_Note_Difference" when TryNumber(arguments[0], out var prompts) && TryNumber(arguments[1], out var attempts) && TryNumber(arguments[2], out var difference):
                return Texts.Format(key, Plural(prompts, "Usage_Count_Prompt"), Plural(attempts, "Usage_Count_Attempt"), Plural(difference, "Usage_Count_Prompt"));
            case "Usage_Note_NoStart" when Equals(arguments[2], Source.Value.Get("Usage_Note_TheRun")):
                arguments[2] = Texts.Get("Usage_Note_TheRun");
                break;
        }

        return Texts.Format(key, arguments);
    }

    private static string Plural(int count, string key) => Texts.Format(key + (count == 1 ? "_One" : "_Other"), count);

    private static bool TryNumber(object? text, out int number)
    {
        number = 0;
        var digits = new string(((string?)text ?? string.Empty).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static List<(string Key, Regex Pattern, int Arguments)> Build()
    {
        var templates = new List<(string, Regex, int)>();
        foreach (var key in Keys)
        {
            var source = Source.Value.Get(key);
            var count = 0;
            var body = Regex.Replace(Regex.Escape(source), @"\\\{(\d)\}", m =>
            {
                count = Math.Max(count, m.Groups[1].Value[0] - '0' + 1);
                return "(?<g" + m.Groups[1].Value + ">.*?)";
            });
            templates.Add((key, new Regex("^(?<prefix>.+?: )?" + body + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), count));
        }

        return templates;
    }
}
