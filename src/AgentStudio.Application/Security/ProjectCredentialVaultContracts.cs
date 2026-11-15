namespace AgentStudio.Application.Security;

/// <summary>
/// Storage of Agent Studio's own secrets for one project (docs/16 D-03). Binding the storage does not authorize use or
/// disclosure: the caller must obtain the configured human presence before reading, as with <see cref="ICredentialVault"/>.
/// </summary>
public interface IProjectCredentialVault : ICredentialVault, ICredentialVaultProbe
{
    string ProjectId { get; }
}

/// <summary>
/// Selects a vault by the stable project id, never by a project-controlled path. It does not read or migrate the global
/// skills doctor's token, provider credentials or any existing secret. Project ids are case-sensitive.
/// </summary>
public interface IProjectCredentialVaults
{
    IProjectCredentialVault ForProject(string projectId);
}
