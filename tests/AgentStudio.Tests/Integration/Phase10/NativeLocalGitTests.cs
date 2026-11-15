using System.IO;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Integration.Phase10;

public sealed class NativeLocalGitTests
{
    [Fact]
    [Trait("Category", "LocalTools")]
    public void The_native_transport_round_trips_a_commit_when_the_bare_path_contains_spaces_percent_and_unicode()
    {
        var envelope = Path.Combine(Path.GetTempPath(), "as native transport " + Guid.NewGuid().ToString("N"));
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(envelope, "work")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(work, "private home")).FullName;
            var guard = Directory.CreateDirectory(Path.Combine(envelope, "guard outside transport")).FullName;
            var sentinel = Path.Combine(guard, "untouched.txt");
            const string sentinelText = "fixture-owned sentinel outside every repository and HOME";
            File.WriteAllText(sentinel, sentinelText);
            var sentinelWriteTime = File.GetLastWriteTimeUtc(sentinel);

            var bare = Path.Combine(work, "remote %s répo.git");
            var source = Path.Combine(work, "source");
            var clone = Path.Combine(work, "clone");
            var git = new NativeLocalGit(bare, home);
            git.Run(work, "init", "-q", "--bare", "-b", "main", bare);
            git.Run(work, "init", "-q", "-b", "main", source);
            git.Run(source, "config", "--local", "user.name", "Agent Studio Tests");
            git.Run(source, "config", "--local", "user.email", "tests@localhost");
            git.Run(source, "config", "--local", "commit.gpgsign", "false");
            git.Run(source, "config", "--local", "core.autocrlf", "false");
            const string payload = "native transport payload\n";
            File.WriteAllText(Path.Combine(source, "payload.txt"), payload);
            git.Run(source, "add", "payload.txt");
            git.Run(source, "commit", "-q", "-m", "transport round trip");
            var commit = git.Run(source, "rev-parse", "HEAD").Trim();

            git.Run(source, "push", "-q", git.Address, "HEAD:refs/heads/main");
            git.Run(work, "clone", "-q", "--branch", "main", git.Address, clone);

            Assert.Equal(commit, git.Run(bare, "rev-parse", "refs/heads/main").Trim());
            Assert.Equal(commit, git.Run(clone, "rev-parse", "HEAD").Trim());
            Assert.Equal(commit, git.Run(clone, "rev-parse", "refs/remotes/origin/main").Trim());
            Assert.Equal(commit, git.Run(source, "rev-parse", "HEAD").Trim());
            Assert.Equal("refs/heads/main", git.Run(source, "symbolic-ref", "HEAD").Trim());
            Assert.Equal(payload, File.ReadAllText(Path.Combine(clone, "payload.txt")));
            Assert.Equal(string.Empty, git.Run(source, "status", "--porcelain=v1", "--untracked-files=all"));
            Assert.Equal(sentinel, Assert.Single(Directory.EnumerateFileSystemEntries(guard)));
            Assert.Equal(sentinelText, File.ReadAllText(sentinel));
            Assert.Equal(sentinelWriteTime, File.GetLastWriteTimeUtc(sentinel));
        }
        finally
        {
            // The guard is outside the transport tree but remains in this test-owned envelope for safe cleanup.
            TempFolders.Delete(envelope);
        }
    }

    [Fact]
    public void The_child_environment_removes_all_inherited_git_controls_before_setting_the_fixture_values()
    {
        // A case-sensitive map ensures mixed-case controls are removed independently of Windows dictionary behavior.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG"] = "external-config",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = "external-hooks",
            ["GIT_CONFIG_PARAMETERS"] = "inherited-config",
            ["GIT_NAMESPACE"] = "other-namespace",
            ["GIT_EXEC_PATH"] = "other-helpers",
            ["gIt_dIr"] = "other-repository",
            ["git_trace2_event"] = "external-trace",
            ["gIt_cOnFiG_gLoBaL"] = "other-global-config",
            ["hOmE"] = "other-home",
            ["USERPROFILE"] = "other-profile",
            ["HomeDrive"] = "Z:",
            ["HOMEPATH"] = "other-home-path",
            ["FIXTURE_KEEP"] = "unchanged",
        };
        const string home = "C:/private fixture/home with spaces";

        NativeLocalGit.ConfigureEnvironment(environment, home);

        var expected = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["FIXTURE_KEEP"] = "unchanged",
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig"),
            ["GIT_ALLOW_PROTOCOL"] = "ext",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            environment.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }
}
