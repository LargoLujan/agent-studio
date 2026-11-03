using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Codex.AppServer;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>
/// The real read of the Codex quota (docs/14 D-15, F4-4): <c>codex app-server</c> with the three messages of Agent Studio,
/// on the installed CLI and the user's session. No turn of the model runs, but it reads the account from the backend of
/// ChatGPT, so it only runs with <c>AGENTSTUDIO_PROVIDER_TESTS=1</c> and the user's OK, at integration time.
/// </summary>
/// <remarks>
/// With <c>AGENTSTUDIO_CAPTURE_DIR</c> it writes the capture <c>05-rate-limits.jsonl</c> that pins the answer of the
/// version (<c>CodexRateLimitsParserTests</c> reads it from <c>Fixtures/codex-cli/&lt;versión&gt;/</c>): only the answer
/// of the read, with the account id, the credit balance, the upsell and the reset credits replaced. Its standard error
/// is never saved: it can carry paths of the user's profile.
/// </remarks>
[Trait("Category", "ProviderLive")]
public sealed class CodexQuotaLiveTests
{
    [Fact]
    public async Task The_real_app_server_answers_the_quota_read_and_only_the_whitelist_is_kept()
    {
        LiveTestSupport.RequireOptIn();
        var ct = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner(new StreamingProcessRunner());
        var reader = new CodexAppServerQuotaReader(new ExecutableLocator(), runner, new ProcessRunner(), () => true, TimeProvider.System);

        var result = await reader.ReadAsync(ct);

        var process = Assert.Single(runner.Processes);
        LiveTestSupport.Note($"codex app-server: {result.Status} · {result.Observations.Count} ventanas · plan {result.PlanType ?? "-"} · {result.Reason ?? "sin motivo"} · avisos: {string.Join(" | ", result.Warnings)}");
        Assert.Equal(CodexAppServerQuotaReader.Arguments, process.Spec.Arguments);
        Assert.True(process.Spec.KeepStandardInputOpen);
        Assert.Equal(CodexAppServerQuotaReader.Messages("0.0.0").Count, process.Spec.StandardInput!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // With a ChatGPT session the read answers; with an API key the quota is not exposed. Anything else is a finding.
        Assert.True(result.Status is QuotaReadStatus.Read or QuotaReadStatus.NotExposed, result.Reason);
        if (result.Status == QuotaReadStatus.Read)
        {
            Assert.NotEmpty(result.Observations);
            Assert.All(result.Observations, o => Assert.True(o.Utilization.Value is >= 0, o.Window));
        }

        // Whatever identifies the account never leaves the parser.
        var answer = AnswerLine(process.StandardOutput);
        Assert.NotNull(answer);
        var kept = JsonSerializer.Serialize(result);
        foreach (var secret in SecretsOf(answer))
        {
            Assert.DoesNotContain(secret, kept, StringComparison.Ordinal);
        }

        // The tree is ended once the answer arrived.
        await WaitForExitAsync(process, ct);

        SaveSanitizedCapture(answer);
    }

    /// <summary>The line of the answer to the read (id 2), as the app-server wrote it.</summary>
    private static string? AnswerLine(IReadOnlyList<string> stdout) =>
        stdout.FirstOrDefault(line => CodexRateLimitsParser.FromLine(line, DateTimeOffset.UnixEpoch) is not null);

    /// <summary>The values of the answer that identify the account or its money, to prove none is kept.</summary>
    private static IEnumerable<string> SecretsOf(string answer)
    {
        var result = JsonNode.Parse(answer)?["result"];
        if (result?["accountId"]?.GetValue<string>() is { Length: > 3 } account)
        {
            yield return account;
        }

        foreach (var bucket in Buckets(result))
        {
            if (bucket?["credits"]?["balance"] is JsonValue balance && balance.TryGetValue<string>(out var text) && text.Length > 1)
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<JsonNode?> Buckets(JsonNode? result)
    {
        yield return result?["rateLimits"];
        if (result?["rateLimitsByLimitId"] is JsonObject byId)
        {
            foreach (var (_, bucket) in byId)
            {
                yield return bucket;
            }
        }
    }

    /// <summary>
    /// Writes the answer with what identifies the account replaced, as <c>05-rate-limits.jsonl</c>, when
    /// <see cref="LiveTestSupport.CaptureVariable"/> names a folder; the command line goes next to it.
    /// </summary>
    private static void SaveSanitizedCapture(string? answer)
    {
        var directory = Environment.GetEnvironmentVariable(LiveTestSupport.CaptureVariable);
        if (string.IsNullOrWhiteSpace(directory) || answer is null)
        {
            return;
        }

        var root = JsonNode.Parse(answer)!.AsObject();
        if (root["result"] is JsonObject result)
        {
            if (result.ContainsKey("accountId"))
            {
                result["accountId"] = "acct-REDACTADO";
            }

            if (result.ContainsKey("rateLimitUpsell"))
            {
                result["rateLimitUpsell"] = result["rateLimitUpsell"] is null ? null : new JsonObject { ["redactado"] = true };
            }

            if (result["rateLimitResetCredits"] is JsonObject credits)
            {
                credits["credits"] = null;
            }

            foreach (var bucket in Buckets(result).OfType<JsonObject>())
            {
                if (bucket["credits"] is JsonObject balance && balance.ContainsKey("balance"))
                {
                    balance["balance"] = "REDACTADO";
                }
            }
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "05-rate-limits.jsonl"), root.ToJsonString() + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(directory, "05-rate-limits.cmd.txt"),
            "codex " + string.Join(' ', CodexAppServerQuotaReader.Arguments) + " · stdin: " + string.Join(" | ", CodexAppServerQuotaReader.Messages("<versión>")) + Environment.NewLine);
    }

    private static async Task WaitForExitAsync(RecordedProcess process, CancellationToken ct)
    {
        for (var i = 0; i < 100 && process.ExitedAt is null; i++)
        {
            await Task.Delay(100, ct);
        }

        Assert.NotNull(process.ExitedAt);
    }
}
