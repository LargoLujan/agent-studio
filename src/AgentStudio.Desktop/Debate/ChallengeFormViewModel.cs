using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Debate;

/// <summary>An agent of the template, as a choice of a list.</summary>
public sealed record AgentChoice(AgentDefinition Agent)
{
    public string Label => $"{Agent.Id} · {NewDebateViewModel.Where(Agent.Model)}";

    /// <summary>What a screen reader says of the choice (A1-bis): its label, never the members of the record.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// «Impugnar» (docs/15 D-38 and D-39): your reasons, which are stored as a note of yours, and the revision debate they
/// open. It is adversarial: the proponent defends the verdict and the critic defends your challenge, on the requirements of
/// the challenged verdict. «Preparar» asks the challenge service, which inherits who may judge the challenged object and
/// picks another judge (another agent when the template has one); nothing is written until «Impugnar», which asks your
/// approval. A revision never delivers by itself: a turn towards an approval only counts once you accept it.
/// </summary>
public sealed partial class ChallengeFormViewModel : ObservableObject
{
    private readonly DebateScreenBackend _backend;
    private ProjectContext? _project;
    private StoredVerdict? _verdict;
    private FrozenRequirementSet? _requirements;
    private bool _pending;
    private bool _loading;

    public ChallengeFormViewModel(DebateScreenBackend backend, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        _backend = backend;
    }

    /// <summary>Raised once the revision debate was opened (after your approval): the document follows it in the room.</summary>
    public event EventHandler<OpenedDebate>? Opened;

    /// <summary>The id of the challenged verdict.</summary>
    [ObservableProperty]
    private string? _verdictId;

    /// <summary>What the challenged verdict said and who said it; null until it is read.</summary>
    [ObservableProperty]
    private string? _verdictText;

    [ObservableProperty]
    private string? _challengeText;

    [ObservableProperty]
    private Tone _challengeTone = Tone.Neutral;

    [ObservableProperty]
    private string? _requirementsText;

    /// <summary>Why you challenge the verdict: stored as a note of yours (<c>HumanNote</c>) and the start of the critic's round 1.</summary>
    [ObservableProperty]
    private string _reasons = string.Empty;

    [ObservableProperty]
    private string _question = string.Empty;

    /// <summary>The agents of the template that can take part (any role but judge, auditor and orchestrator).</summary>
    public ObservableCollection<AgentChoice> Candidates { get; } = [];

    /// <summary>Who defends the verdict.</summary>
    [ObservableProperty]
    private AgentChoice? _proponent;

    /// <summary>Who defends your challenge.</summary>
    [ObservableProperty]
    private AgentChoice? _critic;

    public ObservableCollection<JudgeChoiceViewModel> Judges { get; } = [];

