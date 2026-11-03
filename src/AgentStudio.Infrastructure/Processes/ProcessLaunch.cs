using System.Diagnostics;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes.Isolation;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>Start rules shared by <see cref="ProcessRunner"/> and <see cref="StreamingProcessRunner"/>, with or without isolation.</summary>
internal static class ProcessLaunch
{
    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // cmd.exe re-parses the arguments of .cmd/.bat files, so these characters would be interpreted as syntax.
    private static readonly char[] CmdMetacharacters = ['&', '|', '<', '>', '^', '%', '"', '!', '(', ')', '\r', '\n'];

    /// <summary>
    /// Validates the spec and starts the process. Without isolation it is a <see cref="Process"/> tied to the Agent Studio
    /// Job Object; with <see cref="ProcessIsolation.LowIntegrity"/> it is a Low-integrity process in a Job Object of its
    /// own (ADR-0017). An isolation that cannot be applied throws <see cref="ProcessIsolationException"/>: there is no
    /// path from an isolated spec to a process without isolation.
    /// </summary>
    internal static IStartedProcess Start(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidateArguments(spec);

        return spec.Isolation switch
        {
            ProcessIsolation.None => StartWithoutIsolation(spec),
            ProcessIsolation.LowIntegrity => OperatingSystem.IsWindows()
                ? LowIntegrityProcess.Start(spec)
                : throw new ProcessIsolationException("Low integrity isolation only exists on Windows; the process was not started."),
            _ => throw new ProcessIsolationException($"Unknown isolation '{spec.Isolation}'; the process was not started."),
        };
    }

    private static ManagedProcess StartWithoutIsolation(ProcessSpec spec)
    {
        var process = new Process { StartInfo = CreateStartInfo(spec) };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        // A failure is logged and ignored: KillTree remains the fallback.
        JobObject.Shared.TryAssign(process);
        return new ManagedProcess(process);
    }

    internal static void ValidateArguments(ProcessSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.FileName);
        ArgumentNullException.ThrowIfNull(spec.Arguments);

        // A NUL truncates the command line at the OS level and CR/LF or other control characters can split it:
        // reject them everywhere (native executables included), before the batch-file rules.
        if (ContainsControlCharacter(spec.FileName))
        {
            throw new ArgumentException("The executable path contains a control character.", nameof(spec));
        }

        foreach (var argument in spec.Arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (ContainsControlCharacter(argument))
            {
                throw new ArgumentException("An argument contains a control character (NUL, CR, LF...); free text must go through standard input.", nameof(spec));
            }
        }

        if (!OperatingSystem.IsWindows() || !IsBatchFile(spec.FileName))
        {
            return;
        }

        foreach (var argument in spec.Arguments)
        {
            if (argument.IndexOfAny(CmdMetacharacters) >= 0)
            {
                throw new ArgumentException(
                    "An argument contains cmd.exe metacharacters and cannot be passed to a .cmd/.bat file; free text must go through standard input.",
                    nameof(spec));
            }
        }
    }

    private static bool ContainsControlCharacter(string text) => text.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0;

    internal static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            RedirectStandardInput = spec.StandardInput is not null,
        };

        if (spec.StandardInput is not null)
        {
            startInfo.StandardInputEncoding = Utf8NoBom;
        }

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        if (spec.Environment is not null)
        {
            foreach (var (name, value) in spec.Environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        return startInfo;
    }

    internal static async Task WriteStandardInputAsync(IStartedProcess process, string text, bool close = true)
    {
        try
        {
            await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            if (close)
            {
                process.StandardInput.Close();
            }
        }
        catch (IOException)
        {
            // The process ended (or was killed) before reading all of its input.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Windows drops trailing dots and spaces, so "x.cmd." still runs x.cmd through cmd.exe.
    private static bool IsBatchFile(string fileName)
    {
        var name = fileName.TrimEnd('.', ' ');
        return name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
    }
}
