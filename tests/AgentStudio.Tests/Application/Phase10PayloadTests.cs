using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/21 §5.3 (P0 of Phase 10, ADR-0067 and ADR-0068): <c>HostingPolicyGranted</c>, <c>HostingPolicyRevoked</c>, <c>HostingOperationPerformed</c> and
/// <c>RemoteStateObserved</c>. The log hashes the text of every payload, so each is pinned here as text and read back byte for byte. All four belong to no run, carry
/// no free text, no credential and nothing of the remote that is not an identifier, a number or an enum, and validate when they are built and when they are read.
/// </summary>
public sealed class Phase10PayloadTests
{
    private static readonly HostingPolicyGrant Grant = new("g-1", HostingOperationClass.PullRequestWrite, Repository, Main, "manuel", At);

    private static readonly HostingPolicyGrantedPayload Granted = HostingPolicyGrantedPayload.Granting(Grant);

    private static readonly HostingPolicyRevokedPayload Revoked = HostingPolicyRevokedPayload.Revoking("g-1", "manuel");

    private static readonly HostingOperationPerformedPayload Performed = HostingOperationPerformedPayload.Of(
        HostingOperationKind.OpenPullRequest, Run.Value, Repository, 12, "agentstudio/run-20261113-a1b2c3", Head, null, "g-1", null,
        HostingOutcome.Ok, null, RemoteLogin.TryParse("manuel"), Eid("ev-text"), HashA, null, null);

    private static readonly RemoteStateObservedPayload Observed = RemoteStateObservedPayload.Observing(
        Merged() with { Deployment = new RemoteDeployment(7, Result, DeploymentState.Succeeded), RevertCommits = [Revert] },
        Run.Value, HashA, [RemoteChangeKind.Merged, RemoteChangeKind.DeploymentChanged]);

    /// <summary>One sample of every payload type Phase 10 adds; <see cref="DecisionPayloadTests"/> adds them to the samples of every payload.</summary>
    internal static readonly DecisionPayload[] Samples = [Granted, Revoked, Performed, Observed];

    [Fact]
    public void Each_payload_is_written_in_one_fixed_text_outside_every_run_and_read_back_byte_for_byte()
    {
        Assert.Equal("""{"grantId":"g-1","class":"PullRequestWrite","provider":"GitHub","host":"github.com","owner":"LargoLujan","repository":"agent-studio","baseBranch":"main","grantedBy":"manuel","v":1,"runId":""}""", DecisionPayloadSerializer.Serialize(Granted));
        Assert.Equal("""{"grantId":"g-1","revokedBy":"manuel","v":1,"runId":""}""", DecisionPayloadSerializer.Serialize(Revoked));
        Assert.Equal("""{"operation":"OpenPullRequest","class":"PullRequestWrite","run":"20261113-a1b2c3","provider":"GitHub","host":"github.com","owner":"LargoLujan","repository":"agent-studio","pullRequest":12,"branch":"agentstudio/run-20261113-a1b2c3","commit":"9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7","grantId":"g-1","outcome":"Ok","account":"manuel","textEvidence":"ev-text","textSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","v":1,"runId":""}""", DecisionPayloadSerializer.Serialize(Performed));
        Assert.Equal("""{"provider":"GitHub","host":"github.com","owner":"LargoLujan","repository":"agent-studio","pullRequest":12,"run":"20261113-a1b2c3","fingerprint":"b1b3f6687b65fa72aa5d482fac7269b3c43eccc6d190f3ae6c6c6385eb49b2c2","previousFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","changes":["Merged","DeploymentChanged"],"snapshot":{"state":"Merged","headCommit":"9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7","baseBranch":"main","headBranchExists":false,"mergeCommit":"3333333333333333333333333333333333333333","mergeMode":"Squash","mergedBy":"manuel","originalCommits":["1111111111111111111111111111111111111111","2222222222222222222222222222222222222222"],"resultCommits":["3333333333333333333333333333333333333333"],"commitsTruncated":false,"checksRollup":"Passing","checksPassing":3,"checksFailing":0,"checksPending":0,"checksTruncated":false,"reviewsApproved":1,"reviewsChangesRequested":0,"reviewsCommented":0,"approvers":["revisora"],"forcePushes":0,"timelineTruncated":false,"revertCommits":["4444444444444444444444444444444444444444"],"deploymentId":7,"deploymentCommit":"3333333333333333333333333333333333333333","deploymentState":"Succeeded","updatedAt":"2026-11-13T10:05:00+00:00"},"v":1,"runId":""}""", DecisionPayloadSerializer.Serialize(Observed));

        foreach (var sample in Samples)
        {
            var json = DecisionPayloadSerializer.Serialize(sample);
            var read = DecisionPayloadSerializer.Deserialize(sample.GetType(), json);
            Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
            Assert.Equal(DecisionPayload.NoRun, sample.RunId);
            Assert.Equal(DecisionPayload.NoRun, read.RunId);
        }

        Assert.Equal(typeof(HostingPolicyGrantedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.HostingPolicyGranted));
        Assert.Equal(typeof(HostingPolicyRevokedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.HostingPolicyRevoked));
        Assert.Equal(typeof(HostingOperationPerformedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.HostingOperationPerformed));
        Assert.Equal(typeof(RemoteStateObservedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.RemoteStateObserved));
    }

