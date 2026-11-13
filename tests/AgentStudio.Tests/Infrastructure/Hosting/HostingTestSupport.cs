using System.IO;
using System.Text;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Infrastructure.Hosting.GitHub;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Infrastructure.Hosting;

/// <summary>The call a <c>gh</c> of the tests received, read from its <see cref="ProcessSpec"/>.</summary>
internal sealed record GhCallSeen(ProcessSpec Spec)
{
    public string Method => Spec.Arguments[Spec.Arguments.ToList().IndexOf("-X") + 1];

    public string Path => Spec.Arguments[^1];

    public string? IfNoneMatch => Spec.Arguments.FirstOrDefault(a => a.StartsWith("If-None-Match: ", StringComparison.Ordinal))?["If-None-Match: ".Length..];

    public string? Body => Spec.StandardInput;

    public override string ToString() => Method + " " + Path;
}

/// <summary>
/// A <c>gh</c> that starts nothing: it records every <see cref="ProcessSpec"/> and answers from a script of routes, the last one added that matches wins (a test overrides what a helper routed). The answers are written by hand in the form of <c>gh api -i</c>
/// (see <c>GitHub/Fixtures/README.md</c>). A call no route matches is recorded in <see cref="Unmatched"/> and answers like a <c>gh</c> that reached nobody, so a test can say that nothing else was asked.
/// </summary>
internal sealed class ScriptedGh : IProcessRunner
{
    private readonly List<(Func<GhCallSeen, bool> Match, Func<GhCallSeen, ProcessResult> Answer)> _routes = [];

    public List<ProcessSpec> Specs { get; } = [];

    public List<GhCallSeen> Calls => [.. Specs.Select(s => new GhCallSeen(s))];

    public List<GhCallSeen> Unmatched { get; } = [];

    /// <summary>Another process that is running when this one starts: a test of the single queue reads it.</summary>
    public int MostAtOnce { get; private set; }

    private int _running;

    public ScriptedGh Route(string method, string pathStart, Func<GhCallSeen, ProcessResult> answer)
    {
        _routes.Add((c => c.Method == method && PathMatches(c.Path, pathStart), answer));
        return this;
    }

    /// <summary>The path itself, the path with a query, or (when the route ends with a separator: <c>/</c>, <c>=</c>, <c>?</c> or <c>&amp;</c>) any path that starts with it.</summary>
    private static bool PathMatches(string path, string route) =>
        path == route || path.StartsWith(route + "?", StringComparison.Ordinal) || (route[^1] is '/' or '=' or '?' or '&' && path.StartsWith(route, StringComparison.Ordinal));

    public ScriptedGh Route(string method, string pathStart, ProcessResult answer) => Route(method, pathStart, _ => answer);

    public ScriptedGh Route(string method, string pathStart, string fixture) => Route(method, pathStart, _ => Gh.Fixture(fixture));

    /// <summary>When set, every call waits for it after it was recorded: lets a test hold a <c>gh</c> in the air and see that the next one does not start.</summary>
    public Task? Hold { get; set; }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var running = Interlocked.Increment(ref _running);
        lock (Specs)
        {
            Specs.Add(spec);
            MostAtOnce = Math.Max(MostAtOnce, running);
        }

