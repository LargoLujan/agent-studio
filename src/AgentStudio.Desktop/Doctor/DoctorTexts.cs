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
public static class DoctorTexts
{
    /// <summary>How many facts an explanation can quote; the rest of its placeholders show a dash instead of failing.</summary>
    public const int ArgumentCount = 6;

    private const string CodePrefix = "doctor.";
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
    public static string FixKey(string fixCode) => "Doctor_Fix_" + Part(DoctorFixCatalog.TextFamily(fixCode));

    /// <summary>The key of the name of a state («Correcto», «Aviso»…).</summary>
    public static string StatusKey(DoctorStatus status) => "Doctor_Status_" + status;

    /// <summary>The key of the name of a section.</summary>
    public static string SectionKey(DoctorSection section) => "Doctor_Section_" + section;

    /// <summary>The title of a check; it may quote the facts too (the name of a program of an agent), so the several providers are told apart.</summary>
    public static string Title(IUiTextProvider texts, DoctorCheck check)
    {
        var key = TitleKey(check.Code);

        // A check of another package that has no text yet (the declared environment, until the integration adds its own) is named by what it declares, or by its code: never by a missing key.
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
