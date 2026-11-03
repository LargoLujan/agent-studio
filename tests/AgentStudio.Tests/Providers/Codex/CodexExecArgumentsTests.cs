using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Tests.Providers.Codex;

public sealed class CodexExecArgumentsTests
{
    private const string Prompt = "PROMPT-SECRETO con \"comillas\" & metacaracteres";

    // Hand-written on purpose (not read from CodexExecArguments): the isolation block every launch must start with.
    private static readonly string[] Isolation =
        ["--ignore-user-config", "--ignore-rules", "--disable", "hooks", "-c", "windows.sandbox=unelevated"];

    [Fact]
    public void Read_only_profile_without_repository()
    {
        var args = CodexExecArguments.Build(CodexTestSupport.Request(workingDirectory: "C:/work", model: null), null, isRepository: false);

        Assert.Equal(["exec", "--json", .. Isolation, "-s", "read-only", "-C", "C:/work", "--skip-git-repo-check", "-"], args);
    }

    [Fact]
    public void Writable_profile_without_network_in_a_repository_with_model()
    {
        var request = CodexTestSupport.Request(CodexTestSupport.Permissions(writes: true), "C:/repo");

        var args = CodexExecArguments.Build(request, null, isRepository: true);

        Assert.Equal(
            ["exec", "--json", .. Isolation, "-s", "workspace-write", "-c", "sandbox_workspace_write.network_access=false", "-C", "C:/repo", "-m", "gpt-x", "-"],
            args);
    }

    [Fact]
    public void Writable_profile_with_network_and_additional_directories()
    {
        var request = CodexTestSupport.Request(CodexTestSupport.Permissions(true, true, "C:/a", "C:/b"), "C:/repo", model: null);

        var args = CodexExecArguments.Build(request, null, isRepository: true);

        Assert.Equal(
            ["exec", "--json", .. Isolation, "-s", "workspace-write", "-c", "sandbox_workspace_write.network_access=true", "--add-dir", "C:/a", "--add-dir", "C:/b", "-C", "C:/repo", "-"],
            args);
    }

    [Fact]
    public void Output_schema_and_resume_go_before_the_prompt_marker()
    {
        var request = CodexTestSupport.Request(schema: "{}", resume: "01a1-thread", model: null);

        var args = CodexExecArguments.Build(request, "C:/tmp/schema.json", isRepository: true);

        Assert.Equal(["exec", "--json", .. Isolation, "-s", "read-only", "-C", "C:/work", "--output-schema", "C:/tmp/schema.json", "resume", "01a1-thread", "-"], args);
    }

