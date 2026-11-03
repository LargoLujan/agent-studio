using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Audit;

/// <summary>A vendor you may declare as an author of the code (D-28): the log does not know who wrote what Agent Studio did not.</summary>
public sealed partial class AuthorChoiceViewModel(VendorId vendor, Action changed) : ObservableObject
{
    public VendorId Vendor { get; } = vendor;

    public string Name => DebateTexts.Vendor(Vendor);

    [ObservableProperty]
    private bool _isDeclared;

    partial void OnIsDeclaredChanged(bool value) => changed();
}

/// <summary>One piece of the verification of HEAD, as the report lists it.</summary>
public sealed record VerificationRow(string Title, string Description, string? Tests);

/// <summary>
/// The conformity report of an audit (docs/15 D-31): whether it can be trusted and why, the commit audited and the changes
/// left out, the integrity of the log, the verification of HEAD, the problems the system lists without a judge, and the
/// card of the verdict (or the system's escalation when no judge ran).
/// </summary>
public sealed class AuditReportViewModel
{
    public AuditReportViewModel(AuditReport report, VerdictCardViewModel? card, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        Report = report;
        Card = card;
        Title = $"Auditoría del proyecto · {TimeZoneInfo.ConvertTime(report.GeneratedAt, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} · {report.Audit}";
        var why = new List<string>();
        if (!report.DecisionLogIntact)
        {
            why.Add("el registro de decisiones no está íntegro");
        }

        if (report.VerdictId is null)
        {
            why.Add("ningún juez emitió un veredicto");
        }

        if (report.ValidationIssues.Count > 0)
        {
            why.Add(DebateTexts.Count(report.ValidationIssues.Count, "incidencia de validación", "incidencias de validación"));
        }

        TrustText = report.IsTrustworthy
            ? "Fiable: lo emitió un juez, pasó la validación y el registro de decisiones está íntegro."
            : "No fiable: " + string.Join(", ", why) + ".";
        TrustTone = report.IsTrustworthy ? Tone.Success : Tone.Error;
        IntegrityText = report.DecisionLogIntact
            ? "Registro de decisiones íntegro."
            : "Registro de decisiones roto: no se lanzó al juez, y lo que sigue es solo lo que el sistema comprueba sin escribir.";
        CommitText = report.HeadCommit is { } head ? $"Commit auditado: {DebateTexts.Short(head)}" : "Sin commit: el repositorio no tiene HEAD.";
        UncommittedText = report.UncommittedChanges ? "Hay cambios sin commit: no entran en la auditoría." : null;
        DispositionText = $"Disposición: {VerdictTexts.Disposition(report.Disposition)}";
        DispositionTone = VerdictTexts.ToneOf(report.Disposition);
        EscalationText = report.VerdictId is null
            ? $"Escalado del sistema, no de un juez: {report.Verdict.Summary}"
            : null;
        Verification = [.. report.Verification.Select(e => new VerificationRow(
            e.Title,
            $"{EvidenceText.Kind(e.Kind)} · {EvidenceText.Origin(e.Origin)}{(e.SubjectCommit is { } commit ? $" · commit {DebateTexts.Short(commit)}" : " · sin commit")}",
            EvidenceText.Tests(e.TestRun)))];
        Pending = [.. report.Pending.Select(p => $"{p.Message} · {p.Source} ({p.Code})")];
    }

    public AuditReport Report { get; }

    public string Title { get; }

    public string TrustText { get; }

    public Tone TrustTone { get; }

    public string IntegrityText { get; }

    public string CommitText { get; }

    /// <summary>In the warning colour; null when the working tree was clean.</summary>
    public string? UncommittedText { get; }

    public string DispositionText { get; }

    public Tone DispositionTone { get; }

    /// <summary>When no judge ran: the system's escalation, which is never stored as a judge's verdict.</summary>
    public string? EscalationText { get; }

    /// <summary>The card of the judge's verdict; null when no judge ran.</summary>
    public VerdictCardViewModel? Card { get; }

    public IReadOnlyList<VerificationRow> Verification { get; }

    public bool HasVerification => Verification.Count > 0;

    /// <summary>The problems the system lists without a judge (D-31).</summary>
    public IReadOnlyList<string> Pending { get; }

    public bool HasPending => Pending.Count > 0;
}

