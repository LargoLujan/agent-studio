using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>
/// «Idioma» in «Ajustes» (docs/17 D-49): the list of languages (each in its own language, plus «El de Windows»), what is saved,
/// and the notice that the change applies on restart. Over a store in memory and texts of a fixed language: nothing depends on
/// the language of the machine, and the settings of the user are never touched.
/// </summary>
public sealed class LanguageSettingsViewModelTests
{
    private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");

    private static LanguageSettingsViewModel Create(
        SettingsDocumentViewModelTests.CountingStore store,
        CultureInfo? running = null,
        CultureInfo? windows = null) =>
        new(store, new ResourceTextProvider(running ?? SpanishCulture), windows ?? SpanishCulture);

    private static SettingsDocumentViewModel Document(SettingsDocumentViewModelTests.CountingStore store, LanguageSettingsViewModel section) =>
        new(store, new ObservabilitySettingsViewModel(store), time: new FakeTimeProvider(T0), language: section);

    private static LanguageChoice Choice(LanguageSettingsViewModel section, string? cultureName) =>
        section.Choices.Single(c => c.CultureName == cultureName);

    [Fact]
    public void It_lists_windows_first_and_each_language_in_its_own_language()
    {
        var section = Create(new SettingsDocumentViewModelTests.CountingStore(), windows: EnglishCulture);

        Assert.Equal(["El de Windows (English)", "Español (España)", "English"], section.Choices.Select(c => c.Label));
        Assert.Equal([null, "es-ES", "en"], section.Choices.Select(c => c.CultureName));
        Assert.Equal("Idioma", section.Title);
    }

    [Fact]
    public void It_shows_what_is_in_force_with_no_changes_and_no_notice()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore(AppSettings.Default with { Language = "en" });

        var section = Create(store, running: EnglishCulture);
        var document = Document(store, section);

        Assert.Equal("en", section.Selected.CultureName);
        Assert.False(document.IsDirty);
        Assert.Null(section.RestartNotice);
        Assert.False(section.HasRestartNotice);
    }

    [Fact]
    public void A_stored_language_that_is_not_in_the_list_reads_as_the_one_of_windows()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore(AppSettings.Default with { Language = "xx-YY" });

        var section = Create(store);

        Assert.Null(section.Selected.CultureName);
        Assert.False(Document(store, section).IsDirty);
    }

    [Fact]
    public void Guardar_saves_the_language_chosen_as_a_preference_and_keeps_the_rest()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore(AppSettings.Default.WithProjectOpened(@"C:\proyectos\calc") with { CloseWindow = CloseWindowAction.Exit });
        var section = Create(store);
        var document = new SettingsDocumentViewModel(store, new ObservabilitySettingsViewModel(store), time: new FakeTimeProvider(T0), language: section);

        section.Selected = Choice(section, "en");
        Assert.True(document.IsDirty);
        document.SaveCommand.Execute(null);

        Assert.Equal("en", store.Current.Language);
        Assert.Equal(1, store.PreferenceSaves);
        Assert.Equal(0, store.Saves);
        Assert.Equal(CloseWindowAction.Exit, store.Current.CloseWindow);
        Assert.Equal(@"C:\proyectos\calc", store.Current.LastProjectPath);
        Assert.False(document.IsDirty);
        Assert.Equal("en", section.Selected.CultureName);
    }

    [Fact]
    public void The_option_of_windows_saves_no_language()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore(AppSettings.Default with { Language = "en" });
        var section = Create(store, running: EnglishCulture);
        var document = new SettingsDocumentViewModel(store, new ObservabilitySettingsViewModel(store), time: new FakeTimeProvider(T0), language: section);

        section.Selected = Choice(section, null);
        Assert.True(document.IsDirty);
        document.SaveCommand.Execute(null);

        Assert.Null(store.Current.Language);
        Assert.Null(section.Selected.CultureName);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void It_says_the_change_applies_on_restart_while_the_choice_would_change_the_language_in_use()
    {
        var section = Create(new SettingsDocumentViewModelTests.CountingStore(), running: SpanishCulture);

        section.Selected = Choice(section, "en");
        Assert.True(section.HasRestartNotice);
        Assert.Equal("El idioma cambia al reiniciar Agent Studio.", section.RestartNotice);

        section.Selected = Choice(section, "es-ES");
        Assert.False(section.HasRestartNotice);
        Assert.Null(section.RestartNotice);
    }

    [Fact]
    public void The_notice_is_written_in_the_language_in_use()
    {
        var section = Create(new SettingsDocumentViewModelTests.CountingStore(), running: EnglishCulture);

        section.Selected = Choice(section, "es-ES");

        Assert.Equal("The language changes when Agent Studio restarts.", section.RestartNotice);
    }

    [Fact]
    public void The_notice_stays_after_saving_because_the_language_in_use_does_not_change_until_restart()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore();
        var section = Create(store, running: SpanishCulture);
        var document = new SettingsDocumentViewModel(store, new ObservabilitySettingsViewModel(store), time: new FakeTimeProvider(T0), language: section);

        section.Selected = Choice(section, "en");
        document.SaveCommand.Execute(null);

        Assert.True(section.HasRestartNotice);
    }

    [Fact]
    public void The_option_of_windows_has_a_notice_only_when_windows_is_in_another_language_than_the_one_in_use()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore(AppSettings.Default with { Language = "en" });

        var differs = Create(store, running: EnglishCulture, windows: SpanishCulture);
        differs.Selected = Choice(differs, null);
        var same = Create(store, running: EnglishCulture, windows: EnglishCulture);
        same.Selected = Choice(same, null);

        Assert.True(differs.HasRestartNotice);
        Assert.False(same.HasRestartNotice);
    }

    [Fact]
    public void Choosing_again_what_is_in_force_leaves_nothing_to_save()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore();
        var section = Create(store);
        var document = Document(store, section);

        section.Selected = Choice(section, "en");
        Assert.True(document.IsDirty);
        section.Selected = Choice(section, null);

        Assert.False(document.IsDirty);
        Assert.Equal(0, store.PreferenceSaves);
    }
}
