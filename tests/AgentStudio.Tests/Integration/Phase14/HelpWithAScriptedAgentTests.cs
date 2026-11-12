using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Help;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Help;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Integration.Phase9;
using AgentStudio.Tests.Orchestration.Fakes;
using DebateReplies = AgentStudio.Tests.Debate.Fakes.ScriptedDebateProvider;

namespace AgentStudio.Tests.Integration.Phase14;

/// <summary>
/// F14-8 (docs/17 D-56 and D-57, ADR-0056; §9.4 «Integración») in the app as it composes itself, through the «Ayuda» document the person
/// uses: the card of what is sent is in front of them with the estimate before anything is launched, the OK is theirs, the agent of the help
/// (scripted, no CLI) runs read-only in an empty folder of its own with the purpose <c>Help</c>, what it answers is checked against the closed
/// list of actions, and a secret sown in the project, in the last notice and in the question is not in anything that goes to the agent unless
/// the person marks «Incluir datos del proyecto» (and even then a secret never goes).
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class HelpWithAScriptedAgentTests
{
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    private const string Offered = "open.usage";

    private const string NotOffered = "delete.everything";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the scripted agent of the help saw when it was launched.</summary>
    private sealed record Launch(AgentRunRequest Request, int EntriesInItsFolder, bool FolderExisted);

    private static string Answer(params string[] actions) =>
        System.Text.Json.JsonSerializer.Serialize(new { text = "Abre la pantalla de Consumo.", articles = Array.Empty<string>(), actions });

    private static async Task<(AppWorld World, List<Launch> Launches, HelpDocumentViewModel Help)> OpenAsync(string notice)
    {
        var launches = new List<Launch>();
        var clis = new Phase5Clis();
        var executor = clis.Executor;
        clis.Executor = (request, attempt) =>
        {
            if (request.Agent.Id != HelpAgent.Id)
            {
                return executor(request, attempt);
            }

            lock (launches)
            {
                launches.Add(new Launch(request, Directory.GetFileSystemEntries(request.WorkingDirectory).Length, Directory.Exists(request.WorkingDirectory)));
            }

            return new AgentScript
            {
                StructuredOutput = Answer(Offered, NotOffered),
                FinalText = "ok",
                Events = [new UsageReported(DateTimeOffset.UtcNow, DebateReplies.Usage(1_200, 80))],
            };
        };

        var world = await AppWorld.OpenAsync(clis: clis);
        try
        {
            // The secret sown in the project: a file with a false token, and a note of the memory that carries it too.
            world.Repo.Write(".env", $"GITHUB_TOKEN={Token}\n");
            await world.Get<IProjectMemory>().RecordAsync(
                world.App.Project,
                new MemoryDraft(MemoryKind.Decision, "Acceso", $"La clave del repositorio es {Token}.", MemoryOrigin.System, "nota:1"),
                Ct);
            var help = world.Get<HelpDocumentViewModel>();
            help.ShowFor(HelpScreens.Canvas, null, notice, world.Repo.Root);
            Assert.True(help.AgentAvailable, help.AgentUnavailableText);
            return (world, launches, help);
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Everything the agent reads: the prompt, the schema, the purpose and the tags. Not the folder it runs in: that is the empty folder of its
    /// own under the temporary one (checked apart: it is outside the project and holds nothing), so it names the machine's temporary folder.
    /// </summary>
    private static string Sent(AgentRunRequest request) =>
        string.Join("\n", request.Prompt, request.OutputJsonSchema, request.Purpose, string.Join(' ', request.Tags ?? []));

    [Fact]
    public async Task The_card_and_the_estimate_come_first_then_the_OK_then_a_read_only_agent_in_an_empty_folder_with_the_purpose_Help_and_only_actions_of_the_closed_list()
    {
        var path = string.Empty;
        var (world, launches, help) = await OpenAsync("pendiente");
        await using var _ = world;
        path = Path.Combine(world.Repo.Root, ".env");
        help.ShowFor(HelpScreens.Canvas, null, $"No se pudo leer {path} con la clave {Token}", world.Repo.Root);
        help.Question = "¿Dónde veo cuánto he gastado?";

        // 1. The card is in front of the person, with the estimate, and nothing was launched.
        await help.AskCommand.ExecuteAsync(null);
        Assert.True(help.Consent.IsOpen);
        Assert.True(help.Consent.IsReady);
        Assert.NotEmpty(help.CardLines);
        Assert.False(string.IsNullOrWhiteSpace(help.Consent.EstimateText));
        Assert.NotEqual(AgentStudio.Desktop.Localization.Texts.Get("Canvas_Consent_Estimating"), help.Consent.EstimateText);
        Assert.Contains("#", help.Consent.Description, StringComparison.Ordinal); // the fingerprint of this card and this question
        Assert.Empty(launches);
        Assert.Empty(world.Clis.Launches);

        // 2. The person gives their OK: one launch, read-only, in an empty folder of its own, outside the project.
        await help.ConfirmCommand.ExecuteAsync(null);
        Assert.False(help.IsBusy);
        var launch = Assert.Single(launches);
        Assert.True(launch.FolderExisted);
        Assert.Equal(0, launch.EntriesInItsFolder);
        Assert.False(Directory.Exists(launch.Request.WorkingDirectory), "The folder of the agent of the help was not deleted.");
        Assert.False(launch.Request.WorkingDirectory.StartsWith(world.Repo.Root, StringComparison.OrdinalIgnoreCase));
        var permissions = launch.Request.Permissions;
        Assert.False(permissions.AllowFileWrites || permissions.AllowShell || permissions.AllowNetwork);
        Assert.Equal(["Read", "Grep", "Glob"], permissions.AllowedTools);
        Assert.False(launch.Request.PersistSession);

        // 3. The purpose is Help, in the log and in the consumption.
        var started = Assert.Single(await world.App.PayloadsAsync<AgentRunStartedPayload>(), p => p.Payload.Purpose == AgentRunPurpose.Help);
        Assert.Equal(HelpAgent.Id, started.Payload.Agent);
        var usage = await world.Get<IUsageReport>().QueryAsync(
            [world.Repo.Root],
            new UsageQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), TimeZoneInfo.Utc, [UsageDimension.Purpose]),
            Ct);
        var group = Assert.Single(usage.Groups, g => g.Key.Any(k => k.Value.Contains("Help", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal((1, 1_200L), (group.Totals.Attempts, group.Totals.InputTokens.Sum));

        // 4. The actions come from the closed list: the one it invented is discarded, said, and offers no button.
        Assert.Equal([Offered], help.ReplyActions.Select(a => a.Id));
        Assert.Contains(help.ReplyDiscarded, note => note.Contains(NotOffered, StringComparison.Ordinal));
        Assert.Contains("Consumo", help.ReplyText, StringComparison.Ordinal);
        Assert.Contains($"\"{Offered}\"", launch.Request.OutputJsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain(NotOffered, launch.Request.OutputJsonSchema, StringComparison.Ordinal);

        // 5. The OK was spent: the same card does not launch twice.
        Assert.Single(launches);
    }

    [Fact]
    public async Task A_secret_sown_in_the_project_and_the_notice_the_question_and_the_memory_is_in_nothing_that_goes_to_the_agent_without_the_mark_of_the_person()
    {
        var (world, launches, help) = await OpenAsync("pendiente");
        await using var _ = world;
        var path = Path.Combine(world.Repo.Root, ".env");
        var notice = $"No se pudo leer {path} con la clave {Token}";
        help.ShowFor(HelpScreens.Canvas, null, notice, world.Repo.Root);
        help.Question = $"¿Por qué falla? Mi clave es {Token}";

        // Without the mark: neither the secret nor the path of the machine nor the project's name travel, and the card says what it kept back.
        await help.AskCommand.ExecuteAsync(null);
        Assert.DoesNotContain(help.CardLines, line => line.Contains(Token, StringComparison.Ordinal) || line.Contains(world.Repo.Root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(help.CardLines, line => line.Contains(AgentStudio.Desktop.Localization.Texts.Get("Help_Card_Withheld_ProjectName"), StringComparison.Ordinal));
        await help.ConfirmCommand.ExecuteAsync(null);
        var plain = Assert.Single(launches).Request;
        AssertNoSecretAndNoPath(Sent(plain), world);
        Assert.DoesNotContain(Path.GetFileName(world.Repo.Root) + Path.DirectorySeparatorChar, Sent(plain), StringComparison.OrdinalIgnoreCase);

        // With the mark: the name of the project may go, and the secret still does not.
        help.IncludeProjectData = true;
        help.Question = $"¿Y ahora? La clave sigue siendo {Token}";
        await help.AskCommand.ExecuteAsync(null);
        await help.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(2, launches.Count);
        var marked = launches[1].Request;
        Assert.DoesNotContain(Token, Sent(marked), StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", Sent(marked), StringComparison.Ordinal);

        // Nor is it in the journal of prompts that the project keeps, nor in the log.
        Assert.DoesNotContain(Phase5Checks.JournalLines(world.Repo.Root), line => line.Contains(Token, StringComparison.Ordinal));
        var log = await world.App.LoadLogAsync();
        Assert.DoesNotContain(log.Entries, e => e.PayloadJson.Contains(Token, StringComparison.Ordinal) || e.Summary.Contains(Token, StringComparison.Ordinal));
    }

    private static void AssertNoSecretAndNoPath(string sent, AppWorld world)
    {
        Assert.DoesNotContain(Token, sent, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Repo.Root, sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".env", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName + Path.DirectorySeparatorChar, sent, StringComparison.OrdinalIgnoreCase);
    }
}
