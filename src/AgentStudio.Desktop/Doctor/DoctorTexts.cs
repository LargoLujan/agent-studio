using System.Text.RegularExpressions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Desktop.Doctor;

/// <summary>
/// The texts of Auto Doctor, from the code of a check and its state (docs/20 D-01, ADR-0054): the title is <c>Doctor_Check_&lt;code&gt;_Title</c>, the
/// explanation <c>Doctor_Check_&lt;code&gt;_&lt;State&gt;</c> and the sentence of a fix <c>Doctor_Fix_&lt;code&gt;</c>, each code with <c>.</c> and <c>-</c> as <c>_</c> and
/// without <c>doctor.</c>. The checks of a provider are one family for every provider (<c>doctor.provider.&lt;id&gt;.install</c> uses the texts of
/// <c>doctor.provider.install</c>, with the name of the program as a fact), so a provider that appears later does not show a missing key.
/// The facts of the check are the arguments of the explanation, in their order, up to <see cref="ArgumentCount"/>.
/// </summary>
public static partial class DoctorTexts
{
    /// <summary>How many facts an explanation can quote; the rest of its placeholders show a dash instead of failing.</summary>
    public const int ArgumentCount = 6;

    private const string CodePrefix = "doctor.";
    private const string FixPrefix = "doctor.fix.";
    private const string ProviderPrefix = "doctor.provider.";
    private const string SourceFailedPrefix = "doctor.source-failed.";

    /// <summary>The code of the texts of <paramref name="code"/>: the family it belongs to.</summary>
    public static string Family(string code)
    {
        if (code.StartsWith(ProviderPrefix, StringComparison.Ordinal))
        {
            var lastDot = code.LastIndexOf('.');
            return lastDot > ProviderPrefix.Length - 1 ? ProviderPrefix + code[(lastDot + 1)..] : code;
        }

        return code.StartsWith(SourceFailedPrefix, StringComparison.Ordinal) ? "doctor.source-failed" : code;
    }

    /// <summary>The key of the title of a check.</summary>
    public static string TitleKey(string code) => "Doctor_Check_" + Part(Family(code)) + "_Title";

    /// <summary>The key of the explanation of a check in a state.</summary>
    public static string ExplanationKey(string code, DoctorStatus status) => "Doctor_Check_" + Part(Family(code)) + "_" + status;

    /// <summary>The key of the sentence of a fix.</summary>
    public static string FixKey(string fixCode)
    {
        var family = DoctorFixCatalog.TextFamily(fixCode);
        return "Doctor_Fix_" + (family.StartsWith(FixPrefix, StringComparison.Ordinal) ? family[FixPrefix.Length..] : family).Replace('.', '_').Replace('-', '_');
    }

    /// <summary>The key of the name of a state («Correcto», «Aviso»…).</summary>
    public static string StatusKey(DoctorStatus status) => "Doctor_Status_" + status;

    /// <summary>The key of the name of a section.</summary>
    public static string SectionKey(DoctorSection section) => "Doctor_Section_" + section;

    /// <summary>The title of a check; it may quote the facts too (the name of a program of an agent), so the several providers are told apart.</summary>
    public static string Title(IUiTextProvider texts, DoctorCheck check)
    {
        var key = TitleKey(check.Code);

        // A check of another package that has no text yet is named by what it declares, or by its code: never by a missing key.
        return texts.Has(key) ? texts.Format(key, Arguments(check)) : check.Fact(DoctorFactNames.Declared) ?? check.Code;
    }

    public static string Explanation(IUiTextProvider texts, DoctorCheck check)
    {
        var key = ExplanationKey(check.Code, check.Status);
        return texts.Format(texts.Has(key) ? key : "Doctor_Check_Generic_" + check.Status, Arguments(check));
    }

    /// <summary>The sentence of a fix, with the name of the program of the check when it has one (the guides of the providers).</summary>
    public static string FixSentence(IUiTextProvider texts, string fixCode, DoctorCheck check)
    {
        var key = FixKey(fixCode);
        return texts.Has(key) ? texts.Format(key, Arguments(check)) : texts.Get("Doctor_Fix_Generic");
    }

