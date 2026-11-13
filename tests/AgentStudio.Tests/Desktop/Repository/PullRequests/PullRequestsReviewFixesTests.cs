using System.Globalization;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

/// <summary>
/// The round of fixes after the independent review of WP5: a branch another pull request still uses, a merge only of the commit the judge saw, permissions and cards that do not outlive their
/// project, the change of project, the thread of the interface, a refresh that leaves the card where it is, a timeout that is said, and the data the section now reads (the mode, the verdict
/// published, the milestone, the activity, the switch of the header).
/// </summary>
public sealed class PullRequestsReviewFixesTests
{
    private const string RunA = "20261112-a1b2c3";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PullRequestView View(int number, PullRequestState state, string? run = RunA, bool branchExists = true, string? judged = null, bool headIsJudged = true, bool judgedKnown = true) =>
        new(
            PrData.Pr(number),
            run is null ? null : new RunId(run),
            PrData.Snapshot(number, state, headBranchExists: branchExists, results: state == PullRequestState.Merged ? [PrData.Sha('f')] : null),
            judgedKnown ? judged ?? PrData.Sha('a') : null,
            headIsJudged,
            VerdictStanding.Approved,
            new RemoteDisplayTexts("x-1", [], null),
            PrData.Now.AddMinutes(-3),
            []);

    private static async Task<PrWorld> WorldAsync(params PullRequestView[] views)
    {
        var world = new PrWorld();
        world.Projector.Views.AddRange(views);
        await world.Model.SetProjectAsync(PrData.Project);
        return world;
    }

    private static async Task SelectAsync(PrWorld world, int number)
    {
        world.Model.Selected = world.Model.Rows.Single(r => r.Number == number);
        for (var i = 0; i < 200 && world.Model.Detail is null; i++)
        {
            await Task.Yield();
        }
    }

    // ---- 1. The branch of a run that another pull request still uses ----

    [Fact]
    public async Task The_branch_is_not_offered_for_deletion_while_another_pull_request_of_the_run_is_open_or_a_draft()
    {
        var world = await WorldAsync(View(1, PullRequestState.Closed), View(2, PullRequestState.Open));
        await SelectAsync(world, 1);
        Assert.False(world.Model.Detail!.CanDeleteBranch);

        var draft = await WorldAsync(View(1, PullRequestState.Merged), View(2, PullRequestState.Draft));
        await SelectAsync(draft, 1);
        Assert.False(draft.Model.Detail!.CanDeleteBranch);

        // Another one that is closed too, or one of another run, does not use it.
        var free = await WorldAsync(View(1, PullRequestState.Merged), View(2, PullRequestState.Closed), View(3, PullRequestState.Open, run: "20261113-zz"));
        await SelectAsync(free, 1);
        Assert.True(free.Model.Detail!.CanDeleteBranch);

        // The one in force is the open one: the closed one cannot delete the branch, and the open one is not offered a deletion either (it is not merged or closed).
        await SelectAsync(world, 2);
        Assert.False(world.Model.Detail!.CanDeleteBranch);
    }

    [Fact]
    public async Task A_pull_request_not_observed_yet_counts_as_alive_and_the_branch_of_the_run_speaks_for_the_one_in_force()
    {
        var unobserved = new PullRequestView(PrData.Pr(3), new RunId(RunA), null, null, false, null, RemoteDisplayTexts.None, null, []);
        var world = await WorldAsync(View(1, PullRequestState.Closed), unobserved);
        await SelectAsync(world, 1);
        Assert.False(world.Model.Detail!.CanDeleteBranch);
        Assert.Equal(Texts.Format("Hosting_Branch_NotObserved", 3), world.Model.RemoteBranchText(RunA));

        var many = await WorldAsync(View(1, PullRequestState.Closed, branchExists: false), View(2, PullRequestState.Open));
        Assert.Equal(Texts.Format("Hosting_Branch_Published", new string('a', 8), 2), many.Model.RemoteBranchText(RunA));

        var allClosed = await WorldAsync(View(1, PullRequestState.Closed, branchExists: false), View(2, PullRequestState.Merged));
        Assert.Equal(Texts.Format("Hosting_Branch_Published", new string('a', 8), 2), allClosed.Model.RemoteBranchText(RunA));
    }

