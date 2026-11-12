using System.Globalization;
using AgentStudio.Application.Git;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration;

namespace AgentStudio.Tests.Orchestration.Integration;

/// <summary>
/// Wraps the delivery service of a test so that every answer it gives (ready or not, delivered or not) is checked for what the
/// screen needs (ADR-0054 addendum of 2026-11-12): a coded text next to the sentence, a known code, and a text in Spanish and in
/// English with every argument in place. The sentence itself stays as it was (the tests assert on it).
/// </summary>
internal sealed class CodedTextChecks(IDeliveryService inner) : IDeliveryService
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    public async Task<DeliveryReadiness> CheckAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        var readiness = await inner.CheckAsync(project, run, cancellationToken).ConfigureAwait(false);
        Verify(readiness.Reason, readiness.ReasonText);
        return readiness;
    }

    public async Task<DeliveryResult> DeliverAsync(ProjectContext project, RunId run, DeliveryMode mode, CancellationToken cancellationToken)
    {
        var result = await inner.DeliverAsync(project, run, mode, cancellationToken).ConfigureAwait(false);
        Verify(result.Message, result.MessageText);
        return result;
    }

    /// <summary>The sentence has its coded text, and the text is painted in both languages.</summary>
    public static void Verify(string sentence, UserText? text)
    {
        Assert.False(string.IsNullOrWhiteSpace(sentence));
        Assert.NotNull(text);
        Assert.Contains(text.Code, OrchestrationUserTexts.All);
        var spanish = Spanish.RenderNested(text);
        var english = English.RenderNested(text);
        Assert.False(string.IsNullOrWhiteSpace(spanish), text.Code);
        Assert.False(string.IsNullOrWhiteSpace(english), text.Code);
        Assert.DoesNotContain('{', spanish);
        Assert.DoesNotContain('{', english);
        Assert.NotEqual(spanish, english);
    }
}