    /// <summary>The label of a fact in «Detalles»; a fact with no text of its own shows its name.</summary>
    public static string FactLabel(IUiTextProvider texts, string name)
    {
        var key = "Doctor_Fact_" + Part(name);
        return texts.Has(key) ? texts.Get(key) : name;
    }

    /// <summary>
    /// The value of a fact as the panel paints it. The declared environment writes some values as fixed English words and phrases
    /// (<c>not in the NuGet cache</c>, <c>docker running (client 27.0, engine 27.0)</c>, <c>image redis:7</c>): they are painted by code in the language in use, with
    /// the part that came from a file of the project (a version, a path) as an argument. Any other value, and every fact of another section, is shown as it is.
    /// </summary>
    public static string FactValue(IUiTextProvider texts, DoctorCheck check, DoctorFact fact)
    {
        if (!check.Code.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
        {
            return fact.Value;
        }

        if (fact.Name is DoctorFactNames.Installed or DoctorFactNames.Declared)
        {
            // A fixed phrase is only the state of «Installed»: the same word in «Declared» came from a file of the project and is data.
            if (fact.Name == DoctorFactNames.Installed && FixedValues.TryGetValue(fact.Value, out var token))
            {
                return Translated(texts, token, fact.Value);
            }

            foreach (var (pattern, key) in ValuePatterns)
            {
                var match = pattern.Match(fact.Value);
                if (match.Success)
                {
                    var arguments = new object?[match.Groups.Count - 1];
                    for (var i = 1; i < match.Groups.Count; i++)
                    {
                        arguments[i - 1] = match.Groups[i].Value;
                    }

                    return texts.Has(key) ? texts.Format(key, arguments) : fact.Value;
                }
            }
        }

        return fact.Value;
    }

    private const string EnvironmentPrefix = "doctor.env.";

    // The values the comparer writes as one fixed phrase, by the key of their text.
    private static readonly IReadOnlyDictionary<string, string> FixedValues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["none"] = "Doctor_Value_none",
        ["absent"] = "Doctor_Value_absent",
        ["store-alias"] = "Doctor_Value_store_alias",
        ["in the NuGet cache"] = "Doctor_Value_in_nuget_cache",
        ["not in the NuGet cache"] = "Doctor_Value_not_in_nuget_cache",
        ["not checked"] = "Doctor_Value_not_checked",
        ["docker not installed"] = "Doctor_Value_docker_not_installed",
    };

    // The values the comparer builds from a phrase and a datum, in the order the comparer writes them; the groups are the arguments of the text.
    private static readonly (Regex Pattern, string Key)[] ValuePatterns =
    [
        (DockerRunning(), "Doctor_Value_docker_running"),
        (DockerStopped(), "Doctor_Value_docker_stopped"),
        (ComposeSize(), "Doctor_Value_compose_size"),
        (Labelled("path"), "Doctor_Value_path"),
        (Labelled("venv"), "Doctor_Value_venv"),
        (Labelled("image"), "Doctor_Value_image"),
        (Labelled("dockerfile"), "Doctor_Value_dockerfile"),
        (Labelled("compose file"), "Doctor_Value_compose_file"),
        (Labelled("extends"), "Doctor_Value_extends"),
    ];

    private static string Translated(IUiTextProvider texts, string key, string fallback) => texts.Has(key) ? texts.Get(key) : fallback;

    private static Regex Labelled(string label) => new("^" + Regex.Escape(label) + " (.+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [GeneratedRegex(@"^docker running \(client (.+), engine (.+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DockerRunning();

    [GeneratedRegex(@"^docker engine stopped \(client (.+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DockerStopped();

    [GeneratedRegex(@"^compose size (\d+) bytes$", RegexOptions.CultureInvariant)]
    private static partial Regex ComposeSize();
    private static object?[] Arguments(DoctorCheck check)
    {
        var arguments = new object?[ArgumentCount];
        for (var i = 0; i < ArgumentCount; i++)
        {
            arguments[i] = i < check.Facts.Count ? check.Facts[i].Value : "—";
        }

        return arguments;
    }

    private static string Part(string code)
    {
        var text = code.StartsWith(CodePrefix, StringComparison.Ordinal) ? code[CodePrefix.Length..] : code;
        return text.Replace('.', '_').Replace('-', '_');
    }
}