    [Theory]
    [InlineData(false, false, null, null)]
    [InlineData(true, false, null, null)]
    [InlineData(true, true, null, null)]
    [InlineData(false, false, "{}", null)]
    [InlineData(false, false, null, "01a13f53-e4b3-7071-8396-4afc4d6c0323")]
    public void Every_launch_ignores_the_user_and_project_configuration(bool writes, bool network, string? schema, string? resume)
    {
        var request = CodexTestSupport.Request(CodexTestSupport.Permissions(writes, network), schema: schema, resume: resume);

        var args = CodexExecArguments.Build(request, schema is null ? null : "C:/tmp/schema.json", isRepository: true).ToList();

        // Right after "exec --json", so they also apply to "resume" (which only accepts exec options placed before it).
        Assert.Equal(Isolation, args.Skip(2).Take(Isolation.Length));
        Assert.Single(args, a => a == "--ignore-user-config");
        Assert.DoesNotContain("--dangerously-bypass-hook-trust", args);
        Assert.DoesNotContain(args, a => a.StartsWith("projects.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_isolation_block_is_safe_to_pass_through_a_cmd_shim()
    {
        var args = CodexExecArguments.Build(CodexTestSupport.Request(), null, isRepository: true);

        // The runner refuses these characters when it has to launch codex.cmd; a quoted TOML value would be refused.
        char[] cmdMetacharacters = ['&', '|', '<', '>', '^', '%', '"', '!', '(', ')'];
        Assert.DoesNotContain(args.Take(2 + Isolation.Length), a => a.IndexOfAny(cmdMetacharacters) >= 0);
    }

    [Fact]
    public void Schema_without_file_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CodexExecArguments.Build(CodexTestSupport.Request(schema: "{}"), null, true));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void Never_unsafe_flags_and_prompt_never_in_arguments(bool writes, bool network, bool persist)
    {
        var request = CodexTestSupport.Request(CodexTestSupport.Permissions(writes, network), prompt: Prompt, instructions: "INSTRUCCIONES") with { PersistSession = persist };

        var args = CodexExecArguments.Build(request, null, true);

        Assert.DoesNotContain("danger-full-access", args);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain(args, a => a.Contains("PROMPT-SECRETO") || a.Contains("INSTRUCCIONES"));
        Assert.Equal("-", args[^1]);
    }

    // ---- turns without session persistence (docs/15 D-16) ----------------------------------------------------------

    [Theory]
    [InlineData(false, false, null, null)]
    [InlineData(true, false, null, null)]
    [InlineData(true, true, null, null)]
    [InlineData(false, false, "{}", null)]
    [InlineData(false, false, null, "01a13f53-e4b3-7071-8396-4afc4d6c0323")]
    public void A_session_is_saved_unless_the_request_says_it_must_not_be(bool writes, bool network, string? schema, string? resume)
    {
        var request = CodexTestSupport.Request(CodexTestSupport.Permissions(writes, network), schema: schema, resume: resume);

        var args = CodexExecArguments.Build(request, schema is null ? null : "C:/tmp/schema.json", isRepository: true);

        // Without that request, the session (and the judge's, which resumes it for its correction) is saved as always.
        Assert.True(request.PersistSession);
        Assert.DoesNotContain("--ephemeral", args);
        Assert.DoesNotContain(args, a => a.StartsWith("history.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_turn_that_must_not_be_saved_is_ephemeral_and_keeps_no_message_history()
    {
        var request = CodexTestSupport.Request(schema: "{}", model: null) with { PersistSession = false };

        var args = CodexExecArguments.Build(request, "C:/tmp/schema.json", isRepository: false);

        // --ephemeral leaves out the files of the session; history.persistence=none, the history of messages, in case exec
        // writes it. Both where resume would go, and resume never comes with them.
        Assert.Equal(
            ["exec", "--json", .. Isolation, "-s", "read-only", "-C", "C:/work", "--skip-git-repo-check", "--output-schema", "C:/tmp/schema.json", "--ephemeral", "-c", "history.persistence=none", "-"],
            args);
    }

    [Fact]
    public void The_flags_of_a_turn_that_must_not_be_saved_are_safe_to_pass_through_a_cmd_shim()
    {
        var args = CodexExecArguments.Build(CodexTestSupport.Request() with { PersistSession = false }, null, isRepository: true).ToList();
        var ephemeral = args.IndexOf("--ephemeral");

        // Constant text without quotes: codex takes a value that is not TOML literally, as with windows.sandbox=unelevated.
        char[] cmdMetacharacters = ['&', '|', '<', '>', '^', '%', '"', '!', '(', ')'];
        Assert.Equal(["--ephemeral", "-c", "history.persistence=none"], args.Skip(ephemeral).Take(3));
        Assert.DoesNotContain(args.Skip(ephemeral).Take(3), a => a.IndexOfAny(cmdMetacharacters) >= 0);
    }

    [Theory]
    [InlineData("01a1-thread")]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    public void A_turn_that_must_not_be_saved_and_resumes_a_session_is_refused(string resume)
    {
        // Not saving and resuming exclude each other: a thread without its files cannot be resumed.
        var request = CodexTestSupport.Request(schema: "{}", resume: resume) with { PersistSession = false };

        var refused = Assert.Throws<ArgumentException>(() => CodexExecArguments.Build(request, "C:/tmp/schema.json", isRepository: true));

        Assert.Contains("--ephemeral", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_turn_of_a_debate_participant_is_ephemeral_and_read_only_in_its_copy()
    {
        // D-09: the fixed read-only profile of a participant; the folder of its turn is not added (the read-only sandbox
        // takes no --add-dir: it reads anyway, which is the residual of docs/15 section 7) and debate.json goes in the prompt.
        var profile = new PermissionProfile(false, false, false, ["Read", "Grep", "Glob"], [], [@"C:\as\debates\0a1b2c3d\a1b2c3\turns\r1-critico-1"]);
        var request = CodexTestSupport.Request(profile, @"C:\as\debates\0a1b2c3d\a1b2c3\snapshot", schema: "{}") with { PersistSession = false };

        var args = CodexExecArguments.Build(request, @"C:\runs\1\schema.json", isRepository: false);

        Assert.Equal(
            [
                "exec", "--json", .. Isolation, "-s", "read-only", "-C", @"C:\as\debates\0a1b2c3d\a1b2c3\snapshot", "--skip-git-repo-check",
                "-m", "gpt-x", "--output-schema", @"C:\runs\1\schema.json", "--ephemeral", "-c", "history.persistence=none", "-",
            ],
            args);
    }

    [Fact]
    public void Standard_input_is_the_prompt_when_there_are_no_instructions()
    {
        Assert.Equal(Prompt, CodexExecArguments.BuildStandardInput(CodexTestSupport.Request(prompt: Prompt)));
    }

    [Fact]
    public void Standard_input_prepends_instructions_inside_delimiters()
    {
        var input = CodexExecArguments.BuildStandardInput(CodexTestSupport.Request(prompt: "Tarea", instructions: "Eres un ejecutor."));

        Assert.Equal("<agent_instructions>\nEres un ejecutor.\n</agent_instructions>\n\n<task>\nTarea\n</task>\n", input);
    }

    [Fact]
    public void Repository_detection_walks_up_to_a_marker()
    {
        var root = CodexTestSupport.NewTempDirectory();
        try
        {
            var child = Directory.CreateDirectory(Path.Combine(root, "a", "b")).FullName;
            Assert.False(CodexExecArguments.IsRepository(child));

            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.True(CodexExecArguments.IsRepository(child));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