/// <summary>
/// «Auditar proyecto completo» (docs/15 D-28 to D-31 and D-43, <c>Ctrl+Shift+A</c>): the preparation (the latest frozen
/// requirements of the project, the judge of the template, the authors you declare, the estimate and the exact
/// configuration, without writing anything) and then the conformity report. The judge never shares a vendor with whoever
/// produced the code (rule P, never waived): when nobody may judge, the audit still runs and reports only what the system
/// checks without a judge, and says so. «Auditar» asks your approval; «Volver a auditar» goes back to the preparation.
/// </summary>
public sealed partial class ProjectAuditDocumentViewModel : DocumentViewModel
{
    public const string IdPrefix = "audit:";

    private readonly DebateScreenBackend _backend;
    private readonly TimeProvider _time;
    private readonly DebateScreens? _screens;
    private readonly SynchronizationContext? _ui;
    private FrozenRequirementSet? _requirements;
    private bool _opened;
    private bool _loading;

    public ProjectAuditDocumentViewModel(string projectRoot, DebateScreenBackend backend, TimeProvider time, DebateScreens? screens = null, SynchronizationContext? uiContext = null)
        : base(CreateId(projectRoot), $"Auditoría · {RecentProjectItem.From(projectRoot).Name}")
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        ProjectRoot = projectRoot;
        _backend = backend;
        _time = time;
        _screens = screens;
        _ui = uiContext ?? SynchronizationContext.Current;
        Authors = [.. new[] { VendorId.Anthropic, VendorId.OpenAI, VendorId.Google }.Select(v => new AuthorChoiceViewModel(v, Changed))];
        Activity.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActivity));
    }

    public static string CreateId(string projectRoot) => IdPrefix + Path.GetFullPath(projectRoot).ToUpperInvariant();

    public string ProjectRoot { get; }

    public ProjectContext? Project { get; private set; }

    public ObservableCollection<string> Notices { get; } = [];

    /// <summary>The frozen requirements the audit is against: the latest of the project.</summary>
    [ObservableProperty]
    private string _requirementsText = "Sin requisitos congelados.";

    /// <summary>The judges of the template.</summary>
    public ObservableCollection<AgentChoice> Judges { get; } = [];

    [ObservableProperty]
    private AgentChoice? _selectedJudge;

    /// <summary>The vendors you declare as authors of the code (D-28).</summary>
    public IReadOnlyList<AuthorChoiceViewModel> Authors { get; }

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProposal))]
    [NotifyCanExecuteChangedFor(nameof(AuditCommand))]
    private ProjectAuditProposal? _proposal;

    public bool HasProposal => Proposal is not null;

    [ObservableProperty]
    private string? _headText;

    [ObservableProperty]
    private string? _uncommittedText;

    [ObservableProperty]
    private string? _judgeText;

    [ObservableProperty]
    private string? _producersText;

    [ObservableProperty]
    private string? _profileText;

    public ObservableCollection<string> ProfileSteps { get; } = [];

    /// <summary>The steps of the profile that do not run in an audit (<c>isolation: none</c>, D-27), in the warning colour.</summary>
    public ObservableCollection<string> SkippedSteps { get; } = [];

    public ObservableCollection<string> ProposalProblems { get; } = [];

    [ObservableProperty]
    private string? _estimateText;

    [ObservableProperty]
    private string? _approvalText;

    /// <summary>What «Auditar» will do: with a judge, or only what the system checks without one.</summary>
    [ObservableProperty]
    private string? _auditMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsReport))]
    [NotifyPropertyChangedFor(nameof(ShowsPreparation))]
    private AuditReportViewModel? _report;

    public bool ShowsReport => Report is not null;

    public bool ShowsPreparation => Report is null;

    /// <summary>What the judge and the verification did while the audit ran.</summary>
    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public bool HasActivity => Activity.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrepareCommand))]
    [NotifyCanExecuteChangedFor(nameof(AuditCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Opens the project and reads its latest frozen requirements and its judges once; called when the tab is opened.</summary>
    public async Task OpenAsync()
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        Notices.Clear();
        if (_backend.Audits is null)
        {
            Notices.Add("Falta el servicio de auditoría en esta composición: no se puede auditar el proyecto.");
        }

        if (_backend.Workspace is null)
        {
            Notices.Add("Falta el almacén del proyecto: no se puede abrir el proyecto.");
            Changed();
            return;
        }

        _loading = true;
        try
        {
            Project = await _backend.Workspace.OpenAsync(ProjectRoot, CancellationToken.None).ConfigureAwait(true);
            OnPropertyChanged(nameof(Project));
            if (_backend.Requirements is { } store)
            {
                _requirements = await store.LatestAsync(Project.ProjectId, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                Notices.Add("Falta el almacén de requisitos: sin requisitos congelados no se audita.");
            }

            RequirementsText = _requirements is { } set
                ? $"Contra los últimos requisitos congelados del proyecto: {DebateTexts.Count(set.Requirements.Count, "requisito", "requisitos")} · hash {DebateTexts.Short(set.ContentHash)}. Para auditar contra otros, congélalos antes en «Requisitos» del orquestador."
                : "El proyecto no tiene requisitos congelados: congélalos en «Requisitos» del orquestador para poder auditar.";
            if (_backend.Roster is { } roster)
            {
                foreach (var judge in (await roster.ListAsync(Project.ProjectId, CancellationToken.None).ConfigureAwait(true)).Where(a => a.Role == AgentRole.Judge).OrderBy(a => a.Id.Value, StringComparer.Ordinal))
                {
                    Judges.Add(new AgentChoice(judge));
                }
            }
            else
            {
                Notices.Add("Falta la plantilla de agentes: no hay juez que elegir.");
            }

            SelectedJudge = Judges.FirstOrDefault();
        }
#pragma warning disable CA1031 // The stores are behind ports: a failure is shown in the document, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo preparar la auditoría: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }

        Changed();
    }

    /// <summary>«Preparar»: the judge (rule P with the producers of the log and your declared authors), the profile and the estimate. Nothing is written.</summary>
    [RelayCommand(CanExecute = nameof(CanPrepare))]
    private async Task PrepareAsync()
    {
        if (_backend.Audits is not { } audits || Project is not { } project || _requirements is not { } requirements || SelectedJudge is not { } judge)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "Preparando la auditoría: sin gastar cuota ni escribir nada…";
        try
        {
            var request = new ProjectAuditRequest(project, requirements.ContentHash, judge.Agent.Id, [.. Authors.Where(a => a.IsDeclared).Select(a => a.Vendor)]);
            Show(await audits.PrepareAsync(request, CancellationToken.None).ConfigureAwait(true));
            StatusMessage = "Revisa la configuración y la estimación: «Auditar» te pedirá aprobación.";
        }
#pragma warning disable CA1031 // The audit service is behind a port: a failure is shown in the document, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Show(null);
            StatusMessage = null;
            ErrorMessage = $"No se pudo preparar la auditoría: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPrepare() => !IsBusy && Problems.Count == 0;

    /// <summary>«Auditar»: asks your approval (<c>audit.run</c>) and runs the audit; with nobody to judge, it reports what the system checks without a judge.</summary>
    [RelayCommand(CanExecute = nameof(CanAudit))]
    private async Task AuditAsync()
    {
        if (_backend.Audits is not { } audits || Proposal is not { } proposal || Project is not { } project)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Activity.Clear();
        StatusMessage = "Esperando tu aprobación…";
        try
        {
            var report = await audits.AuditAsync(proposal, new UiProgress(_ui, OnProgress), CancellationToken.None).ConfigureAwait(true);
            Report = new AuditReportViewModel(report, await CardAsync(project, report).ConfigureAwait(true), _time.LocalTimeZone);
            StatusMessage = null;
        }
#pragma warning disable CA1031 // The audit service is behind a port: a refusal (approval denied, busy project) is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = null;
            ErrorMessage = $"No se auditó: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAudit() => !IsBusy && Proposal is not null && _backend.Audits is not null;

    /// <summary>«Volver a auditar»: back to the preparation, which is asked again (HEAD and the log may have moved).</summary>
    [RelayCommand]
    private async Task AuditAgainAsync()
    {
        Report = null;
        Activity.Clear();
        Show(null);
        Changed();
        if (CanPrepare())
        {
            await PrepareAsync().ConfigureAwait(true);
        }
    }

    partial void OnSelectedJudgeChanged(AgentChoice? value) => Changed();

    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        if (Proposal is not null)
        {
            Show(null);
        }

        Problems = Validate();
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        PrepareCommand.NotifyCanExecuteChanged();
        AuditCommand.NotifyCanExecuteChanged();
    }

    private List<string> Validate()
    {
        var problems = new List<string>();
        if (_backend.Audits is null)
        {
            problems.Add("Falta el servicio de auditoría en esta composición.");
        }

        if (Project is null)
        {
            problems.Add("Abre el proyecto para auditarlo.");
            return problems;
        }

        if (_requirements is null)
        {
            problems.Add("Sin requisitos congelados no se audita: congélalos en «Requisitos» del orquestador.");
        }

        if (SelectedJudge is null)
        {
            problems.Add(Judges.Count == 0 ? "La plantilla del proyecto no tiene ningún juez." : "Elige el juez de la auditoría.");
        }

        return problems;
    }

    private void Show(ProjectAuditProposal? proposal)
    {
        Proposal = proposal;
        ProfileSteps.Clear();
        SkippedSteps.Clear();
        ProposalProblems.Clear();
        if (proposal is null)
        {
            HeadText = null;
            UncommittedText = null;
            JudgeText = null;
            ProducersText = null;
            ProfileText = null;
            EstimateText = null;
            ApprovalText = null;
            AuditMode = null;
            return;
        }

        HeadText = proposal.HeadCommit is { } head ? $"Se audita el commit HEAD {DebateTexts.Short(head)}." : "El repositorio no tiene commit HEAD.";
        UncommittedText = proposal.UncommittedChanges ? "Hay cambios sin commit: no entran en la auditoría, y el informe lo dirá." : null;
        JudgeText = proposal.Judge is { } judge
            ? $"Juez: {judge.Id} · {NewDebateViewModel.Where(judge.Model)}"
            : "Nadie puede juzgar: la regla de los productores no se dispensa.";
        ProducersText = proposal.ProducerVendors.Count == 0
            ? "El registro no conoce ningún productor y no declaraste autores."
            : $"Produjeron el código (el juez no puede ser de estos vendors): {string.Join(", ", proposal.ProducerVendors.Select(DebateTexts.Vendor).Order(StringComparer.Ordinal))}.";
        ProfileText = proposal.Verification is { } profile
            ? $"Perfil de verificación «{profile.Name}» · hash {DebateTexts.Short(profile.ContentHash)}: se ejecuta aislado a integridad Low sobre una copia de HEAD."
            : "Sin perfil de verificación: no se ejecutan pruebas.";
        foreach (var step in proposal.Verification?.Steps ?? [])
        {
            ProfileSteps.Add(VerificationStepText.Line(step));
        }

        foreach (var skipped in proposal.SkippedSteps)
        {
            SkippedSteps.Add($"{skipped}: no se ejecuta en una auditoría (isolation: none), porque lo haría con tus permisos.");
        }

        foreach (var problem in proposal.Problems)
        {
            ProposalProblems.Add(problem);
        }

        var tokens = MetricText.Tokens(proposal.EstimatedTokens);
        EstimateText = proposal.CanJudge
            ? $"Estimación: {tokens.Shown} tokens ({tokens.Origin}), más una corrección del juez como mucho. La verificación no gasta cuota."
            : "Sin juez no se gasta cuota: solo se verifica HEAD y se listan los problemas pendientes.";
        ApprovalText = proposal.ApprovalText;
        AuditMode = proposal.CanJudge
            ? "«Auditar» lanza al juez tras la verificación aislada."
            : "«Auditar» no lanzará a ningún juez: el informe dará solo lo que el sistema comprueba sin juez (la verificación y los problemas pendientes).";
    }

    private async Task<VerdictCardViewModel?> CardAsync(ProjectContext project, AuditReport report)
    {
        if (report.VerdictId is not { } id)
        {
            return null;
        }

        FrozenRequirementSet? requirements = null;
        if (_backend.Requirements is { } store)
        {
            try
            {
                requirements = await store.FindAsync(project.ProjectId, report.Verdict.RequirementSetHash, CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // Best effort: without the requirements the card says it shows ids only.
            catch (Exception)
#pragma warning restore CA1031
            {
                requirements = null;
            }
        }

        var judged = new JudgedVerdict(id, report.Verdict, report.ValidationIssues, report.Corrected);
        var actions = new VerdictCardActions(
            _screens is { CanNavigate: true } screens && _backend.Challenges is not null ? verdict => screens.ChallengeAsync(ProjectRoot, verdict) : null,
            null,
            _screens is { CanNavigate: true } navigation ? request => navigation.CreateCorrectiveTasksAsync(ProjectRoot, request) : null);
        var context = new VerdictCardContext("Veredicto de la auditoría", Requirements: requirements);
        return new VerdictCardViewModel([judged], null, context, _time.LocalTimeZone, actions);
    }

    private void OnProgress(AgentEvent agentEvent)
    {
        if (AgentActivityText.Describe(agentEvent, _time.LocalTimeZone) is { } text)
        {
            var now = _time.GetUtcNow();
            Activity.Add(new ActivityEntry(now, TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture), null, text));
        }
    }

    /// <summary>Reports the events of the judge on the UI context (or at once, without one).</summary>
    private sealed class UiProgress(SynchronizationContext? ui, Action<AgentEvent> handler) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value)
        {
            if (ui is null)
            {
                handler(value);
            }
            else
            {
                ui.Post(_ => handler(value), null);
            }
        }
    }
}
