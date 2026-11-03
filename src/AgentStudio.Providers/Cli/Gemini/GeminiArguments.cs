using AgentStudio.Application.Providers;

namespace AgentStudio.Providers.Cli.Gemini;

/// <summary>
/// Pure translation of an <see cref="AgentRunRequest"/> to Gemini CLI headless arguments and stdin.
/// <list type="bullet">
/// <item>The prompt always travels through stdin; <c>-p ""</c> only switches the CLI to headless mode (the flag text is
/// appended to stdin).</item>
/// <item><c>--skip-trust</c> is always passed: without it Gemini forces <c>plan</c> to <c>default</c> in folders that are
/// not trusted (verified with 0.64.0).</item>
/// <item>Approval mode is <c>auto_edit</c> when file writes are allowed, otherwise <c>plan</c>; <c>yolo</c> is never used.</item>
/// <item>System instructions: <c>GEMINI_SYSTEM_MD</c> is a full replacement of the built-in system prompt (official
/// docs, docs/cli/system-prompt.md), which would drop the tool instructions, so it is not used. The instructions are
/// prepended to the stdin prompt between delimiters instead.</item>
/// <item>Resume: <c>-r</c> accepts <c>latest</c>, an index or a session UUID (docs/cli/session-management.md); the id
/// reported by <c>init</c> is a UUID. An id with unsafe characters is rejected with an ArgumentException (see <see cref="CliArgumentGuard"/>).</item>
/// <item>Structured output: headless has no native JSON-schema option, so <see cref="AgentRunRequest.OutputJsonSchema"/>
/// is ignored and the adapter reports <c>SupportsStructuredOutput = false</c>.</item>
/// </list>
/// </summary>
public static class GeminiArguments
{
    public const string InstructionsBegin = "<<<AGENT_STUDIO_SYSTEM_INSTRUCTIONS>>>";
    public const string InstructionsEnd = "<<<END_AGENT_STUDIO_SYSTEM_INSTRUCTIONS>>>";
    public const string TaskBegin = "<<<TASK>>>";

    public static IReadOnlyList<string> Build(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var args = new List<string>
        {
            "-p", "",
            "-o", "stream-json",
            "--skip-trust",
            "--approval-mode", request.Permissions.AllowFileWrites ? "auto_edit" : "plan",
        };

        var model = request.Agent.Model.Model;
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(CliArgumentGuard.Model(model, nameof(request)));
        }

        foreach (var directory in request.Permissions.AdditionalDirectories.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            args.Add("--include-directories");
            args.Add(CliArgumentGuard.Directory(directory, nameof(request)));
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            args.Add("-r");
            args.Add(CliArgumentGuard.SessionId(request.ResumeSessionId, nameof(request)));
        }

        return args;
    }

    /// <summary>The text written to stdin: the system instructions (if any) followed by the prompt.</summary>
    public static string BuildStandardInput(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instructions = request.Agent.SystemInstructions;
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return request.Prompt;
        }

        return $"{InstructionsBegin}\n{instructions.Trim()}\n{InstructionsEnd}\n\n{TaskBegin}\n{request.Prompt}";
    }
}
