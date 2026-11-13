using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Hosting.State;
using AgentStudio.Tests.Domain.Hosting;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Hosting.State;

/// <summary>The local state of the remote (docs/21 §6): read as not trusted, written atomically, and it authorises nothing (F10-6).</summary>
public sealed class RemoteSyncStoreTests : IDisposable
{
    private const string Project = "3f1c2a64-0000-4000-8000-000000000001";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "as-sync-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempFolders.Delete(_root);

    private RemoteSyncStore Store() => new(new RemoteSyncStoreOptions(_root));

    private string File(string name) => Path.Combine(_root, Project, name);

    private static FollowedPullRequest Followed(RemoteSnapshot? last = null, RemoteSnapshot? pending = null) => new(
        HostingP0Data.PullRequest,
        HostingP0Data.Run.Value,
        new ObservationCursor(new Dictionary<ObservedResource, string> { [ObservedResource.PullRequest] = "\"abc\"" }, last),
        new RemoteDisplayTexts("A title", ["build"], "production"),
        pending);

    [Fact]
    public async Task WithoutFilesTheStateIsEmpty()
    {
        var state = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Empty(state.Followed);
        Assert.Empty(state.Pending);
        Assert.Null(state.Repository);
        Assert.Null(state.RateLimitedUntil);
    }

    [Fact]
    public async Task WhatIsSavedIsLoadedBack()
    {
        var work = new PendingRemoteWork(
            new RemoteWorkTicket("t1", HostingOperationKind.MergePullRequest, HostingP0Data.PullRequest),
            new HostingOperation(new ProjectContext(Project, @"C:\work"), HostingP0Data.Run, HostingOperationKind.MergePullRequest, HostingP0Data.Repository, HostingP0Data.Main, HostingP0Data.PullRequest, HostingP0Data.Head, MergeMethod.Squash),
            HostingP0Data.Open(),
            "note:merge",
            PendingReason.ProjectBusy,
            HostingP0Data.At,
            HostingP0Data.At.AddMinutes(1));
        var saved = new RemoteSyncState(HostingP0Data.Repository, [Followed(HostingP0Data.Open(), HostingP0Data.Merged())], [work], HostingP0Data.At.AddHours(1), HostingP0Data.At, HostingOutcome.Ok, HostingP0Data.At);

        await Store().SaveAsync(Project, saved, CancellationToken.None);
        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Equal(HostingP0Data.Repository, loaded.Repository);
        var follow = Assert.Single(loaded.Followed);
        Assert.Equal(HostingP0Data.PullRequest, follow.PullRequest);
        Assert.Equal(HostingP0Data.Open().Fingerprint, follow.Cursor.LastSnapshot!.Fingerprint);
        Assert.Equal(HostingP0Data.Merged().Fingerprint, follow.PendingConfirmation!.Fingerprint);
        Assert.Equal("\"abc\"", follow.Cursor.ETags[ObservedResource.PullRequest]);
        Assert.Equal("A title", follow.Display.Title);
        var parked = Assert.Single(loaded.Pending);
        Assert.Equal("t1", parked.Ticket.Id);
        Assert.Equal(HostingOperationKind.MergePullRequest, parked.Operation!.Kind);
        Assert.Equal(MergeMethod.Squash, parked.Operation.Method);
        Assert.Equal(string.Empty, parked.Operation.Project.RootPath);
        Assert.Equal(PendingReason.ProjectBusy, parked.Reason);
        Assert.Equal(saved.RateLimitedUntil, loaded.RateLimitedUntil);
        Assert.Equal(HostingOutcome.Ok, loaded.LastOutcome);
    }

