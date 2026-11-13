using System.Globalization;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

/// <summary>
/// F10-1 and F10-8 on the screen (docs/21 section 9): the section of pull requests, with doubles of the ports. An action whose capability is not declared has no button, the states with
/// no remote, no <c>gh</c> or no session say so, and nothing is shown that a port did not give.
/// </summary>
public sealed class PullRequestsViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PrWorld WorldWith(PullRequestView view)
    {
        var world = new PrWorld();
        world.Projector.Views.Add(view);
        return world;
    }

    private static PullRequestView OpenView(int number = 12) => PrData.View(PrData.Snapshot(number), number);

    // ---- States that are not a list ----

    [Fact]
    public async Task Without_a_project_the_section_says_to_open_one_and_shows_no_list()
    {
        var world = new PrWorld();
        await world.Model.SetProjectAsync(null);

        Assert.Equal(PullRequestsStatus.NoProject, world.Model.Status);
        Assert.Equal(Texts.Get("Hosting_Pr_NoProject"), world.Model.GuideText);
        Assert.Empty(world.Model.Rows);
        Assert.Equal(0, world.Projector.Reads);
    }

    [Fact]
    public async Task Without_the_ports_of_the_remote_the_section_says_it_is_not_available()
    {
        var model = PullRequestsViewModel.Unavailable(PrData.Clock());
        await model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.Unavailable, model.Status);
        Assert.False(model.IsAvailable);
        Assert.Equal(Texts.Get("Hosting_Pr_Unavailable"), model.GuideText);
    }

    [Fact]
    public async Task Without_a_recognised_remote_the_guide_is_said_and_the_log_is_not_shown_as_an_empty_list()
    {
        var world = new PrWorld();
        world.Projector.Views.Add(OpenView());
        var model = new PullRequestsViewModel(new FakeProviders(world.Provider, null), world.Projector, world.Sync, world.Publication, world.Gate, null, PrData.Clock());
        await model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.NoRemote, model.Status);
        Assert.Equal(Texts.Get("Hosting_Pr_NoRemote"), model.GuideText);
        Assert.Empty(model.Rows);
        Assert.False(model.HasRows);
        Assert.Null(model.RepositoryText);
    }

    [Fact]
    public async Task Without_gh_the_guide_says_how_to_install_it_and_no_action_is_offered()
    {
        var world = WorldWith(OpenView());
        world.Provider.Availability = new HostingAvailability(false, null, HostingCapabilities.None);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.NoGh, world.Model.Status);
        Assert.Contains("winget install", world.Model.GuideText, StringComparison.Ordinal);
        Assert.Empty(world.Model.PublishableRuns);
    }

    [Fact]
    public async Task Without_a_session_the_guide_says_how_to_sign_in()
    {
        var world = WorldWith(OpenView());
        world.Sync.HealthNow = new RemoteSyncHealth(null, HostingOutcome.SignInNeeded, null, PrData.Now);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.SignInNeeded, world.Model.Status);
        Assert.Equal(Texts.Get("Hosting_SignIn"), world.Model.GuideText);
        Assert.Equal(Texts.Get("Hosting_SignIn"), world.Model.SessionText);
    }

    [Fact]
    public async Task A_limit_of_the_platform_says_until_when()
    {
        var world = WorldWith(OpenView());
        world.Sync.HealthNow = new RemoteSyncHealth(null, HostingOutcome.RateLimited, PrData.Now.AddMinutes(20), PrData.Now);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.RateLimited, world.Model.Status);
        Assert.Equal(Texts.Format("Hosting_RateLimited", "10:20"), world.Model.GuideText);
    }

    [Fact]
    public async Task With_a_remote_and_nothing_followed_it_says_so_only_when_the_log_was_read()
    {
        var world = new PrWorld();
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.Ready, world.Model.Status);
        Assert.Equal(Texts.Get("Hosting_Pr_None"), world.Model.GuideText);
        Assert.Equal(Texts.Format("Hosting_Header_Repository", "acme/calc"), world.Model.RepositoryText);
    }

    [Fact]
    public async Task A_projection_that_fails_is_said_and_never_shown_as_an_empty_list()
    {
        var world = new PrWorld();
        world.Projector.Failure = new InvalidOperationException("log");
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(Texts.Get("Hosting_Pr_Unreadable"), world.Model.GuideText);
        Assert.Empty(world.Model.Rows);
    }

    [Fact]
    public async Task The_header_says_the_session_the_last_good_sync_and_whether_the_poll_is_on()
    {
        var world = WorldWith(OpenView());
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(Texts.Format("Hosting_Header_SessionOk", "13/11/2026 09:55"), world.Model.SessionText);
        Assert.Equal(Texts.Format("Hosting_Header_LastSync", "13/11/2026 09:55"), world.Model.LastSyncText);
        Assert.Equal(Texts.Get("Hosting_Header_SyncOn"), world.Model.SyncPolicyText);
    }

    // ---- The list ----

    [Fact]
    public async Task Each_state_of_a_pull_request_is_said_and_a_merge_says_its_mode_as_a_deduction()
    {
        var world = new PrWorld();
        world.Projector.Views.AddRange(
        [
            PrData.View(PrData.Snapshot(1), 1),
            PrData.View(PrData.Snapshot(2, PullRequestState.Draft), 2),
            PrData.View(PrData.Snapshot(3, PullRequestState.Merged, mode: RemoteMergeMode.Squash, results: [PrData.Sha('f')]), 3),
            PrData.View(PrData.Snapshot(4, PullRequestState.Closed), 4),
            PrData.View(null, number: 5),
        ]);
        await world.Model.SetProjectAsync(PrData.Project);

        string StateOf(int n) => world.Model.Rows.Single(r => r.Number == n).StateText;
        Assert.Equal(Texts.Get("Hosting_State_Open"), StateOf(1));
        Assert.Equal(Texts.Get("Hosting_State_Draft"), StateOf(2));
        Assert.Equal(Texts.Format("Hosting_State_MergedDeduced", Texts.Get("Text_remote_merge_squash")), StateOf(3));
        Assert.Equal(Texts.Get("Hosting_State_Closed"), StateOf(4));
        Assert.Equal(Texts.Get("Hosting_State_NotObserved"), StateOf(5));
    }

    [Fact]
    public async Task A_row_says_the_origin_the_verdict_the_checks_the_reviews_and_when_it_was_seen()
    {
        var world = WorldWith(OpenView());
        await world.Model.SetProjectAsync(PrData.Project);
        var row = world.Model.Rows.Single();

        Assert.Equal("#12", row.NumberText);
        Assert.Equal("T-101 calc", row.Title);
        Assert.Equal(Texts.Format("Hosting_Origin_Run", "20261112-a1b2c3"), row.OriginText);
        Assert.Equal(Texts.Get("Hosting_Verdict_Approved"), row.VerdictText);
        Assert.Equal(Texts.Get("Hosting_Judged_Same"), row.JudgedText);
        Assert.Equal(Texts.Format("Hosting_Checks", 5, 1, 2), row.ChecksText);
        Assert.Equal(Texts.Format("Hosting_Reviews_Approved", 1), row.ReviewsText);
        Assert.Equal(Texts.Format("Hosting_Ago_Minutes", 3), row.SyncText);
    }

    [Fact]
    public async Task A_head_that_moved_after_the_verdict_is_said_and_an_unknown_judged_version_is_not_a_claim()
    {
        var world = new PrWorld();
        world.Projector.Views.Add(new PullRequestView(PrData.Pr(12), null, PrData.Snapshot(12), PrData.Sha('e'), false, VerdictStanding.Approved, RemoteDisplayTexts.None, PrData.Now, []));
        world.Projector.Views.Add(new PullRequestView(PrData.Pr(13), null, PrData.Snapshot(13), null, false, null, RemoteDisplayTexts.None, PrData.Now, []));
        await world.Model.SetProjectAsync(PrData.Project);

        var moved = world.Model.Rows.Single(r => r.Number == 12);
        Assert.True(moved.HeadMoved);
        Assert.Equal(Texts.Get("Hosting_Head_Moved"), moved.JudgedText);
        var unknown = world.Model.Rows.Single(r => r.Number == 13);
        Assert.False(unknown.HeadMoved);
        Assert.Equal(Texts.Get("Hosting_Judged_Unknown"), unknown.JudgedText);
        Assert.Equal(Texts.Get("Hosting_Verdict_None"), unknown.VerdictText);
    }

    [Fact]
    public async Task The_deployment_is_said_only_when_the_platform_reports_deployments_and_one_was_seen()
    {
        var results = new[] { PrData.Sha('f') };
        var deployed = PrData.Snapshot(12, PullRequestState.Merged, results: results, deployment: new RemoteDeployment(7, PrData.Sha('f'), DeploymentState.Succeeded));
        var world = WorldWith(PrData.View(deployed));
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(Texts.Get("Hosting_Deployed"), world.Model.Rows.Single().DeploymentText);
        Assert.Equal("prod", world.Model.Rows.Single().EnvironmentText);

        // Without the capability the column does not exist, even if the snapshot has the data.
        var without = WorldWith(PrData.View(deployed));
        without.Provider.Availability = new HostingAvailability(true, new Version(2, 96, 0), PrData.Capabilities(PrData.Reads & ~HostingCapability.ReadDeployments));
        await without.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(string.Empty, without.Model.Rows.Single().DeploymentText);

        // With the capability and no deployment seen there is no «not deployed».
        var none = WorldWith(PrData.View(PrData.Snapshot(12, PullRequestState.Merged, results: results)));
        await none.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(string.Empty, none.Model.Rows.Single().DeploymentText);
    }

    [Fact]
    public async Task What_waits_to_sync_is_said_with_its_reason_above_and_in_its_row()
    {
        var ticket = new RemoteWorkTicket("t-1", HostingOperationKind.MergePullRequest, PrData.Pr(12));
        var offline = new RemoteWorkStatus(ticket, RemoteWorkState.PendingSync, PendingReason.Offline, PrData.Now.AddMinutes(5), null);
        var world = WorldWith(PrData.View(PrData.Snapshot(12), pending: [offline]));
        world.Sync.PendingWork.Add(offline);
        world.Sync.PendingWork.Add(new RemoteWorkStatus(ticket, RemoteWorkState.WaitingForProject, PendingReason.ProjectBusy, null, null));
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(Texts.Format("Hosting_Pending_Offline", "10:05"), world.Model.Pending[0].Text);
        Assert.Equal(Texts.Get("Hosting_Pending_Busy"), world.Model.Pending[1].Text);
        var row = world.Model.Rows.Single();
        Assert.True(row.IsPending);
        Assert.Equal(Texts.Format("Hosting_Pending_Offline", "10:05"), row.SyncText);
    }

    [Fact]
    public async Task A_coded_message_of_pending_work_is_painted_and_an_unknown_code_is_said_generically()
    {
        var ticket = new RemoteWorkTicket("t-1", null, PrData.Pr(12));
        var known = new RemoteWorkStatus(ticket, RemoteWorkState.Failed, null, null, new UserText("merge.blocked"));
        var unknown = new RemoteWorkStatus(ticket, RemoteWorkState.Failed, null, null, new UserText("something.new"));
        var world = WorldWith(OpenView());
        world.Sync.PendingWork.AddRange([known, unknown]);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(Texts.Get("Text_merge_blocked"), world.Model.Pending[0].Text);
        Assert.Contains("something.new", world.Model.Pending[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[Text_", world.Model.Pending[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_title_and_the_names_of_the_checks_of_the_remote_are_data_cleaned_and_bounded()
    {
        var hostile = "T-1\u001b[31m\u202e \r\n[link](http://evil) " + new string('x', 400);
        var world = WorldWith(PrData.View(PrData.Snapshot(12), display: new RemoteDisplayTexts(hostile, ["unit\u0000tests", new string('y', 500)], null)));
        var row = await world.OpenAsync();

        Assert.DoesNotContain('\u001b', row.Title);
        Assert.DoesNotContain('\u202e', row.Title);
        Assert.DoesNotContain('\n', row.Title);
        Assert.True(row.Title.Length <= HostingTexts.MaxDataLength);
        Assert.All(world.Model.Detail!.FailingChecks, c => Assert.True(c.Length <= HostingTexts.MaxDataLength && !c.Contains('\u0000', StringComparison.Ordinal)));
    }

    // ---- F10-1: no capability, no button ----

    [Fact]
    public async Task An_action_whose_capability_is_not_declared_has_no_button()
    {
        var world = WorldWith(OpenView());
        world.Provider.Availability = new HostingAvailability(true, new Version(2, 96, 0), PrData.Capabilities(PrData.Reads));
        await world.OpenAsync();
        var detail = world.Model.Detail!;

        Assert.False(detail.CanMerge);
        Assert.False(detail.CanClose);
        Assert.False(detail.CanRequestReviewers);
        Assert.False(detail.CanPublishVerdict);
        Assert.False(detail.CanDeleteBranch);
        Assert.True(detail.CanOpenInBrowser);
        Assert.True(detail.CanUnfollow);
    }

    [Fact]
    public async Task Each_action_appears_with_its_own_capability_and_only_for_a_state_that_admits_it()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        var open = world.Model.Detail!;
        Assert.True(open.CanMerge && open.CanClose && open.CanRequestReviewers && open.CanPublishVerdict);
        Assert.False(open.CanDeleteBranch);

        var merged = new PrWorld();
        merged.Projector.Views.Add(PrData.View(PrData.Snapshot(12, PullRequestState.Merged, results: [PrData.Sha('f')])));
        await merged.OpenAsync();
        var detail = merged.Model.Detail!;
        Assert.False(detail.CanMerge);
        Assert.False(detail.CanClose);
        Assert.False(detail.CanRequestReviewers);
        Assert.False(detail.CanPublishVerdict);
        Assert.True(detail.CanDeleteBranch);

        var draft = new PrWorld();
        draft.Projector.Views.Add(PrData.View(PrData.Snapshot(12, PullRequestState.Draft)));
        await draft.OpenAsync();
        Assert.False(draft.Model.Detail!.CanMerge);
        Assert.True(draft.Model.Detail.CanClose);
    }

    [Fact]
    public async Task Deleting_the_remote_branch_is_offered_only_while_it_still_exists_and_the_run_is_known()
    {
        var gone = new PrWorld();
        gone.Projector.Views.Add(PrData.View(PrData.Snapshot(12, PullRequestState.Closed, headBranchExists: false)));
        await gone.OpenAsync();
        Assert.False(gone.Model.Detail!.CanDeleteBranch);

        var noRun = new PrWorld();
        noRun.Projector.Views.Add(PrData.View(PrData.Snapshot(12, PullRequestState.Closed), run: null));
        await noRun.OpenAsync();
        Assert.False(noRun.Model.Detail!.CanDeleteBranch);
        Assert.False(noRun.Model.Detail.CanPublishVerdict);
    }

    [Fact]
    public async Task With_the_three_methods_declared_only_those_the_repository_allows_are_offered()
    {
        var world = WorldWith(OpenView());
        world.Provider.Facts = new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Ok, new RemoteRepositoryFacts(PrData.Main, [MergeMethod.Squash, MergeMethod.Rebase], true, false));
        await world.OpenAsync();

        Assert.Equal([MergeMethod.Squash, MergeMethod.Rebase], world.Model.Detail!.MergeMethods);
    }

    [Fact]
    public async Task With_one_method_allowed_the_card_has_one_option_and_a_branch_of_linear_history_takes_out_the_merge_commit()
    {
        var one = WorldWith(OpenView());
        one.Provider.Facts = new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Ok, new RemoteRepositoryFacts(PrData.Main, [MergeMethod.Squash], true, false));
        await one.OpenAsync();
        one.Model.OpenMergeCardCommand.Execute(null);
        var card = Assert.IsType<MergeCardViewModel>(one.Model.Card);
        Assert.Single(card.Methods);
        Assert.Equal(MergeMethod.Squash, card.SelectedMethod!.Method);

        var linear = WorldWith(OpenView());
        linear.Provider.Rules = new HostingResult<BranchRules>(HostingOutcome.Ok, new BranchRules(true, [new BranchRule(BranchRuleKind.LinearHistory, null)]));
        await linear.OpenAsync();
        Assert.Equal([MergeMethod.Squash, MergeMethod.Rebase], linear.Model.Detail!.MergeMethods);
    }

    [Fact]
    public async Task A_repository_the_account_cannot_write_to_or_that_is_archived_offers_no_write_and_no_merge()
    {
        foreach (var facts in new[] { new RemoteRepositoryFacts(PrData.Main, [MergeMethod.Squash], false, false), new RemoteRepositoryFacts(PrData.Main, [MergeMethod.Squash], true, true) })
        {
            var world = WorldWith(OpenView());
            world.Provider.Facts = new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Ok, facts);
            await world.OpenAsync();
            var detail = world.Model.Detail!;
            Assert.False(detail.CanMerge || detail.CanClose || detail.CanRequestReviewers || detail.CanPublishVerdict);
        }
    }

    [Fact]
    public async Task Settings_of_the_repository_that_cannot_be_read_are_said_and_no_merge_is_offered_without_a_mode()
    {
        var world = WorldWith(OpenView());
        world.Provider.Facts = new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Forbidden);
        await world.OpenAsync();
        var detail = world.Model.Detail!;

        Assert.False(detail.CanMerge);
        Assert.Contains(detail.Notes, n => n.Text.StartsWith(Texts.Get("Hosting_Facts_Unreadable").Split("{0}")[0], StringComparison.Ordinal) && n.IsWarning);
    }

    [Fact]
    public async Task Protections_that_could_not_be_read_say_so_and_are_never_shown_as_no_protections()
    {
        var world = WorldWith(OpenView());
        world.Provider.Rules = new HostingResult<BranchRules>(HostingOutcome.Ok, BranchRules.Unreadable);
        await world.OpenAsync();
        Assert.Equal([Texts.Format("Hosting_Rules_Unreadable", "main")], world.Model.Detail!.Rules.Select(r => r.Text));

        var failed = WorldWith(OpenView());
        failed.Provider.Rules = new HostingResult<BranchRules>(HostingOutcome.Unreachable);
        await failed.OpenAsync();
        Assert.Equal([Texts.Format("Hosting_Rules_Unreadable", "main")], failed.Model.Detail!.Rules.Select(r => r.Text));

        var none = WorldWith(OpenView());
        none.Provider.Rules = new HostingResult<BranchRules>(HostingOutcome.Ok, new BranchRules(true, []));
        await none.OpenAsync();
        Assert.Equal([Texts.Format("Hosting_Rules_None", "main")], none.Model.Detail!.Rules.Select(r => r.Text));
    }

    [Fact]
    public async Task Without_a_status_of_commits_the_verdict_is_offered_as_a_comment_only()
    {
        var world = WorldWith(OpenView());
        world.Provider.Availability = new HostingAvailability(true, new Version(2, 96, 0), PrData.Capabilities((PrData.Reads | PrData.Writes) & ~HostingCapability.CommitStatus, MergeMethod.Squash));
        await world.OpenAsync();

        Assert.True(world.Model.Detail!.CanPublishVerdict);
        Assert.True(world.Model.Detail.VerdictOnlyAsComment);
        world.Model.OpenVerdictCardCommand.Execute(null);
        Assert.Contains(world.Model.Card!.Lines, l => l.Text == Texts.Get("Hosting_Verdict_OnlyComment"));
    }

    [Fact]
    public async Task A_second_provider_with_other_capabilities_goes_through_the_same_screen()
    {
        // F10-9: numeric votes and no status of commits: the screen only reads capabilities and enums, so nothing of it changes.
        var world = WorldWith(OpenView());
        world.Provider.Availability = new HostingAvailability(true, new Version(1, 0, 0), PrData.Capabilities(HostingCapability.ReadRepository | HostingCapability.ReadReviews | HostingCapability.Comment | HostingCapability.ClosePullRequest));
        await world.OpenAsync();

        Assert.True(world.Model.Detail!.CanClose);
        Assert.True(world.Model.Detail.VerdictOnlyAsComment);
        Assert.Equal(string.Empty, world.Model.Rows.Single().ChecksText);
        Assert.False(world.Model.Detail.CanMerge);
    }

    // ---- Acting ----

    [Fact]
    public async Task The_merge_card_says_what_it_does_and_the_fixed_notice_of_windows_hello_before_its_button()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        world.Model.OpenMergeCardCommand.Execute(null);
        var card = Assert.IsType<MergeCardViewModel>(world.Model.Card);

        Assert.Equal(Texts.Format("Hosting_Merge_Hello", "main"), card.HelloNotice);
        Assert.Contains(card.Lines, l => l.Text == Texts.Format("Hosting_Merge_Commit", new string('a', 8)));
        Assert.Contains(card.Lines, l => l.Text == Texts.Format("Hosting_Rule_RequiredReviews_Count", 1));
        Assert.Contains(card.Lines, l => l.Text == Texts.Format("Hosting_Checks", 5, 1, 2));
        Assert.False(card.ConfirmCommand.CanExecute(null));
        Assert.DoesNotContain("merge", world.Publication.Calls);

        card.SelectedMethod = card.Methods.Single(m => m.Method == MergeMethod.Rebase);
        await card.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(MergeMethod.Rebase, world.Publication.MergedWith);
        Assert.Null(world.Model.Card);
        Assert.Equal(Texts.Get("Hosting_Card_Sent"), world.Model.Message);
    }

    [Fact]
    public async Task A_head_that_changed_after_the_verdict_is_a_warning_on_the_merge_card()
    {
        var world = new PrWorld();
        world.Projector.Views.Add(PrData.View(PrData.Snapshot(12), judged: PrData.Sha('e'), headIsJudged: false));
        await world.OpenAsync();
        world.Model.OpenMergeCardCommand.Execute(null);

        Assert.Contains(world.Model.Card!.Lines, l => l.IsWarning && l.Text == Texts.Get("Hosting_Head_Moved"));
    }

    [Fact]
    public async Task A_refusal_of_the_service_is_said_with_its_coded_text_and_the_card_stays_open()
    {
        var world = WorldWith(OpenView());
        world.Publication.Failure = new UserTextException(new UserText("merge.head-moved"), "head moved");
        await world.OpenAsync();
        world.Model.OpenMergeCardCommand.Execute(null);
        var card = (MergeCardViewModel)world.Model.Card!;
        card.SelectedMethod = card.Methods[0];
        await card.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Texts.Get("Text_merge_head_moved"), card.Error);
        Assert.NotNull(world.Model.Card);

        world.Publication.Failure = new InvalidOperationException("boom");
        await card.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Hosting_Card_Failed"), card.Error);
    }

    [Fact]
    public async Task Closing_and_deleting_show_their_card_with_the_notice_of_windows_hello_and_call_the_service_only_on_confirm()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        world.Model.OpenCloseCardCommand.Execute(null);
        var close = Assert.IsType<ActionCardViewModel>(world.Model.Card);
        Assert.True(close.HasHelloNotice);
        Assert.Empty(world.Publication.Calls);
        await close.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["close"], world.Publication.Calls);

        var merged = new PrWorld();
        merged.Projector.Views.Add(PrData.View(PrData.Snapshot(12, PullRequestState.Merged, results: [PrData.Sha('f')])));
        await merged.OpenAsync();
        merged.Model.OpenDeleteBranchCardCommand.Execute(null);
        var delete = Assert.IsType<ActionCardViewModel>(merged.Model.Card);
        Assert.Equal(Texts.Get("Hosting_Delete_Hello"), delete.HelloNotice);
        Assert.Contains(delete.Lines, l => l.Text == Texts.Format("Hosting_Delete_Explain", "agentstudio/run-20261112-a1b2c3"));
        await delete.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["delete"], merged.Publication.Calls);
    }

    [Fact]
    public async Task Cancelling_a_card_closes_it_and_calls_nothing()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        world.Model.OpenCloseCardCommand.Execute(null);
        world.Model.Card!.CancelCommand.Execute(null);

        Assert.Null(world.Model.Card);
        Assert.Empty(world.Publication.Calls);
    }

    [Fact]
    public async Task The_reviewers_card_refuses_an_account_that_is_not_one_before_calling_the_service()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        world.Model.OpenReviewersCardCommand.Execute(null);
        var card = Assert.IsType<ReviewersCardViewModel>(world.Model.Card);
        Assert.False(card.ConfirmCommand.CanExecute(null));

        card.LoginsText = "octo --admin";
        await card.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Text_account_login_invalid"), card.Error);
        Assert.Empty(world.Publication.Calls);

        card.LoginsText = "octo, hubot";
        await card.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["octo", "hubot"], world.Publication.Reviewers!.Select(r => r.Value));
    }

    [Fact]
    public async Task Publishing_the_verdict_unfollowing_and_syncing_go_to_their_ports()
    {
        var world = WorldWith(OpenView());
        var row = await world.OpenAsync();
        world.Model.OpenVerdictCardCommand.Execute(null);
        await world.Model.Card!.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["verdict"], world.Publication.Calls);

        await world.Model.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(1, world.Sync.SyncNows);

        await world.Model.UnfollowCommand.ExecuteAsync(null);
        Assert.Equal([row.Reference], world.Sync.Unfollowed);
    }

    [Fact]
    public async Task The_screen_never_asks_the_gate_to_authorize_and_never_writes_through_the_provider()
    {
        // FakeGate.AuthorizeAsync and the writes of FakeProvider throw: the whole flow of a merge and of a publication runs without touching them.
        var world = WorldWith(OpenView());
        world.Model.SetBranches(["agentstudio/run-20261113-aa"]);
        await world.OpenAsync();
        world.Model.OpenMergeCardCommand.Execute(null);
        var merge = (MergeCardViewModel)world.Model.Card!;
        merge.SelectedMethod = merge.Methods[0];
        await merge.ConfirmCommand.ExecuteAsync(null);
        await world.Model.OpenPublishCardCommand.ExecuteAsync(world.Model.PublishableRuns.Single());
        await world.Model.Card!.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["merge", "preview", "publish"], world.Publication.Calls);
    }

    [Fact]
    public async Task Checking_the_connection_says_the_account_or_the_reason()
    {
        var world = WorldWith(OpenView());
        await world.Model.SetProjectAsync(PrData.Project);
        await world.Model.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Format("Hosting_Connection_Ok", "octo"), world.Model.Message);

        world.Provider.Account = new HostingResult<RemoteLogin>(HostingOutcome.RateLimited, null, null, PrData.Now.AddMinutes(30));
        await world.Model.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Format("Hosting_RateLimited", "10:30"), world.Model.Error);

        world.Provider.Account = new HostingResult<RemoteLogin>(HostingOutcome.Unreachable);
        await world.Model.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Text_hosting_outcome_unreachable"), world.Model.Error);

        world.Provider.Account = new HostingResult<RemoteLogin>(HostingOutcome.InvalidAnswer, null, null, null, "account.login-invalid");
        await world.Model.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Text_account_login_invalid"), world.Model.Error);
    }

    // ---- The card of publication ----

    [Fact]
    public async Task The_publish_card_says_where_it_goes_what_goes_up_and_the_protections_and_what_covers_it()
    {
        var world = WorldWith(OpenView());
        world.Model.SetBranches(["main", "agentstudio/run-20261113-aa"]);
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(["20261113-aa"], world.Model.PublishableRuns.Select(r => r.Run));

        await world.Model.OpenPublishCardCommand.ExecuteAsync(world.Model.PublishableRuns.Single());
        var card = Assert.IsType<PublishCardViewModel>(world.Model.Card);

        Assert.Contains(card.Lines, l => l.Text == Texts.Format("Hosting_Publish_Target", "acme/calc", "agentstudio/run-20261113-aa", "main"));
        Assert.Contains(card.Lines, l => l.Text == Texts.Format("Hosting_Publish_Uploads", 3, 2, 1));
        Assert.Contains(card.Lines, l => l.Text == Texts.Format("Hosting_Rule_RequiredChecks_Count", 2));
        Assert.Equal(Texts.Get("Hosting_Publish_CoverHello"), card.CoverageText);
        Assert.Equal("T-101 calc", card.Title);
        Assert.True(card.ShowsConfirm);
        Assert.True(card.CanOpenAsDraft);

        card.OpenAsDraft = true;
        card.Title = "  Mine  ";
        await card.ConfirmCommand.ExecuteAsync(null);
        var request = world.Publication.Published!;
        Assert.Equal("Mine", request.Text.Title);
        Assert.True(request.Draft);
        Assert.Equal("20261113-aa", request.Run.Value);
        Assert.Equal(PrData.Main, request.BaseBranch);
    }

    [Fact]
    public async Task A_standing_permission_that_covers_the_push_and_the_pull_request_is_the_cover_and_any_other_asks_for_hello()
    {
        var world = new PrWorld();
        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now.AddDays(-1)));
        await world.Model.SetProjectAsync(PrData.Project);
        await world.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        Assert.Equal(Texts.Get("Hosting_Publish_CoverHello"), ((PublishCardViewModel)world.Model.Card!).CoverageText);

        world.Gate.Held.Add(new HostingPolicyGrant("g-2", HostingOperationClass.PullRequestWrite, PrData.Repository, PrData.Main, "person", PrData.Now.AddHours(-2)));
        await world.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        Assert.Equal(Texts.Format("Hosting_Publish_CoverGrant", "13/11/2026 08:00"), ((PublishCardViewModel)world.Model.Card!).CoverageText);

        // A grant of another repository covers nothing.
        var other = new PrWorld();
        other.Gate.Held.Add(new HostingPolicyGrant("g-3", HostingOperationClass.PublishRunBranch, RemoteRepository.TryCreateGitHub("acme", "other")!, null, "person", PrData.Now));
        other.Gate.Held.Add(new HostingPolicyGrant("g-4", HostingOperationClass.PullRequestWrite, RemoteRepository.TryCreateGitHub("acme", "other")!, null, "person", PrData.Now));
        await other.Model.SetProjectAsync(PrData.Project);
        await other.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        Assert.Equal(Texts.Get("Hosting_Publish_CoverHello"), ((PublishCardViewModel)other.Model.Card!).CoverageText);
    }

    [Fact]
    public async Task A_run_the_service_says_cannot_be_published_shows_why_and_has_no_button()
    {
        var world = new PrWorld();
        world.Publication.Preview = world.Publication.Preview with { Ready = false, NotReadyReason = new UserText("delivery.no-integration") };
        await world.Model.SetProjectAsync(PrData.Project);
        await world.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        var card = (PublishCardViewModel)world.Model.Card!;

        Assert.False(card.ShowsConfirm);
        Assert.Contains(card.Lines, l => l.IsWarning && l.Text.StartsWith(Texts.Format("Hosting_Publish_NotReady", string.Empty), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_busy_project_and_a_configuration_that_refuses_are_said_in_the_card()
    {
        var busy = new PrWorld();
        busy.Publication.Preview = busy.Publication.Preview with { Ready = false, NotReadyReason = new UserText("project.busy-remote") };
        await busy.Model.SetProjectAsync(PrData.Project);
        await busy.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        Assert.Contains(busy.Model.Card!.Lines, l => l.Text == Texts.Get("Hosting_Publish_Busy"));

        var refused = new PrWorld();
        refused.Publication.Preview = refused.Publication.Preview with
        {
            Ready = false,
            NotReadyReason = new UserText("configuration-refused"),
            Inspection = new RemoteGitInspection(false, [new RemoteConfigRefusal("url.x.insteadOf", "local", ".git/config")], true, null),
        };
        await refused.Model.SetProjectAsync(PrData.Project);
        await refused.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        Assert.Contains(refused.Model.Card!.Lines, l => l.Text == Texts.Format("Hosting_Config_Refused", "url.x.insteadOf", ".git/config"));
        Assert.Contains(refused.Model.Card.Lines, l => l.Text == Texts.Get("Hosting_Lfs_Refused"));
    }

    [Fact]
    public async Task Without_the_capability_to_open_pull_requests_nothing_is_offered_to_publish()
    {
        var world = new PrWorld();
        world.Provider.Availability = new HostingAvailability(true, new Version(2, 96, 0), PrData.Capabilities(PrData.Reads));
        world.Model.SetBranches(["agentstudio/run-20261113-aa"]);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Empty(world.Model.PublishableRuns);

        // A card asked for anyway shows what the preview says, and its button needs the capability of the preview.
        world.Publication.Preview = world.Publication.Preview with { Capabilities = PrData.Capabilities(PrData.Reads) };
        await world.Model.ShowPublishCardAsync(new AgentStudio.Domain.Orchestration.RunId("r-1"));
        Assert.False(((PublishCardViewModel)world.Model.Card!).ShowsConfirm);
    }

    [Fact]
    public async Task A_pull_request_the_log_followed_already_is_not_offered_again_to_publish()
    {
        var world = WorldWith(OpenView());
        world.Model.SetBranches(["agentstudio/run-20261112-a1b2c3", "agentstudio/run-20261113-aa"]);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(["20261113-aa"], world.Model.PublishableRuns.Select(r => r.Run));
    }

    // ---- The browser ----

    [Fact]
    public async Task Open_in_the_browser_opens_only_the_address_the_code_builds_from_the_identifiers()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        world.Model.OpenInBrowserCommand.Execute(null);

        Assert.Equal(["https://github.com/acme/calc/pull/12"], world.Opener.Opened.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("https://github.com/acme/calc/pull/12", true)]
    [InlineData("http://github.com/acme/calc/pull/12", false)]
    [InlineData("https://github.com.evil.com/acme/calc/pull/12", false)]
    [InlineData("https://user@github.com/acme/calc/pull/12", false)]
    [InlineData("https://github.com:8443/acme/calc/pull/12", false)]
    [InlineData("https://github.com/acme/calc/pull/12?x=1", false)]
    [InlineData("https://github.com/acme/calc/pull/12#c", false)]
    [InlineData("https://github.com/acme/calc/issues/12", false)]
    [InlineData("https://github.com/acme/calc/pull/-1", false)]
    [InlineData("file:///c:/windows/system32/calc.exe", false)]
    public void Only_the_page_of_a_pull_request_of_github_passes(string address, bool expected)
    {
        Assert.Equal(expected, PullRequestAddress.IsPullRequestPage(address, out _));
    }

    [Fact]
    public void The_shell_opener_refuses_any_other_address()
    {
        Assert.Throws<ArgumentException>(() => new ShellAddressOpener().Open(new Uri("https://example.com/")));
    }

    // ---- Threads, cancellation and the chain ----

    [Fact]
    public async Task A_change_of_the_mailbox_from_another_thread_reads_the_section_again_and_only_for_its_project()
    {
        var world = WorldWith(OpenView());
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(1, world.Projector.Reads);

        world.Sync.RaiseChanged("another-project");
        Assert.Equal(1, world.Projector.Reads);

        await Task.Run(() => world.Sync.RaiseChanged("p-1"), Ct);
        await world.Projector.ReadAgain.Task.WaitAsync(Ct);
        Assert.Equal(2, world.Projector.Reads);
    }

    [Fact]
    public async Task The_mailbox_is_left_when_the_project_changes_or_the_section_is_disposed()
    {
        var world = WorldWith(OpenView());
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(1, world.Sync.Subscribers);

        await world.Model.SetProjectAsync(null);
        Assert.Equal(0, world.Sync.Subscribers);

        await world.Model.SetProjectAsync(PrData.Project);
        world.Model.Dispose();
        Assert.Equal(0, world.Sync.Subscribers);
    }

    [Fact]
    public async Task A_read_that_a_later_one_overtakes_is_dropped()
    {
        var world = new PrWorld();
        world.Projector.Views.Add(OpenView(1));
        world.Projector.Hold = new TaskCompletionSource();
        var slow = world.Model.SetProjectAsync(PrData.Project);

        world.Projector.Views.Clear();
        world.Projector.Views.Add(OpenView(2));
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Equal([2], world.Model.Rows.Select(r => r.Number));

        await slow;
        Assert.Equal([2], world.Model.Rows.Select(r => r.Number));
    }

    [Fact]
    public async Task The_chain_button_is_offered_only_while_someone_listens_and_raises_the_node_of_the_pull_request()
    {
        var world = WorldWith(OpenView());
        await world.OpenAsync();
        Assert.False(world.Model.CanOpenChain);

        AgentStudio.Domain.Tracing.TraceRef? raised = null;
        world.Model.ChainRequested += (_, node) => raised = node;
        Assert.True(world.Model.CanOpenChain);
        world.Model.ShowChainCommand.Execute(null);

        Assert.Equal(AgentStudio.Domain.Tracing.TraceRef.PullRequest(PrData.Pr(12)), raised);
    }

    // ---- Permissions ----

    [Fact]
    public async Task The_permissions_list_who_and_when_and_only_the_classes_that_admit_one_can_be_given()
    {
        var world = new PrWorld();
        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now.AddDays(-1)));
        await world.Model.SetProjectAsync(PrData.Project);
        var permissions = world.Model.Permissions;

        Assert.Single(permissions.Grants);
        Assert.Equal("12/11/2026 10:00", permissions.Grants[0].WhenText);
        Assert.Equal([Texts.Get("Hosting_Class_PublishRunBranch"), Texts.Get("Hosting_Class_PullRequestWrite")], permissions.Classes.Select(c => c.Label));
        Assert.True(permissions.CanGrant);

        permissions.SelectedClass = permissions.Classes[1];
        permissions.BaseBranchText = "main";
        await permissions.GrantCommand.ExecuteAsync(null);
        Assert.Equal(2, permissions.Grants.Count);
        Assert.Equal(Texts.Get("Hosting_Permissions_Given"), permissions.Note);
        Assert.Equal(PrData.Main, world.Gate.Held[1].BaseBranch);

        permissions.BaseBranchText = "bad..name";
        await permissions.GrantCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Text_branch_invalid_form"), permissions.Note);
        Assert.Equal(2, world.Gate.Held.Count);
    }

    [Fact]
    public async Task A_permission_the_gate_does_not_give_is_said_and_withdrawing_one_goes_to_the_gate()
    {
        var world = new PrWorld();
        world.Gate.GrantAnswersNull = true;
        await world.Model.SetProjectAsync(PrData.Project);
        await world.Model.Permissions.GrantCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Hosting_Permissions_NotGiven"), world.Model.Permissions.Note);

        world.Gate.Held.Add(new HostingPolicyGrant("g-9", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now));
        await world.Model.SetProjectAsync(PrData.Project);
        await world.Model.Permissions.RevokeCommand.ExecuteAsync(world.Model.Permissions.Grants.Single());
        Assert.Equal(["g-9"], world.Gate.Revoked);
        Assert.Empty(world.Model.Permissions.Grants);
        Assert.Equal(Texts.Get("Hosting_Permissions_Revoked"), world.Model.Permissions.Note);
    }

    // ---- The words ----

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void Every_known_code_has_its_text_in_both_languages_and_none_is_painted_as_a_generic_one(string language)
    {
        using var scope = Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo(language)));
        foreach (var code in HostingTexts.KnownCodes)
        {
            Assert.True(Texts.Current.Has(UserText.KeyOf(code)), $"{code} has no text in {language}");
            Assert.DoesNotContain("[Text_", HostingTexts.Code(code), StringComparison.Ordinal);
            Assert.NotEqual(Texts.Format("Hosting_Code_Unknown", code), HostingTexts.Code(code));
        }
    }

    [Fact]
    public void A_code_without_a_text_is_said_naming_it_and_never_as_nothing_or_as_an_exception()
    {
        Assert.Contains("brand.new-code", HostingTexts.Code("brand.new-code"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(HostingTexts.Code(null)));
        Assert.False(string.IsNullOrWhiteSpace(HostingTexts.Code("NOT A CODE \u001b[31m")));
        Assert.DoesNotContain('\u001b', HostingTexts.Code("NOT A CODE \u001b[31m"));
        Assert.Contains("x.y", HostingTexts.Say(new UserText("x.y", ["a", 1])), StringComparison.Ordinal);

        // A coded text whose arguments do not fit its sentence is not an exception either.
        Assert.False(string.IsNullOrWhiteSpace(HostingTexts.Say(new UserText("hosting.outcome.ok", ["extra"]))));
    }

    [Fact]
    public void The_codes_of_every_outcome_are_the_kebab_of_the_enum()
    {
        Assert.Equal("hosting.outcome.rate-limited", HostingTexts.OutcomeCode(HostingOutcome.RateLimited));
        Assert.Equal("hosting.outcome.sign-in-needed", HostingTexts.OutcomeCode(HostingOutcome.SignInNeeded));
        Assert.Equal("hosting.outcome.ok", HostingTexts.OutcomeCode(HostingOutcome.Ok));
        foreach (var outcome in Enum.GetValues<HostingOutcome>())
        {
            Assert.Contains(HostingTexts.OutcomeCode(outcome), HostingTexts.KnownCodes);
        }
    }

    [Fact]
    public void Every_dynamic_key_of_the_screens_has_a_text_in_both_languages()
    {
        foreach (var language in new[] { "es", "en" })
        {
            using var scope = Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo(language)));
            var keys = new List<string>();
            keys.AddRange(Enum.GetValues<MergeMethod>().Select(m => "Hosting_Method_" + m));
            keys.AddRange(Enum.GetValues<HostingOperationClass>().Select(c => "Hosting_Class_" + c));
            keys.AddRange(Enum.GetValues<BranchRuleKind>().Select(k => "Hosting_Rule_" + k));
            keys.AddRange(Enum.GetValues<DeploymentState>().Select(d => "Hosting_Deployment_" + d));
            keys.AddRange(Enum.GetValues<VerdictStanding>().Select(v => "Hosting_Verdict_" + v));
            keys.AddRange(Enum.GetValues<RemoteWorkState>().Select(s => "Hosting_Work_" + s));
            Assert.All(keys, key => Assert.True(Texts.Current.Has(key), $"{key} has no text in {language}"));
        }
    }
}
