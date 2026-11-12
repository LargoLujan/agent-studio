using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;

namespace AgentStudio.Tests.Application.Diagnostics;

/// <summary>A source of checks as the test scripts it.</summary>
internal sealed class ScriptedSource(DoctorSection section, Func<ProjectContext?, IReadOnlyList<DoctorCheck>> checks) : IDoctorCheckSource
{
    public DoctorSection Section => section;

    public int Calls { get; private set; }

    public Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(checks(project));
    }

    public static DoctorCheck Check(string code, DoctorSection section = DoctorSection.Tools, DoctorStatus status = DoctorStatus.Ok) => new(code, section, status, []);
}

internal sealed class ThrowingSource(DoctorSection section) : IDoctorCheckSource
{
    public DoctorSection Section => section;

    public Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct) =>
        throw new InvalidOperationException(@"Secret text of the failure: C:\Users\someone\.claude\token.txt line 4");
}

internal sealed class CountingScope : IDoctorRunScope
{
    public int Begins { get; private set; }

    public int Ends { get; private set; }

    public void Begin() => Begins++;

    public void End() => Ends++;
}

/// <summary>The aggregator of Auto Doctor (docs/20 D-01, F6-1): it never fails whole and its report is always valid.</summary>
public sealed class DoctorServiceTests
{
    private static readonly ProjectContext Project = new("p1", @"C:\work\calc");

    [Fact]
    public async Task A_source_that_throws_becomes_one_check_in_error_without_the_text_of_its_exception_and_the_others_still_answer()
    {
        var good = new ScriptedSource(DoctorSection.Tools, _ => [ScriptedSource.Check("doctor.git")]);
        var service = new DoctorService([new ThrowingSource(DoctorSection.Providers), good, new ThrowingSource(DoctorSection.Containers)]);

        var report = await service.RunAsync(null, CancellationToken.None);

        Assert.Equal(3, report.Checks.Count);
        var failed = report.Checks.Where(c => c.Code.StartsWith("doctor.source-failed.", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, c => Assert.Equal(DoctorStatus.Error, c.Status));
        Assert.Equal(DoctorSection.Providers, failed[0].Section);
        Assert.Equal(DoctorSection.Containers, failed[1].Section);
        Assert.NotEqual(failed[0].Code, failed[1].Code);
        Assert.Contains(report.Checks, c => c.Code == "doctor.git" && c.Status == DoctorStatus.Ok);
        Assert.DoesNotContain(report.Checks.SelectMany(c => c.Facts), f => f.Value.Contains("Secret", StringComparison.Ordinal) || f.Value.Contains(".claude", StringComparison.Ordinal));
        Assert.Equal("ThrowingSource", failed[0].Fact("source"));
    }

    [Fact]
    public async Task Two_sources_that_repeat_a_code_do_not_break_the_report()
    {
        var a = new ScriptedSource(DoctorSection.Tools, _ => [ScriptedSource.Check("doctor.git")]);
        var b = new ScriptedSource(DoctorSection.Tools, _ => [ScriptedSource.Check("doctor.git", status: DoctorStatus.Error)]);

        var report = await new DoctorService([a, b]).RunAsync(null, CancellationToken.None);

        Assert.Equal(["doctor.git", "doctor.git-2"], report.Checks.Select(c => c.Code).ToArray());
    }

    [Fact]
    public async Task Without_a_project_the_sections_that_need_one_are_not_run()
    {
        var project = new ScriptedSource(DoctorSection.Project, _ => [ScriptedSource.Check("doctor.project.repository", DoctorSection.Project)]);
        var declared = new ScriptedSource(DoctorSection.DeclaredEnvironment, _ => [ScriptedSource.Check("doctor.env.x", DoctorSection.DeclaredEnvironment)]);
        var tools = new ScriptedSource(DoctorSection.Tools, _ => [ScriptedSource.Check("doctor.git")]);
        var service = new DoctorService([project, declared, tools]);

        var without = await service.RunAsync(null, CancellationToken.None);
        var with = await service.RunAsync(Project, CancellationToken.None);

        Assert.Equal(["doctor.git"], without.Checks.Select(c => c.Code).ToArray());
        Assert.Equal(1, project.Calls); // only the run with a project asked it
        Assert.Equal(3, with.Checks.Count);
    }

    [Fact]
    public async Task The_checks_come_in_the_order_of_the_sections_and_then_of_the_sources()
    {
        var containers = new ScriptedSource(DoctorSection.Containers, _ => [ScriptedSource.Check("doctor.docker", DoctorSection.Containers)]);
        var providers = new ScriptedSource(DoctorSection.Providers, _ => [ScriptedSource.Check("doctor.provider.a.install", DoctorSection.Providers)]);
        var tools = new ScriptedSource(DoctorSection.Tools, _ => [ScriptedSource.Check("doctor.git"), ScriptedSource.Check("doctor.node")]);

        var report = await new DoctorService([containers, tools, providers]).RunAsync(null, CancellationToken.None);

        Assert.Equal(["doctor.provider.a.install", "doctor.git", "doctor.node", "doctor.docker"], report.Checks.Select(c => c.Code).ToArray());
    }

    [Fact]
    public async Task The_scopes_open_before_the_sources_and_close_after_even_when_a_source_throws()
    {
        var scope = new CountingScope();
        var service = new DoctorService([new ThrowingSource(DoctorSection.Tools)], [scope]);

        await service.RunAsync(null, CancellationToken.None);

        Assert.Equal((1, 1), (scope.Begins, scope.Ends));
    }

    [Fact]
    public async Task Cancelling_cancels_the_run_and_closes_the_scope()
    {
        var scope = new CountingScope();
        var source = new CancellingSource();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DoctorService([source], [scope]).RunAsync(null, cancel.Token));

        Assert.Equal(1, scope.Ends);
    }

    private sealed class CancellingSource : IDoctorCheckSource
    {
        public DoctorSection Section => DoctorSection.Tools;

        public Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct) =>
            Task.FromCanceled<IReadOnlyList<DoctorCheck>>(ct);
    }
}
