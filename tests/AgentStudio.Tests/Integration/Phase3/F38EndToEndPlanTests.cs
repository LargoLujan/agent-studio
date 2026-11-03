using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// What the live end to end of F3-8 relies on, checked without quota: its roster is valid, the plan the user would
/// complete on the canvas is the plan of docs/13 section 6 and passes the validator, the check of the plan's shape
/// tells it apart from an incomplete one, and the report never carries the user's profile folder.
/// </summary>
public sealed class F38EndToEndPlanTests
{
    private static PlanContext Context() => new(
        F38EndToEndLiveTests.Requirements(),
        F38EndToEndLiveTests.Roster(),
        new HashSet<string>(["build", "test"], StringComparer.Ordinal),
        new HashSet<string>(["build", "test"], StringComparer.Ordinal),
        new PlanLimits(MaxTasks: 6, MaxRetriesPerTask: 1));

    [Fact]
    public void The_roster_is_valid_and_the_judge_runs_on_another_vendor_read_only()
    {
        var roster = F38EndToEndLiveTests.Roster();

        Assert.All(roster, agent => Assert.Empty(agent.Validate()));
        Assert.Equal(["anthropic", "anthropic", "openai"], roster.Select(a => a.Model.Vendor.Value));
        Assert.Equal(["haiku", "haiku", null], roster.Select(a => a.Model.Model));
        Assert.DoesNotContain("Bash", roster.SelectMany(a => a.AllowedTools));
        Assert.Equal(["Read", "Grep", "Glob"], roster[0].AllowedTools);
    }

    [Fact]
    public void The_plan_completed_on_the_canvas_is_the_plan_of_docs13_and_the_validator_accepts_it()
    {
        var plan = F38EndToEndLiveTests.UserPlan();

        Assert.Empty(PlanValidator.Validate(plan, Context()));
        Assert.True(F38EndToEndLiveTests.HasExpectedShape(plan));
        Assert.Equal(PlanSource.User, plan.Source);
    }

    [Fact]
    public void A_plan_without_the_review_or_the_integration_is_not_the_expected_shape()
    {
        var plan = F38EndToEndLiveTests.UserPlan();
        PlanDefinition Without(WorkTaskKind kind) =>
            PlanDefinition.Create(plan.Summary, [.. plan.Tasks.Where(t => t.Kind != kind)], plan.Risks, plan.Source, plan.Author);

        Assert.False(F38EndToEndLiveTests.HasExpectedShape(Without(WorkTaskKind.Review)));
        Assert.False(F38EndToEndLiveTests.HasExpectedShape(Without(WorkTaskKind.Integration)));

        // A review that does not come after the integration does not review the integrated code.
        var detached = PlanDefinition.Create(
            plan.Summary,
            [.. plan.Tasks.Select(t => t.Kind == WorkTaskKind.Review ? t with { DependsOn = [new DependencySpec(new TaskId("T1"))] } : t)],
            plan.Risks,
            plan.Source,
            plan.Author);
        Assert.False(F38EndToEndLiveTests.HasExpectedShape(detached));
    }

    [Fact]
    public void The_report_replaces_the_users_profile_folder_in_every_spelling()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"a {profile}\\AppData\\x; b {profile.Replace('\\', '/')}/y; c {profile.Replace("\\", "\\\\", StringComparison.Ordinal)}\\\\z; d {profile.ToUpperInvariant()}\\w";

        var scrubbed = F38Report.Scrub(text);

        Assert.DoesNotContain(profile, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Replace('\\', '/'), scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a C:\\Users\\usuario\\AppData\\x; b C:/Users/usuario/y; c C:\\\\Users\\\\usuario\\\\z; d C:\\Users\\usuario\\w", scrubbed);
    }
}
