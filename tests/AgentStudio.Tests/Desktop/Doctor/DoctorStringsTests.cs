using System.IO;
using System.Globalization;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Tests.Application.Diagnostics;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Desktop.RepositoryScreen;
using AgentStudio.Tests.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Desktop.Doctor;

/// <summary>
/// The texts of Auto Doctor (ADR-0054): every check that a source can produce has its title and its sentence for its state, every fix its sentence, every section its
/// name and its ⓘ, in Spanish and in English. The checks are the ones the sources really produce over a set of computers and projects, so a new check without text fails here.
/// </summary>
public sealed class DoctorStringsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static async Task<IReadOnlyList<DoctorCheck>> ProducedAsync()
    {
        var checks = new List<DoctorCheck>();
        var probe = new FakeEnvironmentProbe { Containers = true };

        var computers = new[]
        {
            ScriptedDoor.LikeTheDevelopmentMachine(),
            new ScriptedDoor(),
            new ScriptedDoor().Says(DoctorTool.DotNetListSdks, "").Says(DoctorTool.DotNetListRuntimes, "").Says(DoctorTool.NodeVersion, "?", exit: 1).Says(DoctorTool.GitVersion, "?").Says(DoctorTool.DockerVersionJson, "text").Says(DoctorTool.WslStatus, "text"),
            new ScriptedDoor().Fails(DoctorTool.NodeVersion, DoctorToolOutcome.InsideProject, @"C:\p\node.exe").Fails(DoctorTool.GitVersion, DoctorToolOutcome.InsideProject, @"C:\p\git.exe").Fails(DoctorTool.DockerVersionJson, DoctorToolOutcome.InsideProject, @"C:\p\docker.exe").Fails(DoctorTool.WslStatus, DoctorToolOutcome.InsideProject, @"C:\p\wsl.exe")
                .Fails(DoctorTool.PythonVersion, DoctorToolOutcome.NotInstalled, @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe"),
            new ScriptedDoor().Says(DoctorTool.DockerVersionJson, DoctorFixtures.Read("handmade-docker-version-running.json")).Says(DoctorTool.WslStatus, DoctorFixtures.Read("handmade-wsl-status-en-ubuntu.txt")),
            new ScriptedDoor().Says(DoctorTool.WslStatus, DoctorFixtures.Read("handmade-wsl-status-en-ubuntu-wsl1.txt")),
        };
        foreach (var door in computers)
        {
            foreach (var declared in new[] { true, false })
            {
                var provider = new ToolSnapshotProvider(door, new FixedWebView2(door == computers[1] ? null : "130.0.2849.80"));
                var env = new FakeEnvironmentProbe { Containers = declared };
                checks.AddRange(await new ToolsCheckSource(provider).CheckAsync(null, CancellationToken.None));
                checks.AddRange(await new ContainersCheckSource(provider, env).CheckAsync(new(  "p", @"C:\p"), CancellationToken.None));
            }
        }

        // The project, healthy and in every kind of trouble.
        var project = new AgentStudio.Application.Projects.ProjectContext("p", @"C:\p");
        for (var scenario = 0; scenario < 5; scenario++)
        {
            var git = new AuditRepository("a".PadRight(40, 'a'));
            var remotes = new FakeRemotes();
            var log = new LogRecorder();
            var profiles = new FakeProfiles();
            var env = new FakeEnvironmentProbe();
            var installed = InstalledTools.None;
            switch (scenario)
            {
                case 0:
                    log.Entries.Add(LogRecorder.Entry(AgentStudio.Domain.Decisions.DecisionKind.ProjectSetupApplied));
                    profiles.Stored = SetupFakes.Profile("dotnet");
                    break;
                case 4:
                    log.Entries.Add(LogRecorder.Entry(AgentStudio.Domain.Decisions.DecisionKind.ProjectSetupApplied));
                    profiles.Stored = new AgentStudio.Application.Verification.VerificationProfile("local", [SetupFakes.Profile("dotnet", @".\dotnet.cmd").Steps[0]]);
                    break;
                case 1:
                    env.Layout = new AgentStudio.Application.Diagnostics.ProjectLayout(true, true, ["pre-push"]);
                    env.Inside = [@"C:\p\bin"];
                    remotes.Listing = AgentStudio.Application.Git.RemoteListing.Refused("unsafe-repository");
                    profiles.Stored = SetupFakes.Profile("dotnet");
                    log.Verification = new AgentStudio.Domain.Decisions.DecisionLogVerification(false, 3, "broken");
                    break;
                case 2:
                    env.Layout = new AgentStudio.Application.Diagnostics.ProjectLayout(false, false, []);
                    profiles.Unreadable = true;
                    log.LoadFailure = new IOException("disk");
                    break;
                default:
                    env.Layout = new AgentStudio.Application.Diagnostics.ProjectLayout(true, false, []);
                    git.InspectFailure = new InvalidOperationException("refused");
                    break;
            }

            checks.AddRange(await new ProjectCheckSource(git, remotes, log, profiles, new FixedInventory(installed), env).CheckAsync(project, CancellationToken.None));
        }

        // The providers, in each state, and the failure of a source.
        foreach (var (installation, authentication, version) in new[]
        {
            (AgentStudio.Application.Providers.InstallationState.Installed, AgentStudio.Application.Providers.AuthenticationState.Authenticated, "9.9"),
            (AgentStudio.Application.Providers.InstallationState.Installed, AgentStudio.Application.Providers.AuthenticationState.NotAuthenticated, null),
            (AgentStudio.Application.Providers.InstallationState.Installed, AgentStudio.Application.Providers.AuthenticationState.Unknown, "1.0"),
            (AgentStudio.Application.Providers.InstallationState.NotInstalled, AgentStudio.Application.Providers.AuthenticationState.Unknown, null),
            (AgentStudio.Application.Providers.InstallationState.Error, AgentStudio.Application.Providers.AuthenticationState.Unknown, null),
        })
        {
            foreach (var parked in new[] { true, false })
            {
                var result = new AgentStudio.Application.Providers.ProviderProbeResult(new AgentStudio.Domain.Providers.ProviderId("claude-code"), "Agent", installation, null, version, authentication, null, []);
                var source = new ProviderCheckSource(new ProviderDiagnostics([]), new ProviderCheckOptions(r => r.Version == "9.9", _ => parked));
                checks.AddRange(source.Checks(result));
            }
        }

        var failed = await new DoctorService([new ThrowingSource(DoctorSection.Tools)]).RunAsync(null, CancellationToken.None);
        checks.AddRange(failed.Checks);
        return checks;
    }

    [Fact]
    public async Task Every_check_the_sources_produce_has_its_title_and_its_sentence_in_both_languages()
    {
        var checks = await ProducedAsync();
        var missing = new List<string>();
        foreach (var check in checks)
        {
            foreach (var texts in new[] { Spanish, English })
            {
                if (!texts.Has(DoctorTexts.TitleKey(check.Code)))
                {
                    missing.Add($"{texts.Culture.Name}: {DoctorTexts.TitleKey(check.Code)}");
                }

                if (!texts.Has(DoctorTexts.ExplanationKey(check.Code, check.Status)))
                {
                    missing.Add($"{texts.Culture.Name}: {DoctorTexts.ExplanationKey(check.Code, check.Status)}");
                }
            }
        }

        Assert.True(checks.Count > 100, $"The scenarios produce {checks.Count} checks.");
        Assert.True(missing.Count == 0, "Missing texts: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public async Task Every_fix_a_check_names_has_its_sentence_and_so_does_every_fix_of_the_list()
    {
        var codes = (await ProducedAsync()).Select(c => c.FixCode).Where(c => c is not null).Cast<string>().Concat(DoctorFixCatalog.All.Select(f => f.Code)).Distinct().ToList();
        var missing = new List<string>();
        foreach (var code in codes)
        {
            foreach (var texts in new[] { Spanish, English })
            {
                if (!texts.Has(DoctorTexts.FixKey(code)))
                {
                    missing.Add($"{texts.Culture.Name}: {DoctorTexts.FixKey(code)}");
                }
            }
        }

        Assert.NotEmpty(codes);
        Assert.True(missing.Count == 0, "Missing sentences of fixes: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_section_and_every_state_has_its_name_and_its_info_in_both_languages()
    {
        foreach (var texts in new[] { Spanish, English })
        {
            foreach (var section in Enum.GetValues<DoctorSection>())
            {
                Assert.True(texts.Has(DoctorTexts.SectionKey(section)), DoctorTexts.SectionKey(section));
                foreach (var part in new[] { "Title", "What", "Actions" })
                {
                    Assert.True(texts.Has($"Info_Doctor_{section}_{part}"), $"Info_Doctor_{section}_{part}");
                }
            }

            foreach (var status in Enum.GetValues<DoctorStatus>())
            {
                Assert.True(texts.Has(DoctorTexts.StatusKey(status)));
                Assert.True(texts.Has("Doctor_Check_Generic_" + status));
            }

            foreach (var part in new[] { "Title", "What", "Actions" })
            {
                Assert.True(texts.Has("Info_Doctor_" + part));
            }
        }
    }

    [Fact]
    public async Task The_facts_the_sources_write_have_a_label_in_both_languages()
    {
        var names = (await ProducedAsync()).SelectMany(c => c.Facts).Select(f => f.Name).Distinct().ToList();

        foreach (var texts in new[] { Spanish, English })
        {
            var missing = names.Where(n => !texts.Has("Doctor_Fact_" + n.Replace('-', '_'))).ToList();
            Assert.True(missing.Count == 0, $"{texts.Culture.Name}: facts without a label: " + string.Join(", ", missing));
        }
    }

    [Fact]
    public void The_codes_of_the_texts_follow_the_family_of_the_check()
    {
        Assert.Equal("doctor.provider.install", DoctorTexts.Family("doctor.provider.claude-code.install"));
        Assert.Equal("doctor.provider.session", DoctorTexts.Family("doctor.provider.codex-cli.session"));
        Assert.Equal("doctor.source-failed", DoctorTexts.Family("doctor.source-failed.tools-check-source-1"));
        Assert.Equal("doctor.git", DoctorTexts.Family("doctor.git"));
        Assert.Equal("Doctor_Check_project_decision_log_Ok", DoctorTexts.ExplanationKey("doctor.project.decision-log", DoctorStatus.Ok));
        Assert.Equal("Doctor_Fix_install_provider", DoctorTexts.FixKey("doctor.fix.install-claude-code"));
        Assert.Equal("Doctor_Fix_install_git", DoctorTexts.FixKey("doctor.fix.install-git"));
    }

    [Fact]
    public void An_explanation_with_more_placeholders_than_facts_never_fails()
    {
        var check = new DoctorCheck("doctor.project.profile-tools", DoctorSection.Project, DoctorStatus.Warning, []);

        var text = DoctorTexts.Explanation(Spanish, check);

        Assert.Equal("Faltan herramientas de las pruebas: —.", text);
    }
}
