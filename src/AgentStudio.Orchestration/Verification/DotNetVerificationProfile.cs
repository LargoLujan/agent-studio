using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// A proposed verification profile for a .NET repository whose tests use xUnit v3 on Microsoft.Testing.Platform, with
/// the flags verified in spike S4 (docs/investigacion/2026-10-15-spikes-fase-3.md): <c>dotnet build</c>, then
/// <c>dotnet test --no-build</c> writing a TRX report into a fresh results folder, with zero tests treated as a
/// failure. The test step requires the build (<see cref="VerificationStep.Requires"/>): a verification starts from a fresh
/// copy of the commit, so a plan that names only <c>test</c> still gets its binaries built first. It is never applied on
/// its own: the user reviews it and saves it with <see cref="VerificationProfileFile.SaveAsync"/>. A repository on MSTest
/// or NUnit needs other report flags, and one with several solutions in its root needs the solution named.
/// </summary>
public static class DotNetVerificationProfile
{
    public const string ProfileName = "dotnet";

    public static VerificationProfile Propose() => new(
        ProfileName,
        [
            new VerificationStep(
                "build",
                "Compilar",
                "dotnet",
                ["build"],
                EvidenceKind.BuildLog,
                TestReportFormat.None,
                null,
                TimeSpan.FromMinutes(10)),
            new VerificationStep(
                "test",
                "Pruebas",
                "dotnet",
                [
                    "test", "--no-build", "--results-directory", VerificationRunner.ResultsFolderToken, "--",
                    "--report-xunit-trx", "--report-xunit-trx-filename", "test.trx", "--zero-tests-policy", "strict",
                ],
                EvidenceKind.TestRun,
                TestReportFormat.Trx,
                VerificationRunner.ResultsFolderToken + "/test.trx",
                TimeSpan.FromMinutes(20),
                Requires: ["build"]),
        ]);
}
