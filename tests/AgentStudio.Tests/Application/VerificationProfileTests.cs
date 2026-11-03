using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Tests.Application;

public sealed class VerificationProfileTests
{
    private static VerificationStep Build() => new(
        "build",
        "Compilar",
        "dotnet",
        ["build", "AgentStudio.slnx"],
        EvidenceKind.BuildLog,
        TestReportFormat.None,
        null,
        TimeSpan.FromMinutes(10));

    private static VerificationStep Test() => new(
        "test",
        "Pruebas",
        "dotnet",
        ["test", "--no-build", "--results-directory", "results", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "t.trx", "--zero-tests-policy", "strict"],
        EvidenceKind.TestRun,
        TestReportFormat.Trx,
        "results/t.trx",
        TimeSpan.FromMinutes(20));

    private static VerificationProfile Profile(params VerificationStep[] steps) => new("dotnet", steps);

    [Fact]
    public void The_hash_is_64_lowercase_hex_and_stable()
    {
        var hash = Profile(Build(), Test()).ContentHash;

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, Profile(Build(), Test()).ContentHash);
    }

    [Fact]
    public void A_profile_whose_steps_are_all_isolated_keeps_the_hash_it_had_before_isolation_existed()
    {
        // Computed with the code before ADR-0017: runs record this hash, and the default (isolated) must not change it.
        Assert.Equal("89a954840741436b0786da148fab60ca49e586f573417f4104084f288f50ec56", Profile(Build(), Test()).ContentHash);
        Assert.Equal(ProcessIsolation.LowIntegrity, Build().Isolation);
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_of_the_steps_because_plans_refer_to_them_by_id()
    {
        Assert.Equal(Profile(Build(), Test()).ContentHash, Profile(Test(), Build()).ContentHash);
    }

    public static TheoryData<string> Mutations() =>
    [
        "name", "id", "title", "executable", "argument-changed", "argument-order", "argument-added", "kind", "report", "report-path",
        "timeout", "step-removed", "step-added", "isolation-off", "requires-added",
    ];

    [Theory]
    [MemberData(nameof(Mutations))]
    public void The_hash_changes_when_anything_that_decides_what_runs_changes(string mutation)
    {
        var baseline = Profile(Build(), Test()).ContentHash;
        var test = Test();
        var changed = mutation switch
        {
            "name" => new VerificationProfile("other", [Build(), test]),
            "id" => Profile(Build(), test with { Id = "tests" }),
            "title" => Profile(Build(), test with { Title = "Otras pruebas" }),
            "executable" => Profile(Build(), test with { Executable = "dotnet.exe" }),
            "argument-changed" => Profile(Build(), test with { Arguments = [.. test.Arguments.Take(9), "relaxed"] }),
            "argument-order" => Profile(Build(), test with { Arguments = [.. test.Arguments.Reverse()] }),
            "argument-added" => Profile(Build(), test with { Arguments = [.. test.Arguments, "--extra"] }),
            "kind" => Profile(Build(), test with { Kind = EvidenceKind.CommandOutput }),
            "report" => Profile(Build(), test with { Report = TestReportFormat.JUnit }),
            "report-path" => Profile(Build(), test with { ReportPath = "results/other.trx" }),
            "timeout" => Profile(Build(), test with { Timeout = TimeSpan.FromMinutes(21) }),
            "isolation-off" => Profile(Build(), test with { Isolation = ProcessIsolation.None }),
            "requires-added" => Profile(Build(), test with { Requires = ["build"] }),
            "step-removed" => Profile(Build()),
            _ => Profile(Build(), test, Build() with { Id = "lint" }),
        };

        Assert.NotEqual(baseline, changed.ContentHash);
    }

    [Fact]
    public void Arguments_moved_between_neighbours_do_not_collide()
    {
        var a = Profile(Build() with { Arguments = ["ab", "c"] });
        var b = Profile(Build() with { Arguments = ["a", "bc"] });
        var c = Profile(Build() with { Arguments = ["abc"] });

        Assert.NotEqual(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    [Fact]
    public void An_empty_profile_has_a_hash_of_its_own()
    {
        Assert.NotEqual(Profile().ContentHash, new VerificationProfile("other", []).ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", Profile().ContentHash);
    }

    [Fact]
    public void Find_looks_a_step_up_by_its_exact_id()
    {
        var profile = Profile(Build(), Test());

        Assert.Equal("Pruebas", profile.Find("test")!.Title);
        Assert.Null(profile.Find("Test"));
        Assert.Null(profile.Find("lint"));
    }

    // ---- requirements (profile file version 2) ----------------------------------------------------------------------

    private static VerificationStep Step(string id, params string[] requires) =>
        Build() with { Id = id, Arguments = [id], Requires = requires.Length == 0 ? null : requires };

    private static IReadOnlyList<string> Run(VerificationProfile profile, params string[] named) => [.. profile.StepsToRun(named).Select(s => s.Id)];

    [Fact]
    public void An_empty_list_of_requirements_hashes_as_none_and_the_pinned_hash_does_not_move()
    {
        Assert.Equal("89a954840741436b0786da148fab60ca49e586f573417f4104084f288f50ec56", Profile(Build(), Test() with { Requires = [] }).ContentHash);
        Assert.Equal(Profile(Build(), Test()).ContentHash, Profile(Build() with { Requires = [] }, Test()).ContentHash);
    }

    [Fact]
    public void The_order_a_step_lists_its_requirements_in_does_not_change_the_hash_because_they_run_in_the_order_of_the_profile()
    {
        var one = Profile(Step("restore"), Step("build"), Step("test", "restore", "build"));
        var other = Profile(Step("restore"), Step("build"), Step("test", "build", "restore"));

        Assert.Equal(one.ContentHash, other.ContentHash);
        Assert.Equal(["restore", "build", "test"], Run(one, "test"));
        Assert.Equal(Run(one, "test"), Run(other, "test"));
    }

    [Fact]
    public void With_requirements_the_order_of_the_steps_decides_what_runs_first_so_the_hash_covers_it()
    {
        var restoreFirst = Profile(Step("restore"), Step("build"), Step("test", "build", "restore"));
        var buildFirst = Profile(Step("build"), Step("restore"), Step("test", "build", "restore"));

        Assert.Equal(["restore", "build", "test"], Run(restoreFirst, "test"));
        Assert.Equal(["build", "restore", "test"], Run(buildFirst, "test"));
        Assert.NotEqual(restoreFirst.ContentHash, buildFirst.ContentHash);
    }

    [Fact]
    public void A_task_that_names_only_the_test_gets_the_build_it_requires_first()
    {
        var profile = Profile(Build(), Test() with { Requires = ["build"] });

        Assert.Equal(["build", "test"], Run(profile, "test"));
        Assert.Equal(["build", "test"], Run(profile, "build", "test"));
        Assert.Equal(["build", "test"], Run(profile, "test", "build"));
        Assert.Equal(["build", "test"], Run(profile, "test", "test"));
        Assert.Equal(["build"], Run(profile, "build"));
    }

    [Fact]
    public void Requirements_are_followed_through_other_steps_inserted_before_the_first_step_that_needs_them_and_never_repeated()
    {
        var profile = Profile(Step("restore"), Step("build", "restore"), Step("lint"), Step("test", "build"), Step("pack", "build"));

        Assert.Equal(["restore", "build", "test"], Run(profile, "test"));
        Assert.Equal(["lint", "restore", "build", "test", "pack"], Run(profile, "lint", "test", "pack"));
        Assert.Equal(["restore", "build", "pack", "test"], Run(profile, "pack", "test"));
    }

    [Fact]
    public void Without_requirements_the_steps_run_as_the_task_names_them_as_they_always_did()
    {
        var profile = Profile(Build(), Test(), Step("lint"));

        Assert.Equal(["test", "build"], Run(profile, "test", "build"));
        Assert.Equal(["lint"], Run(profile, "lint"));
        Assert.Empty(Run(profile));
    }

    [Fact]
    public void A_step_that_is_not_in_the_profile_named_or_required_is_never_left_out_in_silence()
    {
        var profile = Profile(Build(), Test() with { Requires = ["restore"] });

        Assert.Throws<InvalidOperationException>(() => profile.StepsToRun(["lint"]));
        Assert.Throws<InvalidOperationException>(() => profile.StepsToRun(["test"]));

        var steps = profile.StepsToRun(["lint", "test", "build"], out var missing);
        Assert.Equal(["test", "build"], steps.Select(s => s.Id));
        Assert.Equal(["lint", "restore"], missing);
    }
}