        try
        {
            if (Hold is { } hold)
            {
                await hold;
            }

            var seen = new GhCallSeen(spec);
            foreach (var (match, answer) in Enumerable.Reverse(_routes))
            {
                if (match(seen))
                {
                    return answer(seen);
                }
            }

            lock (Specs)
            {
                Unmatched.Add(seen);
            }

            return new ProcessResult(1, string.Empty, "gh: error connecting to api.github.com", TimeSpan.Zero, false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>Answers written by hand in the form of <c>gh api -i</c>: the status line, the headers, a blank line and the body.</summary>
internal static class Gh
{
    public const string Sha1 = "1111111111111111111111111111111111111111";
    public const string Sha2 = "2222222222222222222222222222222222222222";
    public const string Sha3 = "3333333333333333333333333333333333333333";
    public const string Sha4 = "4444444444444444444444444444444444444444";
    public const string Merge = "9999999999999999999999999999999999999999";

    public static ProcessResult Http(int status, string body = "", string eol = "\r\n", params (string Name, string Value)[] headers)
    {
        var text = new StringBuilder("HTTP/2.0 ").Append(status).Append(' ').Append(status == 304 ? "Not Modified" : status < 300 ? "OK" : "Error").Append(eol);
        text.Append("Content-Type: application/json; charset=utf-8").Append(eol);
        foreach (var (name, value) in headers)
        {
            text.Append(name).Append(": ").Append(value).Append(eol);
        }

        text.Append(eol).Append(body);
        // gh api treats every status above 299 as an error: it exits with a code that is not zero and says «gh: HTTP nnn» on the standard error.
        return new ProcessResult(status > 299 ? 1 : 0, text.ToString(), status > 299 ? "gh: HTTP " + status : string.Empty, TimeSpan.Zero, false);
    }

    public static ProcessResult Ok(string body, string? etag = null) =>
        Http(200, body, "\r\n", etag is null ? [] : [("Etag", etag)]);

    public static ProcessResult NotModified() => Http(304);

    /// <summary>A fixture of <c>GitHub/Fixtures/gh-2.96.0</c> (handwritten, not recorded).</summary>
    public static ProcessResult Fixture(string name)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Hosting", "GitHub", "Fixtures", "gh-2.96.0", name));
        var status = int.Parse(text.AsSpan(text.IndexOf(' ', StringComparison.Ordinal) + 1, 3), System.Globalization.CultureInfo.InvariantCulture);
        return new ProcessResult(status > 299 ? 1 : 0, text, status > 299 ? "gh: HTTP " + status : string.Empty, TimeSpan.Zero, false);
    }

    public static string Json(params string[] objects) => "[" + string.Join(",", objects) + "]";
}

/// <summary>
/// The adapter wired as the app wires it, over a <c>gh.exe</c> that is a stand-in file, a scripted process runner, a clock of the test and the door of Auto Doctor with the version of <c>gh</c> scripted.
/// Every call goes through the real <see cref="GhRunner"/> and <see cref="GhQueue"/>.
/// </summary>
internal sealed class GhRig : IDisposable
{
    public static readonly RemoteRepository Repo = RemoteRepository.TryCreateGitHub("octo", "hello")!;
    public static readonly RemoteBranch Main = RemoteBranch.TryParse("main")!;
    public static readonly RemoteBranch RunBranch = RemoteBranch.ForRun(new RunId("run1"));
    public static readonly PullRequestRef Pr = new(Repo, 7);

    private readonly TempDirectory _temp = new();

    public GhRig(string version = "2.96.0", IReadOnlyDictionary<string, string>? ambient = null, IUntrustedRoots? roots = null, bool installed = true)
    {
        Tools = _temp.CreateDirectory("tools");
        if (installed)
        {
            _temp.WriteFile("tools/gh.exe", "stand-in");
        }

        Folders = GhFolders.Under(_temp.Combine("local"));
        Script = new ScriptedGh();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        Runner = new GhRunner(Script, Folders, roots, () => Tools, () => ambient ?? new Dictionary<string, string>(), Time);
        Queue = new GhQueue(Runner, Time);
        Door = new ScriptedDoor();
        if (installed)
        {
            Door.Says(DoctorTool.GhVersion, "gh version " + version + " (2026-07-02)\r\nhttps://github.com/cli/cli/releases/tag/v" + version + "\r\n");
        }
        else
        {
            Door.Fails(DoctorTool.GhVersion, DoctorToolOutcome.NotInstalled);
        }

        Provider = new GitHubHostingProvider(Queue, Door);
    }

    public string Tools { get; }

    public GhFolders Folders { get; }

    public ScriptedGh Script { get; }

    public FakeTimeProvider Time { get; }

    public GhRunner Runner { get; }

    public GhQueue Queue { get; }

    public ScriptedDoor Door { get; }

    public GitHubHostingProvider Provider { get; }

    public void Dispose()
    {
        Queue.Dispose();
        _temp.Dispose();
    }
}
