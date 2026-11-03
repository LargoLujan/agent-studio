using System.IO;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Verification;

public sealed class VerificationProfileFileTests : IDisposable
{
    private const string ValidJson = """
        {
          "version": 1,
          "name": "dotnet",
          "steps": [
            {
              "id": "build",
              "title": "Compilar",
              "executable": "dotnet",
              "arguments": ["build", "App.slnx"],
              "kind": "BuildLog",
              "report": "None",
              "reportPath": null,
              "timeoutSeconds": 600
            },
            {
              "id": "test",
              "title": "Pruebas",
              "executable": "dotnet",
              "arguments": ["test", "--no-build", "--results-directory", "{results}", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "test.trx"],
              "kind": "TestRun",
              "report": "Trx",
              "reportPath": "{results}/test.trx",
              "timeoutSeconds": 1200
            }
          ]
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("as-profile-").FullName;

    private static readonly ProjectStorageFiles Files = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The profile <see cref="ValidJson"/> describes, built by hand, without requirements. A version 1 file gets them by
    /// <see cref="VerificationProfileFile.LegacyRequirements"/> (<see cref="ExpectedFromVersion1"/>); written by
    /// <see cref="VerificationProfileFile.Serialize"/> (version 2), it reads back exactly as it is.
    /// </summary>
    private static VerificationProfile Expected() => new("dotnet",
    [
        new VerificationStep("build", "Compilar", "dotnet", ["build", "App.slnx"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(10)),
        new VerificationStep(
            "test",
            "Pruebas",
            "dotnet",
            ["test", "--no-build", "--results-directory", "{results}", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "test.trx"],
            EvidenceKind.TestRun,
            TestReportFormat.Trx,
            "{results}/test.trx",
            TimeSpan.FromMinutes(20)),
    ]);

    /// <summary>What <see cref="ValidJson"/>, a version 1 file, reads as: its test with <c>--no-build</c> requires its build.</summary>
    private static VerificationProfile ExpectedFromVersion1() =>
        Expected() with { Steps = [Expected().Steps[0], Expected().Steps[1] with { Requires = ["build"] }] };

    private string ProfilePath => Path.Combine(_root, ".agentstudio", "verification.json");

    private void WriteProfile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, json, new UTF8Encoding(false));
    }

    [Fact]
    public void The_profile_lives_in_the_agentstudio_folder_of_the_project_root()
    {
        Assert.Equal(ProfilePath, VerificationProfileFile.PathFor(_root));
    }

    [Fact]
    public async Task A_project_without_a_profile_has_none_and_no_default_is_applied_silently()
    {
        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.NotFound, load.Status);
        Assert.Null(load.Profile);
        Assert.Empty(load.Problems);
        Assert.Equal(ProfilePath, load.Path);
    }

    [Fact]
    public async Task A_valid_file_is_loaded_exactly()
    {
        WriteProfile(ValidJson);

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Empty(load.Problems);
        Assert.NotNull(load.Profile);
        Assert.Equal(ExpectedFromVersion1().ContentHash, load.Profile.ContentHash);
        Assert.Equal(["build", "test"], load.Profile.Steps.Select(s => s.Id));
    }

    public static TheoryData<string, string, string> BrokenFiles() => new()
    {
        // Rules of the profile, with their own messages.
        { "version 3", "\"version\": 1", "\"version\": 3" },
        { "version 0", "\"version\": 1", "\"version\": 0" },
        { "requires in version 1", "\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200, \"requires\": [\"build\"]" },
        { "empty requires in version 1", "\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200, \"requires\": []" },
        { "null requires in version 1", "\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200, \"requires\": null" },
        { "duplicate id", "\"id\": \"test\"", "\"id\": \"Build\"" },
        { "test run without report", "\"report\": \"Trx\"", "\"report\": \"None\"" },
        { "zero timeout", "\"timeoutSeconds\": 600", "\"timeoutSeconds\": 0" },
        { "negative timeout", "\"timeoutSeconds\": 600", "\"timeoutSeconds\": -5" },
        { "control character", "\"App.slnx\"", "\"App.slnx\\n--evil\"" },
        { "bad id", "\"id\": \"build\"", "\"id\": \"1build\"" },
        { "no steps", ValidJson, "{ \"version\": 1, \"name\": \"vacio\", \"steps\": [] }" },

        // The JSON itself: strict, nothing is guessed.
        { "unknown property", "\"name\": \"dotnet\",", "\"name\": \"dotnet\", \"shell\": \"pwsh\"," },
        { "enum as a number", "\"kind\": \"BuildLog\"", "\"kind\": 1" },
        { "unknown enum value", "\"report\": \"None\"", "\"report\": \"Html\"" },
        { "missing name", "\"name\": \"dotnet\",", string.Empty },
        { "null title", "\"title\": \"Compilar\"", "\"title\": null" },
        { "missing timeout", ",\n      \"timeoutSeconds\": 600", string.Empty },
        { "trailing comma", "\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200," },
        { "comment", "\"version\": 1,", "\"version\": 1, // comentario" },
        { "truncated", ValidJson, ValidJson[..(ValidJson.Length / 2)] },
        { "not an object", ValidJson, "[1, 2, 3]" },
        { "null document", ValidJson, "null" },

        // Isolation can only be turned off by name: anything else is an error, never a silent "none".
        { "unknown isolation", "\"timeoutSeconds\": 600", "\"timeoutSeconds\": 600, \"isolation\": \"off\"" },
        { "empty isolation", "\"timeoutSeconds\": 600", "\"timeoutSeconds\": 600, \"isolation\": \"\"" },
        { "isolation as a number", "\"timeoutSeconds\": 600", "\"timeoutSeconds\": 600, \"isolation\": 0" },
        { "null isolation", "\"timeoutSeconds\": 600", "\"timeoutSeconds\": 600, \"isolation\": null" },
    };

    [Theory]
    [InlineData("", ProcessIsolation.LowIntegrity)]
    [InlineData(", \"isolation\": \"none\"", ProcessIsolation.None)]
    [InlineData(", \"isolation\": \"None\"", ProcessIsolation.None)]
    [InlineData(", \"isolation\": \"lowIntegrity\"", ProcessIsolation.LowIntegrity)]
    [InlineData(", \"isolation\": \"LowIntegrity\"", ProcessIsolation.LowIntegrity)]
    public async Task A_step_is_isolated_unless_the_file_says_isolation_none_for_it(string field, ProcessIsolation expected)
    {
        WriteProfile(ReplaceFirst(Lf(ValidJson), "\"timeoutSeconds\": 600", "\"timeoutSeconds\": 600" + field));

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(expected, load.Profile!.Find("build")!.Isolation);
        Assert.Equal(ProcessIsolation.LowIntegrity, load.Profile.Find("test")!.Isolation);
    }

    [Fact]
    public void A_step_without_isolation_is_written_and_read_back_as_such()
    {
        var profile = new VerificationProfile("dotnet", [Expected().Steps[0] with { Isolation = ProcessIsolation.None }, Expected().Steps[1]]);

        var load = VerificationProfileFile.Parse(VerificationProfileFile.Serialize(profile), "memoria");

        Assert.Equal(ProcessIsolation.None, load.Profile!.Find("build")!.Isolation);
        Assert.Equal(ProcessIsolation.LowIntegrity, load.Profile.Find("test")!.Isolation);
        Assert.Equal(profile.ContentHash, load.Profile.ContentHash);
        Assert.NotEqual(Expected().ContentHash, load.Profile.ContentHash);
    }

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public async Task A_broken_file_is_invalid_and_says_what_is_wrong(string problem, string find, string replace)
    {
        WriteProfile(ReplaceFirst(Lf(ValidJson), Lf(find), replace));

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.True(load.Status == VerificationProfileStatus.Invalid, $"'{problem}' was accepted.");
        Assert.Null(load.Profile);
        Assert.NotEmpty(load.Problems);
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 3", "version")]
    [InlineData("\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200, \"requires\": [\"build\"]", "\"requires\"")]
    [InlineData("\"id\": \"test\"", "\"id\": \"Build\"", "used more than once")]
    [InlineData("\"report\": \"Trx\"", "\"report\": \"None\"", "reads no report")]
    [InlineData("\"timeoutSeconds\": 600", "\"timeoutSeconds\": 0", "timeout")]
    public async Task The_problems_of_a_profile_name_the_rule_it_breaks(string find, string replace, string fragment)
    {
        WriteProfile(ReplaceFirst(Lf(ValidJson), find, replace));

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Contains(load.Problems, p => p.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_with_a_byte_order_mark_is_read_and_one_that_is_not_UTF8_is_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, ValidJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(VerificationProfileStatus.Loaded, (await VerificationProfileFile.LoadAsync(_root, Ct)).Status);

        File.WriteAllBytes(ProfilePath, [.. Encoding.UTF8.GetBytes("{ \"version\": 1, \"name\": \""), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("\" }")]);

        var broken = await VerificationProfileFile.LoadAsync(_root, Ct);
        Assert.Equal(VerificationProfileStatus.Invalid, broken.Status);
        Assert.Contains(broken.Problems, p => p.Contains("UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused_without_parsing_it()
    {
        WriteProfile(ValidJson + new string(' ', VerificationProfileFile.MaxFileBytes));

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.Invalid, load.Status);
        Assert.Contains(load.Problems, p => p.Contains("larger than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_saved_profile_loads_back_with_the_same_hash()
    {
        await VerificationProfileFile.SaveAsync(_root, Expected(), Files, Ct);

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.True(File.Exists(ProfilePath));
        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(Expected().ContentHash, load.Profile!.ContentHash);
    }

    [Fact]
    public async Task Saving_again_replaces_the_previous_profile()
    {
        await VerificationProfileFile.SaveAsync(_root, Expected(), Files, Ct);
        var other = new VerificationProfile("solo-compilar", [Expected().Steps[0]]);

        await VerificationProfileFile.SaveAsync(_root, other, Files, Ct);

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);
        Assert.Equal(other.ContentHash, load.Profile!.ContentHash);
        Assert.Equal(["verification.json"], Directory.EnumerateFiles(Path.GetDirectoryName(ProfilePath)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("fractional-timeout")]
    public async Task A_profile_that_the_file_cannot_hold_exactly_is_never_saved(string problem)
    {
        var steps = Expected().Steps;
        var profile = problem == "invalid"
            ? new VerificationProfile("roto", [steps[1] with { Report = TestReportFormat.None, ReportPath = null }])
            : new VerificationProfile("dotnet", [steps[0] with { Timeout = TimeSpan.FromMilliseconds(1500) }]);

        await Assert.ThrowsAsync<ArgumentException>(() => VerificationProfileFile.SaveAsync(_root, profile, Files, Ct));

        Assert.False(File.Exists(ProfilePath));
    }

    [Fact]
    public void Parse_accepts_what_Serialize_writes()
    {
        var text = VerificationProfileFile.Serialize(Expected());

        var load = VerificationProfileFile.Parse(text, "memoria");

        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(Expected().ContentHash, load.Profile!.ContentHash);
        Assert.Equal("memoria", load.Path);
    }

    // ---- requirements: version 2, and the one rule of version 1 -------------------------------------------------------

    /// <summary><see cref="ValidJson"/> as version 2: no rule applies to it, the file says what each step requires.</summary>
    private static string Version2(string json = ValidJson) => ReplaceFirst(Lf(json), "\"version\": 1", "\"version\": 2");

    [Fact]
    public void Serialize_writes_version_2_with_the_requirements_only_of_the_steps_that_have_them()
    {
        var text = VerificationProfileFile.Serialize(ExpectedFromVersion1());

        Assert.Contains("\"version\": 2", text, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"requires\""));
        var load = VerificationProfileFile.Parse(text, "memoria");
        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(["build"], load.Profile!.Find("test")!.Requires);
        Assert.Null(load.Profile.Find("build")!.Requires);
        Assert.Equal(ExpectedFromVersion1().ContentHash, load.Profile.ContentHash);
    }

    [Fact]
    public async Task A_saved_profile_with_requirements_loads_back_with_them_and_the_same_hash()
    {
        await VerificationProfileFile.SaveAsync(_root, DotNetVerificationProfile.Propose(), Files, Ct);

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(["build"], load.Profile!.Find("test")!.Requires);
        Assert.Equal(DotNetVerificationProfile.Propose().ContentHash, load.Profile.ContentHash);
    }

    [Fact]
    public async Task A_version_2_file_is_read_as_it_is_written_and_gets_no_rule_of_version_1()
    {
        WriteProfile(Version2());
        var loaded = await VerificationProfileFile.LoadAsync(_root, Ct);
        Assert.Equal(VerificationProfileStatus.Loaded, loaded.Status);
        Assert.Null(loaded.Profile!.Find("test")!.Requires);
        Assert.Equal(Expected().ContentHash, loaded.Profile.ContentHash);

        WriteProfile(ReplaceFirst(Version2(), "\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200, \"requires\": [\"build\"]"));
        var required = await VerificationProfileFile.LoadAsync(_root, Ct);
        Assert.Equal(VerificationProfileStatus.Loaded, required.Status);
        Assert.Equal(ExpectedFromVersion1().ContentHash, required.Profile!.ContentHash);
    }

    [Fact]
    public async Task The_profile_of_the_demonstration_written_as_version_1_runs_its_build_before_its_test()
    {
        // B6, run 20261029-ff3333: the profile «dotnet» the app proposed, saved as version 1 (isolation written out), and a
        // plan whose writer named only «test». On a fresh copy of the commit, «dotnet test --no-build» found no test project.
        WriteProfile("""
            {
              "version": 1,
              "name": "dotnet",
              "steps": [
                { "id": "build", "title": "Compilar", "executable": "dotnet", "arguments": ["build"], "kind": "BuildLog",
                  "report": "None", "reportPath": null, "timeoutSeconds": 600, "isolation": "LowIntegrity" },
                { "id": "test", "title": "Pruebas", "executable": "dotnet",
                  "arguments": ["test", "--no-build", "--results-directory", "{results}", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "test.trx", "--zero-tests-policy", "strict"],
                  "kind": "TestRun", "report": "Trx", "reportPath": "{results}/test.trx", "timeoutSeconds": 1200, "isolation": "LowIntegrity" }
              ]
            }
            """);

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(["build", "test"], load.Profile!.StepsToRun(["test"]).Select(s => s.Id));
        Assert.Equal(DotNetVerificationProfile.Propose().ContentHash, load.Profile.ContentHash);
    }

    /// <summary>Where the step «build» is for a case of the rule of version 1.</summary>
    public enum BuildStep
    {
        /// <summary>Above the step, with its isolation.</summary>
        Above,

        /// <summary>Above the step, isolated while the step is not.</summary>
        AboveWithOtherIsolation,

        /// <summary>Below the step.</summary>
        Below,

        /// <summary>There is none: the build step has another id.</summary>
        None,
    }

    public static TheoryData<string, string, string, ProcessIsolation, BuildStep, bool> LegacyCases() => new()
    {
        // What the rule needs: dotnet, --no-build among its own arguments, a step «build» above it with the same isolation.
        { "the demonstration", "dotnet", "test --no-build --results-directory {results} -- --report-xunit-trx", ProcessIsolation.LowIntegrity, BuildStep.Above, true },
        { "a full path to dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", "test --no-build", ProcessIsolation.LowIntegrity, BuildStep.Above, true },
        { "both without isolation", "dotnet", "test --no-build", ProcessIsolation.None, BuildStep.Above, true },
        { "a pack of what was built", "dotnet", "pack --no-build", ProcessIsolation.LowIntegrity, BuildStep.Above, true },
        { "a test that builds", "dotnet", "test", ProcessIsolation.LowIntegrity, BuildStep.Above, false },
        { "--no-build of the test application", "dotnet", "test -- --no-build", ProcessIsolation.LowIntegrity, BuildStep.Above, false },
        { "another executable", "pwsh", "-File test.ps1 --no-build", ProcessIsolation.LowIntegrity, BuildStep.Above, false },
        { "a build with another isolation", "dotnet", "test --no-build", ProcessIsolation.None, BuildStep.AboveWithOtherIsolation, false },
        { "no build step", "dotnet", "test --no-build", ProcessIsolation.LowIntegrity, BuildStep.None, false },
        { "the build after it", "dotnet", "test --no-build", ProcessIsolation.LowIntegrity, BuildStep.Below, false },
    };

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public void The_rule_of_version_1_gives_a_step_that_does_not_build_the_build_above_it_and_nothing_else(
        string why, string executable, string arguments, ProcessIsolation isolation, BuildStep where, bool requiresBuild)
    {
        var step = Expected().Steps[1] with { Executable = executable, Arguments = arguments.Split(' '), Isolation = isolation };
        var build = Expected().Steps[0] with { Isolation = where == BuildStep.AboveWithOtherIsolation ? ProcessIsolation.LowIntegrity : isolation };
        VerificationStep[] steps = where switch
        {
            BuildStep.None => [build with { Id = "compilar" }, step],
            BuildStep.Below => [step, build],
            _ => [build, step],
        };

        var result = VerificationProfileFile.LegacyRequirements(steps);

        var requires = result.Single(s => s.Id == "test").Requires;
        Assert.True(requiresBuild ? requires is ["build"] : requires is null, $"{why}: {(requires is null ? "nothing" : string.Join(", ", requires))}");
        Assert.All(result.Where(s => s.Id != "test"), s => Assert.Null(s.Requires));
    }

    [Theory]
    [InlineData("[\"restore\"]", "not one of the steps")]
    [InlineData("[\"test\"]", "requires itself")]
    [InlineData("[\"build\", \"build\"]", "more than once")]
    [InlineData("[null]", "missing")]
    [InlineData("null", "is null")]
    public async Task A_requirement_that_breaks_a_rule_makes_the_file_invalid_and_says_which(string requires, string fragment)
    {
        WriteProfile(ReplaceFirst(Version2(), "\"timeoutSeconds\": 1200", "\"timeoutSeconds\": 1200, \"requires\": " + requires));

        var load = await VerificationProfileFile.LoadAsync(_root, Ct);

        Assert.Equal(VerificationProfileStatus.Invalid, load.Status);
        Assert.Contains(load.Problems, p => p.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void A_step_only_requires_a_step_above_it_with_its_own_isolation()
    {
        var steps = Expected().Steps;
        var after = new VerificationProfile("dotnet", [steps[0] with { Requires = ["test"] }, steps[1]]);
        var otherIsolation = new VerificationProfile("dotnet", [steps[0] with { Isolation = ProcessIsolation.None }, steps[1] with { Requires = ["build"] }]);

        Assert.Contains(VerificationRules.Check(after), p => p.Contains("comes after it", StringComparison.Ordinal));
        Assert.Contains(VerificationRules.Check(otherIsolation), p => p.Contains("another isolation", StringComparison.Ordinal));
        Assert.Empty(VerificationRules.Check(ExpectedFromVersion1()));
        Assert.Throws<ArgumentException>(() => VerificationProfileFile.Serialize(after));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // The source file may have either line ending; the edits below are written for LF.
    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReplaceFirst(string text, string find, string replace)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, $"The test edit '{find}' does not apply.");
        return text[..index] + replace + text[(index + find.Length)..];
    }
}
