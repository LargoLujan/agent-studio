using System.IO;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// What a step runs is shown to the person before it runs (docs/24, wave 1): an executable, an argument or a report path with a character
/// that cannot be seen as it is (the marks and isolates of direction, the zero-width ones, the byte-order mark, the soft hyphen, the
/// tag characters, DEL and the C1 controls, the line and paragraph separators, half of a surrogate pair) is refused, so a profile in the
/// repository cannot show one command and run another. The characters are given by code point so that none sits in this source.
/// </summary>
public sealed class VerificationCommandRulesTests
{
    private static VerificationStep Step(string executable = "dotnet", string argument = "build", string? report = null) =>
        report is null
            ? new("build", "Compilar", executable, [argument], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(1))
            : new("test", "Pruebas", executable, [argument], EvidenceKind.TestRun, TestReportFormat.JUnit, report, TimeSpan.FromMinutes(1));

    private static string With(string text, int codePoint) => text + char.ConvertFromUtf32(codePoint) + "x";

    public static TheoryData<int> Invisible() =>
    [
        0x202E, // right-to-left override
        0x202A, // left-to-right embedding
        0x2066, // left-to-right isolate
        0x2069, // pop directional isolate
        0x200E, // left-to-right mark
        0x200B, // zero-width space
        0x200C, // zero-width non-joiner
        0x200D, // zero-width joiner
        0x2060, // word joiner
        0xFEFF, // byte-order mark
        0x00AD, // soft hyphen
        0x061C, // Arabic letter mark
        0xE0041, // tag latin capital A
        0x007F, // DEL
        0x0085, // next line (C1)
        0x2028, // line separator
        0x2029, // paragraph separator
    ];

    [Theory]
    [MemberData(nameof(Invisible))]
    public void An_invisible_or_direction_character_is_refused_in_the_executable_an_argument_and_the_report_path(int codePoint)
    {
        Assert.Contains(VerificationRules.Check(Step(executable: With("dotnet", codePoint))), p => p.Contains("executable contains an invisible or direction character", StringComparison.Ordinal));
        Assert.Contains(VerificationRules.Check(Step(argument: With("build", codePoint))), p => p.Contains("argument 0 contains an invisible or direction character", StringComparison.Ordinal));
        Assert.Contains(VerificationRules.Check(Step(argument: "--report={results}/t.xml", report: With("{results}/t.xml", codePoint))), p => p.Contains("report path contains an invisible or direction character", StringComparison.Ordinal));
    }

    [Fact]
    public void Half_of_a_surrogate_pair_is_refused_and_a_whole_one_is_not()
    {
        Assert.NotEmpty(VerificationRules.Check(Step(argument: "build" + '\uD83D')));
        Assert.NotEmpty(VerificationRules.Check(Step(argument: '\uDE00' + "build")));
        Assert.Empty(VerificationRules.Check(Step(argument: "build-" + char.ConvertFromUtf32(0x1F600))));
    }

    [Fact]
    public void Visible_text_of_any_script_and_the_control_characters_keep_their_own_messages()
    {
        Assert.Empty(VerificationRules.Check(Step(argument: "compilación")));
        Assert.Empty(VerificationRules.Check(Step(executable: @"C:\Program Files\dotnet\dotnet.exe", argument: "ビルド")));
        Assert.Contains(VerificationRules.Check(Step(argument: "build\n--evil")), p => p.Contains("control character", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_profile_file_with_a_hidden_character_in_a_command_does_not_load()
    {
        var root = Directory.CreateTempSubdirectory("as-rules-").FullName;
        try
        {
            var profile = new VerificationProfile("dotnet", [Step(argument: "build-PLACEHOLDER")]);
            var json = VerificationProfileFile.Serialize(profile).Replace("-PLACEHOLDER", "\u202Etxt.exe", StringComparison.Ordinal);
            Directory.CreateDirectory(Path.Combine(root, ".agentstudio"));
            await File.WriteAllTextAsync(Path.Combine(root, ".agentstudio", VerificationProfileFile.FileName), json, TestContext.Current.CancellationToken);

            var load = await VerificationProfileFile.LoadAsync(root, TestContext.Current.CancellationToken);

            Assert.Equal(VerificationProfileStatus.Invalid, load.Status);
            Assert.Contains(load.Problems, p => p.Contains("invisible or direction character", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
