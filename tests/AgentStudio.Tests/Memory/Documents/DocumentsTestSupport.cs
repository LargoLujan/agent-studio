using System.IO;
using System.Text;
using AgentStudio.Application.Documents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Memory.Documents;

/// <summary>A model made by hand with every kind of block and text, so the golden files do not depend on any store.</summary>
internal static class GoldenModel
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public static readonly DateTimeOffset At = new(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(2));

    public static ClientDocument Build() => new(
        new ClientDocumentCover("Mi tienda", "Hito 1", At, 2, Commit),
        [
            new(ClientDocumentSectionKind.Summary,
            [
                new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.SummaryFigures, 3, 2, 1, 120, 0, 2)),
                new ClientParagraph(ClientText.ByAgent("Primera línea con <b>etiqueta</b> y * asteriscos.\nSegunda línea.", new ClientAgentMark(new RunId("aux-7"), true))),
            ]),
            new(ClientDocumentSectionKind.Scope,
            [
                new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ScopeIntro)),
                new ClientTable(
                    [ClientText.OfSystem(ClientDocumentTexts.ColId), ClientText.OfSystem(ClientDocumentTexts.ColRequirement), ClientText.OfSystem(ClientDocumentTexts.ColIntegrated)],
                    [
                        [ClientText.Written("R1"), ClientText.Written("Pagar con tarjeta | sin recargos"), ClientText.OfSystem(ClientDocumentTexts.Yes)],
                        [ClientText.Written("R2"), ClientText.Written("- Catálogo con búsqueda"), ClientText.OfSystem(ClientDocumentTexts.Unknown)],
                    ]),
                new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.ScopeDeliveryNote, Commit[..12])),
            ]),
            new(ClientDocumentSectionKind.Verification,
            [
                new ClientList(Ordered: true, [ClientText.Written("dotnet test"), ClientText.ByAgent("Revisado a mano", new ClientAgentMark(new RunId("aux-8"), false))]),
            ]),
            new(ClientDocumentSectionKind.Decisions, [new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.DecisionsNone))]),
            new(ClientDocumentSectionKind.Changes,
            [
                new ClientList(Ordered: false, [ClientText.OfSystem(ClientDocumentTexts.ChangeDelivery, "refs/heads/main", Commit[..12], At.ToUniversalTime())]),
            ]),
            new(ClientDocumentSectionKind.RisksAndPending, [new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.RisksNone))]),
            new(ClientDocumentSectionKind.Annexes,
            [
                new ClientEvidenceList([new ClientEvidenceReference(new EvidenceId("ev-1"), new string('a', 64), ClientText.Written("Prueba de la caja"))]),
                new ClientParagraph(ClientText.OfSystem(ClientDocumentTexts.NotIncludedIntro)),
                new ClientList(Ordered: false, [ClientText.OfSystem(ClientDocumentTexts.NotIncludedPhase9), ClientText.OfSystem(ClientDocumentTexts.NotIncludedDeployed)]),
            ]),
        ]);

    public static ClientDocumentBuild Prepared() => new(Build(), ClientDocumentDisclosure.Of(Build(), 1, ["~\\x"], 2, includesConsumption: false));
}

/// <summary>Golden files: the expected bytes next to the tests, compared with line ends normalised; <c>AGENTSTUDIO_UPDATE_GOLDEN=1</c> rewrites them.</summary>
internal static class Golden
{
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static void Check(string area, string name, string actual)
    {
        var source = Path.Combine(RepositoryRoot, "tests", "AgentStudio.Tests", area, "Fixtures", name);
        if (Environment.GetEnvironmentVariable("AGENTSTUDIO_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, Normalize(actual), new UTF8Encoding(false));
        }

        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, area, "Fixtures", name), Encoding.UTF8);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }
}