    [Fact]
    public void A_grant_is_read_back_from_its_entry_and_the_entry_is_validated_again_as_the_log_is_read_as_data()
    {
        var read = DecisionPayloadSerializer.Deserialize<HostingPolicyGrantedPayload>(DecisionPayloadSerializer.Serialize(Granted)).ToGrant(At);

        Assert.Equal(Grant, read);
        Assert.Throws<ArgumentException>(() => (Granted with { Class = HostingOperationClass.MergePullRequest }).ToGrant(At));
        Assert.Throws<ArgumentException>(() => (Granted with { Owner = "../x" }).ToGrant(At));
        Assert.Throws<ArgumentException>(() => (Granted with { Host = "gitlab.com" }).ToGrant(At));
        Assert.Throws<ArgumentException>(() => (Granted with { BaseBranch = "a..b" }).ToGrant(At));
        Assert.Throws<ArgumentException>(() => (Granted with { Class = HostingOperationClass.PublishRunBranch }).ToGrant(At));
    }

    [Theory]
    [InlineData(HostingOperationClass.MergePullRequest)]
    [InlineData(HostingOperationClass.Destructive)]
    public void A_standing_grant_of_a_class_that_admits_none_cannot_be_recorded(HostingOperationClass @class) =>
        Assert.Throws<ArgumentException>(() => HostingPolicyGrantedPayload.Of("g-1", @class, HostingProviderKind.GitHub, "github.com", "o", "r", null, "manuel"));

    [Fact]
    public void The_fields_of_a_grant_and_of_its_revocation_are_one_line_of_identifiers()
    {
        Assert.Throws<ArgumentException>(() => HostingPolicyGrantedPayload.Of("", HostingOperationClass.PublishRunBranch, HostingProviderKind.GitHub, "github.com", "o", "r", null, "manuel"));
        Assert.Throws<ArgumentException>(() => HostingPolicyGrantedPayload.Of("g\n1", HostingOperationClass.PublishRunBranch, HostingProviderKind.GitHub, "github.com", "o", "r", null, "manuel"));
        Assert.Throws<ArgumentException>(() => HostingPolicyGrantedPayload.Of("g", HostingOperationClass.PublishRunBranch, HostingProviderKind.AzureDevOps, "github.com", "o", "r", null, "manuel"));
        Assert.Throws<ArgumentException>(() => HostingPolicyGrantedPayload.Of("g", HostingOperationClass.PublishRunBranch, HostingProviderKind.GitHub, "github.com", "o", "r", "main", "manuel"));
        Assert.Throws<ArgumentException>(() => HostingPolicyGrantedPayload.Of("g", HostingOperationClass.PublishRunBranch, HostingProviderKind.GitHub, "github.com", "o", "r", null, new string('x', 201)));
        Assert.Throws<ArgumentException>(() => HostingPolicyRevokedPayload.Revoking(" ", "manuel"));
        Assert.Throws<ArgumentException>(() => HostingPolicyRevokedPayload.Revoking("g-1", "a\tb"));
        Assert.Equal(DecisionPayload.NoRun, Revoked.RunId);
    }

