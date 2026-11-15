using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Integration.Phase10;

/// <summary>
/// A fixture-owned bare repository reached over stdio by native git, without the file transport's MSYS shell.
/// The executable and destination are fixed here; no repository, provider or user text chooses a command.
/// </summary>
internal sealed class NativeLocalGit
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _home;

    public NativeLocalGit(string bare, string home)
    {
        Bare = Path.GetFullPath(bare);
        _home = Path.GetFullPath(home);
        Executable = Path.GetFullPath(new ExecutableLocator().Find("git.exe")
            ?? throw new InvalidOperationException("The native git executable was not found."));

        // remote-ext parses its own argv: quotes are literal; '% ' is a space and '%%' a percent sign.
        // Its child inherits GIT_DIR from the helper, so explicitly bind the server to our bare repository.
        Address = "ext::" + Escape(Executable) + " " + Escape("--git-dir=" + Bare) + " %s " + Escape(Bare);
    }

    public string Bare { get; }

    public string Executable { get; }

    public string Address { get; }

    /// <summary>Runs only this fixture's git commands; protocol settings and private HOME apply to this process tree.</summary>
    public string Run(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in new[] { "-c", "protocol.allow=never", "-c", "protocol.ext.allow=always" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        ConfigureEnvironment(start.Environment, _home);

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture git {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.GetAwaiter().GetResult()}");
        }

        _ = error.GetAwaiter().GetResult();
        return output;
    }

    /// <summary>Changes only the supplied child environment, keeping Git controls and HOME private to this fixture.</summary>
    internal static void ConfigureEnvironment(IDictionary<string, string?> environment, string home)
    {
        // Git accepts more overrides than repository markers, including config destinations and helper executables.
        // Remove inherited controls before setting our closed list; use Windows casing even for a case-sensitive map.
        foreach (var name in environment.Keys.Where(name =>
                     name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("HOME", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("HOMEDRIVE", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("HOMEPATH", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            environment.Remove(name);
        }

        environment["HOME"] = home;
        environment["USERPROFILE"] = home;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig");
        environment["GIT_ALLOW_PROTOCOL"] = "ext";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_OPTIONAL_LOCKS"] = "0";
    }

    private static string Escape(string argument) =>
        argument.Replace('\\', '/').Replace("%", "%%", StringComparison.Ordinal).Replace(" ", "% ", StringComparison.Ordinal);
}
