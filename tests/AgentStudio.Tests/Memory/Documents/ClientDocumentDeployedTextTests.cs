using AgentStudio.Application.Documents;
using AgentStudio.Application.Localization;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Memory.Documents;

/// <summary>
/// docs/18 P74: the document for clients does not carry the pull requests, so it still lists «desplegado» as not included, but it no longer says that Agent Studio «does not know»: since Phase 10 the
/// report of the milestone says it when the platform reports a deployment (docs/21 D-29). The sentence is the resource of <see cref="ClientDocumentTexts.NotIncludedDeployed"/>, in both languages.
/// </summary>
public sealed class ClientDocumentDeployedTextTests
{
    [Fact]
    public void The_not_included_sentence_about_deployment_points_to_the_report_of_the_milestone_and_to_the_platform_in_both_languages()
    {
        var key = UserText.KeyOf(ClientDocumentTexts.NotIncludedDeployed);
        var (neutral, satellites) = LocalizationTestSupport.ResourceFiles()["DocumentsStrings"];

        var spanish = LocalizationTestSupport.Read(neutral)[key];
        var english = LocalizationTestSupport.Read(satellites["en"])[key];

        Assert.Contains("informe del hito", spanish, StringComparison.Ordinal);
        Assert.Contains("GitHub", spanish, StringComparison.Ordinal);
        Assert.DoesNotContain("no lo sabe", spanish, StringComparison.Ordinal);
        Assert.Contains("milestone report", english, StringComparison.Ordinal);
        Assert.Contains("GitHub", english, StringComparison.Ordinal);
        Assert.DoesNotContain("does not know", english, StringComparison.Ordinal);
    }
}
