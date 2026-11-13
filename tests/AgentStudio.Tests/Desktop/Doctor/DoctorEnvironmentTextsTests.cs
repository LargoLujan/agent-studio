using System.IO;
using System.Globalization;
using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Environments;

namespace AgentStudio.Tests.Desktop.Doctor;

/// <summary>
/// The texts of the checks of the declared environment (WP-E, docs/20 D-07 and D-08) in the panel of Auto Doctor, in Spanish and in English: every code of
/// <see cref="EnvironmentCheckCodes"/> has its title and its sentence for each state it can be in, every fact its label, the fixed values their translation,
/// and every fix the checks name is on the closed list of Auto Doctor (a fix that is not there paints no button).
/// </summary>
public sealed class DoctorEnvironmentTextsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
    private static readonly IUiTextProvider[] Cultures = [Spanish, English];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The states each check can be in, as the comparer writes them (EnvironmentComparer): a code that gains a state without a text fails here.
    private static readonly IReadOnlyDictionary<string, DoctorStatus[]> States = new Dictionary<string, DoctorStatus[]>(StringComparer.Ordinal)
    {
        [EnvironmentCheckCodes.None] = [DoctorStatus.NotApplicable],
        [EnvironmentCheckCodes.DotNetSdk] = [DoctorStatus.Ok, DoctorStatus.Error, DoctorStatus.Unknown],
        [EnvironmentCheckCodes.DotNetSdkInRepo] = [DoctorStatus.Warning],
        [EnvironmentCheckCodes.DotNetLocalTools] = [DoctorStatus.Ok, DoctorStatus.Warning, DoctorStatus.Unknown],
        [EnvironmentCheckCodes.NodeNvmrc] = Range(),
        [EnvironmentCheckCodes.NodeVersionFile] = Range(),
        [EnvironmentCheckCodes.NodeEngine] = Range(),
        [EnvironmentCheckCodes.NpmEngine] = Range(),
        [EnvironmentCheckCodes.PythonVersionFile] = Range(),
        [EnvironmentCheckCodes.PythonRequires] = Range(),
        [EnvironmentCheckCodes.PythonVenv] = [DoctorStatus.Ok, DoctorStatus.Warning, DoctorStatus.Unknown, DoctorStatus.NotApplicable],
        [EnvironmentCheckCodes.Containers] = [DoctorStatus.Ok, DoctorStatus.Warning, DoctorStatus.Error],
        [EnvironmentCheckCodes.NotRead] = [DoctorStatus.Warning],
    };

    private static DoctorStatus[] Range() => [DoctorStatus.Ok, DoctorStatus.Warning, DoctorStatus.Error, DoctorStatus.Unknown];

    private static ToolVersion V(string text) => ToolVersion.Parse(text);

    private static InstalledTools Machine(DockerState docker) =>
        new(
            [new InstalledSdk(V("10.0.103"), null)],
            [],
            new InstalledTool(V("22.11.0"), null),
            new InstalledTool(V("10.9.0"), null),
            new InstalledTool(V("3.12.5"), null),
            false,
            null,
            new DockerStatus(docker, docker == DockerState.NotInstalled ? null : V("27.0.1"), docker == DockerState.Running ? V("27.0.1") : null),
            WslStatus.NotInstalled,
            null);

    [Fact]
    public void Every_code_of_the_declared_environment_has_its_title_and_its_sentence_for_each_state_in_every_language()
    {
        var missing = new List<string>();
        foreach (var code in EnvironmentCheckCodes.All)
        {
            Assert.True(States.ContainsKey(code), $"{code} has no list of states in this test.");
            foreach (var texts in Cultures)
            {
                if (!texts.Has(DoctorTexts.TitleKey(code)))
                {
                    missing.Add($"{texts.Culture.Name}: {DoctorTexts.TitleKey(code)}");
                }

                foreach (var status in States[code])
                {
                    if (!texts.Has(DoctorTexts.ExplanationKey(code, status)))
                    {
                        missing.Add($"{texts.Culture.Name}: {DoctorTexts.ExplanationKey(code, status)}");
                    }
                }
            }
        }

        Assert.Equal(States.Count, EnvironmentCheckCodes.All.Count);
        Assert.True(missing.Count == 0, "Missing texts: " + string.Join(", ", missing));
    }

    [Fact]
    public void What_cannot_be_compared_is_never_told_as_met()
    {
        foreach (var texts in Cultures)
        {
            foreach (var code in EnvironmentCheckCodes.All.Where(c => States[c].Contains(DoctorStatus.Unknown)))
            {
                var sentence = texts.Get(DoctorTexts.ExplanationKey(code, DoctorStatus.Unknown));
                var says = texts.Culture.TwoLetterISOLanguageName == "es" ? "no se puede comparar" : "cannot be compared";
                Assert.Contains(says, sentence, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("cumple", sentence.Replace("no se puede comparar", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task The_checks_the_comparer_really_produces_have_texts_labels_and_a_fix_that_is_on_the_list()
    {
        var reader = new DeclaredEnvironmentReader();
        var checks = new List<DoctorCheck>();
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Environments", "Fixtures");
        foreach (var fixture in Directory.EnumerateDirectories(fixtures))
        {
            var declared = await reader.ReadAsync(fixture, Ct);
            foreach (var installed in new[] { Machine(DockerState.Running), Machine(DockerState.EngineStopped), Machine(DockerState.NotInstalled), InstalledTools.None })
            {
                foreach (var cache in new IReadOnlySet<string>?[] { null, new HashSet<string>() })
                {
                    checks.AddRange(EnvironmentComparer.Compare(declared, installed, fixture, cache));
                }
            }
        }

        Assert.NotEmpty(checks);
        Assert.True(checks.Select(c => c.Code).Distinct().Count() >= 8, "The fixtures produce few kinds of check.");
        foreach (var texts in Cultures)
        {
            foreach (var check in checks)
            {
                Assert.True(texts.Has(DoctorTexts.TitleKey(check.Code)), DoctorTexts.TitleKey(check.Code));
                Assert.True(texts.Has(DoctorTexts.ExplanationKey(check.Code, check.Status)), DoctorTexts.ExplanationKey(check.Code, check.Status));
                foreach (var fact in check.Facts)
                {
                    Assert.True(texts.Has("Doctor_Fact_" + fact.Name.Replace('-', '_')), $"{texts.Culture.Name}: label of {fact.Name}");
                }

                if (check.FixCode is not null)
                {
                    Assert.True(texts.Has(DoctorTexts.FixKey(check.FixCode)), DoctorTexts.FixKey(check.FixCode));
                }
            }
        }

        Assert.All(checks.Where(c => c.FixCode is not null), c => Assert.NotNull(DoctorFixCatalog.Find(c.FixCode)));
    }

    [Fact]
    public void Every_fix_the_package_names_is_a_guide_of_the_closed_list_with_its_sentence_in_every_language()
    {
        var codes = typeof(EnvironmentCheckCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Fix", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(6, codes.Count);
        foreach (var code in codes)
        {
            var fix = DoctorFixCatalog.Find(code);
            Assert.True(fix is DoctorFix.Guide, $"{code} is not a guide of the list.");
            foreach (var texts in Cultures)
            {
                Assert.True(texts.Has(DoctorTexts.FixKey(code)), $"{texts.Culture.Name}: {DoctorTexts.FixKey(code)}");
            }
        }
    }

    [Theory]
    [InlineData("none", "ninguno", "none")]
    [InlineData("in the NuGet cache", "descargada", "downloaded")]
    [InlineData("not in the NuGet cache", "sin descargar", "not downloaded")]
    [InlineData("not checked", "sin comprobar", "not checked")]
    [InlineData("store-alias", "solo el acceso directo de la Tienda", "only the Store shortcut")]
    [InlineData("docker not installed", "Docker no está instalado", "Docker is not installed")]
    [InlineData("docker running (client 27.0.1, engine 27.0.2)", "Docker en marcha (cliente 27.0.1, motor 27.0.2)", "Docker running (client 27.0.1, engine 27.0.2)")]
    [InlineData("docker engine stopped (client 27.0.1)", "Docker instalado, motor parado (cliente 27.0.1)", "Docker installed, engine stopped (client 27.0.1)")]
    public void The_fixed_values_of_the_installed_column_are_painted_by_code_in_the_language_in_use(string value, string spanish, string english)
    {
        var check = new DoctorCheck(EnvironmentCheckCodes.Containers, DoctorSection.DeclaredEnvironment, DoctorStatus.Ok, [DoctorFact.Of(DoctorFactNames.Installed, value)]);

        Assert.Equal(spanish, DoctorTexts.FactValue(Spanish, check, check.Facts[0]));
        Assert.Equal(english, DoctorTexts.FactValue(English, check, check.Facts[0]));
    }

    [Fact]
    public void The_phrases_the_comparer_writes_are_the_shared_constants_and_the_evidence_text_did_not_change()
    {
        Assert.Equal("docker running (client 27.0.1, engine 27.0.2)", EnvironmentPhrases.DockerRunning("27.0.1", "27.0.2"));
        Assert.Equal("docker engine stopped (client 27.0.1)", EnvironmentPhrases.DockerStopped("27.0.1"));
        Assert.Equal("compose size 120 bytes", EnvironmentPhrases.ComposeSize("120"));
        Assert.Equal("image redis:7", EnvironmentPhrases.Labelled(EnvironmentPhrases.ImageLabel, "redis:7"));
        Assert.Equal("compose file docker-compose.yml", EnvironmentPhrases.Labelled(EnvironmentPhrases.ComposeFileLabel, "docker-compose.yml"));
        Assert.Equal(["none", "absent", "store-alias", "in the NuGet cache", "not in the NuGet cache", "not checked", "docker not installed"],
            [EnvironmentPhrases.None, EnvironmentPhrases.Absent, EnvironmentPhrases.StoreAlias, EnvironmentPhrases.InNuGetCache, EnvironmentPhrases.NotInNuGetCache, EnvironmentPhrases.NotChecked, EnvironmentPhrases.DockerNotInstalled]);

        // What the panel recognises is what the builders write: the compose size is painted too.
        var check = new DoctorCheck(EnvironmentCheckCodes.Containers, DoctorSection.DeclaredEnvironment, DoctorStatus.Ok, [DoctorFact.Of(DoctorFactNames.Declared, EnvironmentPhrases.ComposeSize("120"))]);
        Assert.NotEqual("compose size 120 bytes", DoctorTexts.FactValue(Spanish, check, check.Facts[0]));
    }

    [Fact]
    public void The_values_that_came_from_a_file_are_data_and_a_fact_of_another_section_is_never_touched()
    {
        var declared = new DoctorCheck(EnvironmentCheckCodes.NodeEngine, DoctorSection.DeclaredEnvironment, DoctorStatus.Ok, [DoctorFact.Of(DoctorFactNames.Declared, "none"), DoctorFact.Of(DoctorFactNames.Installed, "22.11.0")]);
        var other = new DoctorCheck("doctor.node", DoctorSection.Tools, DoctorStatus.Ok, [DoctorFact.Of(DoctorFactNames.Installed, "none")]);
        var labelled = new DoctorCheck(EnvironmentCheckCodes.Containers, DoctorSection.DeclaredEnvironment, DoctorStatus.Ok, [DoctorFact.Of(DoctorFactNames.Declared, "image redis:7"), DoctorFact.Of(DoctorFactNames.Declared, "path ..\\sdk")]);

        Assert.Equal("none", DoctorTexts.FactValue(Spanish, declared, declared.Facts[0]));
        Assert.Equal("22.11.0", DoctorTexts.FactValue(Spanish, declared, declared.Facts[1]));
        Assert.Equal("none", DoctorTexts.FactValue(Spanish, other, other.Facts[0]));
        Assert.Equal("imagen redis:7", DoctorTexts.FactValue(Spanish, labelled, labelled.Facts[0]));
        Assert.Equal("path ..\\sdk", DoctorTexts.FactValue(English, labelled, labelled.Facts[1]));
        Assert.Equal("ruta ..\\sdk", DoctorTexts.FactValue(Spanish, labelled, labelled.Facts[1]));
    }
}
