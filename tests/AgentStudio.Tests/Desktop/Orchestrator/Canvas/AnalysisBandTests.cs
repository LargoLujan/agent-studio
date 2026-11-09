using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration.Analysis;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Canvas;

/// <summary>
/// F13-9 (docs/17 D-16, D-18 and D-21), the side of the view model: the band shows the findings of <c>MontageAnalyzer</c> with
/// the fix of one click each, runs 200 ms after the last edit off the UI thread and applies its result only if no edit came
/// meanwhile, and every fix is an edit that Ctrl+Z and Ctrl+Y undo and redo. The analyzer spends no quota: nothing in this test
/// has a provider, and the band has no way to reach one.
/// </summary>
public sealed class AnalysisBandTests
{
    /// <summary>The document as the band sees it: a plan and a template that fixes change, and what was written back.</summary>
    private sealed class FakeHost : IDraftHost
    {
        public IReadOnlyList<AgentDefinition> Roster { get; set; } = MontageFixtures.Roster();

        public PlanDefinition? Plan { get; set; } = MontageFixtures.Plan(MontageFixtures.WriterBehindReview());

        public int MaxAgentRuns { get; set; } = 12;

        public int Builds { get; private set; }

        public List<(Fix? Fix, bool Roster)> Written { get; } = [];

        public Action? OnBuild { get; set; }

        public bool RefuseWrites { get; set; }

        public bool Thin { get; set; }

        public FixEnvironment Environment => new("Integración", MontageFixtures.Steps());

        public MontageEditState ReadState() => new(Plan, Roster, MaxAgentRuns);

        public MontageDraft? BuildDraft(long generation)
        {
            Builds++;
            OnBuild?.Invoke();
            var draft = MontageFixtures.Draft(Roster, Plan) with { Generation = generation };
            return Thin ? draft with { TaskContexts = [MontageFixtures.Facts("T1", memory: false)] } : draft;
        }

        public Task<bool> ApplyAsync(MontageEditState before, MontageEditState after, Fix? fix)
        {
            if (RefuseWrites)
            {
                return Task.FromResult(false);
            }

            Written.Add((fix, !ReferenceEquals(before.Roster, after.Roster)));
            Plan = after.Plan;
            Roster = after.Roster;
            MaxAgentRuns = after.MaxAgentRuns;
            return Task.FromResult(true);
        }

        public string? DescribeValidator(Finding finding) => "validator: " + finding.Code;
    }

    private static AnalysisBandViewModel Band(IDraftHost host, FakeTimeProvider? time = null) => new(host, Text(), time ?? new FakeTimeProvider(T0));

