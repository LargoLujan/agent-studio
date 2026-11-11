namespace AgentStudio.Application.Documents;

/// <summary>
/// The codes of the sentences of the system in a document for clients (docs/17 D-58, D-59; ADR-0057). The model carries codes, never
/// sentences, so the same model is written in any language: <c>Desktop/Resources/DocumentsStrings.resx</c> has the text of each
/// (<see cref="AgentStudio.Application.Localization.UserText.ResourceKey"/>), and a test checks that none is missing.
/// </summary>
public static class ClientDocumentTexts
{
    public const string Title = "document.title";
    public const string CoverProject = "document.cover-project";
    public const string CoverMilestone = "document.cover-milestone";
    public const string CoverDate = "document.cover-date";
    public const string CoverVersion = "document.cover-version";
    public const string CoverCommit = "document.cover-commit";
    public const string DateValue = "document.date-value";
    public const string AgentReviewed = "document.agent-reviewed";
    public const string AgentNotReviewed = "document.agent-not-reviewed";
    public const string SummaryFigures = "document.summary-figures";
    public const string SummaryNoRequirements = "document.summary-no-requirements";
    public const string ScopeIntro = "document.scope-intro";
    public const string ScopeNone = "document.scope-none";
    public const string ColId = "document.col-id";
    public const string ColRequirement = "document.col-requirement";
    public const string ColPriority = "document.col-priority";
    public const string ColImplemented = "document.col-implemented";
    public const string ColReviewed = "document.col-reviewed";
    public const string ColIntegrated = "document.col-integrated";
    public const string PriorityMandatory = "document.priority-mandatory";
    public const string PriorityDesirable = "document.priority-desirable";
    public const string Yes = "document.yes";
    public const string No = "document.no";
    public const string VerificationTestsIntro = "document.verification-tests-intro";
    public const string VerificationNoTests = "document.verification-no-tests";
    public const string VerificationVerdictsIntro = "document.verification-verdicts-intro";
    public const string VerificationNoVerdict = "document.verification-no-verdict";
    public const string ColTest = "document.col-test";
    public const string ColPassed = "document.col-passed";
    public const string ColFailed = "document.col-failed";
    public const string ColSkipped = "document.col-skipped";
    public const string ColExit = "document.col-exit";
    public const string ColDate = "document.col-date";
    public const string ColStatus = "document.col-status";
    public const string ColConfidence = "document.col-confidence";
    public const string ColValid = "document.col-valid";
    public const string StatusSatisfied = "document.status-satisfied";
    public const string StatusNotSatisfied = "document.status-not-satisfied";
    public const string StatusNotVerifiable = "document.status-not-verifiable";
    public const string ConfidenceLow = "document.confidence-low";
    public const string ConfidenceMedium = "document.confidence-medium";
    public const string ConfidenceHigh = "document.confidence-high";
    public const string DecisionsIntro = "document.decisions-intro";
    public const string DecisionsNone = "document.decisions-none";
    public const string ColTopic = "document.col-topic";
    public const string ColCategory = "document.col-category";
    public const string ColContent = "document.col-content";
    public const string KindDecision = "document.kind-decision";
    public const string KindArchitecture = "document.kind-architecture";
    public const string KindConvention = "document.kind-convention";
    public const string ChangeDelivery = "document.change-delivery";
    public const string ChangesNone = "document.changes-none";
    public const string ChangesMemoryIntro = "document.changes-memory-intro";
    public const string RiskUnsatisfied = "document.risk-unsatisfied";
    public const string RiskNotVerifiable = "document.risk-not-verifiable";
    public const string RiskChallenges = "document.risk-challenges";
    public const string RisksAlertsIntro = "document.risks-alerts-intro";
    public const string RisksErrorsIntro = "document.risks-errors-intro";
    public const string RisksObjectionsIntro = "document.risks-objections-intro";
    public const string RisksNone = "document.risks-none";
    public const string AnnexEvidenceIntro = "document.annex-evidence-intro";
    public const string AnnexEvidenceNone = "document.annex-evidence-none";
    public const string AnnexGlossaryIntro = "document.annex-glossary-intro";
    public const string GlossaryEvidence = "document.glossary-evidence";
    public const string GlossaryVerdict = "document.glossary-verdict";
    public const string GlossaryRequirement = "document.glossary-requirement";
    public const string GlossaryFingerprint = "document.glossary-fingerprint";

    /// <summary>Every code of this class and the section titles, sorted, for the test of the resources.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. typeof(ClientDocumentTexts).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Concat(ClientDocumentSections.Order.Select(ClientDocumentSections.TitleCode))
            .Order(StringComparer.Ordinal),
    ];
}
