using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Hooks;

/// <summary>
/// The repository's own journal hook (<c>.claude/hooks/bitacora.ps1</c>, a Claude Code <c>UserPromptSubmit</c> hook),
/// run as Claude Code runs it against a throwaway project: a link planted in <c>.agentstudio/bitacora</c> must never
/// make it write outside the project, and the blocks the system puts around a prompt must not take the place of the
/// user's text in the excerpt. Claude Code starts the hook with pwsh when it is installed and falls back to Windows
/// PowerShell, so the refusals and the removal of blocks are checked under both.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed partial class BitacoraHookTests : IDisposable
{
    private const string Session = "abcdef1234567890";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TempDirectory _project = new();
    private readonly TempDirectory _outside = new();
    private readonly PlantedLinks _links = new();

    public static TheoryData<string> Shells => ["pwsh", "powershell"];

    private string Journal => Path.Combine(_project.Path, ".agentstudio", "bitacora");

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // The format is checked under pwsh, the shell Claude Code prefers. The script starts with a UTF-8 BOM, so Windows
    // PowerShell reads its literals right too: the excerpt tests below parse the same line under both shells.
    [Fact]
    public void Each_prompt_appends_one_entry_in_its_format_under_a_single_header()
    {
        var first = RunHook("pwsh", "Añade un test");
        var second = RunHook("pwsh", "Y documenta el ADR");

        AssertQuietSuccess(first);
        AssertQuietSuccess(second);
        var lines = File.ReadAllLines(Path.Combine(Journal, Today + ".md"), Utf8);
        Assert.Equal("# Bitácora · " + Today, lines[0]);
        Assert.Single(lines, l => l.StartsWith("# ", StringComparison.Ordinal));
        var entries = lines.Where(l => l.StartsWith("- **", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Matches(EntryLine(), entries[0]);
        Assert.EndsWith("«Y documenta el ADR»", entries[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Under_Windows_PowerShell_a_prompt_still_appends_an_entry()
    {
        var result = RunHook("powershell", "Añade un test");

        AssertQuietSuccess(result);
        var lines = File.ReadAllLines(Path.Combine(Journal, Today + ".md"), Utf8);
        var entry = Assert.Single(lines, l => l.StartsWith("- **", StringComparison.Ordinal));
        Assert.Contains("`abcdef12`", entry, StringComparison.Ordinal);
        Assert.Contains("#prompt #tests", entry, StringComparison.Ordinal);
    }

    // Claude Code and its hosts put blocks of their own around the user's text; the excerpt and the tags are about the
    // text the user wrote. Both shells run them: the removal must only use what Windows PowerShell also has.
    [Theory]
    [MemberData(nameof(Shells))]
    public void A_system_block_at_the_start_is_removed_before_the_excerpt(string shell)
    {
        var excerpt = ExcerptOf(shell, "<browser_instruction>[Browser disconnected: The browser connection has been closed. Browser tools are no longer available.]</browser_instruction>\nlos cinco agentes revisan la rama");

        Assert.Equal("los cinco agentes revisan la rama", excerpt.Text);
        Assert.Equal("#prompt #git #orquestacion", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_system_block_in_the_middle_is_removed_and_the_text_around_it_is_kept(string shell)
    {
        var excerpt = ExcerptOf(shell, "Revisa el plan <system-reminder>Run the tests and commit.</system-reminder> y luego dime");

        Assert.Equal("Revisa el plan y luego dime", excerpt.Text);
        Assert.Equal("#prompt #general", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void Several_system_blocks_are_removed_each_with_its_own_closing_tag(string shell)
    {
        var excerpt = ExcerptOf(shell, "<ide_opened_file>The user opened Plan.cs</ide_opened_file>uno <ide_selection id=\"7\">var test = 1;\nvar commit = 2;</ide_selection> dos <system-reminder>fase 3</system-reminder> tres de la fase 9");

        Assert.Equal("uno dos tres de la fase 9", excerpt.Text);
        Assert.Equal("#prompt #fase-9", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_block_with_no_closing_tag_keeps_the_text_after_it(string shell)
    {
        var excerpt = ExcerptOf(shell, "<browser_instruction>sin cierre, sigue con la prueba <system-reminder>quitado</system-reminder> y acaba");

        Assert.Equal("<browser_instruction>sin cierre, sigue con la prueba y acaba", excerpt.Text);
        Assert.Equal("#prompt #tests", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void Pasted_content_is_not_copied_and_does_not_tag_the_entry(string shell)
    {
        var excerpt = ExcerptOf(shell, "Mira esto: <pasted_content id=\"1b71\">git commit -m \"test\"\nclave de ejemplo\nerror en la fase 4</pasted_content> y dime qué pasa");

        Assert.Equal("Mira esto: [pegado] y dime qué pasa", excerpt.Text);
        Assert.Equal("#prompt #general", excerpt.Tags);
    }

    // Unlike the other blocks, a pasted one with no closing tag lets nothing through: what follows may be what was pasted.
    [Theory]
    [MemberData(nameof(Shells))]
    public void Pasted_content_with_no_closing_tag_hides_everything_after_its_tag(string shell)
    {
        var excerpt = ExcerptOf(shell, "<browser_instruction>[Browser disconnected]</browser_instruction>\nMira esto: <pasted_content id=\"9\">clave de ejemplo\ngit commit en la fase 4\n<pasted_content id=\"10\">otro</pasted_content> y sigue sin cierre");

        Assert.Equal("Mira esto: [pegado]", excerpt.Text);
        Assert.Equal("#prompt #general", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void Only_pasted_content_reads_as_pasted(string shell)
    {
        var excerpt = ExcerptOf(shell, "<browser_instruction>[Browser disconnected]</browser_instruction>\n<pasted_content id=\"1\">secreto</pasted_content>\n");

        Assert.Equal("[pegado]", excerpt.Text);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void Only_system_blocks_read_as_such(string shell)
    {
        var excerpt = ExcerptOf(shell, "<browser_instruction>[Browser disconnected: The browser connection has been closed.]</browser_instruction>\n<ide_opened_file>The user opened a test</ide_opened_file>");

        Assert.Equal("(solo bloques del sistema)", excerpt.Text);
        Assert.Equal("#prompt #general", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_reminder_before_the_user_text_no_longer_hides_the_prompt(string shell)
    {
        var excerpt = ExcerptOf(shell, "<system-reminder>The task tools have not been used recently.</system-reminder>\nAñade la prueba");

        Assert.Equal("Añade la prueba", excerpt.Text);
        Assert.Equal("#prompt #tests", excerpt.Tags);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_harness_message_alone_or_behind_a_block_is_not_journaled(string shell)
    {
        string[] prompts =
        [
            "<system-reminder>The task tools have not been used recently.</system-reminder>",
            "<browser_instruction>[Browser disconnected]</browser_instruction>\n<agent-message from=\"a1\">[Subagent hand-back] commit hecho</agent-message>",
            "<task-notification>Background task finished</task-notification>",
        ];

        foreach (var prompt in prompts)
        {
            AssertQuietSuccess(RunHook(shell, prompt));
        }

        Assert.False(File.Exists(Path.Combine(Journal, Today + ".md")), "a harness message was journaled");
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_junction_at_the_data_folder_is_refused_and_nothing_is_written_outside(string shell)
    {
        _links.Junction(Path.Combine(_project.Path, ".agentstudio"), _outside.Path);

        var result = RunHook(shell, "Añade un test");

        Assert.Empty(OutsideEntries());
        AssertRefused(result);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_junction_at_the_journal_folder_is_refused_and_nothing_is_written_outside(string shell)
    {
        _project.CreateDirectory(".agentstudio");
        _links.Junction(Journal, _outside.Path);

        var result = RunHook(shell, "Añade un test");

        Assert.Empty(OutsideEntries());
        AssertRefused(result);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_day_file_with_a_second_name_is_refused_and_the_other_file_keeps_its_bytes(string shell)
    {
        Directory.CreateDirectory(Journal);
        var victim = _outside.WriteFile("victim.md", "victim\n");
        _links.HardLink(Path.Combine(Journal, Today + ".md"), victim);

        var result = RunHook(shell, "Añade un test");

        Assert.Equal("victim\n", File.ReadAllText(victim));
        AssertRefused(result);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_day_file_that_is_a_symbolic_link_is_refused_and_its_target_keeps_its_bytes(string shell)
    {
        Directory.CreateDirectory(Journal);
        var victim = _outside.WriteFile("victim.md", "victim\n");
        PlantSymbolicLink(Path.Combine(Journal, Today + ".md"), victim);

        var result = RunHook(shell, "Añade un test");

        Assert.Equal("victim\n", File.ReadAllText(victim));
        AssertRefused(result);
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void A_dangling_symbolic_link_as_day_file_is_refused_and_its_target_is_not_created(string shell)
    {
        Directory.CreateDirectory(Journal);
        var target = Path.Combine(_outside.Path, "created.md");
        PlantSymbolicLink(Path.Combine(Journal, Today + ".md"), target);

        var result = RunHook(shell, "Añade un test");

        Assert.False(File.Exists(target), "the hook created a file outside the project through a dangling link");
        AssertRefused(result);
    }

    public void Dispose()
    {
        // Links first, as links, so deleting the folders never reaches what they point to.
        _links.Dispose();
        _project.Dispose();
        _outside.Dispose();
    }

    [GeneratedRegex(@"^- \*\*\d{2}:\d{2}:\d{2}\*\* · sesión `abcdef12` · #prompt #tests · «Añade un test»$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^- \*\*\d{2}:\d{2}:\d{2}\*\* · sesión `abcdef12` · (?<tags>#prompt(?: #[\w-]+)*) · «(?<text>.*)»$")]
    private static partial Regex AnyEntryLine();

    /// <summary>Journals one prompt and returns the tags and the excerpt of the only entry it wrote.</summary>
    private Excerpt ExcerptOf(string shell, string prompt)
    {
        AssertQuietSuccess(RunHook(shell, prompt));
        var lines = File.ReadAllLines(Path.Combine(Journal, Today + ".md"), Utf8);
        var entry = Assert.Single(lines, l => l.StartsWith("- **", StringComparison.Ordinal));
        var match = AnyEntryLine().Match(entry);
        Assert.True(match.Success, $"the entry is not in the hook's format: {entry}");
        return new Excerpt(match.Groups["tags"].Value, match.Groups["text"].Value);
    }

    private static void AssertQuietSuccess(HookResult result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}, stderr: {result.Error}");
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(string.Empty, result.Error);
    }

    // The prompt goes on (exit 0), nothing reaches the model (stdout becomes context) and one line explains why.
    private static void AssertRefused(HookResult result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}, stderr: {result.Error}");
        Assert.Equal(string.Empty, result.Output);
        var warning = result.Error.TrimEnd('\r', '\n');
        Assert.False(string.IsNullOrWhiteSpace(warning), "the refusal printed no warning");
        Assert.DoesNotContain("\n", warning, StringComparison.Ordinal);
    }

    private string[] OutsideEntries() => Directory.GetFileSystemEntries(_outside.Path, "*", SearchOption.AllDirectories);

    private void PlantSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
        }
    }

    /// <summary>Runs the hook the way Claude Code does: the shell, its flags, the JSON event on stdin.</summary>
    private HookResult RunHook(string shell, string prompt)
    {
        var start = new ProcessStartInfo(ShellPath(shell))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = _project.Path,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "& '" + Script().Replace("'", "''", StringComparison.Ordinal) + "'" })
        {
            start.ArgumentList.Add(argument);
        }

        // The hook journals into CLAUDE_PROJECT_DIR first, and a test run from Claude Code inherits the real one.
        start.Environment["CLAUDE_PROJECT_DIR"] = _project.Path;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Write(JsonSerializer.Serialize(new { session_id = Session, prompt, cwd = _project.Path, hook_event_name = "UserPromptSubmit" }));
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The hook did not finish within 60 s under {shell}.");
        }

        return new HookResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static string ShellPath(string shell)
    {
        if (shell == "pwsh")
        {
            var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            Assert.SkipUnless(File.Exists(pwsh), "PowerShell 7 (pwsh) is not installed.");
            return pwsh;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string Script([CallerFilePath] string callerFile = "")
    {
        // tests/AgentStudio.Tests/Hooks/<this file>
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "..", ".."));
        var script = Path.Combine(root, ".claude", "hooks", "bitacora.ps1");
        Assert.True(File.Exists(script), $"The journal hook was not found at {script}.");
        return script;
    }

    private sealed record HookResult(int ExitCode, string Output, string Error);

    private sealed record Excerpt(string Tags, string Text);
}