    [Fact]
    public async Task TheWriteLeavesNoTemporaryFile()
    {
        await Store().SaveAsync(Project, RemoteSyncState.Empty, CancellationToken.None);
        await Store().SaveAsync(Project, RemoteSyncState.Empty, CancellationToken.None);

        var names = Directory.GetFiles(Path.Combine(_root, Project)).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["pending.json", "sync.json"], names);
    }

    [Fact]
    public async Task AProjectIdThatIsNotAFolderNameIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().LoadAsync(@"..\x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Store().SaveAsync("a/b", RemoteSyncState.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task APullRequestOfAnotherRepositoryThatDoesNotParseIsLeftOutAndTheRestIsKept()
    {
        await Store().SaveAsync(Project, new RemoteSyncState(HostingP0Data.Repository, [Followed()], [], null, null, null), CancellationToken.None);
        var json = System.IO.File.ReadAllText(File("sync.json"));
        // A second followed entry with an identifier that is not one: spaces, a path, a flag.
        var tampered = json.Replace("\"Followed\":[", "\"Followed\":[{\"PullRequest\":\"github.com/--admin/x y#1\",\"Run\":null,\"ETags\":{},\"LastSnapshot\":null,\"Display\":null,\"PendingConfirmation\":null},{\"PullRequest\":\"../../etc#4\"},", StringComparison.Ordinal);
        System.IO.File.WriteAllText(File("sync.json"), tampered);

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Equal(HostingP0Data.PullRequest, Assert.Single(loaded.Followed).PullRequest);
    }

    [Fact]
    public async Task HugeCursorsAndAbsurdDatesAreCutOrDropped()
    {
        var huge = new string('x', 100_000);
        var json = $$"""
            {"V":1,"Repository":"github.com/LargoLujan/agent-studio","Followed":[{"PullRequest":"github.com/LargoLujan/agent-studio#12","Run":"20261113-a1b2c3",
            "ETags":{"PullRequest":"{{huge}}","Commits":"\"ok\"","Nonsense":"\"x\"","Reviews":"a\r\nX-Evil: 1"},"LastSnapshot":null,
            "Display":{"Title":"{{huge}}","FailingCheckNames":["a\u001b[31mred","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v"],"DeploymentEnvironment":"prod\u0007"},
            "PendingConfirmation":null}],
            "RateLimitedUntil":"9999-12-31T00:00:00+00:00","LastGoodSync":"1900-01-01T00:00:00+00:00","LastOutcome":"Nonsense","LastOutcomeAt":null}
            """;
        Directory.CreateDirectory(Path.Combine(_root, Project));
        System.IO.File.WriteAllText(File("sync.json"), json);

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        var follow = Assert.Single(loaded.Followed);
        Assert.Equal(["\"ok\""], follow.Cursor.ETags.Values);
        Assert.Equal(ObservedResource.Commits, Assert.Single(follow.Cursor.ETags).Key);
        Assert.True(follow.Display.Title!.Length <= 256);
        Assert.True(follow.Display.FailingCheckNames.Count <= 20);
        Assert.Equal("a[31mred", follow.Display.FailingCheckNames[0]);
        Assert.Equal("prod", follow.Display.DeploymentEnvironment);
        Assert.Null(loaded.RateLimitedUntil);
        Assert.Null(loaded.LastGoodSync);
        Assert.Null(loaded.LastOutcome);
    }

    [Fact]
    public async Task AFileOverTheLimitIsAsIfThereWereNone()
    {
        Directory.CreateDirectory(Path.Combine(_root, Project));
        System.IO.File.WriteAllText(File("sync.json"), "{\"V\":1,\"Repository\":\"github.com/a/b\",\"Pad\":\"" + new string('x', RemoteSyncStore.MaxFileBytes) + "\"}");

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Null(loaded.Repository);
    }

    [Fact]
    public async Task AFileThatIsNotJsonOrIsTooDeepIsAsIfThereWereNone()
    {
        Directory.CreateDirectory(Path.Combine(_root, Project));
        System.IO.File.WriteAllText(File("sync.json"), "not json at all");
        System.IO.File.WriteAllText(File("pending.json"), new StringBuilder().Insert(0, "[", 5000).ToString());

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Empty(loaded.Followed);
        Assert.Empty(loaded.Pending);
    }

    [Fact]
    public async Task AParkedItemWithAStageOrATicketThatBreaksTheRulesIsLeftOut()
    {
        var good = new PendingRemoteWork(new RemoteWorkTicket("t-ok", null, null), null, HostingP0Data.Open(), "observe", PendingReason.ProjectBusy, HostingP0Data.At, null);
        await Store().SaveAsync(Project, new RemoteSyncState(null, [], [good, good with { Ticket = new RemoteWorkTicket("t 2", null, null) }, good with { Ticket = new RemoteWorkTicket("t-3", null, null), Stage = "Bad Stage!" }], null, null, null), CancellationToken.None);

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Equal("t-ok", Assert.Single(loaded.Pending).Ticket.Id);
    }

    [Fact]
    public async Task ASnapshotThatDoesNotValidateIsLeftOut()
    {
        await Store().SaveAsync(Project, new RemoteSyncState(null, [Followed(HostingP0Data.Open())], [], null, null, null), CancellationToken.None);
        var text = System.IO.File.ReadAllText(File("sync.json"));
        using var document = JsonDocument.Parse(text);
        var head = document.RootElement.GetProperty("Followed")[0].GetProperty("LastSnapshot").GetProperty("HeadCommit").GetString()!;
        System.IO.File.WriteAllText(File("sync.json"), text.Replace(head, "not-a-commit", StringComparison.Ordinal));

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Null(Assert.Single(loaded.Followed).Cursor.LastSnapshot);
    }

    [Fact]
    public async Task AFileThatIsALinkIsNotFollowed()
    {
        var elsewhere = Path.Combine(_root, "elsewhere.json");
        Directory.CreateDirectory(Path.Combine(_root, Project));
        System.IO.File.WriteAllText(elsewhere, "{\"V\":1,\"Repository\":\"github.com/LargoLujan/agent-studio\",\"Followed\":[]}");
        try
        {
            System.IO.File.CreateSymbolicLink(File("sync.json"), elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A symbolic link needs a privilege this machine did not give: the rule is checked where it can be made.
            return;
        }

        var loaded = await Store().LoadAsync(Project, CancellationToken.None);

        Assert.Null(loaded.Repository);
        await Assert.ThrowsAsync<IOException>(() => Store().SaveAsync(Project, RemoteSyncState.Empty, CancellationToken.None));
    }

    [Fact]
    public void TheStateHoldsNothingThatAuthorises()
    {
        // The records of the local state have no grant, no approval and no secret: what they carry is repositories, pull requests, cursors, texts for the screen and parked work.
        var names = new[] { typeof(RemoteSyncState), typeof(FollowedPullRequest), typeof(PendingRemoteWork) }
            .SelectMany(t => t.GetProperties().Select(p => p.Name))
            .ToList();
        Assert.DoesNotContain(names, n => n.Contains("Grant", StringComparison.Ordinal) || n.Contains("Approval", StringComparison.Ordinal) || n.Contains("Token", StringComparison.Ordinal));
    }
}
