using System.Text;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class EvidenceRecordTests
{
    private static EvidenceRecord Make(string hash, EvidenceKind kind = EvidenceKind.CommandOutput, TestRunSummary? run = null, EvidenceOrigin origin = EvidenceOrigin.CapturedBySystem) =>
        new(Eid("e"), kind, origin, "title", "producer", T0, hash, run);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void Hashes_of_the_wrong_length_are_rejected(string hash)
    {
        Violation(() => Make(hash));
    }

    [Fact]
    public void Hashes_with_uppercase_or_non_hex_characters_are_rejected()
    {
        Violation(() => Make(new string('A', 64)));
        Violation(() => Make(new string('g', 64)));
        Violation(() => Make(new string('a', 63)));
        Violation(() => Make(new string('a', 65)));
    }

    [Fact]
    public void A_valid_lowercase_hex_hash_is_accepted()
    {
        Assert.Equal(HashA, Make(HashA).ContentSha256);
    }

    [Fact]
    public void A_null_hash_is_rejected_as_a_domain_violation()
    {
        Violation(() => Make(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void The_subject_commit_is_a_full_git_object_id_or_nothing(string? commit)
    {
        var record = new EvidenceRecord(Eid("e"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test", "agent-studio", T0, HashA, Green, subjectCommit: commit);

        Assert.Equal(commit, record.SubjectCommit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HEAD")]
    [InlineData("abc123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    public void A_subject_commit_that_is_not_a_full_object_id_is_refused(string commit)
    {
        Violation(() => new EvidenceRecord(Eid("e"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test", "agent-studio", T0, HashA, Green, subjectCommit: commit));
    }

    [Fact]
    public void TestRun_evidence_requires_a_summary()
    {
        Violation(() => Make(HashA, EvidenceKind.TestRun));
        Assert.NotNull(Make(HashA, EvidenceKind.TestRun, Green).TestRun);
    }

    [Fact]
    public void Matches_accepts_the_original_content_and_detects_any_alteration()
    {
        var content = Encoding.UTF8.GetBytes("test output: 10 passed");
        var record = Make(ContentHash.Sha256Hex(content));

        Assert.True(record.Matches(content));
        Assert.False(record.Matches(Encoding.UTF8.GetBytes("test output: 10 passed.")));
        Assert.False(record.Matches([]));
    }

    [Fact]
    public void Flipping_any_single_byte_is_detected()
    {
        var content = Encoding.UTF8.GetBytes("abcdefghij");
        var record = Make(ContentHash.Sha256Hex(content));
        for (var i = 0; i < content.Length; i++)
        {
            var altered = (byte[])content.Clone();
            altered[i] ^= 0x01;
            Assert.False(record.Matches(altered), $"byte {i}");
        }
    }

    [Theory]
    [InlineData(10, 0, 0, 0, true)]
    [InlineData(10, 0, 3, 0, true)] // skipped tests do not fail the run
    [InlineData(0, 0, 0, 0, false)] // nothing executed proves nothing
    [InlineData(0, 0, 5, 0, false)] // everything skipped is still nothing executed
    [InlineData(10, 1, 0, 0, false)]
    [InlineData(10, 0, 0, 1, false)]
    [InlineData(10, 0, 0, -1, false)]
    [InlineData(0, 2, 0, 1, false)]
    public void TestRunSummary_Succeeded_requires_executed_tests_no_failures_and_exit_code_zero(int passed, int failed, int skipped, int exitCode, bool expected)
    {
        Assert.Equal(expected, new TestRunSummary(passed, failed, skipped, exitCode).Succeeded);
    }

    [Theory]
    [InlineData(EvidenceOrigin.CapturedBySystem, true)]
    [InlineData(EvidenceOrigin.ProvidedByHuman, true)]
    [InlineData(EvidenceOrigin.ReportedByAgent, false)]
    public void Only_system_captured_or_human_provided_evidence_is_objective(EvidenceOrigin origin, bool objective)
    {
        Assert.Equal(objective, Make(HashA, origin: origin).IsObjective);
    }
}
