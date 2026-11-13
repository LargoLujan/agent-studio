using System.Collections;
using AgentStudio.Application.Diagnostics;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// The environment of a tool launched by the door (docs/20 D-02): a short allow list, not a block list. A block list goes stale with every
/// variable of every tool (<c>NODE_OPTIONS</c>, <c>DOTNET_ROOT</c>, <c>PYTHONSTARTUP</c>, <c>DOCKER_HOST</c>, <c>GIT_*</c>…); the allow list fails
/// towards safe. <c>ProcessSpec.Environment</c> is a change over what the child inherits, so every variable the process has that is not on the
/// list goes in as null, which removes it.
/// </summary>
public static class DoctorEnvironment
{
    /// <summary>The variables a tool receives as they are, by name and ignoring case. <c>PATH</c> is here because the runner cleans it (relative entries and projects out) and sets it.</summary>
    public static IReadOnlyList<string> Allowed { get; } =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "USERNAME", "USERDOMAIN", "PATH",

        // The folder of the commands of Windows is set by the runner of processes so that cmd.exe and CreateProcess skip the current folder: it must survive.
        "NoDefaultCurrentDirectoryInExePath",
    ];

    /// <summary>The variables that keep <c>gh</c> from reaching the network when it is launched for its version (docs/21 D-31): no update notice, no telemetry, no prompt.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GhOffline { get; } =
    [
        new("GH_NO_UPDATE_NOTIFIER", "1"),
        new("GH_NO_EXTENSION_UPDATE_NOTIFIER", "1"),
        new("GH_TELEMETRY", "false"),
        new("DO_NOT_TRACK", "1"),
        new("GH_PROMPT_DISABLED", "1"),
    ];

    /// <summary>The environment variables of the current process, as the door reads them by default.</summary>
    public static IReadOnlyDictionary<string, string> CurrentProcess()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.Length > 0)
            {
                result[name] = entry.Value as string ?? string.Empty;
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>ProcessSpec.Environment</c> of <paramref name="tool"/>: null for everything the process has that is not allowed; <c>TEMP</c> and <c>TMP</c>
    /// in the temporary folder of the door; <c>DOTNET_NOLOGO</c> and <c>DOTNET_CLI_TELEMETRY_OPTOUT</c> for every tool; <c>DOCKER_CONFIG</c> in an empty folder
    /// for Docker; <c>WSL_UTF8=1</c> for WSL. It never names <c>PATH</c>: the runner of processes sets the clean one.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> For(DoctorTool tool, DoctorFolders folders, IReadOnlyDictionary<string, string> ambient)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(ambient);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ambient.Keys)
        {
            if (!Allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                environment[name] = null;
            }
        }

        environment["TEMP"] = folders.TempFolder;
        environment["TMP"] = folders.TempFolder;
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (tool is DoctorTool.DockerVersionJson or DoctorTool.DockerVersionDesktopPipe)
        {
            environment["DOCKER_CONFIG"] = folders.DockerConfigFolder;
        }

        if (tool == DoctorTool.WslStatus)
        {
            environment["WSL_UTF8"] = "1";
        }

        // gh has telemetry and an update notice that go out to the network with ANY command, --version too (gh help environment): without these five
        // variables Auto Doctor would break F10-13 (docs/21 D-31). The allow list above already removes GH_TOKEN, GH_HOST, GH_REPO, GH_CONFIG_DIR and GH_DEBUG.
        if (tool == DoctorTool.GhVersion)
        {
            foreach (var (name, value) in GhOffline)
            {
                environment[name] = value;
            }
        }

        return environment;
    }
}