    [Fact]
    public void An_operation_names_exactly_one_of_the_grant_and_the_approval_that_allowed_it()
    {
        var approved = HostingPolicyKinds.Approved();

        Assert.Null(approved.GrantId);
        Assert.Equal(41, approved.ApprovalSequence);
        Assert.Equal(HostingOperationClass.MergePullRequest, approved.Class);
        Assert.Equal(MergeMethod.Squash, approved.Method);
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(grantId: "g-1", approvalSequence: 41));
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(grantId: null, approvalSequence: null));
    }

    [Fact]
    public void A_merge_cannot_be_recorded_as_allowed_by_a_standing_grant()
    {
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(HostingOperationKind.MergePullRequest, grantId: "g-1", approvalSequence: null));
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(HostingOperationKind.RequestReviewers, grantId: "g-1", approvalSequence: null));
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(HostingOperationKind.ClosePullRequest, grantId: "g-1", approvalSequence: null));
        Assert.NotNull(HostingPolicyKinds.Operation(HostingOperationKind.PushRunBranch, grantId: "g-1", approvalSequence: null));
        Assert.NotNull(HostingPolicyKinds.Operation(HostingOperationKind.MergePullRequest, grantId: null, approvalSequence: 3));
    }

    [Fact]
    public void The_class_of_an_operation_is_the_one_of_its_kind_and_cannot_be_chosen()
    {
        foreach (var kind in Enum.GetValues<HostingOperationKind>())
        {
            var payload = HostingPolicyKinds.Operation(kind, grantId: null, approvalSequence: 1);
            Assert.Equal(HostingPolicy.ClassOf(kind), payload.Class);
        }
    }

    [Theory]
    [InlineData("commit", "abc")]
    [InlineData("commit", "9C4DA5D1F2E3A4B5C6D7E8F9A0B1C2D3E4F5A6B7")]
    [InlineData("branch", "a..b")]
    [InlineData("branch", "-x")]
    [InlineData("failure", "Credentials needed")]
    [InlineData("failure", "remote said: nope")]
    [InlineData("failure", "C:\\Users\\x\\file")]
    [InlineData("sha", "short")]
    [InlineData("verdict", "Maybe")]
    [InlineData("verdict", "approved")]
    [InlineData("pull", "0")]
    [InlineData("comment", "0")]
    public void What_is_not_an_identifier_a_code_or_a_number_does_not_become_a_payload(string field, string value)
    {
        Action build = field switch
        {
            "commit" => () => HostingPolicyKinds.Operation(commit: value),
            "branch" => () => HostingPolicyKinds.Operation(branch: value),
            "failure" => () => HostingPolicyKinds.Operation(failure: value),
            "sha" => () => HostingPolicyKinds.Operation(sha: value),
            "verdict" => () => HostingPolicyKinds.Operation(verdict: value),
            "pull" => () => HostingPolicyKinds.Operation(pull: int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => () => HostingPolicyKinds.Operation(comment: long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
        };

        Assert.Throws<ArgumentException>(build);
    }

    [Fact]
    public void The_text_that_was_sent_is_evidence_with_its_sha_or_neither_and_the_run_is_an_identifier_of_a_run()
    {
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(evidence: Eid("ev-1"), sha: null));
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(evidence: null, sha: HashA));
        Assert.Throws<ArgumentException>(() => HostingPolicyKinds.Operation(run: "a b"));
        Assert.Null(HostingPolicyKinds.Operation().TextEvidence);
        Assert.Equal(Eid("ev-text"), Performed.TextEvidence);
        Assert.Equal("manuel", Performed.Account);
        Assert.Null(HostingPolicyKinds.Operation().Account);
    }

    [Fact]
    public void An_observation_stores_the_snapshot_flat_and_gives_it_back_validated_with_its_fingerprint()
    {
        var snapshot = Merged() with { Deployment = new RemoteDeployment(7, Result, DeploymentState.Succeeded), RevertCommits = [Revert] };

        var read = DecisionPayloadSerializer.Deserialize<RemoteStateObservedPayload>(DecisionPayloadSerializer.Serialize(Observed));

        Assert.Equal(snapshot.Fingerprint, read.Fingerprint);
        Assert.Equal(HashA, read.PreviousFingerprint);
        Assert.Equal(PullRequest, read.ToPullRequest());
        Assert.Equal(snapshot.Fingerprint, read.ToSnapshot().Fingerprint);
        Assert.Equal(snapshot.UpdatedAt, read.ToSnapshot().UpdatedAt);
        Assert.Equal(snapshot, RemoteSnapshotPayload.From(snapshot).ToSnapshot(PullRequest) with { UpdatedAt = snapshot.UpdatedAt }, SnapshotComparer.ByFingerprint);
    }

    [Fact]
    public void An_observation_read_from_a_log_that_was_altered_is_refused()
    {
        var snapshot = Observed.Snapshot;

        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { HeadCommit = "abc" } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { BaseBranch = "a..b" } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { MergedBy = "bad login!" } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { Approvers = ["ok", "no no"] } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { ChecksPassing = -1 } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { DeploymentId = null } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = snapshot with { HeadCommit = Original1 } }).ToSnapshot());
        Assert.Throws<ArgumentException>(() => (Observed with { Owner = "a/b" }).ToPullRequest());
        Assert.Throws<ArgumentException>(() => (Observed with { Host = "gitlab.com" }).ToPullRequest());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Observed with { PullRequest = 0 }).ToPullRequest());
    }

    [Fact]
    public void An_observation_is_built_only_from_a_valid_snapshot_a_valid_run_and_valid_fingerprints()
    {
        Assert.Throws<ArgumentException>(() => RemoteStateObservedPayload.Observing(Open() with { HeadCommit = "abc" }, null, null, []));
        Assert.Throws<ArgumentException>(() => RemoteStateObservedPayload.Observing(Open(), "a b", null, []));
        Assert.Throws<ArgumentException>(() => RemoteStateObservedPayload.Observing(Open(), null, "not-a-fingerprint", []));
        Assert.Throws<ArgumentException>(() => RemoteStateObservedPayload.Observing(Open(), null, null, [(RemoteChangeKind)99]));
        Assert.Null(RemoteStateObservedPayload.Observing(Open(), null, null, [RemoteChangeKind.Opened]).Run);
    }

    [Fact]
    public void Every_payload_validates_as_it_is_read_and_one_that_skips_the_factories_is_caught()
    {
        foreach (var sample in Samples)
        {
            var read = DecisionPayloadSerializer.Deserialize(sample.GetType(), DecisionPayloadSerializer.Serialize(sample));
            switch (read)
            {
                case HostingPolicyGrantedPayload p: p.EnsureValid(); break;
                case HostingPolicyRevokedPayload p: p.EnsureValid(); break;
                case HostingOperationPerformedPayload p: p.EnsureValid(); break;
                case RemoteStateObservedPayload p: p.EnsureValid(); break;
                default: Assert.Fail("A payload of the phase without a validator: " + read.GetType().Name); break;
            }
        }

        // Built with the public constructor, which is what a log altered by hand amounts to.
        Assert.Throws<ArgumentException>(() => (Performed with { Class = HostingOperationClass.MergePullRequest }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { RunId = "r1" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { Run = "a b" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { Account = "bad login" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { Owner = "a/b" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { ApprovalSequence = 3 }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { Operation = HostingOperationKind.MergePullRequest, Class = HostingOperationClass.MergePullRequest }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { Operation = (HostingOperationKind)99 }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { TextSha256 = null }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Performed with { Verdict = "Maybe" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Granted with { RunId = "r1" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Revoked with { RunId = "r1" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Revoked with { GrantId = "" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Observed with { RunId = "r1" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Observed with { Run = "a b" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Observed with { Fingerprint = "short" }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Observed with { Changes = [(RemoteChangeKind)99] }).EnsureValid());
        Assert.Throws<ArgumentException>(() => (Observed with { Snapshot = null! }).EnsureValid());
    }

    [Fact]
    public void Entries_with_lists_are_equal_when_their_content_is_not_when_their_lists_are_the_same_object()
    {
        var again = RemoteStateObservedPayload.Observing(
            Merged() with { Deployment = new RemoteDeployment(7, Result, DeploymentState.Succeeded), RevertCommits = [Revert] },
            Run.Value, HashA, [RemoteChangeKind.Merged, RemoteChangeKind.DeploymentChanged]);
        var read = DecisionPayloadSerializer.Deserialize<RemoteStateObservedPayload>(DecisionPayloadSerializer.Serialize(Observed));

        Assert.Equal(Observed, again);
        Assert.Equal(Observed, read);
        Assert.Equal(Observed.GetHashCode(), read.GetHashCode());
        Assert.Equal(Observed.Snapshot, again.Snapshot);
        Assert.Equal(Observed.Snapshot.GetHashCode(), again.Snapshot.GetHashCode());
        Assert.NotEqual(Observed, Observed with { Changes = [RemoteChangeKind.Merged] });
        Assert.NotEqual(Observed.Snapshot, Observed.Snapshot with { RevertCommits = [] });
        Assert.NotEqual(Observed.Snapshot, Observed.Snapshot with { OriginalCommits = [Original2, Original1] });
    }

    [Fact]
    public void No_payload_of_the_phase_has_a_field_that_could_hold_text_of_the_remote_or_a_credential()
    {
        var forbidden = new[] { "Title", "Body", "Message", "Name", "Token", "Password", "Secret", "Credential", "Url", "Path", "Environment", "CheckName", "Error", "Stderr", "Output" };
        foreach (var type in Samples.Select(s => s.GetType()).Append(typeof(RemoteSnapshotPayload)))
        {
            Assert.DoesNotContain(type.GetProperties().Select(p => p.Name), name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Operation payloads with one field changed at a time, over a valid base.</summary>
    private static class HostingPolicyKinds
    {
        public static HostingOperationPerformedPayload Approved() => Operation(HostingOperationKind.MergePullRequest, grantId: null, approvalSequence: 41, method: MergeMethod.Squash);

        public static HostingOperationPerformedPayload Operation(
            HostingOperationKind kind = HostingOperationKind.OpenPullRequest,
            string? grantId = "g-1",
            long? approvalSequence = null,
            MergeMethod? method = null,
            string run = "20261113-a1b2c3",
            string? commit = Head,
            string? branch = "agentstudio/run-20261113-a1b2c3",
            string? failure = null,
            string? sha = null,
            string? verdict = null,
            int? pull = 12,
            long? comment = null,
            EvidenceId? evidence = null) =>
            HostingOperationPerformedPayload.Of(kind, run, Repository, pull, branch, commit, method, grantId, approvalSequence, HostingOutcome.Ok, failure, null, evidence, sha, verdict, comment);
    }

    private sealed class SnapshotComparer : IEqualityComparer<RemoteSnapshot>
    {
        public static SnapshotComparer ByFingerprint { get; } = new();

        public bool Equals(RemoteSnapshot? x, RemoteSnapshot? y) => x?.Fingerprint == y?.Fingerprint;

        public int GetHashCode(RemoteSnapshot obj) => obj.Fingerprint.GetHashCode(StringComparison.Ordinal);
    }
}
