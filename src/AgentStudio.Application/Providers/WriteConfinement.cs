using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

/// <summary>
/// Whether an agent CLI, as installed now, keeps the file writes of an executor without a shell inside its working
/// directory (ADR-0018, «Medición»). Only a version on which Agent Studio measured it does: any other version, any other
/// CLI and a CLI that cannot be asked count as able to write wherever the user can (fail closed). Asking runs no prompt
/// and spends no quota (<c>--version</c> only).
/// </summary>
public interface IWriteConfinementProbe
{
    /// <summary>True only when the installed version of <paramref name="provider"/> is one where the confinement was measured. Never throws but for cancellation.</summary>
    Task<bool> IsWriteConfinedAsync(ProviderId provider, CancellationToken cancellationToken);
}

/// <summary>
/// The permissions whose confinement to the worktree was measured (ADR-0018, «Medición»): an executor that writes files
/// in its worktree with the file tools Read, Grep, Glob, Edit and Write, without a shell, network or any other folder. An
/// <see cref="IWriteConfinementProbe"/> that says a CLI is confined speaks for these permissions and no wider ones.
/// </summary>
public static class MeasuredWriteConfinement
{
    /// <summary>The measured permissions: what the engine gives the default executor in a task that writes.</summary>
    public static PermissionProfile Executor { get; } = new(true, false, false, ["Read", "Grep", "Glob", "Edit", "Write"], [], []);

    /// <summary>
    /// True when <paramref name="permissions"/> give no more than <see cref="Executor"/>: no shell, no network, no
    /// additional directory, and no allowed tool but those of <see cref="Executor"/> (fewer is fine; so is denying more).
    /// Any other tool was not measured, so an executor that has it counts as able to write wherever the user can.
    /// </summary>
    public static bool Covers(PermissionProfile permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return !permissions.AllowShell
            && !permissions.AllowNetwork
            && permissions.AdditionalDirectories.Count == 0
            && permissions.AllowedTools.All(tool => Executor.AllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase));
    }
}
