using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Memory.Consistency;
using AgentStudio.Tests.Infrastructure.Diagnostics;
using AgentStudio.Tests.Memory.Diagrams;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase6;

/// <summary>
/// F6-1, F6-7 and D-30 of docs/20 end to end (section 9.8): Auto Doctor over a test repository with a <c>global.json</c>, through <see cref="IDoctorService"/> with the real
/// sources of the app's composition (tools, project, declared environment, containers and the line of the discrepancies). The door for tools answers from recorded output
/// (no tool is started) and the sources of the providers are left out (a probe would start a CLI of a provider); nothing goes to the network.
/// </summary>
public sealed class Phase6AutoDoctorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Phase6Host Host(FixedHead head, MovingGraphBuilder builder) => Phase6Host.Create(services =>
    {
        services.AddSingleton<IDoctorToolRunner>(ScriptedDoor.LikeTheDevelopmentMachine());
        services.AddSingleton<IWebView2Probe>(new FixedWebView2("130.0.2849.80"));
        services.AddSingleton<IRepositoryHistoryReader>(head);
        services.AddSingleton<IRepositorySnapshots>(new FilesSnapshots(("README.md", "[roto](no.md)")));
        services.AddSingleton<ICodeGraphBuilder>(builder);
    });

    private static IDoctorService DoctorOf(Phase6Host host) => new DoctorService(
        host.Provider.GetServices<IDoctorCheckSource>().Where(s => s is not ProviderCheckSource),
        host.Provider.GetServices<IDoctorRunScope>());

    [Fact]
    public async Task A_repository_with_a_global_json_is_read_by_every_real_source_and_the_discrepancies_line_goes_from_not_calculated_to_a_count()
    {
        var head = new FixedHead("fixed", Phase6Host.Commit);
        var builder = new MovingGraphBuilder(DiagramFixtures.Graph(withInference: false));
        using var host = Host(head, builder);
        var root = host.Temp.CreateDirectory("repositorio de prueba");
        File.WriteAllText(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\"}}");
        var project = await host.Provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        var doctor = DoctorOf(host);

        var first = await doctor.RunAsync(project, Ct);

        // The sources of the five sections of D-01 (providers aside) all answered, and «Entorno declarado» compared the file with what the door said is installed.
        var sections = first.Checks.Select(c => c.Section).Distinct().Order().ToArray();
        Assert.Equal([DoctorSection.Tools, DoctorSection.Project, DoctorSection.DeclaredEnvironment, DoctorSection.Containers], sections);
        var sdk = Assert.Single(first.Checks, c => c.Code == EnvironmentCheckCodes.DotNetSdk);
        Assert.Equal(DoctorStatus.Ok, sdk.Status);
        Assert.Equal("10.0.303", sdk.Fact("resolved"));

        // Nothing saved for the commit yet: the line says so and sends the person to Auto Graph (it never starts the analysis).
        var before = Assert.Single(first.Checks, c => c.Code == DiscrepanciesCheckSource.Code);
        Assert.Equal(DoctorStatus.Unknown, before.Status);
        Assert.Equal(DoctorFixCatalog.OpenGraph, before.FixCode);
        var navigation = Assert.IsType<DoctorFix.Navigate>(DoctorFixCatalog.Find(before.FixCode));
        Assert.Equal("graph", navigation.Target);

        // Auto Graph calculates (this is what «Calcular» does); the line now counts what it found, with the code of each kind.
        var analysis = await host.Provider.GetRequiredService<ICommitAnalysisService>().AnalyzeAsync(project, head.Commit, Ct);
        var second = await doctor.RunAsync(project, Ct);

        var after = Assert.Single(second.Checks, c => c.Code == DiscrepanciesCheckSource.Code);
        Assert.Equal(analysis.Consistency.Findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), after.Fact("count"));
        Assert.Equal(DoctorStatus.Warning, after.Status);
        Assert.Equal("1", after.Fact(ConsistencyCodes.LinkBroken));
        Assert.Equal(DoctorFixCatalog.OpenGraph, after.FixCode);
        Assert.Equal(Phase6Host.Commit[..7], after.Fact("describes"));
    }

    [Fact]
    public async Task Without_an_open_project_the_declared_environment_and_the_discrepancies_have_nothing_to_say_and_no_provider_is_asked()
    {
        var head = new FixedHead("fixed", Phase6Host.Commit);
        using var host = Host(head, new MovingGraphBuilder(DiagramFixtures.Graph(withInference: false)));

        var report = await DoctorOf(host).RunAsync(null, Ct);

        Assert.DoesNotContain(report.Checks, c => c.Section == DoctorSection.DeclaredEnvironment);
        Assert.DoesNotContain(report.Checks, c => c.Code == DiscrepanciesCheckSource.Code);
        Assert.DoesNotContain(report.Checks, c => c.Section == DoctorSection.Providers);
        Assert.Contains(report.Checks, c => c.Section == DoctorSection.Tools);
    }
}
