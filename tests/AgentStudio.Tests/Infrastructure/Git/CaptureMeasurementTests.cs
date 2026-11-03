using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Measures the capture of a real repository: two captures back to back of the repository that
/// <c>AGENTSTUDIO_CAPTURE_MEASURE_ROOT</c> names, read-only (git runs with <c>GIT_OPTIONAL_LOCKS=0</c> and nothing
/// refreshes the index), and what their comparison holds. Skipped unless the variable is set. Nothing may change in the
/// repository between the two captures: then the comparison finds no difference and no fingerprint without metadata,
/// and only the folders past the capture budget are indeterminate. The report goes to the test output and, when
/// <c>AGENTSTUDIO_CAPTURE_MEASURE_REPORT</c> names a file, is appended to it.
/// </summary>
[Trait("Category", "Measurement")]
public sealed class CaptureMeasurementTests
{
    public const string RootVariable = "AGENTSTUDIO_CAPTURE_MEASURE_ROOT";

    public const string ReportVariable = "AGENTSTUDIO_CAPTURE_MEASURE_REPORT";

    [Fact]
    public async Task Two_captures_of_an_untouched_repository_find_no_difference()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"Set {RootVariable} to the root of the repository to measure.");
        var ct = TestContext.Current.CancellationToken;

        // The capture never creates a worktree: their root only has to lie outside the repository.
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(Path.Combine(Path.GetTempPath(), "agentstudio-measure-wt")));
        var clock = Stopwatch.StartNew();
        var before = await manager.CaptureUserRepositoryStateAsync(root!, ct);
        var first = clock.Elapsed;
        clock.Restart();
        var after = await manager.CaptureUserRepositoryStateAsync(root!, ct);
        var second = clock.Elapsed;

        var (differences, indeterminate) = UserRepositoryState.CompareInDetail(before, after);
        var withoutMetadata = Fingerprints(before).Concat(Fingerprints(after)).Where(f => f.Fingerprint.Approximation.Length == 0).Select(f => f.Path).Distinct().ToList();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Repository: {root}")
            .AppendLine(CultureInfo.InvariantCulture, $"Capture 1 (first of the process): {first.TotalMilliseconds:F0} ms; capture 2 (right after): {second.TotalMilliseconds:F0} ms")
            .AppendLine(CultureInfo.InvariantCulture, $"Status lines: {before.PorcelainStatus.Count}; contents: {before.Contents?.Count}; hidden tracked paths: {before.HiddenTracked?.Count}; git files: {before.GitFiles?.Count}")
            .AppendLine(CultureInfo.InvariantCulture, $"Folders: {Folders(before).Count()}, with {Folders(before).Sum(f => f.Entries)} entries listed by metadata")
            .AppendLine(CultureInfo.InvariantCulture, $"Differences: {differences.Count}; indeterminate: {indeterminate.Count}; without metadata: {withoutMetadata.Count}");
        foreach (var line in differences.Concat(indeterminate.Select(u => u.Description)).Concat(withoutMetadata.Select(p => $"No metadata: {p}")))
        {
            report.AppendLine(line);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } file)
        {
            File.AppendAllText(file, report.AppendLine().ToString());
        }

        Assert.True(differences.Count == 0 && withoutMetadata.Count == 0, report.ToString());
    }

    private static IEnumerable<(string Path, FileFingerprint Fingerprint)> Fingerprints(UserRepositoryState state) =>
        new[] { state.Contents, state.HiddenTracked, state.GitFiles }.OfType<IReadOnlyDictionary<string, FileFingerprint>>().SelectMany(part => part).Select(p => (p.Key, p.Value));

    /// <summary>The folders of a capture with the number of entries their approximation (<c>folder:N entries;…</c>) says were listed.</summary>
    private static IEnumerable<(string Path, int Entries)> Folders(UserRepositoryState state) =>
        Fingerprints(state)
            .Where(f => f.Fingerprint.Approximation.StartsWith("folder:", StringComparison.Ordinal))
            .Select(f => (f.Path, int.Parse(f.Fingerprint.Approximation["folder:".Length..f.Fingerprint.Approximation.IndexOf(' ', StringComparison.Ordinal)], CultureInfo.InvariantCulture)));
}