    [Fact]
    public async Task It_shows_the_findings_with_their_text_severity_and_the_button_of_their_fix()
    {
        var host = new FakeHost();
        var band = Band(host);

        await band.RefreshAsync();

        var integration = Assert.Single(band.Findings, f => f.Finding.Code == FindingCodes.NoIntegration);
        Assert.Equal(FindingSeverity.Error, integration.Severity);
        Assert.Equal("Error", integration.SeverityText);
        Assert.Equal("Falta la integración: nada llegaría a tu rama.", integration.Text);
        Assert.Equal("Añadir una integración al final", integration.ButtonText);
        Assert.True(integration.HasFix);
        Assert.True(band.BlocksApproval);
        Assert.True(band.IsCurrent);
        Assert.StartsWith("1 errores", band.Summary, StringComparison.Ordinal);
        Assert.Contains("Error", integration.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Añadir una integración al final", integration.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_suggestions_are_folded_until_the_person_opens_them()
    {
        var host = new FakeHost { Thin = true, Plan = MontageFixtures.Plan() };
        var band = Band(host);

        await band.RefreshAsync();

        Assert.Contains(band.Findings, f => f.Severity == FindingSeverity.Suggestion);
        Assert.DoesNotContain(band.Visible, f => f.Severity == FindingSeverity.Suggestion);
        band.ShowSuggestions = true;
        Assert.Contains(band.Visible, f => f.Severity == FindingSeverity.Suggestion);
    }

    [Fact]
    public async Task A_fix_is_one_edit_of_the_draft_and_Ctrl_Z_and_Ctrl_Y_undo_and_redo_it()
    {
        var host = new FakeHost();
        var band = Band(host);
        band.Reset();
        await band.RefreshAsync();
        var before = host.Plan!.ContentHash;

        await band.Findings.Single(f => f.Finding.Code == FindingCodes.NoIntegration).ApplyFixCommand.ExecuteAsync(null);

        Assert.NotEqual(before, host.Plan!.ContentHash);
        Assert.Contains(host.Plan.Tasks, t => t.Kind == WorkTaskKind.Integration);
        Assert.StartsWith("Hecho: Añadir una integración al final.", band.Notice, StringComparison.Ordinal);
        Assert.True(band.CanUndo);

        await band.UndoCommand.ExecuteAsync(null);
        Assert.Equal(before, host.Plan.ContentHash);
        Assert.True(band.CanRedo);

        await band.RedoCommand.ExecuteAsync(null);
        Assert.Contains(host.Plan!.Tasks, t => t.Kind == WorkTaskKind.Integration);

        await band.RefreshAsync();
        Assert.DoesNotContain(band.Findings, f => f.Finding.Code == FindingCodes.NoIntegration);
    }

    [Fact]
    public async Task A_fix_that_the_applier_refuses_changes_nothing_and_says_why()
    {
        var host = new FakeHost();
        var band = Band(host);
        band.Reset();
        var before = host.Plan!.ContentHash;

        await band.ApplyFixAsync(new AddDependencyFix(new TaskId("T1"), new TaskId("T1")));

        Assert.Equal(before, host.Plan!.ContentHash);
        Assert.Empty(host.Written);
        Assert.False(string.IsNullOrWhiteSpace(band.Notice));
        Assert.DoesNotContain("[Text_", band.Notice, StringComparison.Ordinal);
        Assert.False(band.CanUndo);
    }

    [Fact]
    public async Task A_fix_the_document_refuses_to_write_leaves_the_history_as_it_was()
    {
        var host = new FakeHost { RefuseWrites = true };
        var band = Band(host);
        band.Reset();
        await band.RefreshAsync();

        await band.Findings.Single(f => f.Finding.Code == FindingCodes.NoIntegration).ApplyFixCommand.ExecuteAsync(null);

        Assert.Empty(host.Written);
        Assert.False(band.CanUndo);
        Assert.Null(band.Notice);
    }

    [Fact]
    public async Task What_a_fix_does_beyond_its_edit_is_said_before_it_is_applied()
    {
        var wrongJudge = MontageFixtures.Roster().Where(a => a.Role != AgentRole.Judge).Append(MontageFixtures.Agent("juez", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic)).ToList();
        var narrow = MontageFixtures.Roster().Where(a => a.Id.Value != "ejecutor2").ToList().With("ejecutor", a => a with { PathGrants = [new PathGrant("docs/", FileAccessLevel.ReadWrite)] });
        var wrong = Band(new FakeHost { Roster = wrongJudge, Plan = MontageFixtures.Plan() });
        var outside = Band(new FakeHost { Roster = narrow, Plan = MontageFixtures.Plan() });

        await wrong.RefreshAsync();
        await outside.RefreshAsync();

        var judge = wrong.Findings.Single(f => f.Finding.Code == FindingCodes.JudgeSameVendor);
        Assert.Equal("Cambia la plantilla: retira la aprobación del plan", judge.Consequence);
        var area = outside.Findings.Single(f => f.Finding.Code == FindingCodes.WriteOutsideArea);
        Assert.Equal("Pide Windows Hello", area.Consequence);
    }

    [Fact]
    public async Task The_analysis_runs_200_ms_after_the_last_edit_and_a_burst_of_edits_is_one_run()
    {
        var time = new FakeTimeProvider(T0);
        var host = new FakeHost();
        var band = Band(host, time);

        band.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(150));
        band.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(150));
        band.Schedule();
        Assert.Equal(0, host.Builds);

        time.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal(0, host.Builds);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await band.Running;

        Assert.Equal(1, host.Builds);
        Assert.True(band.IsCurrent);
        Assert.Equal(AnalysisBandViewModel.Debounce, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task A_result_is_applied_only_if_no_edit_came_while_it_ran()
    {
        var host = new FakeHost();
        var band = Band(host);
        host.OnBuild = band.Schedule;

        await band.RefreshAsync();

        // The edit that came during the analysis made its result old: nothing is shown, and the next analysis is on its way.
        Assert.Empty(band.Findings);
        Assert.False(band.IsCurrent);

        host.OnBuild = null;
        await band.RefreshAsync();
        Assert.NotEmpty(band.Findings);
    }

    [Fact]
    public async Task Without_enough_to_analyze_the_band_says_what_it_needs()
    {
        var band = Band(new NoDraftHost());

        await band.RefreshAsync();

        Assert.Empty(band.Findings);
        Assert.Equal("El análisis necesita requisitos congelados y un perfil de verificación.", band.Summary);
        Assert.False(band.BlocksApproval);
    }

    [Fact]
    public async Task A_clean_draft_says_so()
    {
        var host = new FakeHost { Plan = MontageFixtures.Plan() };
        var band = Band(host);

        await band.RefreshAsync();

        Assert.DoesNotContain(band.Findings, f => f.Severity == FindingSeverity.Error);
        Assert.False(band.BlocksApproval);
    }

    [Fact]
    public async Task A_change_of_the_canvas_by_hand_is_kept_in_the_history_once_per_pause()
    {
        var host = new FakeHost { Plan = MontageFixtures.Plan() };
        var band = Band(host);
        band.Reset();
        await band.RefreshAsync();
        Assert.False(band.CanUndo);

        var original = host.Plan!;
        host.Plan = MontageFixtures.Plan([.. original.Tasks, MontageFixtures.Task("T9")]);
        await band.RefreshAsync();
        Assert.True(band.CanUndo);

        await band.UndoCommand.ExecuteAsync(null);
        Assert.Equal(original.ContentHash, host.Plan!.ContentHash);
    }

    private sealed class NoDraftHost : IDraftHost
    {
        public FixEnvironment Environment => new("-", []);

        public MontageEditState ReadState() => new(null, [], 12);

        public MontageDraft? BuildDraft(long generation) => null;

        public Task<bool> ApplyAsync(MontageEditState before, MontageEditState after, Fix? fix) => Task.FromResult(true);

        public string? DescribeValidator(Finding finding) => null;
    }
}
