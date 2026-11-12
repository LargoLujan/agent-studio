namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// The three folders of Agent Studio the door for tools uses (docs/20 D-02 and D-09): the working folder of every tool, empty and outside
/// every project, so that no <c>global.json</c>, <c>.npmrc</c> or <c>.python-version</c> of a repository influences the answer; the
/// temporary folder (<c>TEMP</c> and <c>TMP</c>); and an empty configuration folder for Docker (<c>DOCKER_CONFIG</c>), so that the context
/// the person chose in <c>~/.docker/config.json</c>, which may point to a remote engine, is not used.
/// </summary>
public sealed record DoctorFolders(string WorkingFolder, string TempFolder, string DockerConfigFolder)
{
    /// <summary>The folders under <paramref name="localBase"/> (<c>%LOCALAPPDATA%\AgentStudio</c>): <c>doctor\cwd</c>, <c>doctor\tmp</c> and <c>doctor\docker-config</c>.</summary>
    public static DoctorFolders Under(string localBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBase);
        var doctor = Path.Combine(localBase, "doctor");
        return new DoctorFolders(Path.Combine(doctor, "cwd"), Path.Combine(doctor, "tmp"), Path.Combine(doctor, "docker-config"));
    }

    /// <summary>Creates the three folders when they do not exist. They are never cleaned: the door writes nothing in them.</summary>
    /// <exception cref="IOException">A folder could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder could not be created.</exception>
    public void Ensure()
    {
        Directory.CreateDirectory(WorkingFolder);
        Directory.CreateDirectory(TempFolder);
        Directory.CreateDirectory(DockerConfigFolder);
    }
}
