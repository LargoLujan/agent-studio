using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>
/// «Reducir movimiento» in «Ajustes» (docs/17 D-15, F13-7): it shows what is in force, it is saved with the one «Guardar» of
/// the document, and together with the option of Windows it decides whether the animations of the canvas run
/// (<see cref="MotionPolicy"/>). Over a store in memory: the settings of the user are never touched.
/// </summary>
public sealed class MotionSettingsViewModelTests
{
    private static MotionSettingsViewModel Create(SettingsDocumentViewModelTests.CountingStore store) =>
        new(store, new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES")));

    [Fact]
    public void It_shows_what_is_in_force_and_says_it_in_the_resources()
    {
        var section = Create(new SettingsDocumentViewModelTests.CountingStore(AppSettings.Default with { ReduceMotion = true }));

        Assert.True(section.ReduceMotion);
        Assert.Equal("Reducir movimiento", section.Title);
        Assert.Contains("animaciones", section.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_saves_it_with_the_other_preferences_and_reverts_what_was_not_saved()
    {
        var store = new SettingsDocumentViewModelTests.CountingStore();
        var document = new SettingsDocumentViewModel(store, new ObservabilitySettingsViewModel(store), time: new FakeTimeProvider(T0));

        Assert.False(document.IsDirty);
        document.Motion.ReduceMotion = true;
        Assert.True(document.IsDirty);
        Assert.False(store.Current.ReduceMotion);

        document.SaveCommand.Execute(null);

        Assert.True(store.Current.ReduceMotion);
        Assert.Equal(1, store.PreferenceSaves);
        Assert.False(document.IsDirty);

        document.Motion.ReduceMotion = false;
        Assert.True(document.IsDirty);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void The_animations_run_only_when_neither_the_setting_nor_windows_turns_them_off(bool reduceMotion, bool windowsAnimates, bool expected) =>
        Assert.Equal(expected, MotionPolicy.AnimationsEnabled(reduceMotion, windowsAnimates));
}
