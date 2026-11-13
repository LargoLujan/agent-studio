using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Environments;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Diagnostics;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>Whether <c>gh</c> is there, as the door of Auto Doctor found it.</summary>
public enum GhInstallState
{
    Installed,
    NotInstalled,

    /// <summary>Found only inside the folder of an open project: it is not used.</summary>
    InsideProject,

    /// <summary>It is there but did not say its version in the form of <c>gh --version</c>.</summary>
    Unreadable,
}

/// <summary>What <c>gh --version</c> said: <see cref="Version"/> is null unless the state is <see cref="GhInstallState.Installed"/>.</summary>
public sealed record GhCliReading(GhInstallState State, ToolVersion? Version);

/// <summary>
/// The version of <c>gh</c> and what the adapter declares for it (docs/21 D-09 and D-14). The version goes through <see cref="IDoctorToolRunner"/> (the door of Auto Doctor, with the variables that
/// keep <c>gh</c> from the network): this is the only place where the adapter and the source of Auto Doctor learn it, so both say the same. The tested version, 2.96.0, is also the minimum (nothing
/// was found about from which version <c>--input</c>, <c>-i</c> and the exit code 4 exist): below it nothing is declared; above it everything is, with a warning that it was not tested.
/// </summary>
public static class GitHubCli
{
    /// <summary>The version the adapter was written against, with handwritten answers: it is also the minimum.</summary>
    public static ToolVersion Tested { get; } = ToolVersion.Parse("2.96.0");

    /// <summary>What the adapter can do with a working <c>gh</c>. <c>ReadWorkflowRuns</c> is not here: no member of the contract delivers it.</summary>
    internal const HostingCapability Declared =
        HostingCapability.ReadRepository | HostingCapability.ReadBranchRules | HostingCapability.OpenPullRequest | HostingCapability.UpdatePullRequest | HostingCapability.Comment
        | HostingCapability.RequestReviewers | HostingCapability.ReadReviews | HostingCapability.ReadChecks | HostingCapability.Merge | HostingCapability.ClosePullRequest
        | HostingCapability.DeleteBranch | HostingCapability.CommitStatus | HostingCapability.ReadDeployments | HostingCapability.ConditionalRequests | HostingCapability.DraftPullRequest;

    public static async Task<GhCliReading> ReadAsync(IDoctorToolRunner door, string? projectRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(door);
        return Interpret(await door.RunAsync(DoctorTool.GhVersion, projectRoot, ct).ConfigureAwait(false));
    }

    public static GhCliReading Interpret(DoctorToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case DoctorToolOutcome.NotInstalled:
                return new GhCliReading(GhInstallState.NotInstalled, null);
            case DoctorToolOutcome.InsideProject:
                return new GhCliReading(GhInstallState.InsideProject, null);
            case DoctorToolOutcome.Completed when result.ExitCode == 0 && !result.Truncated && ToolOutputParsers.Version(DoctorTool.GhVersion, result.Output) is { } version:
                return new GhCliReading(GhInstallState.Installed, version);
            default:
                return new GhCliReading(GhInstallState.Unreadable, null);
        }
    }

    /// <summary>The version is not the tested one (a warning, not a refusal).</summary>
    public static bool IsUntested(ToolVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.CompareTo(Tested) != 0;
    }

    /// <summary>What is declared for a reading: nothing when <c>gh</c> is not usable or is older than the tested version; everything the adapter knows otherwise.</summary>
    public static HostingAvailability Availability(GhCliReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var installed = reading.State is GhInstallState.Installed or GhInstallState.Unreadable;
        if (reading.State != GhInstallState.Installed || reading.Version is not { } version || version.CompareTo(Tested) < 0)
        {
            return new HostingAvailability(installed, reading.Version is { } old ? new Version(old.Major, old.Minor, old.Patch) : null, HostingCapabilities.None);
        }

        return new HostingAvailability(true, new Version(version.Major, version.Minor, version.Patch), new HostingCapabilities(Declared, [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase], IsUntested(version)));
    }
}
