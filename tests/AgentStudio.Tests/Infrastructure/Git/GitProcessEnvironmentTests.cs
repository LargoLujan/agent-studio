using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Tests that change the environment of the test process. They run alone, after every parallel test, because each git
/// started meanwhile by another test would inherit the variables.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>Agent Studio may be started from a shell or a hook whose environment points git elsewhere.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
[Trait("Category", "LocalTools")]
public sealed class GitProcessEnvironmentTests : GitManagerTestBase
{
    [Fact]
    public async Task Inherited_variables_that_redirect_git_or_inject_configuration_are_ignored()
    {
        var manager = NewManager();
        Repo.Write("notas.txt", "sin seguimiento\n");
        var before = Repo.Snapshot();
        var variables = new Dictionary<string, string>
        {
            ["GIT_DIR"] = Path.Combine(Repo.Root, ".git"),
            ["GIT_WORK_TREE"] = Repo.Root,
            ["GIT_INDEX_FILE"] = Path.Combine(Repo.Root, ".git", "index"),
            ["GIT_CEILING_DIRECTORIES"] = Repo.Root,
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "i18n.commitEncoding",
            ["GIT_CONFIG_VALUE_0"] = "ISO-8859-1",
        };

        // Nothing inside the scope may run git outside the manager: it would inherit the variables.
        string commit;
        using (new EnvironmentScope(variables))
        {
            var state = await manager.InspectAsync(Path.Combine(Repo.Root, "src", "Calc"), Ct);
            Assert.True(SamePath(Repo.Root, state.Root));

            var lease = await EnsureAsync(manager, "T1");
            commit = await CommitInAsync(manager, lease, "nuevo.txt", "x\n");
            await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);
            Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: false, Ct));
        }

        Assert.DoesNotContain("encoding ", Repo.Run("cat-file", "-p", commit), StringComparison.Ordinal);
        Assert.Equal("nuevo.txt", Repo.Run("diff-tree", "--no-commit-id", "--name-only", "-r", commit).Trim());
        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task An_inherited_GIT_CONFIG_does_not_blind_the_callback_inspection_of_an_internal_command()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "as-git-config-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(scratch);
        try
        {
            // A benign configuration file with no callback, and the mark an agent script would leave if it ran.
            var benign = Path.Combine(scratch, "benign.cfg");
            File.WriteAllText(benign, "[user]\n\tname = x\n");
            var mark = Path.Combine(scratch, "ran.txt");

            // The user's repository has a filtered file and a refused filter (an interpreter running a repository script).
            Repo.Write(".gitattributes", "*.fmt filter=format\n");
            Repo.Write("tools/filter.sh", "#!/bin/sh\ncat\n");
            Repo.Write("doc.fmt", "contenido\n");
            Repo.Run("add", "-A");
            Repo.Run("commit", "-q", "-m", "filtro");
            var head = Repo.Head;

            var manager = NewManager();
            var lease = await EnsureAsync(manager, "T1", head);
            Repo.Run("config", "filter.format.clean", "sh tools/filter.sh");

            // The agent rewrites the script to leave the mark and touches the filtered file.
            WriteIn(lease, "tools/filter.sh", "#!/bin/sh\necho RAN >> \"" + mark.Replace('\\', '/') + "\"\ncat\n");
            WriteIn(lease, "doc.fmt", "contenido del agente\n");

            // GIT_CONFIG (and the global/system overrides) point `git config` at the benign file, so a layer that does
            // not strip them would inspect that file (no callback) while `git add` still reads the repository's filter.
            Exception? error;
            using (new EnvironmentScope(new Dictionary<string, string>
            {
                ["GIT_CONFIG"] = benign,
                ["GIT_CONFIG_GLOBAL"] = benign,
                ["GIT_CONFIG_SYSTEM"] = benign,
            }))
            {
                error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: cambios", Ct));
            }

            Assert.False(File.Exists(mark), "An inherited GIT_CONFIG blinded the inspection and git ran the agent's filter.");
            Assert.IsType<UnsafeGitCallbackException>(error);
            Assert.Equal(head, Repo.Run("rev-parse", lease.RefName).Trim());
        }
        finally
        {
            TempFolders.Delete(scratch);
        }
    }
}
