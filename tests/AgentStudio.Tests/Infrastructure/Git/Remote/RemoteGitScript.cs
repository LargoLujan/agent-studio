using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>What a scripted <c>git</c> was asked: the sub-command of a launched <see cref="ProcessSpec"/>.</summary>
internal static class RemoteGitCommands
{
    /// <summary><c>config</c>, <c>get-url</c> (an <c>ls-remote --get-url</c>), <c>ls-remote</c> or <c>push</c>: the first argument that is not a <c>-c</c> pair, told apart for <c>get-url</c>.</summary>
    public static string Kind(ProcessSpec spec)
    {
        var index = 0;
        while (index + 1 < spec.Arguments.Count && spec.Arguments[index] == "-c")
        {
            index += 2;
        }

        var command = index < spec.Arguments.Count ? spec.Arguments[index] : string.Empty;
        return command == "ls-remote" && spec.Arguments.Contains("--get-url") ? "get-url" : command;
    }

    /// <summary>The arguments after the pairs of <c>-c</c>: the command itself.</summary>
    public static IReadOnlyList<string> CommandOf(ProcessSpec spec)
    {
        var index = 0;
        while (index + 1 < spec.Arguments.Count && spec.Arguments[index] == "-c")
        {
            index += 2;
        }

        return [.. spec.Arguments.Skip(index)];
    }

    /// <summary>The <c>-c</c> settings of a spec, as <c>key=value</c>.</summary>
    public static IReadOnlyList<string> Settings(ProcessSpec spec)
    {
        var settings = new List<string>();
        for (var index = 0; index + 1 < spec.Arguments.Count && spec.Arguments[index] == "-c"; index += 2)
        {
            settings.Add(spec.Arguments[index + 1]);
        }

        return settings;
    }

    /// <summary>One entry of <c>git config -z --show-scope --show-origin --get-regexp</c>, as git writes it.</summary>
    public static string ConfigEntry(string scope, string origin, string key, string value) => $"{scope}\0{origin}\0{key}\n{value}\0";
}

/// <summary>
/// A <c>git</c> that is only a script (D-05 and D-06 tested without starting anything): it answers the query of the configuration, the question of the address, <c>ls-remote</c> and <c>push</c> from what
/// the test says, and records every <see cref="ProcessSpec"/>. Nothing waits on a clock: a timeout is an answer with <see cref="ProcessResult.TimedOut"/>.
/// </summary>
internal sealed class RemoteGitScript : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessSpec> _specs = [];

    /// <summary>The output of the query of the configuration; empty means «no key matched» (exit code 1).</summary>
    public string ConfigOutput { get; set; } = string.Empty;

    public ProcessResult? ConfigResult { get; set; }

    /// <summary>What the question of the address answers; null answers the address it was given (nothing rewritten).</summary>
    public string? GetUrlOutput { get; set; }

    public ProcessResult? GetUrlResult { get; set; }

    /// <summary>The answers of the successive <c>ls-remote</c>: the last one is repeated.</summary>
    public List<ProcessResult> LsRemote { get; } = [];

    public ProcessResult PushResult { get; set; } = Ok(string.Empty);

    /// <summary>What the local <c>git grep</c> of the attributes of the commit answers; null is «no line says filter=lfs» (exit code 1).</summary>
    public ProcessResult? GrepResult { get; set; }

    /// <summary>The answer of a <c>git grep</c> that found a <c>.gitattributes</c> with <c>filter=lfs</c> in the commit.</summary>
    public static ProcessResult LfsFound(string commit = RemoteGitFixture.Commit) => Ok(commit + ":.gitattributes\n");

    public IReadOnlyList<ProcessSpec> Specs
    {
        get
        {
            lock (_gate)
            {
                return [.. _specs];
            }
        }
    }

    public IReadOnlyList<string> Kinds => [.. Specs.Select(RemoteGitCommands.Kind)];

    public static ProcessResult Ok(string output) => new(0, output, string.Empty, TimeSpan.Zero, false);

    public static ProcessResult Failure(int exit, string error) => new(exit, string.Empty, error, TimeSpan.Zero, false);

    public static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty, TimeSpan.Zero, true);

    public static ProcessResult TooLarge() => new(-1, "x", string.Empty, TimeSpan.Zero, false, true);

    /// <summary>A line of <c>ls-remote</c> for the branch of a run.</summary>
    public static string BranchLine(string commit, string runId = "20261015-a1b2c3") => $"{commit}\trefs/heads/agentstudio/run-{runId}\n";

    /// <summary>A line of <c>push --porcelain</c> for a refspec.</summary>
    public static string PorcelainOutput(string flag, string refspec, string summary, string address = "https://github.com/o/r.git") =>
        $"To {address}\n{flag}\t{refspec}\t{summary}\nDone\n";

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _specs.Add(spec);
        }

        return Task.FromResult(Answer(spec));
    }

    private ProcessResult Answer(ProcessSpec spec)
    {
        switch (RemoteGitCommands.Kind(spec))
        {
            case "config":
                return ConfigResult ?? (ConfigOutput.Length == 0 ? new ProcessResult(1, string.Empty, string.Empty, TimeSpan.Zero, false) : Ok(ConfigOutput));
            case "get-url":
                return GetUrlResult ?? Ok((GetUrlOutput ?? spec.Arguments[^1]) + "\n");
            case "ls-remote":
                lock (_gate)
                {
                    var asked = _specs.Count(s => RemoteGitCommands.Kind(s) == "ls-remote");
                    return LsRemote.Count == 0 ? Ok(string.Empty) : LsRemote[Math.Min(asked, LsRemote.Count) - 1];
                }

            case "grep":
                // No match (exit code 1) unless the test says the attributes of the commit say filter=lfs.
                return GrepResult ?? new ProcessResult(1, string.Empty, string.Empty, TimeSpan.Zero, false);
            case "push":
                return PushResult;
            default:
                throw new InvalidOperationException("The executor launched a command that is not in its closed list: " + string.Join(' ', RemoteGitCommands.CommandOf(spec)));
        }
    }
}

