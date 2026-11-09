using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Questions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/17 §9.3 (P0 of Phase 13): the question of an agent (<c>QuestionAsked</c>, D-29), the answer of the person
/// (<c>QuestionAnswered</c>), what the configurator applied (<c>ProjectSetupApplied</c>, D-27), the parts of the prompt in
/// <c>AgentRunStarted</c> version 3 (D-13) and the verified lock of Windows as a method of confirming an approval (D-40 (a)).
/// The log hashes the text of every payload, so each is pinned here as text, read back byte for byte, and the hash of an entry
/// that carries a new one is computed by an independent implementation of the canonical form of the log.
/// </summary>
public sealed class Phase13PayloadTests
{
    private const string Run = "20261015-a1b2c3";

    private const string Unknown = "unknown: 2 of 3 references";

    /// <summary>T3 of the executor 2 asks with two options: the system estimates the first from 6 attempts, and not the second.</summary>
    private static readonly QuestionAskedPayload Asked = new(
        Tid("T3"),
        1,
        Aid("ejecutor-2"),
        "q-T3-1",
        Eid("ev-question"),
        HashA,
        ["a", "b"],
        "a",
        [
            new OptionEstimateSnapshot(
                "a",
                MetricSnapshot.WithOrigin(MetricValue<long>.EstimatedBySystem(9000, "p25 of 6 attempts")),
                MetricSnapshot.WithOrigin(MetricValue<long>.EstimatedBySystem(15000, "p75 of 6 attempts")),
                6),
            new OptionEstimateSnapshot("b", MetricSnapshot.WithOrigin(MetricValue<long>.Missing(Unknown)), MetricSnapshot.WithOrigin(MetricValue<long>.Missing(Unknown)), 2),
        ])
    {
        RunId = Run,
    };

    private static readonly QuestionAnsweredPayload ChoseOption =
        QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q-T3-1", "a", null, null, "user", QuestionAnswerMethod.ChosenByPerson);

    private static readonly ProjectSetupAppliedPayload Setup = ProjectSetupAppliedPayload.Applying(HashA, HashB, null, null, null, "user");

    /// <summary>One sample of every payload type Phase 13 adds; <see cref="DecisionPayloadTests"/> adds them to the samples of every payload.</summary>
    internal static readonly DecisionPayload[] Samples = [Asked, ChoseOption, Setup];

    // ---- QuestionAsked (D-29) ----

    [Fact]
    public void A_question_is_written_in_one_fixed_text_with_its_evidence_its_options_and_the_estimates_with_their_origin()
    {
        var json = DecisionPayloadSerializer.Serialize(Asked);

        Assert.Equal(
            $$"""{"task":"T3","attempt":1,"agent":"ejecutor-2","question":"q-T3-1","questionEvidence":"ev-question","questionSha256":"{{HashA}}","options":["a","b"],"recommendedByAgent":"a","estimates":[{"option":"a","low":{"value":9000,"provenance":"Estimated","source":"p25 of 6 attempts","origin":"EstimatedBySystem"},"high":{"value":15000,"provenance":"Estimated","source":"p75 of 6 attempts","origin":"EstimatedBySystem"},"references":6},{"option":"b","low":{"provenance":"Unavailable","source":"{{Unknown}}","origin":"Missing"},"high":{"provenance":"Unavailable","source":"{{Unknown}}","origin":"Missing"},"references":2}],"v":1,"runId":"{{Run}}"}""",
            json);
        var read = DecisionPayloadSerializer.Deserialize<QuestionAskedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(typeof(QuestionAskedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.QuestionAsked));
        Assert.Null(read.Estimates[1].Low.Value);
    }

    /// <summary>No text of the agent goes in the payload (ADR-0010, point 5): the question is evidence, cited by its id and hash.</summary>
    [Fact]
    public void A_question_carries_no_text_of_the_agent()
    {
        Assert.DoesNotContain(typeof(QuestionAskedPayload).GetProperties(), p => p.Name is "Text" or "Why" or "Label" or "Detail");
    }

    // ---- QuestionAnswered (D-29) ----

    [Fact]
    public void An_answer_with_an_option_is_written_in_one_fixed_text()
    {
        var json = DecisionPayloadSerializer.Serialize(ChoseOption);

        Assert.Equal(
            $$"""{"task":"T3","question":"q-T3-1","chosenOption":"a","decidedBy":"user","method":"ChosenByPerson","v":1,"runId":"{{Run}}"}""",
            json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(DecisionPayloadSerializer.Deserialize<QuestionAnsweredPayload>(json)));
        Assert.Equal(typeof(QuestionAnsweredPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.QuestionAnswered));
    }

