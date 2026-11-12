using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Application.Security;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// F14-4 and docs/17 D-48 for the orchestrator: what reaches the plan, the agents and the decision log does not change with the
/// language; and every key the code and the XAML name by a way the test of keys cannot see exists in both languages.
/// </summary>
public sealed partial class OrchestratorProtocolTextsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"\bTexts\.|ResourceTextProvider|Localization")]
    private static partial Regex ReadsTheResources();

    [GeneratedRegex(@"FormatTextConverter\.Instance\}, ConverterParameter=([A-Za-z0-9_]+)")]
    private static partial Regex ConverterKey();

    private static string Describe()
    {
        var kinds = Enum.GetValues<WorkTaskKind>().Select(OrchestratorProtocolTexts.NewNodeTitle);
        var severities = Enum.GetValues<Severity>().Select(OrchestratorProtocolTexts.Severity);
        return string.Join("|", kinds.Concat(severities).Concat([
            OrchestratorProtocolTexts.IntegrationTitle,
            OrchestratorProtocolTexts.ConfirmationPending,
            OrchestratorProtocolTexts.LockNotAsked("x"),
            .. Enum.GetValues<WindowsLockResult>().Select(OrchestratorProtocolTexts.LockNotConfirmed),
        ]));
    }

    [Fact]
    public void The_protocol_texts_are_the_same_in_every_language_and_never_read_the_resources()
    {
        var source = File.ReadAllText(Path.Combine(DesktopRoot, "Orchestrator", "OrchestratorProtocolTexts.cs"));
        Assert.DoesNotMatch(ReadsTheResources(), string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal))));

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            var inSpanish = Describe();
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Assert.Equal(inSpanish, Describe());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        Assert.Equal("Condición nueva", OrchestratorProtocolTexts.NewNodeTitle(WorkTaskKind.Condition));
    }

    [Fact]
    public void Every_key_a_converter_parameter_names_exists_in_both_languages()
    {
        var keys = Directory.GetFiles(Path.Combine(DesktopRoot, "Views", "Orchestrator"), "*.xaml")
            .SelectMany(f => ConverterKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(Spanish.Has(key) && English.Has(key), key);
            Assert.NotEqual(Spanish.Get(key), English.Get(key));
        });
    }

    [Fact]
    public void Every_title_of_an_approval_and_every_role_has_its_text_in_both_languages()
    {
        var known = (System.Collections.IDictionary)typeof(ApprovalTitles).GetField("Known", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(13, known.Count);
        foreach (string key in known.Values)
        {
            Assert.True(Spanish.Has(key) && English.Has(key), key);
        }

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            Assert.True(Spanish.Has("Canvas_Role_" + role) && English.Has("Canvas_Role_" + role), role.ToString());
            Assert.False(NodeEditorViewModel.RoleLabel(role).StartsWith('['), role.ToString());
        }
    }
}
