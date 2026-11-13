using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Tests.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Infrastructure.Hosting.Cli;

/// <summary>
/// docs/21 D-10 to D-12: how <c>gh</c> is launched and how its answer is read. The program is <c>gh.exe</c> resolved outside the open projects, the folder is the neutral one, the body goes through standard
/// input, the status is the one of the line and not the exit code, and the standard error is never read into anything.
/// </summary>
public sealed class GhRunnerTests
{
    private static readonly GhCall Read = new(GhMethod.Get, "/repos/octo/hello/pulls/7", "W/\"p1\"");

    [Fact]
    public async Task The_process_is_gh_exe_from_the_tools_folder_in_the_neutral_folder_with_the_limits_of_the_design()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));

        var result = await rig.Runner.RunAsync(Read, CancellationToken.None);

        Assert.Equal(GhRunStatus.Response, result.Status);
        var spec = Assert.Single(rig.Script.Specs);
        Assert.Equal(Path.Combine(rig.Tools, "gh.exe"), spec.FileName);
        Assert.Equal(rig.Folders.WorkingFolder, spec.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(20), spec.Timeout);
        Assert.Equal(1024 * 1024, spec.MaxOutputCharacters);
        Assert.Equal(ProcessIsolation.None, spec.Isolation);
        Assert.Null(spec.StandardInput);
        Assert.True(Directory.Exists(rig.Folders.WorkingFolder));
    }

    [Fact]
    public async Task A_body_goes_through_standard_input_and_nowhere_in_the_arguments()
    {
        using var rig = new GhRig();
        rig.Script.Route("PATCH", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));
        const string body = "{\"title\":\"--upload-pack=x @file {owner}\",\"body\":\"line1\\nline2\"}";

        await rig.Runner.RunAsync(new GhCall(GhMethod.Patch, "/repos/octo/hello/pulls/7", Body: body), CancellationToken.None);

        var spec = Assert.Single(rig.Script.Specs);
        Assert.Equal(body, spec.StandardInput);
        Assert.Equal(["--input", "-"], spec.Arguments.Skip(spec.Arguments.Count - 3).Take(2));
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("upload-pack", StringComparison.Ordinal) || a.Contains("{", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_status_is_the_one_of_the_line_and_not_the_exit_code_a_304_and_a_404_leave_with_one_and_are_answers()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.NotModified());
        rig.Script.Route("GET", "/repos/octo/hello/pulls/8", Gh.Http(404, "{\"message\":\"Not Found\"}"));

        var notModified = await rig.Runner.RunAsync(Read, CancellationToken.None);
        var missing = await rig.Runner.RunAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/8"), CancellationToken.None);

        Assert.Equal(GhRunStatus.Response, notModified.Status);
        Assert.Equal(304, notModified.Http!.Status);
        Assert.Equal(GhRunStatus.Response, missing.Status);
        Assert.Equal(404, missing.Http!.Status);
        Assert.Equal(2, rig.Script.Specs.Count);
    }

    [Fact]
    public async Task Exit_code_4_without_a_status_line_is_sign_in_needed_and_any_other_silence_is_no_response()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/4", new ProcessResult(4, string.Empty, "gh: To get started with GitHub CLI, please run:  gh auth login", TimeSpan.Zero, false));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/1", new ProcessResult(1, string.Empty, "error connecting to api.github.com", TimeSpan.Zero, false));

        var signIn = await rig.Runner.RunAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/4"), CancellationToken.None);
        var offline = await rig.Runner.RunAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/1"), CancellationToken.None);

        Assert.Equal(GhRunStatus.SignInNeeded, signIn.Status);
        Assert.Null(signIn.Http);
        Assert.Equal(GhRunStatus.NoResponse, offline.Status);
    }

    [Fact]
    public async Task A_standard_error_is_never_a_body_and_never_comes_back_in_anything()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", new ProcessResult(1, string.Empty, "HTTP 200 {\"login\":\"x\"} ghp_sentinel", TimeSpan.Zero, false));

        var result = await rig.Runner.RunAsync(Read, CancellationToken.None);

        Assert.Equal(GhRunStatus.NoResponse, result.Status);
        Assert.DoesNotContain("ghp_sentinel", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Something_on_the_standard_output_that_is_not_an_answer_is_invalid_and_so_is_an_output_past_the_limit_and_a_timeout_is_its_own_status()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/1", new ProcessResult(0, "{\"login\":\"octocat\"}", string.Empty, TimeSpan.Zero, false));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/2", new ProcessResult(-1, "HTTP/2.0 200 OK\n\n[", string.Empty, TimeSpan.Zero, false, OutputLimitExceeded: true));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/3", new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(20), true));

        Assert.Equal(GhRunStatus.InvalidAnswer, (await rig.Runner.RunAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/1"), CancellationToken.None)).Status);
        Assert.Equal(GhRunStatus.InvalidAnswer, (await rig.Runner.RunAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/2"), CancellationToken.None)).Status);
        Assert.Equal(GhRunStatus.TimedOut, (await rig.Runner.RunAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/3"), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_process_that_cannot_start_is_no_response_and_the_message_of_the_exception_is_not_kept()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", _ => throw new IOException("C:\\secret\\path ghp_sentinel"));

        var result = await rig.Runner.RunAsync(Read, CancellationToken.None);

        Assert.Equal(GhRunStatus.NoResponse, result.Status);
        Assert.DoesNotContain("sentinel", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_goes_through()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", _ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Runner.RunAsync(Read, CancellationToken.None));
    }

    [Fact]
    public async Task Without_gh_exe_nothing_starts_a_cmd_shim_does_not_count_and_one_inside_a_project_is_not_used()
    {
        using var none = new GhRig(installed: false);
        File.WriteAllText(Path.Combine(none.Tools, "gh.cmd"), "shim");
        File.WriteAllText(Path.Combine(none.Tools, "gh.bat"), "shim");

        Assert.Equal(GhRunStatus.NotInstalled, (await none.Runner.RunAsync(Read, CancellationToken.None)).Status);
        Assert.Empty(none.Script.Specs);

        var projectTools = Path.Combine(Path.GetTempPath(), "agentstudio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectTools);
        try
        {
            File.WriteAllText(Path.Combine(projectTools, "gh.exe"), "planted");
            using var planted = new GhRig(roots: new FixedRoots(projectTools));
            // The PATH of the rig is its own folder; plant the project folder first by using the project as the tools folder.
            var path = new GhRunner(planted.Script, planted.Folders, new FixedRoots(projectTools), () => projectTools + Path.PathSeparator + planted.Tools);
            Assert.Equal(Path.Combine(planted.Tools, "gh.exe"), path.FindExecutable());
            var only = new GhRunner(planted.Script, planted.Folders, new FixedRoots(projectTools), () => projectTools);
            Assert.Null(only.FindExecutable());
            Assert.Equal(GhRunStatus.NotInstalled, (await only.RunAsync(Read, CancellationToken.None)).Status);
            Assert.Empty(planted.Script.Specs);
        }
        finally
        {
            Directory.Delete(projectTools, recursive: true);
        }
    }

    [Fact]
    public async Task The_environment_of_the_process_is_the_allow_list_with_what_the_test_planted_removed()
    {
        var ambient = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GH_TOKEN"] = "ghp_sentinel", ["GITHUB_TOKEN"] = "ghp_sentinel", ["GH_HOST"] = "evil.example", ["GH_REPO"] = "evil/x", ["GH_DEBUG"] = "api", ["GIT_ASKPASS"] = "evil", ["GCM_TRACE"] = "1",
        };
        using var rig = new GhRig(ambient: ambient);
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));

        await rig.Runner.RunAsync(Read, CancellationToken.None);

        var environment = Assert.Single(rig.Script.Specs).Environment!;
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_HOST", "GH_REPO", "GH_DEBUG", "GIT_ASKPASS", "GCM_TRACE" })
        {
            Assert.True(environment.ContainsKey(name) && environment[name] is null, name);
        }

        Assert.DoesNotContain(environment.Values, v => v is not null && v.Contains("sentinel", StringComparison.Ordinal));
    }
}
