using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Tests.Application.Diagnostics;

/// <summary>F6-2 (docs/20 D-10 and D-11): the closed list of fixes, and the runner that applies, undoes and records.</summary>
public sealed class DoctorFixTests
{
    private static readonly ProjectContext Project = new("p1", @"C:\work\calc");

    /// <summary>The state a test fix changes: a stand-in for the data of Agent Studio.</summary>
    private sealed class State
    {
        public int Value { get; set; } = 1;
    }

    private sealed class ScriptedGate(bool approve) : IApprovalGate
    {
        public List<ApprovalRequest> Asked { get; } = [];

        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(new ApprovalDecision(approve, "person", DateTimeOffset.UnixEpoch, null));
        }
    }

    private static DoctorFix.Reversible Increment(State state) =>
        new("doctor.fix.test-increment", (_, _) => { state.Value++; return Task.CompletedTask; }, (_, _) => { state.Value--; return Task.CompletedTask; });

    private static ApprovalRequest Approval() => new("doctor.test", "Change the state of a test", RiskLevel.Low, null, []);

    [Fact]
    public void The_list_of_fixes_of_Phase_6_holds_only_navigations_and_guides_so_nothing_touches_the_system()
    {
        Assert.NotEmpty(DoctorFixCatalog.All);
        Assert.All(DoctorFixCatalog.All, fix => Assert.True(fix is DoctorFix.Navigate or DoctorFix.Guide, $"{fix.Code} applies something"));
    }

    [Fact]
    public void Every_fix_that_applies_is_reversible_or_carries_its_approval_by_its_type()
    {
        // The hierarchy is closed: the constructor is private, so there is no other form. A fix that applies has Apply and either Undo or Approval.
        var forms = typeof(DoctorFix).GetNestedTypes().OrderBy(t => t.Name).ToArray();

        Assert.Equal(["Approved", "Guide", "Navigate", "Reversible"], forms.Select(t => t.Name).ToArray());
        Assert.NotNull(typeof(DoctorFix.Reversible).GetProperty(nameof(DoctorFix.Reversible.Undo)));
        Assert.NotNull(typeof(DoctorFix.Approved).GetProperty(nameof(DoctorFix.Approved.Approval)));
        Assert.Empty(typeof(DoctorFix).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        var registered = DoctorFixCatalog.All.OfType<DoctorFix.Reversible>().Cast<DoctorFix>().Concat(DoctorFixCatalog.All.OfType<DoctorFix.Approved>());
        Assert.Empty(registered);
    }

    [Fact]
    public async Task Every_registered_fix_runs_through_the_runner_without_changing_anything()
    {
        var recorder = new LogRecorder();
        var runner = new DoctorFixRunner(recorder, new ScriptedGate(true));

        foreach (var fix in DoctorFixCatalog.All)
        {
            var result = await runner.ApplyAsync(fix, "doctor.git", Project, CancellationToken.None);

            Assert.Equal(DoctorFixOutcome.Done, result.Outcome);
            Assert.Null(result.AppliedSequence);
        }

        Assert.Empty(recorder.Recorded);
    }

    [Fact]
    public void The_codes_of_the_fixes_are_unique_and_a_code_that_is_not_in_the_list_is_not_a_fix()
    {
        Assert.Equal(DoctorFixCatalog.All.Count, DoctorFixCatalog.All.Select(f => f.Code).Distinct().Count());
        Assert.Null(DoctorFixCatalog.Find("doctor.fix.format-the-disk"));
        Assert.Null(DoctorFixCatalog.Find(null));
    }

    [Fact]
    public void A_guide_never_carries_something_the_app_could_run_by_mistake()
    {
        Assert.All(DoctorFixCatalog.All.OfType<DoctorFix.Guide>(), guide =>
        {
            Assert.True(guide.Command is not null || guide.Page is not null);
            Assert.True(guide.Command is null || (guide.Command.Length <= DoctorFix.MaxCommandLength && !guide.Command.Contains('\n') && !guide.Command.Contains('&') && !guide.Command.Contains('|')));
            Assert.True(guide.Page is null || (guide.Page.Scheme == Uri.UriSchemeHttps && guide.Page.UserInfo.Length == 0));
        });
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.nothing", null, null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.http", null, new Uri("http://example.test/")));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.multi", "a\nb", null));
        Assert.Throws<ArgumentException>(() => new DoctorFix.Guide("doctor.fix.credentials", null, new Uri("https://user:pass@example.test/")));
    }

    [Fact]
    public void The_guides_of_the_providers_are_known_by_the_id_the_probe_gives()
    {
        Assert.Equal("doctor.fix.install-claude-code", DoctorFixCatalog.ProviderInstallFixCode("claude-code"));
        Assert.Equal("doctor.fix.login-claude-code", DoctorFixCatalog.ProviderLoginFixCode("claude-code"));
        Assert.Null(DoctorFixCatalog.ProviderLoginFixCode("gemini-cli"));
        Assert.Null(DoctorFixCatalog.ProviderInstallFixCode("unknown"));
        var login = Assert.IsType<DoctorFix.Guide>(DoctorFixCatalog.Find("doctor.fix.login-claude-code"));
        Assert.Equal("claude auth login", login.Command);
    }

    [Fact]
    public async Task Applying_and_undoing_a_reversible_fix_leaves_the_state_as_it_was_and_two_entries_in_the_log()
    {
        var state = new State();
        var recorder = new LogRecorder();
        var runner = new DoctorFixRunner(recorder);

        var applied = await runner.ApplyAsync(Increment(state), "doctor.git", Project, CancellationToken.None);
        Assert.Equal(2, state.Value);
        Assert.Equal(DoctorFixOutcome.Done, applied.Outcome);
        Assert.Equal(1, applied.AppliedSequence);

        await runner.UndoAsync(applied.AppliedSequence!.Value, Project, CancellationToken.None);

        Assert.Equal(1, state.Value);
        Assert.Equal(2, recorder.Recorded.Count);
        Assert.All(recorder.Recorded, r => Assert.Equal(DecisionKind.DoctorFixApplied, r.Kind));
        var first = Assert.IsType<DoctorFixAppliedPayload>(recorder.Recorded[0].Payload);
        var second = Assert.IsType<DoctorFixAppliedPayload>(recorder.Recorded[1].Payload);
        Assert.Equal(("doctor.fix.test-increment", "doctor.git", (long?)null), (first.FixCode, first.CheckCode, first.Undoes));
        Assert.Equal(1L, second.Undoes);
        Assert.Equal(DecisionPayload.NoRun, first.RunId);
    }

    [Fact]
    public async Task A_fix_cannot_be_undone_twice_or_from_another_session()
    {
        var state = new State();
        var runner = new DoctorFixRunner(new LogRecorder());
        var applied = await runner.ApplyAsync(Increment(state), "doctor.git", Project, CancellationToken.None);
        await runner.UndoAsync(applied.AppliedSequence!.Value, Project, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.UndoAsync(applied.AppliedSequence!.Value, Project, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DoctorFixRunner(new LogRecorder()).UndoAsync(1, Project, CancellationToken.None));
        Assert.Equal(1, state.Value);
    }

    [Fact]
    public async Task A_fix_that_fails_leaves_the_state_as_it_was_and_records_nothing()
    {
        var state = new State();
        var recorder = new LogRecorder();
        var failing = new DoctorFix.Reversible(
            "doctor.fix.test-failing",
            (_, _) => { state.Value = 99; throw new IOException("disk"); },
            (_, _) => { state.Value = 1; return Task.CompletedTask; });

        var result = await new DoctorFixRunner(recorder).ApplyAsync(failing, "doctor.git", Project, CancellationToken.None);

        Assert.Equal(DoctorFixOutcome.Failed, result.Outcome);
        Assert.Equal(1, state.Value);
        Assert.Empty(recorder.Recorded);
    }

    [Fact]
    public async Task A_fix_with_an_approval_that_is_denied_applies_nothing_and_records_nothing()
    {
        var state = new State();
        var recorder = new LogRecorder();
        var gate = new ScriptedGate(approve: false);
        var fix = new DoctorFix.Approved("doctor.fix.test-approved", Approval(), (_, _) => { state.Value = 2; return Task.CompletedTask; });

        var result = await new DoctorFixRunner(recorder, gate).ApplyAsync(fix, "doctor.git", Project, CancellationToken.None);

        Assert.Equal(DoctorFixOutcome.Denied, result.Outcome);
        Assert.Equal(1, state.Value);
        Assert.Single(gate.Asked);
        Assert.Empty(recorder.Recorded);
    }

    [Fact]
    public async Task A_fix_with_an_approval_that_is_granted_applies_and_leaves_its_entry_and_cannot_be_undone()
    {
        var state = new State();
        var recorder = new LogRecorder();
        var fix = new DoctorFix.Approved("doctor.fix.test-approved", Approval(), (_, _) => { state.Value = 2; return Task.CompletedTask; });
        var runner = new DoctorFixRunner(recorder, new ScriptedGate(approve: true));

        var result = await runner.ApplyAsync(fix, "doctor.git", Project, CancellationToken.None);

        Assert.Equal((DoctorFixOutcome.Done, 2), (result.Outcome, state.Value));
        Assert.Single(recorder.Recorded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.UndoAsync(result.AppliedSequence!.Value, Project, CancellationToken.None));
    }

    [Fact]
    public async Task An_approved_fix_without_a_gate_is_denied_rather_than_applied()
    {
        var state = new State();
        var fix = new DoctorFix.Approved("doctor.fix.test-approved", Approval(), (_, _) => { state.Value = 2; return Task.CompletedTask; });

        var result = await new DoctorFixRunner().ApplyAsync(fix, "doctor.git", Project, CancellationToken.None);

        Assert.Equal(DoctorFixOutcome.Denied, result.Outcome);
        Assert.Equal(1, state.Value);
    }

    [Fact]
    public async Task Without_an_open_project_a_fix_applies_but_leaves_no_entry_and_so_cannot_be_undone()
    {
        var state = new State();
        var recorder = new LogRecorder();

        var result = await new DoctorFixRunner(recorder).ApplyAsync(Increment(state), "doctor.git", null, CancellationToken.None);

        Assert.Equal((DoctorFixOutcome.Done, 2), (result.Outcome, state.Value));
        Assert.Null(result.AppliedSequence);
        Assert.Empty(recorder.Recorded);
    }

    [Fact]
    public async Task The_entry_of_the_log_holds_only_codes_no_text_no_path_of_the_machine()
    {
        var state = new State();
        var recorder = new LogRecorder();
        await new DoctorFixRunner(recorder).ApplyAsync(Increment(state), "doctor.git", Project, CancellationToken.None);

        var payload = Assert.IsType<DoctorFixAppliedPayload>(recorder.Recorded.Single().Payload);

        Assert.True(DoctorCodes.IsCode(payload.FixCode) && DoctorCodes.IsCode(payload.CheckCode));
        Assert.DoesNotContain(@"C:\", System.Text.Json.JsonSerializer.Serialize(payload), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => new DoctorFixRunner(recorder).ApplyAsync(Increment(state), @"C:\work\calc", Project, CancellationToken.None));
    }
}
