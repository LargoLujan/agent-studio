using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// «Auditar proyecto completo» on Agent Studio itself (docs/15 F5-5, D-32 and decision 4 a), and the measurement it needs
/// first. The real repository is only read: a clone of its commit HEAD in the workspace of the demonstrations is the project
/// that is opened (its database lives there), and the audit itself works, as the design says, on copies of that commit (the
/// verifier's own at Low integrity, and the read-only one of the judge). The requirements are a set frozen for the audit
/// (C-1 to C-4 and a selection of Phases 3 and 4), the code is declared Anthropic's, so Codex judges and the Claude judge is
/// refused by rule P.
/// </summary>
/// <remarks>
/// <para><see cref="Agent_Studio_suite_at_low_integrity_without_quota"/> spends no quota (it only needs
/// <c>AGENTSTUDIO_PHASE5_DEMOS=1</c>): it runs build and test of HEAD with the isolated verifier, as the audit will, and
/// lists what fails by category, so the profile of the audit can leave out what fails because of the environment
/// (<c>AGENTSTUDIO_PHASE5_AUDIT_EXCLUDE</c>), and the report says so.</para>
/// <para>The test step always leaves out <c>Category=ProviderLive</c>: the isolated step inherits the environment of this
/// process, which carries the switches of the live tests, and without the filter the suite under audit would launch them
/// (these demonstrations among them) and spend quota.</para>
/// </remarks>
[Trait("Category", "ProviderLive")]
public sealed class Phase5AuditDemoLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_Studio_suite_at_low_integrity_without_quota()
    {
        DemoSettings.RequireDemos();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        var copy = AuditDemo.Copy(workspace);
        var exclusions = DemoSettings.AuditExclusions;
        var profile = AuditDemo.Profile(exclusions);
        await using var app = await DemoApp.OpenAsync(copy.Root, workspace, DemoRoster.Audit(), profile, Ct);

        var watch = Stopwatch.StartNew();
        var results = await app.Get<IVerificationRunner>().RunAsync(
            copy.Root,
            [.. profile.Steps.Where(s => s.Isolation != ProcessIsolation.None)],
            new EvidenceScope(app.Project.ProjectId, null, null, null, copy.Head, copy.Root),
            null,
            Ct);
        watch.Stop();

        var steps = new List<DemoStepResult>();
        var failed = new List<DemoFailedTest>();
        var notes = new List<string>(copy.Notes);
        var categories = TestCategories.Map(typeof(Phase5AuditDemoLiveTests).Assembly);
        foreach (var result in results)
        {
            var evidence = await DemoFacts.EvidenceAsync(app, result.Evidence, Ct);
            steps.Add(new DemoStepResult(result.StepId, result.Evidence.Title, result.Succeeded, result.TimedOut, result.ExitCode, evidence.Passed, evidence.Failed, evidence.Skipped, evidence.Isolation));
            if (result.Evidence.Kind == EvidenceKind.TestRun)
            {
                var names = TestCategories.FailedTests(await ReadAsync(app, result.Evidence.Id));
                failed.AddRange(names.Select(n => new DemoFailedTest(n, categories.Of(n))));
                if (result.Evidence.TestRun is { Failed: > 0 } summary && names.Count == 0)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"El informe cuenta {summary.Failed} pruebas fallidas, pero la consola no dejó leer sus nombres."));
                }
            }
        }

        var suggested = failed.SelectMany(f => f.Categories).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(c => "Category=" + c).ToList();
        var uncategorized = failed.Where(f => f.Categories.Count == 0).Select(f => f.Name).ToList();
        notes.Add(suggested.Count == 0
            ? "Ninguna prueba falla por categoría: la auditoría puede usar el perfil sin más filtros."
            : $"Para la auditoría: {DemoSettings.AuditExcludeVariable}={string.Join(';', suggested)} deja fuera las categorías con pruebas que fallan a integridad Low (el informe lo dirá).");
        if (uncategorized.Count > 0)
        {
            notes.Add($"{uncategorized.Count} pruebas sin categoría fallan: ningún filtro de rasgo las deja fuera; se pueden excluir por clase en {DemoSettings.AuditExcludeVariable}.");
        }

        var capture = new DemoCapture
        {
            Block = DemoPlan.Measurement,
            Title = "Suite de Agent Studio a integridad Low (sin cuota)",
            Test = Test(),
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            Result = string.Join("; ", steps.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Id}: {(s.Succeeded ? "bien" : s.TimedOut ? "tiempo agotado" : "falla")} (salida {s.ExitCode}{(s.Passed is null ? string.Empty : $", {s.Passed} correctas, {s.Failed} fallidas, {s.Skipped} omitidas")})"))),
            Completed = steps.Count == profile.Steps.Count,
            Tools = await DemoFacts.ToolsAsync(app, Ct),
            Notes = notes,
            Suite = new DemoSuiteFacts
            {
                Repository = copy.Source,
                Head = copy.Head,
                Profile = AuditDemo.Steps(profile),
                Steps = steps,
                Minutes = Math.Round(watch.Elapsed.TotalMinutes, 1),
                Failed = failed,
                SuggestedExclusions = suggested,
                Uncategorized = uncategorized,
            },
        };
        if (DemoSettings.CaptureDirectory is { } folder)
        {
            DemoCaptures.Write(folder, capture);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(DemoScrub.Text(capture.Result + "\n" + string.Join("\n", notes)));
        Assert.Contains(steps, s => s.Id == "build" && s.Succeeded);
        Assert.Equal(profile.Steps.Count, steps.Count);
    }

    [Fact]
    public async Task Whole_project_audit_of_Agent_Studio_judged_by_Codex()
    {
        DemoSettings.RequireLiveDemo();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        var copy = AuditDemo.Copy(workspace);
        var exclusions = DemoSettings.AuditExclusions;
        var profile = AuditDemo.Profile(exclusions);
        await using var app = await DemoApp.OpenAsync(copy.Root, workspace, DemoRoster.Audit(), profile, Ct);
        var tools = await DemoFacts.ToolsAsync(app, Ct);
        var requirements = await AuditDemo.FreezeAsync(app, Ct);
        var audits = app.Get<IProjectAuditService>();

        // Without quota: the Claude judge is refused by rule P (the code is declared Anthropic's), and Codex may judge.
        var refused = await audits.PrepareAsync(new ProjectAuditRequest(app.Project, requirements.ContentHash, DemoRoster.ClaudeJudge, AuditDemo.DeclaredAuthors), Ct);
        var proposal = await audits.PrepareAsync(new ProjectAuditRequest(app.Project, requirements.ContentHash, DemoRoster.Judge, AuditDemo.DeclaredAuthors), Ct);
        Assert.False(refused.CanJudge, "Rule P must refuse the Claude judge on code declared Anthropic's.");
        Assert.True(proposal.CanJudge, "The audit cannot judge: " + string.Join(" ", proposal.Problems));
        Assert.Empty(proposal.SkippedSteps);

        DemoCapture capture;
        try
        {
            capture = await AuditCaptureAsync(app, workspace, copy, profile, exclusions, requirements, refused, proposal, started, tools);
        }
        catch (Exception ex)
        {
            // The judge may have spent its quota already: whatever stopped the audit, it leaves a capture that says so.
            DemoCaptures.WriteFailure(DemoPlan.Audit, AuditTitle, Test(), started, tools, null, copy.Notes, ex);
            throw;
        }

        var audit = capture.Audit!;
        Assert.True(audit.LogIntact == true, "The decision log does not verify.");
        Assert.True(audit.VerdictId is not null, "No judge gave a verdict: " + capture.Result);
        Assert.Equal(copy.Head, audit.Head);
        Assert.Contains(audit.Verification, e => e.Kind == nameof(EvidenceKind.BuildLog) && e.SubjectCommit == copy.Head);
        Assert.Contains(audit.Verification, e => e.Kind == nameof(EvidenceKind.TestRun) && e.SubjectCommit == copy.Head);
        Assert.All(audit.Verification, e => Assert.StartsWith("low integrity", e.Isolation ?? string.Empty, StringComparison.Ordinal));
        Assert.All(capture.Verdicts, v => Assert.Equal(VendorId.OpenAI.Value, v.Judge.Vendor));
        Assert.NotEmpty(capture.Journal);
        Assert.All(capture.Journal, line => Assert.Contains("#auditoria", line, StringComparison.Ordinal));
        Assert.True(audit.SnapshotRemoved == true, "The copy of the commit for the judge was not removed.");
        Assert.True(audit.DossierBytes is { } bytes && bytes <= ProjectDossierBuilder.MaxBytes, "The dossier.json of the audit is missing or larger than 256 KB.");
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.ReadAllText(Path.Combine(DemoCaptures.Folder(DemoSettings.CaptureDirectory!), capture.Block + ".json")),
            StringComparison.OrdinalIgnoreCase);
    }

    private const string AuditTitle = "Auditar proyecto completo: el propio Agent Studio";

    /// <summary>The audit itself (the approval, the isolated verification of HEAD, the index dossier and the judge) and its capture.</summary>
    private static async Task<DemoCapture> AuditCaptureAsync(
        DemoApp app,
        DemoWorkspace workspace,
        AuditCopy copy,
        VerificationProfile profile,
        IReadOnlyList<string> exclusions,
        FrozenRequirementSet requirements,
        ProjectAuditProposal refused,
        ProjectAuditProposal proposal,
        DateTimeOffset started,
        IReadOnlyList<DemoTool> tools)
    {
        var report = await app.Get<IProjectAuditService>().AuditAsync(proposal, null, Ct);

        var log = await app.LoadLogAsync(Ct);
        var unit = report.Audit.Value;
        var units = new HashSet<string>(StringComparer.Ordinal) { unit };
        var (entries, alerts) = DemoFacts.Log(log.Entries, units);
        var verification = new List<DemoEvidence>();
        foreach (var record in report.Verification)
        {
            verification.Add(await DemoFacts.EvidenceAsync(app, record, Ct));
        }

        var dossier = Directory.Exists(workspace.Paths.Engine.DossierRoot)
            ? Directory.GetFiles(workspace.Paths.Engine.DossierRoot, "dossier.json", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains(unit, StringComparison.Ordinal))
            : null;
        var snapshotRoot = app.Get<AuditOptions>().SnapshotRoot;
        var snapshotRemoved = !Directory.Exists(snapshotRoot) || !Directory.GetDirectories(snapshotRoot, "*", SearchOption.AllDirectories).Any(d => d.Contains(unit, StringComparison.Ordinal) && d.EndsWith("snapshot", StringComparison.OrdinalIgnoreCase));
        var notes = new List<string>(copy.Notes)
        {
            exclusions.Count == 0
                ? "El paso de pruebas deja fuera solo Category=ProviderLive (las pruebas que gastan cuota)."
                : $"El paso de pruebas deja fuera Category=ProviderLive y, por la medición a integridad Low, {string.Join(", ", exclusions)}.",
            AuditDemo.Order(entries),
        };
        var facts = new DemoAuditFacts
        {
            Audit = unit,
            Repository = copy.Source,
            Head = report.HeadCommit,
            Uncommitted = report.UncommittedChanges,
            RequirementSetHash = requirements.ContentHash,
            Requirements = DemoFacts.Requirements(requirements),
            Judge = proposal.Judge is { } judge ? new DemoJudge(judge.Id.Value, judge.Model.Provider.Value, judge.Model.Vendor.Value, judge.Model.Model) : null,
            Producers = [.. proposal.ProducerVendors.Select(v => v.Value)],
            Declared = [.. AuditDemo.DeclaredAuthors.Select(v => v.Value)],
            Skipped = proposal.SkippedSteps,
            Profile = AuditDemo.Steps(profile),
            Exclusions = exclusions,
            EstimatedTokens = proposal.EstimatedTokens.Value,
            CanJudge = proposal.CanJudge,
            Problems = proposal.Problems,
            ClaudeJudgeProblems = refused.Problems,
            ClaudeJudgeCanJudge = refused.CanJudge,
            ApprovalText = proposal.ApprovalText,
            Disposition = report.Disposition.ToString(),
            Trustworthy = report.IsTrustworthy,
            LogIntact = report.DecisionLogIntact,
            Corrected = report.Corrected,
            VerdictId = report.VerdictId,
            Verification = verification,
            Pending = [.. report.Pending.Select(p => new DemoPending(p.Code, p.Source, p.Message))],
            DossierBytes = dossier is null ? null : new FileInfo(dossier).Length,
            SnapshotRemoved = snapshotRemoved,
        };
        var capture = new DemoCapture
        {
            Block = DemoPlan.Audit,
            Title = AuditTitle,
            Test = Test(nameof(Whole_project_audit_of_Agent_Studio_judged_by_Codex)),
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            Result = $"{report.Disposition}: {report.Verdict.Decision}, {(report.ValidationIssues.Count == 0 ? "sin incidencias del validador" : report.ValidationIssues.Count.ToString(CultureInfo.InvariantCulture) + " incidencias del validador")}; {(report.IsTrustworthy ? "fiable" : "no fiable")}.",
            Completed = report.VerdictId is not null,
            Tools = tools,
            Notes = notes,
            Audit = facts,
            Verdicts = await DemoFacts.VerdictsAsync(app, log.Entries, unit, null, Ct),
            Attempts = DemoFacts.Attempts(app.Project.ProjectId, log.Entries, units),
            Quota = await DemoFacts.QuotaAsync(app, Ct),
            Journal = DemoFacts.Journal(app.Project.RootPath, unit),
            Log = entries,
            LogIntact = log.Verification.IsValid,
            HeadHash = log.HeadHash,
            Alerts = alerts,
            Approvals = DemoFacts.Approvals(app.Person),
        };
        DemoCaptures.Write(DemoSettings.CaptureDirectory!, capture);
        return capture;
    }

    private static async Task<string> ReadAsync(DemoApp app, EvidenceId id)
    {
        await using var stream = await app.Get<IEvidenceStore>().OpenContentAsync(app.Project.ProjectId, id, Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    private static string Test([CallerMemberName] string method = "") => nameof(Phase5AuditDemoLiveTests) + "." + method;
}

/// <summary>The copy of the audited repository: where it came from, where it is, its HEAD and what was noted about the source.</summary>
internal sealed record AuditCopy(string Source, string Root, string Head, IReadOnlyList<string> Notes);

/// <summary>What the audit of Agent Studio is checked against and run with (D-32 and decision 4 a).</summary>
internal static class AuditDemo
{
    /// <summary>Who wrote the code Agent Studio did not: sessions of Claude Code, so Anthropic (D-32).</summary>
    public static readonly IReadOnlyList<VendorId> DeclaredAuthors = [VendorId.Anthropic];

    /// <summary>
    /// The set frozen for the audit: the common criteria C-1 to C-4 of docs/08 and a selection of Phases 3 and 4. Build and
    /// tests are objective (the verifier captures them on HEAD); the rest are valuations of the judge on the copy of HEAD.
    /// </summary>
    public static FrozenRequirementSet Requirements(DateTimeOffset at) => FrozenRequirementSet.Freeze(
        [
            Requirement("C-1", "La solución compila sin errores ni avisos (los avisos son errores).", "c1", "El paso build del perfil, ejecutado por el sistema sobre HEAD a integridad Low, termina con código 0.", VerificationMethod.CommandCheck),
            Requirement("C-2", "La suite de pruebas pasa, sin las que gastan cuota de proveedores.", "c1", "El paso test del perfil, ejecutado por el sistema sobre HEAD, no tiene pruebas fallidas.", VerificationMethod.AutomatedTest),
            Requirement("C-3", "Ninguna funcionalidad simulada: lo que no está implementado no aparece en la interfaz o aparece marcado como pendiente.", "c1", "El juez revisa el código de Desktop en la copia de HEAD y no encuentra pantallas que finjan resultados.", VerificationMethod.JudgeAssessment),
            Requirement("C-4", "La documentación y los ADR recogen lo decidido en cada fase.", "c1", "docs/ y docs/adr/ describen lo que hace el código de HEAD, sin contradicciones relevantes.", VerificationMethod.JudgeAssessment),
            Requirement("F3-4", "Toda decisión queda en el registro de decisiones (SQLite), encadenada y verificable tras reiniciar.", "c1", "El registro encadena las entradas con hash, sus triggers impiden modificarlas y hay pruebas que lo comprueban.", VerificationMethod.JudgeAssessment),
            Requirement("F3-5", "Las evidencias de build y pruebas las captura el sistema, con su hash.", "c1", "El runner de verificación guarda la salida como evidencia direccionada por contenido y el juez solo cuenta las anunciadas en el registro.", VerificationMethod.JudgeAssessment),
            Requirement("F3-7", "Las acciones destructivas o críticas piden aprobación humana.", "c1", "La puerta de aprobaciones falla cerrada y registra cada petición y respuesta en orden.", VerificationMethod.JudgeAssessment),
            Requirement("F4-1", "El consumo se muestra por proveedor, modelo, sesión, proyecto y periodo, con la procedencia de cada cifra.", "c1", "El consumo es una proyección del registro y ninguna cifra estimada se presenta como exacta.", VerificationMethod.JudgeAssessment),
            Requirement("F4-4", "La cuota de Codex se lee solo con el permiso del usuario, sin turno del modelo.", "c1", "La lectura usa account/rateLimits/read del app-server, es opt-in y nunca llama a los métodos que gastan o piden crédito.", VerificationMethod.JudgeAssessment),
            Requirement("F4-5", "La memoria del proyecto es append-only, está anclada al registro y se recupera de forma selectiva.", "c1", "La memoria vive en SQLite con FTS5, cada entrada se anuncia en el registro y la recuperación filtra por confianza.", VerificationMethod.JudgeAssessment),
        ],
        at);

    /// <summary>
    /// The trusted profile of the audit: the one proposed for .NET (build, then test with its TRX), with the time the whole
    /// suite needs and its test step leaving out <c>Category=ProviderLive</c> always, and whatever the measurement asked to
    /// leave out (<see cref="DemoSettings.AuditExcludeVariable"/>).
    /// </summary>
    public static VerificationProfile Profile(IReadOnlyList<string> exclusions)
    {
        var proposed = DotNetVerificationProfile.Propose();
        var build = proposed.Find("build") ?? throw new InvalidOperationException("The proposed .NET profile has no build step.");
        var test = proposed.Find("test") ?? throw new InvalidOperationException("The proposed .NET profile has no test step.");
        return new VerificationProfile(
            "dotnet-agent-studio",
            [
                build with { Timeout = TimeSpan.FromMinutes(30) },
                test with { Arguments = [.. test.Arguments, .. Filters(exclusions)], Timeout = TimeSpan.FromMinutes(60) },
            ]);
    }

    /// <summary>
    /// What the test step of the audit never runs: the categories CI leaves out plus the one for tests that need Medium
    /// integrity. <c>ProviderLive</c> spends quota; <c>LocalTools</c>, <c>ConPty</c> and <c>Measurement</c> need the machine
    /// of the developer; <c>NeedsMediumIntegrity</c> cannot work at Low integrity (docs/08, F5-5, ADR-0017 addendum).
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysExcluded =
    [
        "Category=ProviderLive", "Category=LocalTools", "Category=ConPty", "Category=Measurement", "Category=NeedsMediumIntegrity",
    ];

    /// <summary>The filters of the test step: <see cref="AlwaysExcluded"/> first, then each exclusion as a trait or as a class.</summary>
    public static IReadOnlyList<string> Filters(IReadOnlyList<string> exclusions)
    {
        var filters = new List<string>();
        foreach (var category in AlwaysExcluded)
        {
            filters.Add("--filter-not-trait");
            filters.Add(category);
        }

        foreach (var item in exclusions.Where(e => !AlwaysExcluded.Contains(e, StringComparer.OrdinalIgnoreCase)))
        {
            filters.Add(item.Contains('=', StringComparison.Ordinal) ? "--filter-not-trait" : "--filter-not-class");
            filters.Add(item);
        }

        return filters;
    }

    public static IReadOnlyList<DemoStep> Steps(VerificationProfile profile) =>
        [.. profile.Steps.Select(s => new DemoStep(s.Id, s.Title, s.Executable, s.Arguments, s.Timeout.TotalMinutes, s.Isolation.ToString()))];

    /// <summary>
    /// The project of the audit: a clone of the audited repository at its HEAD, made once in the workspace and kept (the real
    /// repository is only read: <c>git rev-parse</c>, <c>git status</c> and <c>git clone</c>). Uncommitted changes of the
    /// source do not enter, as in the app, and are noted.
    /// </summary>
    public static AuditCopy Copy(DemoWorkspace workspace)
    {
        var source = DemoSettings.AuditRepository;
        Assert.True(Directory.Exists(source), $"The repository to audit does not exist: {source}");
        var head = TempGitRepo.Git(source, "rev-parse", "HEAD").Trim();
        var notes = new List<string> { $"Repositorio auditado: {source}, commit HEAD {head}." };
        var (status, porcelain, _) = TempGitRepo.TryGit(source, "status", "--porcelain");
        if (status == 0 && porcelain.Trim().Length > 0)
        {
            notes.Add("El repositorio de origen tiene cambios sin confirmar: no entran en la auditoría, que copia el commit HEAD.");
        }

        var root = workspace.AuditRepository(head);
        if (Directory.Exists(Path.Combine(root, ".git")))
        {
            Assert.Equal(head, TempGitRepo.Git(root, "rev-parse", "HEAD").Trim());
            notes.Add("La copia del commit ya estaba en el espacio de trabajo de las demostraciones: se reutiliza con su registro.");
        }
        else
        {
            Assert.False(
                Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(),
                $"'{root}' exists and is not a copy of the audited repository: delete it or choose another workspace with {DemoSettings.WorkspaceVariable}.");
            TempGitRepo.Git(workspace.Root, "clone", "--quiet", "--no-hardlinks", "--no-checkout", source, root);
            TempGitRepo.Git(root, "checkout", "--quiet", "-B", "auditoria", head);
            notes.Add("Copia nueva del commit HEAD (git clone sin enlaces duros): el repositorio real solo se leyó.");
        }

        return new AuditCopy(source, root, head, notes);
    }

    /// <summary>Saves the requirements of the audit in its project (freezing them costs nothing) and returns them as stored.</summary>
    public static async Task<FrozenRequirementSet> FreezeAsync(DemoApp app, CancellationToken ct)
    {
        var requirements = Requirements(DateTimeOffset.UtcNow);
        var store = app.Get<IRequirementStore>();
        if (await store.FindAsync(app.Project.ProjectId, requirements.ContentHash, ct) is { } stored)
        {
            return stored;
        }

        await store.SaveAsync(app.Project.ProjectId, requirements, ct);
        return requirements;
    }

    /// <summary>
    /// Whether the log of the audit has, in this order, the approval, the request, <c>ProjectAuditStarted</c>, the evidence of
    /// the verification, the index, the launch of the judge, <c>VerdictIssued</c> and <c>ProjectAuditFinished</c> (checklist of
    /// ADR-0030, step 8), as a sentence for the report.
    /// </summary>
    public static string Order(IReadOnlyList<DemoEntryFacts> entries)
    {
        string[] expected =
        [
            nameof(DecisionKind.ApprovalGranted),
            nameof(DecisionKind.EvidenceRegistered),
            nameof(DecisionKind.ProjectAuditStarted),
            nameof(DecisionKind.EvidenceRegistered),
            nameof(DecisionKind.EvidenceRegistered),
            nameof(DecisionKind.AgentRunStarted),
            nameof(DecisionKind.VerdictIssued),
            nameof(DecisionKind.ProjectAuditFinished),
        ];
        var kinds = entries.Select(e => e.Kind).ToList();
        var position = 0;
        foreach (var kind in kinds)
        {
            if (position < expected.Length && kind == expected[position])
            {
                position++;
            }
        }

        return position == expected.Length
            ? "El registro de la auditoría sigue el orden de ADR-0030: aprobación, petición, ProjectAuditStarted, verificación de HEAD, índice, juez, VerdictIssued y ProjectAuditFinished."
            : $"El registro de la auditoría no sigue el orden de ADR-0030 (se reconocen {position} de {expected.Length} pasos): {string.Join(", ", kinds)}.";
    }

    private static Requirement Requirement(string id, string statement, string criterion, string description, VerificationMethod method) =>
        new(new RequirementId(id), statement, RequirementPriority.Mandatory, [new AcceptanceCriterion(criterion, description, method)]);
}

/// <summary>Which category each test of this assembly carries (its <c>[Trait("Category", …)]</c>, of its class or its method), to read a list of failures by category.</summary>
internal sealed partial class TestCategories
{
    private readonly Dictionary<string, IReadOnlyList<string>> _byMethod;

    private TestCategories(Dictionary<string, IReadOnlyList<string>> byMethod) => _byMethod = byMethod;

    public static TestCategories Map(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var byMethod = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        foreach (var type in types)
        {
            var ofClass = Categories(type.GetCustomAttributesData());
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var all = ofClass.Concat(Categories(method.GetCustomAttributesData())).Distinct(StringComparer.Ordinal).ToList();
                byMethod[(type.FullName ?? type.Name).Replace('+', '.') + "." + method.Name] = all;
            }
        }

        return new TestCategories(byMethod);
    }

    /// <summary>The categories of a test by the name the report gives it (arguments of a theory row are ignored).</summary>
    public IReadOnlyList<string> Of(string testName)
    {
        var name = testName.Split('(')[0].Trim().Replace('+', '.');
        return _byMethod.TryGetValue(name, out var categories) ? categories : [];
    }

    /// <summary>The names of the failed tests, read from the console of <c>dotnet test</c> as the evidence keeps it (<c>failed &lt;name&gt; (…)</c>).</summary>
    public static IReadOnlyList<string> FailedTests(string evidence)
    {
        var names = new List<string>();
        foreach (var raw in (evidence ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var bar = line.IndexOf("| ", StringComparison.Ordinal);
            if (bar is > 0 and < 8)
            {
                line = line[(bar + 2)..];
            }

            if (FailedLine().Match(line.Trim()) is { Success: true } match)
            {
                names.Add(match.Groups["name"].Value.Trim());
            }
        }

        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Categories(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a => a.AttributeType.Name == "TraitAttribute" && a.ConstructorArguments.Count == 2)
            .Where(a => string.Equals(a.ConstructorArguments[0].Value as string, "Category", StringComparison.Ordinal))
            .Select(a => a.ConstructorArguments[1].Value as string)
            .OfType<string>();

    [GeneratedRegex(@"^failed (?<name>\S.*?) \(\d", RegexOptions.CultureInvariant)]
    private static partial Regex FailedLine();
}
