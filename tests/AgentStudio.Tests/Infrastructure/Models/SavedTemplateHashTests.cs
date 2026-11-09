using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Observability.Models;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Tests.Infrastructure.Persistence;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Models;

/// <summary>
/// F13-11: «el hash de una plantilla guardada no cambia». A template saved before Phase 13 has no effort; reading it,
/// launching it, recommending for it and learning from the log neither write nor change it, so it hashes as it always did and
/// still starts a run. Only applying a recommendation that changes something changes the template (and withdraws the
/// approval of the plan, D-25).
/// </summary>
public sealed class SavedTemplateHashTests : IDisposable
{
    /// <summary>
    /// The hash of this template, pinned on 2026-11-08 and computed with an independent implementation of the canonical form
    /// (length-prefixed fields, SHA-256), the same one that gives the value <c>RosterHashTests</c> pins for its roster.
    /// </summary>
    private const string PinnedHash = "32612847f3ba8d843ede98baf4898debadd73c1425e0ae54e2b7a1cc79c71793";

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An executor and a judge (read-only, as the store requires), both with haiku, as a template of before.</summary>
    private static AgentDefinition Agent(string id, AgentRole role) => new(
        Aid(id),
        "Agente " + id,
        role,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        "Instrucciones de " + id,
        ["objetivo"],
        ["Read", "Edit"],
        [new PathGrant("src/", role == AgentRole.Judge ? FileAccessLevel.Read : FileAccessLevel.ReadWrite)],
        new ExecutionBudget(20, TimeSpan.FromMinutes(10), 1000, 1.25m),
        3,
        MemoryPolicy.None,
        new ReviewPolicy(true, true));

    [Fact]
    public async Task A_template_saved_before_the_effort_keeps_its_hash_through_launching_recommending_and_learning()
    {
        IReadOnlyList<AgentDefinition> roster = [Agent("ejecutor", AgentRole.Executor), Agent("juez", AgentRole.Judge)];
        string stored;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            foreach (var agent in roster)
            {
                await first.Roster.SaveAsync(first.ProjectId, agent, Ct);
            }

            using var connection = first.Connect();
            stored = Text(connection, "SELECT group_concat(definition_json, '|') FROM (SELECT definition_json FROM agents ORDER BY id)")!;
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var listed = await second.Roster.ListAsync(second.ProjectId, Ct);
        Assert.Equal(PinnedHash, RosterHash.Of(listed));
        Assert.All(listed, agent => Assert.Null(agent.Effort));

        // What WP2 does with a template: launch it, say what is known of its models, recommend and learn. None of it writes.
        var catalog = BuiltInModelCatalog.Shipped.Catalog;
        var learned = LearnedModelsProjection.FromLog(await second.Recorder.LoadAsync(second.ProjectId, Ct), catalog);
        foreach (var agent in listed)
        {
            var request = new AgentRunRequest(agent, "Haz esto", "C:/work", new PermissionProfile(false, false, false, [], [], []));
            Assert.DoesNotContain("--effort", ClaudeCodeArguments.Build(request, "11111111-1111-1111-1111-111111111111", null, "settings.json"));
            Assert.DoesNotContain(CodexExecArguments.Build(request, null, isRepository: true), a => a.Contains(CodexExecArguments.ReasoningEffortKey, StringComparison.Ordinal));
            Assert.Empty(ModelNotices.Of(catalog, agent));

            var recommendation = ModelRecommender.Recommend(
                catalog,
                new ModelRecommendationInput(agent.Role, agent.Model.Provider, [VendorId.Anthropic], 50, 1, 0, "dotnet", 12, false, learned.Performances))!;
            Assert.False(ModelRecommender.IsFollowedBy(catalog, recommendation, agent));
        }

        Assert.Equal(PinnedHash, RosterHash.Of(await second.Roster.ListAsync(second.ProjectId, Ct)));
        using (var connection = second.Connect())
        {
            Assert.Equal(stored, Text(connection, "SELECT group_concat(definition_json, '|') FROM (SELECT definition_json FROM agents ORDER BY id)"));
            Assert.DoesNotContain("effort", stored, StringComparison.Ordinal);
        }

        // Applying a recommendation is changing the template: its hash changes, and that is what withdraws the approval.
        var executor = listed.Single(a => a.Role == AgentRole.Executor);
        var applied = ModelRecommender.Recommend(catalog, new ModelRecommendationInput(AgentRole.Executor, ProviderId.ClaudeCode, [], 50, 1, 0, null, null, false, []))!;
        await second.Roster.SaveAsync(second.ProjectId, executor with { Model = executor.Model with { Model = applied.Model }, Effort = applied.Effort }, Ct);
        var changed = await second.Roster.ListAsync(second.ProjectId, Ct);

        Assert.NotEqual(PinnedHash, RosterHash.Of(changed));
        Assert.True(ModelRecommender.IsFollowedBy(catalog, applied, changed.Single(a => a.Role == AgentRole.Executor)));
        Assert.Contains("--effort", ClaudeCodeArguments.Build(
            new AgentRunRequest(changed.Single(a => a.Role == AgentRole.Executor), "Haz esto", "C:/work", new PermissionProfile(false, false, false, [], [], [])),
            "11111111-1111-1111-1111-111111111111",
            null,
            "settings.json"));
    }
}
