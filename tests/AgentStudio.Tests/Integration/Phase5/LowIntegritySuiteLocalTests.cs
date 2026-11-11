using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// A quick way to run this very test assembly at Low integrity, to see what the verifier of the audit would see
/// (docs/08, F5-5, ADR-0017 addendum). It is not part of any run: it only does something with
/// <c>AGENTSTUDIO_LOW_RUN=1</c>, and it carries <c>LocalTools</c>, which CI and the audit profile leave out.
/// <para>It starts <c>AgentStudio.Tests.exe</c> from the folder of the running assembly with
/// <see cref="ProcessIsolation.LowIntegrity"/> (the same runner the verifier uses), with <c>TEMP</c>, <c>TMP</c> and the
/// .NET home inside a folder labelled Low, as the work area of the verifier does, and the rest of the environment as it is.
/// It is an approximation of the full path (the audit builds a clean copy of the commit first, which takes minutes): the
/// binaries are those already built here. For the full path use
/// <c>Live.Phase5AuditDemoLiveTests.Agent_Studio_suite_at_low_integrity_without_quota</c>.</para>
/// <para><c>AGENTSTUDIO_LOW_RUN_ARGS</c> are the arguments of the test executable (for example
/// <c>--filter-not-trait "Category=LocalTools" --filter-class ...</c>), split on spaces, and
/// <c>AGENTSTUDIO_LOW_RUN_OUT</c> the file that receives its output.</para>
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class LowIntegritySuiteLocalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task This_assembly_run_at_Low_integrity_writes_its_output_to_the_file_asked_for()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("AGENTSTUDIO_LOW_RUN") == "1", "Set AGENTSTUDIO_LOW_RUN=1 to run the suite at Low integrity.");
        var output = Environment.GetEnvironmentVariable("AGENTSTUDIO_LOW_RUN_OUT");
        Assert.False(string.IsNullOrWhiteSpace(output), "Set AGENTSTUDIO_LOW_RUN_OUT to the file that receives the output.");
        var arguments = (Environment.GetEnvironmentVariable("AGENTSTUDIO_LOW_RUN_ARGS") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "as-lowrun-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "home"));
        try
        {
            var label = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{root}\" /setintegritylevel (OI)(CI)L") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            await label.WaitForExitAsync(Ct);
            Assert.Equal(0, label.ExitCode);

            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEMP"] = Path.Combine(root, "tmp"),
                ["TMP"] = Path.Combine(root, "tmp"),
                ["DOTNET_CLI_HOME"] = Path.Combine(root, "home"),
                ["AGENTSTUDIO_LOW_RUN"] = null,
                ["AGENTSTUDIO_PHASE5_DEMOS"] = null,
                ["AGENTSTUDIO_PROVIDER_TESTS"] = null,
            };
            // The binaries stay where they are (Medium): some tests find the source tree from them, so a copy elsewhere breaks
            // them. The one that plants a file next to its assembly (ExecutableLocatorTests) fails here and passes in the audit,
            // where the whole copy of the commit is Low.
            var executable = Path.Combine(AppContext.BaseDirectory, "AgentStudio.Tests.exe");
            var result = await new ProcessRunner().RunAsync(
                new ProcessSpec(executable, arguments, root, environment, TimeSpan.FromMinutes(90), Isolation: ProcessIsolation.LowIntegrity),
                Ct);

            await File.WriteAllTextAsync(output!, result.StandardOutput + Environment.NewLine + result.StandardError, Ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"Exit code {result.ExitCode}; output in {output}.");
            Assert.False(result.TimedOut, "The run at Low integrity timed out.");
        }
        finally
        {
            TempFolders.Delete(root);
        }
    }
}
