using System.IO;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Common;

namespace AgentStudio.Tests.Application.Semantic;

/// <summary>
/// The file the planner's MCP tool reads (docs/17 D-06): written by Agent Studio and read by the server in one strict
/// format, with each vector as the Base64 of its float32 values, exact.
/// </summary>
public sealed class ProjectHistorySnapshotJsonTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 17, 0, 0, TimeSpan.FromHours(1));
    private static readonly string Model = ContentHash.Sha256Hex("modelo");

    private static HistoryFragment Fragment(string id, float[]? vector = null) =>
        new(id, "decisión", At.AddMinutes(-1), "Decisión: el juez es de otro vendor.", vector);

    [Fact]
    public void A_snapshot_with_vectors_round_trips_exactly()
    {
        float[] vector = [0.1f, -0.25f, float.Epsilon, 1f / 3f];
        var snapshot = new ProjectHistorySnapshot(At, Model, 4, [Fragment("mem:m1#0", vector), Fragment("log:7#0")]);

        var json = ProjectHistorySnapshotJson.Write(snapshot);
        var read = ProjectHistorySnapshotJson.Read(json);

        Assert.Contains("\"schema\":\"agentstudio-history-snapshot/1\"", json, StringComparison.Ordinal);
        Assert.Equal((snapshot.At, snapshot.ModelFingerprint, snapshot.Dimensions), (read.At, read.ModelFingerprint, read.Dimensions));
        Assert.Equal(["mem:m1#0", "log:7#0"], read.Fragments.Select(f => f.Id));
        Assert.Equal(vector, read.Fragments[0].Vector);
        Assert.Null(read.Fragments[1].Vector);
        Assert.Equal((snapshot.Fragments[0].Label, snapshot.Fragments[0].At, snapshot.Fragments[0].Text), (read.Fragments[0].Label, read.Fragments[0].At, read.Fragments[0].Text));
    }

    [Fact]
    public void A_snapshot_without_vectors_needs_no_model()
    {
        var read = ProjectHistorySnapshotJson.Read(ProjectHistorySnapshotJson.Write(new ProjectHistorySnapshot(At, null, 0, [Fragment("mem:m1#0")])));

        Assert.Null(read.ModelFingerprint);
        Assert.Single(read.Fragments);
    }

    public static TheoryData<string> NotSnapshots => new()
    {
        "no es JSON",
        "null",
        "{\"schema\":\"agentstudio-skills-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"fragments\":[]}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\"}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"fragments\":[null]}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"fragments\":[{\"id\":\"a\",\"label\":\"x\",\"at\":\"2026-10-30T17:00:00+01:00\",\"text\":\"t\"},{\"id\":\"a\",\"label\":\"x\",\"at\":\"2026-10-30T17:00:00+01:00\",\"text\":\"t\"}]}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"fragments\":[{\"id\":\"a\",\"label\":\"x\",\"at\":\"2026-10-30T17:00:00+01:00\"}]}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"fragments\":[{\"id\":\"a\",\"label\":\"x\",\"at\":\"2026-10-30T17:00:00+01:00\",\"text\":\"t\",\"vector\":\"AAAAAA==\"}]}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"modelFingerprint\":\"" + ContentHash.Sha256Hex("modelo") + "\",\"dimensions\":2,\"fragments\":[{\"id\":\"a\",\"label\":\"x\",\"at\":\"2026-10-30T17:00:00+01:00\",\"text\":\"t\",\"vector\":\"AAAAAA==\"}]}",
        "{\"schema\":\"agentstudio-history-snapshot/1\",\"at\":\"2026-10-30T17:00:00+01:00\",\"modelFingerprint\":\"" + ContentHash.Sha256Hex("modelo") + "\",\"dimensions\":1,\"fragments\":[{\"id\":\"a\",\"label\":\"x\",\"at\":\"2026-10-30T17:00:00+01:00\",\"text\":\"t\",\"vector\":\"no-base64!\"}]}",
    };

    [Theory]
    [MemberData(nameof(NotSnapshots))]
    public void Reading_is_strict(string json) => Assert.Throws<InvalidDataException>(() => ProjectHistorySnapshotJson.Read(json));

    [Fact]
    public void A_snapshot_out_of_the_format_is_not_written()
    {
        Assert.Throws<ArgumentException>(() => ProjectHistorySnapshotJson.Write(new ProjectHistorySnapshot(At, Model, 2, [Fragment("a", [1f])])));
        Assert.Throws<ArgumentException>(() => ProjectHistorySnapshotJson.Write(new ProjectHistorySnapshot(At, null, 1, [Fragment("a", [1f])])));
        Assert.Throws<ArgumentException>(() => ProjectHistorySnapshotJson.Write(new ProjectHistorySnapshot(At, null, 0, [Fragment("a") with { Text = new string('x', 1501) }])));
        Assert.Throws<ArgumentException>(() => ProjectHistorySnapshotJson.Write(new ProjectHistorySnapshot(
            At, null, 0, [.. Enumerable.Range(0, ProjectHistoryNames.MaxFragments + 1).Select(i => Fragment($"f{i}"))])));
    }

    [Fact]
    public void The_tool_has_the_name_and_limits_of_the_design()
    {
        Assert.Equal(("search_project_history", "--history", 10), (ProjectHistoryNames.Tool, ProjectHistoryNames.SnapshotArgument, ProjectHistoryNames.MaxTop));
    }
}