/// <summary>A locator that finds the git of the fixture, and says whether it is inside an untrusted root.</summary>
internal sealed class FixedLocator(string? path, bool untrusted = false) : IExecutableLocator
{
    public string? Find(string command) => command == "git" ? path : null;

    public bool IsUntrusted(string candidate) => untrusted;
}

/// <summary>
/// A folder with a repository that is only a <c>.git</c> folder, a <c>git.exe</c> that is an empty file (the script never starts it), and the folders of the executor: everything under one temporary folder.
/// </summary>
internal sealed class RemoteGitFixture : IDisposable
{
    public const string Commit = "1111111111111111111111111111111111111111";
    public const string OtherCommit = "2222222222222222222222222222222222222222";
    public const string RunIdValue = "20261015-a1b2c3";

    private readonly string _container = Path.Combine(Path.GetTempPath(), "as remoto tést " + Guid.NewGuid().ToString("N")[..10]);

    public RemoteGitFixture()
    {
        Root = Path.Combine(_container, "repo");
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        GitPath = Path.Combine(_container, "gitbin", "git.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(GitPath)!);
        File.WriteAllBytes(GitPath, []);
        WorktreeRoot = Path.Combine(_container, "wt");
        Folders = RemoteGitFolders.Under(Path.Combine(_container, "local"));
    }

    public string Root { get; }

    public string GitPath { get; }

    public string WorktreeRoot { get; }

    public RemoteGitFolders Folders { get; }

    public static RemoteRepository Repository { get; } = RemoteRepository.TryCreateGitHub("o", "r")!;

    public static RunId Run { get; } = new(RunIdValue);

    public static string Refspec => $"{Commit}:refs/heads/agentstudio/run-{RunIdValue}";

    public ProjectContext Project => new("0f8fad5b-d9cb-469f-a165-70867728950e", Root);

    public RemotePush Push(string? commit = null) => new(Project, Repository, Run, commit ?? Commit);

    public RemoteGitRunner NewRunner(
        IProcessRunner process,
        RemoteTransport? transport = null,
        IExecutableLocator? locator = null,
        Func<IReadOnlyDictionary<string, string>>? ambient = null) =>
        new(process, locator ?? new FixedLocator(GitPath), Folders, WorktreeRoot, transport ?? RemoteTransport.GitHubHttps, ambient ?? (() => new Dictionary<string, string>()));

    public void Dispose() => TempFolders.Delete(_container);
}