    // ---- 2. Merge: only the commit the judge saw ----

    [Fact]
    public async Task A_head_that_is_not_the_judged_commit_is_not_offered_for_merging_and_the_detail_says_why()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open, judged: PrData.Sha('e'), headIsJudged: false));
        await SelectAsync(world, 12);

        Assert.False(world.Model.Detail!.CanMerge);
        Assert.Contains(world.Model.Detail.Notes, n => n.IsWarning && n.Text == Texts.Get("Hosting_Merge_HeadMoved"));
        Assert.DoesNotContain(world.Model.Detail.Notes, n => n.Text == Texts.Get("Hosting_Merge_NoJudged"));
    }

    [Fact]
    public async Task When_it_is_not_known_what_the_judge_saw_merging_is_not_offered_and_the_phrase_does_not_blame_the_head()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open, judgedKnown: false, headIsJudged: false));
        await SelectAsync(world, 12);

        Assert.False(world.Model.Detail!.CanMerge);
        Assert.Contains(world.Model.Detail.Notes, n => n.Text == Texts.Get("Hosting_Merge_NoJudged"));
        Assert.DoesNotContain(world.Model.Detail.Notes, n => n.Text == Texts.Get("Hosting_Merge_HeadMoved"));
        Assert.Equal(Texts.Get("Hosting_Judged_Unknown"), world.Model.Rows.Single().JudgedText);
    }

    [Fact]
    public async Task When_the_head_is_the_judged_commit_the_merge_card_shows_that_commit_and_says_the_service_merges_exactly_it()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        await SelectAsync(world, 12);
        Assert.True(world.Model.Detail!.CanMerge);
        world.Model.OpenMergeCardCommand.Execute(null);

        var lines = world.Model.Card!.Lines.Select(l => l.Text).ToList();
        Assert.Contains(Texts.Format("Hosting_Merge_Commit", new string('a', 8)), lines);
        Assert.Contains(Texts.Get("Hosting_Merge_Exact"), lines);
        Assert.DoesNotContain(world.Model.Card.Lines, l => l.IsWarning);
    }

    // ---- 3. The permissions of another project ----

    [Fact]
    public async Task Leaving_the_project_empties_the_permissions_and_neither_giving_nor_withdrawing_acts_on_the_old_one()
    {
        var world = new PrWorld();
        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now));
        await world.Model.SetProjectAsync(PrData.Project);
        var stale = world.Model.Permissions.Grants.Single();
        Assert.True(world.Model.Permissions.CanGrant);

        await world.Model.SetProjectAsync(null);

        var permissions = world.Model.Permissions;
        Assert.Empty(permissions.Grants);
        Assert.False(permissions.CanGrant);
        await permissions.GrantCommand.ExecuteAsync(null);
        await permissions.RevokeCommand.ExecuteAsync(stale);
        Assert.Single(world.Gate.Held);
        Assert.Empty(world.Gate.Revoked);
    }

    [Theory]
    [InlineData("no-remote")]
    [InlineData("error")]
    public async Task Without_a_usable_remote_or_after_an_error_of_reading_the_permissions_are_empty_and_cannot_act(string state)
    {
        var world = new PrWorld();
        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now));
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Single(world.Model.Permissions.Grants);

        if (state == "error")
        {
            world.Projector.Failure = new InvalidOperationException("log");
        }

        if (state == "no-remote")
        {
            world.Providers.Repository = null;
        }

        var model = world.Model;
        await model.SetProjectAsync(PrData.Project);

        Assert.Empty(model.Permissions.Grants);
        Assert.False(model.Permissions.CanGrant);
        await model.Permissions.GrantCommand.ExecuteAsync(null);
        Assert.Single(world.Gate.Held);
    }

    // ---- 4. The change of project ----

    private static async Task<(RepositoryViewModel Model, PrWorld World, FakeHistory History)> RepositoryAsync()
    {
        var world = new PrWorld();
        world.Projector.Views.Add(View(12, PullRequestState.Open));
        var history = new FakeHistory();
        var model = new RepositoryViewModel(new FakeWorkspace(), history, null, new FakeActivity(), null, PrData.Clock(), world.Model);
        await model.SetProjectAsync(@"C:\work\calc");
        await model.PullRequestsLoaded;
        return (model, world, history);
    }

    [Fact]
    public async Task Changing_the_project_empties_the_section_of_pull_requests_at_once_while_the_history_is_read()
    {
        var (model, world, history) = await RepositoryAsync();
        await SelectAsync(world, 12);
        world.Model.OpenMergeCardCommand.Execute(null);
        Assert.NotNull(world.Model.Card);
        Assert.NotEmpty(world.Model.Rows);

        var hold = new TaskCompletionSource();
        history.HoldSummary = hold;
        var change = model.SetProjectAsync(@"C:\work\other");

        Assert.False(change.IsCompleted);
        Assert.Empty(world.Model.Rows);
        Assert.Null(world.Model.Card);
        Assert.Null(world.Model.Detail);
        Assert.Empty(world.Model.Permissions.Grants);
        Assert.False(world.Model.Permissions.CanGrant);
        Assert.Equal(Texts.Get("Hosting_Pr_Loading"), world.Model.GuideText);

        hold.SetResult();
        await change;
    }

    [Fact]
    public async Task A_slow_reading_of_github_does_not_hold_the_busy_of_the_repository_nor_its_refresh()
    {
        var (model, world, _) = await RepositoryAsync();
        var hold = new TaskCompletionSource();
        world.Projector.Hold = hold;

        await model.RefreshCommand.ExecuteAsync(null);

        Assert.False(model.IsBusy);
        Assert.True(model.RefreshCommand.CanExecute(null));
        Assert.False(model.PullRequestsLoaded.IsCompleted);
        Assert.True(world.Model.IsBusy);

        hold.SetResult();
        await model.PullRequestsLoaded;
    }

    // ---- 5. The thread of the interface ----

    [Fact]
    public async Task A_change_of_the_mailbox_is_posted_to_the_context_the_project_was_set_from_whatever_thread_built_the_section()
    {
        // The section is built on a thread of the pool (no context) and the project is set from the «interface»: the posts go to it, never to the thread of the mailbox.
        var world = await Task.Run(() => new PrWorld(), Ct);
        world.Projector.Views.Add(View(12, PullRequestState.Open));
        var ui = new QueueContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            await world.Model.SetProjectAsync(PrData.Project);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(1, world.Projector.Reads);
        Assert.Equal(0, ui.Pump());

        await Task.Run(() => world.Sync.RaiseChanged("p-1"), Ct);

        Assert.Equal(1, world.Projector.Reads);
        Assert.Single(ui.Posted);
        Assert.Equal(1, ui.Pump());
        Assert.Equal(2, world.Projector.Reads);
    }

    [Fact]
    public async Task A_change_of_the_switch_from_settings_is_posted_to_the_context_of_the_interface_too()
    {
        var world = new PrWorld(withPermission: true);
        var ui = new QueueContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            await world.Model.SetProjectAsync(PrData.Project);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var raised = new List<string?>();
        world.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await Task.Run(() =>
        {
            world.Permission!.Allow("p-1");
            world.Permission.RaiseChanged("p-1");
        }, Ct);

        Assert.DoesNotContain(nameof(PullRequestsViewModel.SyncEnabled), raised);
        ui.Pump();
        Assert.Contains(nameof(PullRequestsViewModel.SyncEnabled), raised);
        Assert.True(world.Model.SyncEnabled);
    }

    // ---- Low findings ----

    [Fact]
    public async Task A_change_of_the_mailbox_leaves_the_card_open_and_what_was_typed_and_the_rows_where_they_were()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open), View(11, PullRequestState.Closed));
        await SelectAsync(world, 12);
        world.Model.OpenReviewersCardCommand.Execute(null);
        var card = (ReviewersCardViewModel)world.Model.Card!;
        card.LoginsText = "octo";
        var rows = world.Model.Rows.ToList();
        var selected = world.Model.Selected;

        world.Sync.RaiseChanged("p-1");
        world.Sync.RaiseChanged("p-1");

        Assert.Equal(3, world.Projector.Reads);
        Assert.Same(card, world.Model.Card);
        Assert.Equal("octo", card.LoginsText);
        Assert.Equal(rows, world.Model.Rows);
        Assert.Same(selected, world.Model.Selected);
        Assert.NotNull(world.Model.Detail);
    }

    [Fact]
    public async Task A_change_that_makes_the_action_of_the_open_card_impossible_closes_it_and_says_so()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        await SelectAsync(world, 12);
        world.Model.OpenMergeCardCommand.Execute(null);
        Assert.NotNull(world.Model.Card);

        world.Projector.Views.Clear();
        world.Projector.Views.Add(View(12, PullRequestState.Merged));
        world.Sync.RaiseChanged("p-1");
        for (var i = 0; i < 200 && world.Model.Card is not null; i++)
        {
            await Task.Yield();
        }

        Assert.Null(world.Model.Card);
        Assert.Equal(Texts.Get("Hosting_Card_Stale"), world.Model.Message);
    }

    [Fact]
    public async Task A_pull_request_that_is_no_longer_followed_takes_its_card_and_its_detail_with_it()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open), View(11, PullRequestState.Closed));
        await SelectAsync(world, 12);
        world.Model.OpenCloseCardCommand.Execute(null);

        world.Projector.Views.RemoveAll(v => v.PullRequest.Number == 12);
        world.Sync.RaiseChanged("p-1");

        Assert.Null(world.Model.Selected);
        Assert.Null(world.Model.Card);
        Assert.Null(world.Model.Detail);
        Assert.Equal([11], world.Model.Rows.Select(r => r.Number));
    }

    [Fact]
    public async Task A_reading_after_a_refresh_does_not_abort_a_synchronisation_or_a_publication_in_progress()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        world.Model.SetBranches(["agentstudio/run-20261113-aa"]);
        await world.Model.OpenPublishCardCommand.ExecuteAsync(new PublishableRunRow("20261113-aa"));
        var card = (PublishCardViewModel)world.Model.Card!;
        world.Publication.HoldPublish = new TaskCompletionSource();
        var confirm = card.ConfirmCommand.ExecuteAsync(null);

        world.Sync.HoldSync = new TaskCompletionSource();
        var sync = world.Model.SyncNowCommand.ExecuteAsync(null);
        world.Sync.RaiseChanged("p-1");
        world.Sync.RaiseChanged("p-1");

        world.Sync.HoldSync.SetResult();
        world.Publication.HoldPublish.SetResult();
        await sync;
        await confirm;

        Assert.Null(world.Model.Error);
        Assert.Contains(world.Publication.Calls, c => c == "publish");
        Assert.Equal(Texts.Get("Hosting_Card_Sent"), world.Model.Message);
    }

    [Fact]
    public async Task A_timeout_inside_a_port_is_said_and_never_left_without_an_answer()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        await SelectAsync(world, 12);
        world.Model.OpenCloseCardCommand.Execute(null);
        var card = world.Model.Card!;
        world.Publication.Failure = new OperationCanceledException("timeout of the port");
        await card.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Hosting_Card_Failed"), card.Error);

        world.Sync.HoldSync = new TaskCompletionSource();
        world.Sync.HoldSync.SetException(new OperationCanceledException("timeout of the mailbox"));
        await world.Model.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Hosting_Card_Failed"), world.Model.Error);

        world.Projector.Failure = new OperationCanceledException("timeout of the projection");
        await world.Model.ReloadAsync();
        Assert.Equal(Texts.Get("Hosting_Pr_Unreadable"), world.Model.GuideText);

        world.Provider.Account = new HostingResult<RemoteLogin>(HostingOutcome.Ok, null);
        world.Projector.Failure = null;
        await world.Model.ReloadAsync();
        await world.Model.CheckConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Text_hosting_outcome_invalid_answer"), world.Model.Error);
    }

    [Fact]
    public async Task The_busy_flag_does_not_stay_on_when_the_read_ends_early()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        var hold = new TaskCompletionSource();
        world.Projector.Hold = hold;
        var reading = world.Model.ReloadAsync();
        Assert.True(world.Model.IsBusy);

        await world.Model.SetProjectAsync(null);
        Assert.False(world.Model.IsBusy);
        hold.TrySetResult();
        await reading;

        var unavailable = PullRequestsViewModel.Unavailable(PrData.Clock());
        await unavailable.SetProjectAsync(PrData.Project);
        Assert.False(unavailable.IsBusy);
    }

    [Fact]
    public async Task The_cards_of_closing_and_deleting_say_the_repository_and_the_commit_and_the_verdict_one_says_what_covers_it()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        await SelectAsync(world, 12);
        world.Model.OpenCloseCardCommand.Execute(null);
        Assert.Contains(world.Model.Card!.Lines, l => l.Text == Texts.Format("Hosting_Card_Where", "acme/calc", new string('a', 8)));

        var merged = await WorldAsync(View(12, PullRequestState.Merged));
        await SelectAsync(merged, 12);
        merged.Model.OpenDeleteBranchCardCommand.Execute(null);
        Assert.Contains(merged.Model.Card!.Lines, l => l.Text == Texts.Format("Hosting_Card_Where", "acme/calc", new string('a', 8)));

        await world.Model.OpenVerdictCardCommand.ExecuteAsync(null);
        Assert.Contains(world.Model.Card!.Lines, l => l.Text == Texts.Get("Hosting_Publish_CoverHello"));

        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PullRequestWrite, PrData.Repository, null, "person", PrData.Now.AddDays(-1)));
        await world.Model.OpenVerdictCardCommand.ExecuteAsync(null);
        Assert.Contains(world.Model.Card!.Lines, l => l.Text == Texts.Format("Hosting_Publish_CoverGrant", "12/11/2026 10:00"));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void The_rules_with_a_count_do_not_depend_on_the_plural(string language)
    {
        using var scope = Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo(language)));
        var one = Texts.Format("Hosting_Rule_RequiredReviews_Count", 1);
        var checks = Texts.Format("Hosting_Rule_RequiredChecks_Count", 1);
        Assert.Contains("1", one, StringComparison.Ordinal);
        Assert.DoesNotContain("al menos", one, StringComparison.Ordinal);
        Assert.DoesNotContain("at least", one, StringComparison.Ordinal);
        Assert.DoesNotContain("al menos", checks, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_buttons_repeated_by_row_have_a_name_of_their_own()
    {
        var run = new PublishableRunRow("20261113-aa");
        Assert.Contains("20261113-aa", run.ButtonName, StringComparison.Ordinal);
        Assert.Equal(Texts.Format("Hosting_Publish_OpenFor", "20261113-aa"), run.ButtonName);

        var world = new PrWorld();
        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now));
        await world.Model.SetProjectAsync(PrData.Project);
        var grant = world.Model.Permissions.Grants.Single();
        Assert.Contains(grant.ClassText, grant.RevokeName, StringComparison.Ordinal);
        Assert.Contains("acme/calc", grant.RevokeName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_requests_the_log_follows_of_another_repository_are_said_and_the_list_does_not_claim_there_are_none()
    {
        var other = RemoteRepository.TryCreateGitHub("acme", "other")!;
        var foreign = new PullRequestView(new PullRequestRef(other, 5), null, null, null, false, null, RemoteDisplayTexts.None, null, []);
        var world = new PrWorld();
        world.Projector.Views.Add(foreign);
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Empty(world.Model.Rows);
        Assert.Equal(Texts.Format("Hosting_Pr_OtherRepository", 1), world.Model.OtherRepositoryText);
        Assert.True(world.Model.HasOtherRepository);
        Assert.Null(world.Model.GuideText);
        Assert.Equal(PullRequestsStatus.Ready, world.Model.Status);

        // With one of its own too, the list shows it and the note stays.
        world.Projector.Views.Add(View(12, PullRequestState.Open));
        await world.Model.ReloadAsync();
        Assert.Single(world.Model.Rows);
        Assert.True(world.Model.HasOtherRepository);
    }

    // ---- The data the section now reads ----

    [Fact]
    public async Task The_mode_is_said_as_deduced_only_when_the_view_says_it_is()
    {
        var merged = View(12, PullRequestState.Merged);
        var world = new PrWorld();
        world.Projector.Views.Add(merged with { ModeIsDeduced = false });
        world.Projector.Views.Add(View(13, PullRequestState.Merged));
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(Texts.Get("Text_remote_merge_squash"), world.Model.Rows.Single(r => r.Number == 12).StateText);
        Assert.Equal(Texts.Format("Hosting_State_MergedDeduced", Texts.Get("Text_remote_merge_squash")), world.Model.Rows.Single(r => r.Number == 13).StateText);
    }

    [Fact]
    public async Task The_row_says_whether_the_pull_request_has_the_verdict_the_review_has_now()
    {
        var published = new LastPublishedVerdict(VerdictStanding.Approved, PrData.Sha('a'), PrData.Now);
        var world = new PrWorld();
        world.Projector.Views.AddRange(
        [
            View(1, PullRequestState.Open) with { PublishedVerdict = null },
            View(2, PullRequestState.Open) with { PublishedVerdict = published },
            View(3, PullRequestState.Open) with { PublishedVerdict = published with { Standing = VerdictStanding.Rejected } },
            View(4, PullRequestState.Open) with { PublishedVerdict = published with { Commit = PrData.Sha('c') } },
            View(5, PullRequestState.Merged) with { PublishedVerdict = null },
        ]);
        await world.Model.SetProjectAsync(PrData.Project);

        PullRequestRow Row(int n) => world.Model.Rows.Single(r => r.Number == n);
        Assert.Equal(Texts.Get("Hosting_Published_None"), Row(1).PublishedText);
        Assert.Equal(Texts.Format("Hosting_Published_Same", Texts.Get("Hosting_Verdict_Approved")), Row(2).PublishedText);
        Assert.False(Row(2).PublishedIsStale);
        Assert.Equal(Texts.Get("Hosting_Published_Stale"), Row(3).PublishedText);
        Assert.True(Row(3).PublishedIsStale);
        Assert.True(Row(4).PublishedIsStale);
        Assert.Equal(string.Empty, Row(5).PublishedText);
    }

    [Fact]
    public async Task The_milestone_of_the_run_is_in_the_row_when_the_log_says_it()
    {
        var world = new PrWorld();
        world.Projector.Views.Add(View(12, PullRequestState.Open) with { Milestone = "mil-20261101-aaaaaa" });
        world.Projector.Views.Add(View(13, PullRequestState.Open));
        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(Texts.Format("Hosting_Milestone_Of", "mil-20261101-aaaaaa"), world.Model.Rows.Single(r => r.Number == 12).MilestoneText);
        Assert.Equal(string.Empty, world.Model.Rows.Single(r => r.Number == 13).MilestoneText);
    }

    [Fact]
    public async Task Updating_the_pull_request_is_the_card_of_publication_over_one_that_exists_and_needs_the_capability()
    {
        var world = await WorldAsync(View(12, PullRequestState.Open));
        world.Publication.Preview = world.Publication.Preview with { Existing = PrData.Pr(12) };
        await SelectAsync(world, 12);
        Assert.True(world.Model.Detail!.CanUpdate);

        await world.Model.OpenUpdateCardCommand.ExecuteAsync(null);
        var card = Assert.IsType<PublishCardViewModel>(world.Model.Card);
        Assert.Equal(Texts.Format("Hosting_Update_Heading", 12), card.Heading);
        Assert.Equal(Texts.Get("Hosting_Update_Confirm"), card.ConfirmText);
        Assert.Contains(card.Lines, l => l.Text == Texts.Get("Hosting_Update_Replaces"));
        card.Title = "Edited";
        await card.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("Edited", world.Publication.Published!.Text.Title);
        Assert.Equal(RunA, world.Publication.Published.Run.Value);

        var without = await WorldAsync(View(12, PullRequestState.Open));
        without.Provider.Availability = new HostingAvailability(true, new Version(2, 96, 0), PrData.Capabilities((PrData.Reads | PrData.Writes) & ~HostingCapability.UpdatePullRequest, MergeMethod.Squash));
        await without.Model.ReloadAsync();
        await SelectAsync(without, 12);
        Assert.False(without.Model.Detail!.CanUpdate);

        // A pull request that is not open, or of no run, has nothing to update.
        var merged = await WorldAsync(View(12, PullRequestState.Merged));
        await SelectAsync(merged, 12);
        Assert.False(merged.Model.Detail!.CanUpdate);
    }

    [Fact]
    public async Task The_switch_of_the_header_gives_and_takes_the_permission_only_by_its_click_and_without_the_port_there_is_none()
    {
        var world = new PrWorld(withPermission: true);
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.True(world.Model.HasSyncSwitch);
        Assert.False(world.Model.SyncEnabled);
        Assert.Empty(world.Permission!.Sets);
        Assert.Null(world.Model.SyncPolicyText);

        world.Model.SyncEnabled = true;
        Assert.Equal([("p-1", true)], world.Permission.Sets);
        Assert.True(world.Model.SyncEnabled);
        Assert.Equal(Texts.Get("Hosting_Sync_TurnedOn"), world.Model.Message);

        // The same value again is not a click that changes anything.
        world.Model.SyncEnabled = true;
        Assert.Single(world.Permission.Sets);
        world.Model.SyncEnabled = false;
        Assert.Equal(Texts.Get("Hosting_Sync_TurnedOff"), world.Model.Message);

        // Loading, switching project or a change from Settings never gives it.
        world.Permission.Allow("p-1");
        world.Permission.RaiseChanged("p-1");
        await world.Model.ReloadAsync();
        Assert.Equal(2, world.Permission.Sets.Count);

        var plain = new PrWorld();
        await plain.Model.SetProjectAsync(PrData.Project);
        Assert.False(plain.Model.HasSyncSwitch);
        plain.Model.SyncEnabled = true;
        Assert.False(plain.Model.SyncEnabled);
        Assert.Equal(Texts.Get("Hosting_Header_SyncOn"), plain.Model.SyncPolicyText);
    }

    [Fact]
    public async Task The_switch_is_left_when_the_project_changes()
    {
        var world = new PrWorld(withPermission: true);
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Equal(1, world.Permission!.Subscribers);

        await world.Model.SetProjectAsync(null);
        Assert.Equal(0, world.Permission.Subscribers);
        Assert.False(world.Model.SyncEnabled);
        world.Model.Dispose();
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void Every_operation_and_every_change_the_log_can_hold_has_its_words(string language)
    {
        using var scope = Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo(language)));
        Assert.All(Enum.GetValues<HostingOperationKind>(), k => Assert.True(Texts.Current.Has("Hosting_Op_" + k), k.ToString()));
        Assert.All(Enum.GetValues<RemoteChangeKind>(), k => Assert.True(Texts.Current.Has("Hosting_Change_" + k), k.ToString()));
    }

    [Fact]
    public void The_activity_of_the_remote_says_what_the_entry_says_of_itself_and_the_pull_request()
    {
        var merge = new RepositoryActivity(PrData.Now, RepositoryActivityKind.RemoteOperation, RunA, null, null, false, "merge.blocked", 1, HostingOperationKind.MergePullRequest, HostingOutcome.Refused, null, 12);
        Assert.Equal(Texts.Format("Hosting_Activity_OfPr", Texts.Get("Hosting_Op_MergePullRequest"), 12), RepositoryTexts.Kind(merge));
        Assert.Equal(Texts.Get("Text_merge_blocked"), RepositoryTexts.Error("merge.blocked"));

        var seen = new RepositoryActivity(PrData.Now, RepositoryActivityKind.RemoteObservation, RunA, null, null, true, null, 2, null, null, [RemoteChangeKind.BaseChanged, RemoteChangeKind.Merged], 12);
        Assert.Equal(
            Texts.Format("Hosting_Activity_OfPr", Texts.Get("Hosting_Change_BaseChanged") + ", " + Texts.Get("Hosting_Change_Merged"), 12),
            RepositoryTexts.Kind(seen));

        // An old-style entry with nothing more is named by its kind.
        var plain = new RepositoryActivity(PrData.Now, RepositoryActivityKind.RemoteOperation, RunA, null, null, true, null, 3);
        Assert.Equal(Texts.Get("Hosting_Activity_Operation"), RepositoryTexts.Kind(plain));
    }
}
