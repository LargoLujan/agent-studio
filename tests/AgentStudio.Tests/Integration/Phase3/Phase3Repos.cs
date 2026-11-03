using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// The users' repositories of the Phase 3 integration tests: a <see cref="TempGitRepo"/> (local identity, a space and an
/// accent in its path) with what the user committed on top before running Agent Studio.
/// </summary>
internal static class Phase3Repos
{
    public const string MultiplyRequirement = "RQ-01";

    /// <summary>
    /// A minimal .NET solution, the repository of the F3-8 scenario (docs/13 section 6): the library <c>src/Calc</c>
    /// (<c>CalcLib.Calc</c>, a static partial class with <c>Add</c>) and the xUnit v3 project <c>tests/Calc.Tests</c> with
    /// one test that passes, on the SDK and the package versions of this repository, so it restores from the local NuGet
    /// cache. Empty build files stop MSBuild from looking further up, and <c>bin/</c> and <c>obj/</c> are ignored.
    /// </summary>
    public static TempGitRepo DotNet()
    {
        var repo = TempGitRepo.Create();
        try
        {
            var root = RepositoryRoot();
            var versions = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
                .Descendants("PackageVersion")
                .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.Ordinal);

            repo.Write("global.json", File.ReadAllText(Path.Combine(root, "global.json")));
            repo.Write(".gitignore", "bin/\nobj/\n*.log\n");
            repo.Write("Directory.Build.props", "<Project />\n");
            repo.Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
            repo.Write("Calc.slnx", "<Solution>\n  <Project Path=\"src/Calc/Calc.csproj\" />\n  <Project Path=\"tests/Calc.Tests/Calc.Tests.csproj\" />\n</Solution>\n");
            repo.Write("src/Calc/Calc.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <RootNamespace>CalcLib</RootNamespace>
                  </PropertyGroup>
                </Project>

                """.ReplaceLineEndings("\n"));
            repo.Write("src/Calc/Calc.cs", """
                namespace CalcLib;

                /// <summary>Operaciones de la calculadora.</summary>
                public static partial class Calc
                {
                    public static int Add(int a, int b) => a + b;
                }

                """.ReplaceLineEndings("\n"));
            repo.Write("tests/Calc.Tests/Calc.Tests.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <IsPackable>false</IsPackable>
                    <RootNamespace>CalcLib.Tests</RootNamespace>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{versions["Microsoft.NET.Test.Sdk"]}" />
                    <PackageReference Include="xunit.v3" Version="{versions["xunit.v3"]}" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="{versions["xunit.runner.visualstudio"]}" />
                  </ItemGroup>
                  <ItemGroup>
                    <Using Include="Xunit" />
                  </ItemGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\..\src\Calc\Calc.csproj" />
                  </ItemGroup>
                </Project>

                """.ReplaceLineEndings("\n"));
            repo.Write("tests/Calc.Tests/CalcTests.cs", """
                namespace CalcLib.Tests;

                public sealed class CalcTests
                {
                    [Fact]
                    public void Add_suma() => Assert.Equal(5, Calc.Add(2, 3));
                }

                """.ReplaceLineEndings("\n"));
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "Proyecto .NET de prueba");
            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>A repository with a PowerShell verification script the user committed (no NuGet needed).</summary>
    public static TempGitRepo WithScript(string fileName, string script)
    {
        var repo = TempGitRepo.Create();
        try
        {
            repo.Write(fileName, script.ReplaceLineEndings("\n"));
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "Script de verificación");
            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A step of the trusted profile that runs a script of the repository with PowerShell and reads the JUnit report it
    /// writes into the fresh results folder.
    /// </summary>
    public static VerificationStep ScriptStep(string id, string title, string script, TimeSpan timeout) => new(
        id,
        title,
        "pwsh",
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "{results}/" + id + ".xml"],
        EvidenceKind.TestRun,
        TestReportFormat.JUnit,
        "{results}/" + id + ".xml",
        timeout);

    /// <summary>A C# file that adds one operation to <c>CalcLib.Calc</c>.</summary>
    public static string Operation(string name, char symbol) => $$"""
        namespace CalcLib;

        public static partial class Calc
        {
            public static int {{name}}(int a, int b) => a {{symbol}} b;
        }

        """.ReplaceLineEndings("\n");

    /// <summary>A test class with one test of an operation of <c>CalcLib.Calc</c>.</summary>
    public static string OperationTest(string name, int a, int b, int expected) => $$"""
        namespace CalcLib.Tests;

        public sealed class {{name}}Tests
        {
            [Fact]
            public void {{name}}_da_el_resultado() => Assert.Equal({{expected}}, Calc.{{name}}({{a}}, {{b}}));
        }

        """.ReplaceLineEndings("\n");

    private static string RepositoryRoot([CallerFilePath] string callerFile = "")
    {
        // tests/AgentStudio.Tests/Integration/Phase3/<this file>
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "global.json")), $"Repository root not found from {callerFile}.");
        return root;
    }
}
