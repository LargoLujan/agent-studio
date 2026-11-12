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
    private sealed record Launch(AgentRunRequest Request, int EntriesInItsFolder, bool FolderExisted, string FolderContents, string SkillsContents);

    /// <summary>The names and the text of every file under a folder, as the agent would read them (empty if there is no folder).</summary>
    private static string ContentsOf(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(entry => Path.GetRelativePath(folder, entry) + (File.Exists(entry) ? "\n" + File.ReadAllText(entry) : string.Empty)));
    }

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
                launches.Add(new Launch(
                    request,
                    Directory.GetFileSystemEntries(request.WorkingDirectory).Length,
                    Directory.Exists(request.WorkingDirectory),
                    ContentsOf(request.WorkingDirectory),
                    ContentsOf(request.SkillsDirectory)));
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
    /// Everything that travels in the request to the agent: the whole request as data (the agent with its instructions, the prompt, the schema, the session to resume, the purpose,
    /// the task, the tags, the permissions, the MCP servers, the debate...) and what its working folder and its skills folder held when it launched. The two folders are named by
    /// their content, not by their path: the working folder is an empty folder of its own under the machine's temporary one (checked apart: it is outside the project and holds
    /// nothing), so its path names the machine.
    /// </summary>
    private static string Sent(Launch launch) => Sent(launch, withPaths: false);

    /// <param name="launch">What the agent was launched with.</param>
    /// <param name="withPaths">Also the paths of its working and skills folders: the secret must not be in them either, though they name the machine for the other checks.</param>
    private static string Sent(Launch launch, bool withPaths)
    {
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };
        var request = withPaths ? launch.Request : launch.Request with { WorkingDirectory = string.Empty, SkillsDirectory = launch.Request.SkillsDirectory is null ? null : string.Empty };

        // The strings of the request as the agent receives them, unescaped (JSON doubles the backslashes of a path and writes other characters as \u escapes), with the names of the fields.
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(request, options));
        var texts = new List<string>();
        void Collect(System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    texts.Add(element.GetString()!);
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        texts.Add(property.Name);
                        Collect(property.Value);
                    }

                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item);
                    }

                    break;
                default:
                    texts.Add(element.GetRawText());
                    break;
            }
        }

        Collect(document.RootElement);
        return string.Join("\n", [.. texts, launch.FolderContents, launch.SkillsContents]);
    }

    /// <summary>The secret as it could travel: as it is and in base64 (standard and URL-safe) at any of the three alignments it can have inside a longer text.</summary>
    private static IEnumerable<string> FormsOfTheSecret()
    {
        yield return Token;
        yield return "ghp_";
        for (var offset = 0; offset < 3; offset++)
        {
            var encoded = Convert.ToBase64String([.. new byte[offset], .. System.Text.Encoding.UTF8.GetBytes(Token)]);

            // Without the first and last four characters, which depend on what surrounds the secret.
            var inner = encoded[4..^4];
            yield return inner;
            yield return inner.Replace('+', '-').Replace('/', '_');
        }
    }

    private static void AssertNoSecret(string sent)
    {
        foreach (var form in FormsOfTheSecret())
        {
            Assert.DoesNotContain(form, sent, StringComparison.Ordinal);
        }
    }

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
        var plain = Assert.Single(launches);
        AssertNoSecretAndNoPath(Sent(plain), world);
        AssertNoSecret(Sent(plain, withPaths: true));
        Assert.DoesNotContain(Path.GetFileName(world.Repo.Root) + Path.DirectorySeparatorChar, Sent(plain), StringComparison.OrdinalIgnoreCase);
        Assert.False(plain.Request.WorkingDirectory.StartsWith(world.Repo.Root, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(plain.FolderContents);

        // With the mark: the name of the project may go, and the secret still does not.
        help.IncludeProjectData = true;
        help.Question = $"¿Y ahora? La clave sigue siendo {Token}";
        await help.AskCommand.ExecuteAsync(null);
        await help.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(2, launches.Count);
        AssertNoSecret(Sent(launches[1], withPaths: true));

        // Nor is it in the journal of prompts that the project keeps, nor in the log.
        Assert.DoesNotContain(Phase5Checks.JournalLines(world.Repo.Root), line => line.Contains(Token, StringComparison.Ordinal));
        var log = await world.App.LoadLogAsync();
        Assert.DoesNotContain(log.Entries, e => e.PayloadJson.Contains(Token, StringComparison.Ordinal) || e.Summary.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_search_for_the_secret_and_the_paths_sees_every_field_of_the_request_the_two_folders_and_the_base64_of_the_secret()
    {
        // The checks above would pass if they did not look: plant the secret (or the root of the project) in each field and in each folder and see that they notice.
        var (world, launches, help) = await OpenAsync("pendiente");
        await using var _ = world;
        help.Question = "¿Dónde veo cuánto he gastado?";
        await help.AskCommand.ExecuteAsync(null);
        await help.ConfirmCommand.ExecuteAsync(null);
        var launch = Assert.Single(launches);
        AssertNoSecret(Sent(launch, withPaths: true));
        AssertNoSecretAndNoPath(Sent(launch), world);
        var request = launch.Request;
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("clave=" + Token));
        var urlSafe = base64.Replace('+', '-').Replace('/', '_');

        var withSecret = new Dictionary<string, Launch>
        {
            ["the prompt"] = launch with { Request = request with { Prompt = request.Prompt + "\n" + Token } },
            ["the prompt in base64"] = launch with { Request = request with { Prompt = request.Prompt + "\n" + base64 } },
            ["the prompt in base64 for a url"] = launch with { Request = request with { Prompt = request.Prompt + "\n" + urlSafe } },
            ["the schema"] = launch with { Request = request with { OutputJsonSchema = request.OutputJsonSchema + Token } },
            ["the session to resume"] = launch with { Request = request with { ResumeSessionId = Token } },
            ["the purpose"] = launch with { Request = request with { Purpose = Token } },
            ["the task"] = launch with { Request = request with { TaskId = Token } },
            ["the tags"] = launch with { Request = request with { Tags = [.. request.Tags ?? [], Token] } },
            ["the debate"] = launch with { Request = request with { DebateId = Token } },
            ["the path of the working folder"] = launch with { Request = request with { WorkingDirectory = Path.Combine(request.WorkingDirectory, Token) } },
            ["the path of the skills folder"] = launch with { Request = request with { SkillsDirectory = Path.Combine(Path.GetTempPath(), Token) } },
            ["what the working folder held"] = launch with { FolderContents = "nota.txt\n" + Token },
            ["what the skills folder held"] = launch with { SkillsContents = "SKILL.md\n" + base64 },
        };
        foreach (var (field, planted) in withSecret)
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecret(Sent(planted, withPaths: true)));
            Assert.True(field.Length > 0);
        }

        // The root of the project, with its backslashes as they are written in the machine, is noticed in the prompt, in the folder and in what the folder held.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecretAndNoPath(Sent(launch with { Request = request with { Prompt = request.Prompt + " " + world.Repo.Root } }), world));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecretAndNoPath(Sent(launch with { FolderContents = world.Repo.Root }), world));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoSecretAndNoPath(Sent(launch with { Request = request with { Tags = [world.Repo.Root] } }), world));
    }

    private static void AssertNoSecretAndNoPath(string sent, AppWorld world)
    {
        AssertNoSecret(sent);
        Assert.DoesNotContain(world.Repo.Root, sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".env", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName + Path.DirectorySeparatorChar, sent, StringComparison.OrdinalIgnoreCase);
    }
}