    [Fact]
    public void An_answer_in_the_persons_own_words_cites_its_evidence_and_never_its_text()
    {
        var wrote = QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q-T3-1", null, Eid("ev-answer"), HashB, "user", QuestionAnswerMethod.ChosenByPerson);

        var json = DecisionPayloadSerializer.Serialize(wrote);

        Assert.Equal(
            $$"""{"task":"T3","question":"q-T3-1","answerEvidence":"ev-answer","answerSha256":"{{HashB}}","decidedBy":"user","method":"ChosenByPerson","v":1,"runId":"{{Run}}"}""",
            json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(DecisionPayloadSerializer.Deserialize<QuestionAnsweredPayload>(json)));
    }

    [Fact]
    public void An_answer_is_an_option_or_the_persons_text_and_the_system_only_answers_with_an_option()
    {
        Assert.Throws<ArgumentException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", null, null, null, "user", QuestionAnswerMethod.ChosenByPerson));
        Assert.Throws<ArgumentException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", "a", Eid("ev"), HashA, "user", QuestionAnswerMethod.ChosenByPerson));
        Assert.Throws<ArgumentException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", null, Eid("ev"), null, "user", QuestionAnswerMethod.ChosenByPerson));
        Assert.Throws<ArgumentException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", null, Eid("ev"), "abc", "user", QuestionAnswerMethod.ChosenByPerson));
        Assert.Throws<ArgumentException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", null, Eid("ev"), HashA, "agent-studio", QuestionAnswerMethod.RecommendedByConsent));
        Assert.Throws<ArgumentException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", "a", null, null, " ", QuestionAnswerMethod.ChosenByPerson));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", "a", null, null, "user", (QuestionAnswerMethod)7));

        var byConsent = QuestionAnsweredPayload.Answering(Run, Tid("T3"), "q", "a", null, null, "agent-studio", QuestionAnswerMethod.RecommendedByConsent);
        Assert.Equal(QuestionAnswerMethod.RecommendedByConsent, byConsent.Method);
        Assert.Equal(["ChosenByPerson", "RecommendedByConsent"], Enum.GetNames<QuestionAnswerMethod>());
    }

    // ---- ProjectSetupApplied (D-27) ----

    [Fact]
    public void What_the_configurator_applied_is_written_outside_every_run_with_the_hash_of_each_thing()
    {
        var json = DecisionPayloadSerializer.Serialize(Setup);

        Assert.Equal($$"""{"rosterHash":"{{HashA}}","briefSha256":"{{HashB}}","decidedBy":"user","v":1,"runId":""}""", json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(DecisionPayloadSerializer.Deserialize<ProjectSetupAppliedPayload>(json)));
        Assert.Equal(DecisionPayload.NoRun, Setup.RunId);
        Assert.Equal(typeof(ProjectSetupAppliedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ProjectSetupApplied));
    }

    [Fact]
    public void The_configurator_records_nothing_when_it_applied_nothing_and_names_each_thing_by_its_sha256()
    {
        Assert.Throws<ArgumentException>(() => ProjectSetupAppliedPayload.Applying(null, null, null, null, null, "user"));
        Assert.Throws<ArgumentException>(() => ProjectSetupAppliedPayload.Applying("abc", null, null, null, null, "user"));
        Assert.Throws<ArgumentException>(() => ProjectSetupAppliedPayload.Applying(HashA, null, null, null, null, " "));
    }

    // ---- AgentRunStarted version 3: the parts of the prompt (D-13) ----

    [Fact]
    public void The_start_of_an_attempt_is_written_in_version_3_with_the_parts_of_its_prompt()
    {
        var payload = new AgentRunStartedPayload(
            Tid("T1"), 1, Aid("ejecutor"), ProviderId.ClaudeCode, VendorId.Anthropic, "claude-sonnet-5-5", "C:/wt/T1", Eid("ev-prompt"), HashA, [],
            AgentRunPurpose.Executor, new ContextPackReference(HashB, ["mem-1"], [], 900),
            [new ContextPartSnapshot(ContextPartKind.Protocol, 6000), new ContextPartSnapshot(ContextPartKind.Requirements, 1800), new ContextPartSnapshot(ContextPartKind.ProjectBrief, 900)])
        {
            RunId = Run,
            V = AgentRunStartedPayload.ContextPartsVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(payload);

        Assert.Equal(
            $$"""{"task":"T1","attempt":1,"agent":"ejecutor","provider":"claude-code","vendor":"anthropic","model":"claude-sonnet-5-5","workingDirectory":"C:/wt/T1","promptEvidenceId":"ev-prompt","promptSha256":"{{HashA}}","skills":[],"purpose":"Executor","contextPack":{"sha256":"{{HashB}}","entries":["mem-1"],"omitted":[],"characters":900},"contextParts":[{"kind":"Protocol","characters":6000},{"kind":"Requirements","characters":1800},{"kind":"ProjectBrief","characters":900}],"v":3,"runId":"{{Run}}"}""",
            json);
        var read = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.True(read.RecordsContextParts());
        Assert.True(read.RecordsPurpose());
        Assert.Equal(3, AgentRunStartedPayload.ContextPartsVersion);
    }

    /// <summary>A start of version 2 has no parts, and is read and written back byte for byte: the field is only written when there are parts.</summary>
    [Fact]
    public void A_start_of_version_2_is_still_written_as_it_was()
    {
        const string V2 =
            """{"task":"T1","attempt":1,"agent":"ejecutor","provider":"claude-code","vendor":"anthropic","model":"m","workingDirectory":"C:/wt/T1","promptEvidenceId":"ev-prompt","promptSha256":"x","skills":[],"purpose":"Executor","v":2,"runId":"r"}""";

        var read = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(V2);

        Assert.Equal(V2, DecisionPayloadSerializer.Serialize(read));
        Assert.Null(read.ContextParts);
        Assert.False(read.RecordsContextParts());
    }

    [Fact]
    public void The_parts_of_a_prompt_are_the_documented_ones_in_their_order()
    {
        Assert.Equal(
            ["Protocol", "Requirements", "ProjectBrief", "AgentInstructions", "Memory", "Upstream", "PreviousAttempt", "HumanAnswer", "Skills"],
            Enum.GetNames<ContextPartKind>());
    }

    // ---- ApprovalGranted version 2 with the verified lock of Windows (D-40 (a)) ----

    [Fact]
    public void An_answer_confirmed_by_the_verified_lock_of_windows_names_its_method_by_name()
    {
        var granted = new ApprovalAnsweredPayload("delivery", true, "user", "Entregar en main", Tid("T9"), ApprovalVerification.WindowsLock)
        {
            RunId = Run,
            V = ApprovalAnsweredPayload.VerificationVersion,
        };

        var json = DecisionPayloadSerializer.Serialize(granted);

        Assert.Equal(
            $$"""{"action":"delivery","approved":true,"decidedBy":"user","comment":"Entregar en main","task":"T9","verification":"WindowsLock","v":2,"runId":"{{Run}}"}""",
            json);
        Assert.Equal(ApprovalVerification.WindowsLock, DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(json).ResolvedVerification());
    }

    // ---- the hash of an entry, by an independent implementation of the canonical form ----

    /// <summary>The hash of a <c>QuestionAsked</c> entry against an independent implementation of the canonical form, and a literal computed outside the repository.</summary>
    [Fact]
    public void The_entry_of_a_question_hashes_as_the_canonical_form_of_the_log_says()
    {
        var log = new DecisionLog();
        var payload = DecisionPayloadSerializer.Serialize(Asked);
        const string Summary = "T3 pregunta a la persona: 2 opciones";

        var asked = log.Append(T0, new Actor(ActorKind.System, "agent-studio"), DecisionKind.QuestionAsked, Summary, payload);

        Assert.Equal(Independent(1, T0, "System", "agent-studio", "QuestionAsked", Summary, payload, DecisionLog.GenesisHash), asked.Hash);
        Assert.Equal("a161e6ec94309a0e951efe43d2603e2a47815a4663b404b16b155ea0116aad8d", asked.Hash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }

    private static string Independent(long sequence, DateTimeOffset at, string actorKind, string actorId, string kind, string summary, string payload, string previous)
    {
        var text = new StringBuilder();
        foreach (var field in new[] { sequence.ToString(CultureInfo.InvariantCulture), at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), actorKind, actorId, kind, summary, payload, string.Empty, previous })
        {
            text.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