    [ObservableProperty]
    private string _maxRounds = DebateLimits.Default.MaxRounds.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _maxMinutes = ((int)DebateLimits.Default.MaxDuration.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _maxTokens = (DebateLimits.Default.MaxTotalTokens ?? 0).ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<string> Problems { get; private set; } = [Texts.Get("Debate_Challenge_Problem_PickVerdict")];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProposal))]
    [NotifyPropertyChangedFor(nameof(ArrangementText))]
    [NotifyPropertyChangedFor(nameof(WaiverText))]
    [NotifyPropertyChangedFor(nameof(EstimateText))]
    [NotifyPropertyChangedFor(nameof(ProposalProblems))]
    [NotifyPropertyChangedFor(nameof(Notes))]
    [NotifyPropertyChangedFor(nameof(ApprovalText))]
    [NotifyPropertyChangedFor(nameof(ApprovalLine))]
    [NotifyCanExecuteChangedFor(nameof(ChallengeCommand))]
    private DebateProposal? _proposal;

    public bool HasProposal => Proposal is not null;

    /// <summary>Who judges the revision, as the service chose it (another session and, if it can, another agent).</summary>
    public string? ArrangementText => Proposal is { } proposal
        ? $"{DebateTexts.Judging(proposal.Judging.Kind)}: {string.Join(" | ", proposal.Judging.Judges.Select(VerdictPanelViewModel.JudgeLine))}. "
          + Texts.Get(_verdict is { } challenged && proposal.Judging.Judges.Any(j => j.Agent == challenged.Verdict.Judge.Agent)
              ? "Debate_Challenge_SameJudge"
              : "Debate_Challenge_OtherJudge")
        : null;

    public string? WaiverText => Proposal is { } proposal ? DebateTexts.Waiver([.. proposal.Judging.WaivedParticipantVendors]) : null;

    public string? EstimateText => Proposal is { } proposal && MetricText.Tokens(proposal.Estimate.Tokens) is var tokens
        ? Texts.Format("Debate_New_Estimate", DebateTexts.Plural(proposal.Estimate.MaxAgentRuns, "Debate_Count_AgentRun"), tokens.Shown, tokens.Origin)
        : null;

    public IReadOnlyList<string> ProposalProblems => Proposal is { } shown ? DebateTexts.Painted(shown.Problems, shown.ProblemTexts) : [];

    public IReadOnlyList<string> Notes => Proposal is { } shown ? DebateTexts.Painted(shown.Estimate.Notes, shown.Estimate.NoteTexts) : [];

    public string? ApprovalText => Proposal?.ApprovalText;

    /// <summary>The approval «Impugnar» asks for, introduced.</summary>
    public string? ApprovalLine => ApprovalText is { } text ? Texts.Format("Debate_Form_ApprovalAsked", text) : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrepareCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChallengeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Reads the verdict, its requirements, where its challenge stands and the template. Writes nothing.</summary>
    public async Task LoadAsync(ProjectContext project, string verdictId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(verdictId);
        _project = project;
        _verdict = null;
        _requirements = null;
        _pending = false;
        _loading = true;
        VerdictId = verdictId;
        VerdictText = null;
        ChallengeText = null;
        RequirementsText = null;
        ErrorMessage = null;
        StatusMessage = null;
        Proposal = null;
        Reasons = string.Empty;
        Candidates.Clear();
        Judges.Clear();
        try
        {
            if (_backend.Verdicts is { } verdicts)
            {
                _verdict = (await verdicts.ListAsync(project.ProjectId, null, ct).ConfigureAwait(true)).LastOrDefault(v => v.Id == verdictId);
                if (_verdict is { } stored)
                {
                    var where = stored.RunId is { Length: > 0 } run ? $" · {run}" : string.Empty;
                    VerdictText = Texts.Format(stored.Valid ? "Debate_Challenge_VerdictValid" : "Debate_Challenge_VerdictInvalid", stored.Id, where, VerdictTexts.Decision(stored.Verdict.Decision), VerdictPanelViewModel.JudgeLine(stored.Verdict.Judge));
                    // The question is what the debaters and the judge receive: a prompt of the protocol, fixed in Spanish (docs/17 D-48).
                    Question = ProtocolTexts.ChallengeQuestion(stored.Id, stored.Verdict.Decision);
                    if (_backend.Requirements is { } store)
                    {
                        _requirements = await store.FindAsync(project.ProjectId, stored.Verdict.RequirementSetHash, ct).ConfigureAwait(true);
                    }

                    RequirementsText = _requirements is { } set
                        ? Texts.Format("Debate_Challenge_Requirements", DebateTexts.Plural(set.Requirements.Count, "Debate_Count_Requirement"), DebateTexts.Short(set.ContentHash))
                        : Texts.Format("Debate_Challenge_RequirementsGone", DebateTexts.Short(stored.Verdict.RequirementSetHash));
                }
            }

            if (_backend.Challenges is { } challenges)
            {
                var status = await challenges.StatusAsync(project, verdictId, ct).ConfigureAwait(true);
                ChallengeText = VerdictTexts.Challenge(status);
                ChallengeTone = VerdictTexts.ToneOf(status);
                _pending = status is ChallengeStatus.Pending;
            }

            if (_backend.Roster is { } roster)
            {
                foreach (var agent in (await roster.ListAsync(project.ProjectId, ct).ConfigureAwait(true)).OrderBy(a => a.Id.Value, StringComparer.Ordinal))
                {
                    if (agent.Role == AgentRole.Judge)
                    {
                        Judges.Add(new JudgeChoiceViewModel(agent, Changed));
                    }
                    else if (agent.Role is not (AgentRole.Auditor or AgentRole.Orchestrator))
                    {
                        Candidates.Add(new AgentChoice(agent));
                    }
                }
            }

            Proponent = Candidates.FirstOrDefault();
            Critic = Candidates.Skip(1).FirstOrDefault() ?? Candidates.FirstOrDefault();
        }
#pragma warning disable CA1031 // The stores are behind ports: a failure is shown in the form, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Debate_Challenge_Error_Load", DebateTexts.Reason(ex));
        }
        finally
        {
            _loading = false;
        }

        Changed();
    }

    /// <summary>«Preparar»: the challenge service computes who judges the revision and the estimate. Nothing is written.</summary>
    [RelayCommand(CanExecute = nameof(CanPrepare))]
    private async Task PrepareAsync()
    {
        if (_backend.Challenges is not { } challenges || Request() is not { } request)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = Texts.Get("Debate_Challenge_Status_Preparing");
        try
        {
            Proposal = await challenges.PrepareAsync(request, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = Proposal.Problems.Count == 0
                ? Texts.Get("Debate_Challenge_Status_Prepared")
                : Texts.Get("Debate_Challenge_Status_Problems");
        }
#pragma warning disable CA1031 // The challenge service is behind a port: a failure is shown in the form, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Proposal = null;
            StatusMessage = null;
            ErrorMessage = Texts.Format("Debate_Challenge_Error_Prepare", DebateTexts.Reason(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPrepare() => !IsBusy && _backend.Challenges is not null && Problems.Count == 0;

    /// <summary>«Impugnar»: records the challenge (your reasons as evidence) and opens the revision debate, after your approval.</summary>
    [RelayCommand(CanExecute = nameof(CanChallenge))]
    private async Task ChallengeAsync()
    {
        if (_backend.Challenges is not { } challenges || Proposal is not { } proposal)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = Texts.Get("Debate_Status_WaitingApproval");
        try
        {
            var run = await challenges.ChallengeAsync(proposal, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = Texts.Format("Debate_Challenge_Status_Done", run.Id);
            var context = new DebateRoomContext(proposal.Request.Question, proposal.Request.Requirements, proposal.Judging.Kind, [.. proposal.Judging.WaivedParticipantVendors], proposal.Judging.Judges);
            Opened?.Invoke(this, new OpenedDebate(run, proposal, context));
        }
#pragma warning disable CA1031 // The challenge service is behind a port: a refusal is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = null;
            ErrorMessage = Texts.Format("Debate_Challenge_Error_Challenge", DebateTexts.Reason(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChallenge() => !IsBusy && Proposal is { Problems.Count: 0 } && _backend.Challenges is not null;

    partial void OnReasonsChanged(string value) => Changed();

    partial void OnQuestionChanged(string value) => Changed();

    partial void OnProponentChanged(AgentChoice? value) => Changed();

    partial void OnCriticChanged(AgentChoice? value) => Changed();

    partial void OnMaxRoundsChanged(string value) => Changed();

    partial void OnMaxMinutesChanged(string value) => Changed();

    partial void OnMaxTokensChanged(string value) => Changed();

    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        Proposal = null;
        Problems = Validate();
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        PrepareCommand.NotifyCanExecuteChanged();
        ChallengeCommand.NotifyCanExecuteChanged();
    }

    private VerdictChallengeRequest? Request()
    {
        if (_project is not { } project || VerdictId is not { } verdict || _requirements is not { } requirements
            || Proponent is not { } proponent || Critic is not { } critic || Limits() is not { } limits || Problems.Count > 0)
        {
            return null;
        }

        var revision = new DebateRequest(
            project,
            DebateMode.Adversarial,
            Question.Trim(),
            requirements,
            new DebateSubject(DebateSubjectKind.Verdict, verdict),
            [new DebateParticipantRequest(proponent.Agent.Id, DebateRole.Proponent), new DebateParticipantRequest(critic.Agent.Id, DebateRole.Critic)],
            [.. Judges.Where(j => j.IsIncluded).Select(j => j.Agent.Id)],
            DebateJudgingKind.IndependentJudge,
            limits);
        return new VerdictChallengeRequest(project, verdict, Reasons.Trim(), revision);
    }

    private DebateLimits? Limits()
    {
        if (!int.TryParse(MaxRounds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rounds) || rounds < 1
            || !int.TryParse(MaxMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 1)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(MaxTokens))
        {
            return new DebateLimits(rounds, TimeSpan.FromMinutes(minutes));
        }

        return long.TryParse(MaxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && tokens > 0
            ? new DebateLimits(rounds, TimeSpan.FromMinutes(minutes), tokens)
            : null;
    }

    private List<string> Validate()
    {
        var problems = new List<string>();
        if (_project is null || VerdictId is null)
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_PickVerdict"));
            return problems;
        }

        if (_backend.Challenges is null)
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_NoService"));
        }

        if (_backend.Verdicts is null)
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_NoVerdictStore"));
        }
        else if (_verdict is null)
        {
            problems.Add(Texts.Format("Debate_Challenge_Problem_VerdictGone", VerdictId));
        }
        else if (_requirements is null)
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_NoRequirements"));
        }

        if (_pending)
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_AlreadyPending"));
        }

        if (string.IsNullOrWhiteSpace(Reasons))
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_NoReasons"));
        }

        if (_backend.Roster is null)
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_NoRoster"));
        }
        else
        {
            if (Proponent is null || Critic is null)
            {
                problems.Add(Texts.Get("Debate_Challenge_Problem_PickDefenders"));
            }
            else if (Proponent.Agent.Id == Critic.Agent.Id)
            {
                problems.Add(Texts.Get("Debate_Challenge_Problem_DifferentAgents"));
            }

            if (!Judges.Any(j => j.IsIncluded))
            {
                problems.Add(Texts.Get(Judges.Count == 0 ? "Debate_Problem_NoJudgeInRoster" : "Debate_Problem_KeepAJudge"));
            }
        }

        if (string.IsNullOrWhiteSpace(Question))
        {
            problems.Add(Texts.Get("Debate_Challenge_Problem_NoQuestion"));
        }

        if (Limits() is null)
        {
            problems.Add(Texts.Get("Debate_Problem_Limits"));
        }

        return problems;
    }
}
