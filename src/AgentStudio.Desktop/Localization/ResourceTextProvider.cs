using System.Globalization;
using System.Reflection;
using System.Resources;
using AgentStudio.Application.Localization;

namespace AgentStudio.Desktop.Localization;

/// <summary>
/// Gives the texts of the interface in one language (docs/17 D-47, ADR-0054). Every text the person reads comes from the
/// resources through it: the XAML with <c>{l:T Key}</c> (<see cref="TExtension"/>), the code with <see cref="Texts"/>, and a
/// coded text of the layers below (<see cref="UserText"/>) with <see cref="Render"/>.
/// </summary>
public interface IUiTextProvider
{
    /// <summary>The culture of the language: its resources, and how it writes numbers and dates.</summary>
    CultureInfo Culture { get; }

    /// <summary>Whether a text with this key exists, in any language.</summary>
    bool Has(string key);

    /// <summary>The text of <paramref name="key"/>; a key that does not exist is shown as <c>[key]</c>, never as an empty text.</summary>
    string Get(string key);

    /// <summary>The text of <paramref name="key"/> with its placeholders filled in the culture of the language.</summary>
    string Format(string key, params object?[] arguments);

    /// <summary>A coded text of the layers below, painted in this language (<see cref="UserText.ResourceKey"/>).</summary>
    string Render(UserText text);
}

/// <summary>
/// The texts of the resources of the desktop in one language. The resources are <c>Resources/Strings.resx</c> and the area
/// files of the packages, <c>Resources/&lt;Area&gt;Strings.resx</c>: every embedded resource of the assembly named
/// <c>AgentStudio.Desktop.Resources.*Strings</c> is read, <c>Strings</c> first, so no package has to register its file. A key
/// lives in one file only (<c>LocalizationResourcesTests</c> checks it). A text missing in the language falls back to the
/// source language, Spanish of Spain, as the resources do.
/// </summary>
public sealed class ResourceTextProvider : IUiTextProvider
{
    /// <summary>The start of the name of every resource of texts.</summary>
    public const string ResourcePrefix = "AgentStudio.Desktop.Resources.";

    /// <summary>The end of the name of every resource of texts, before <c>.resources</c>.</summary>
    public const string ResourceSuffix = "Strings";

    private const string ManifestExtension = ".resources";

    private static readonly Lazy<IReadOnlyList<ResourceManager>> Managers = new(Discover);

    public ResourceTextProvider(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        Culture = culture;
    }

    public CultureInfo Culture { get; }

    /// <summary>The base names of the resources of texts of the desktop, <c>Strings</c> first and then in order of name.</summary>
    public static IReadOnlyList<string> BaseNames => [.. Managers.Value.Select(m => m.BaseName)];

    public bool Has(string key) => Find(key) is not null;

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Find(key) ?? "[" + key + "]";
    }

    public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments ?? []);

    public string Render(UserText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Format(text.ResourceKey, [.. text.Arguments]);
    }

    private string? Find(string key)
    {
        foreach (var manager in Managers.Value)
        {
            if (manager.GetString(key, Culture) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static IReadOnlyList<ResourceManager> Discover()
    {
        var assembly = typeof(ResourceTextProvider).Assembly;
        return
        [
            .. assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix + ManifestExtension, StringComparison.Ordinal))
                .Select(n => n[..^ManifestExtension.Length])
                .OrderBy(n => n == ResourcePrefix + ResourceSuffix ? 0 : 1)
                .ThenBy(n => n, StringComparer.Ordinal)
                .Select(n => new ResourceManager(n, assembly)),
        ];
    }
}

/// <summary>
/// The texts of the language the interface uses. The language is applied once, at startup (<see cref="UseLanguage"/>, docs/17
/// D-49: a change applies on restart, so no screen is left half changed); until then, and in tests, the provider is the one of
/// the default language (<see cref="UiLanguages.Resolve"/> with no choice). A screen that must paint in a given language (a
/// test, a render per language) takes an <see cref="IUiTextProvider"/> of its own instead.
/// </summary>
public static class Texts
{
    private static readonly System.Threading.Lock Gate = new();
    private static IUiTextProvider? _applied;
    private static readonly Lazy<IUiTextProvider> Default = new(() => new ResourceTextProvider(UiLanguages.Resolve(null, CultureInfo.CurrentUICulture).Culture));

    /// <summary>The provider of the language in use.</summary>
    public static IUiTextProvider Current => _applied ?? Default.Value;

    /// <summary>The text of <paramref name="key"/> in the language in use.</summary>
    public static string Get(string key) => Current.Get(key);

    /// <summary>The text of <paramref name="key"/> with its placeholders filled, in the language in use.</summary>
    public static string Format(string key, params object?[] arguments) => Current.Format(key, arguments);

    /// <summary>A coded text of the layers below, in the language in use.</summary>
    public static string Render(UserText text) => Current.Render(text);

    /// <summary>
    /// Applies <paramref name="language"/> at startup, before any window exists: the culture of the interface of the threads
    /// (the resources) and the provider. Applying the same language again changes nothing; another one throws, since the
    /// language only changes on restart. The culture of numbers and dates is left as it is: the screens of before format with
    /// their own until Phase 14 moves them to the language (D-47).
    /// </summary>
    /// <exception cref="InvalidOperationException">Another language was applied already.</exception>
    public static void UseLanguage(UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        lock (Gate)
        {
            if (_applied is not null)
            {
                if (!string.Equals(_applied.Culture.Name, language.Culture.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The language '{_applied.Culture.Name}' is in use; '{language.CultureName}' applies on restart.");
                }

                return;
            }

            CultureInfo.DefaultThreadCurrentUICulture = language.Culture;
            Thread.CurrentThread.CurrentUICulture = language.Culture;
            Resources.Strings.Culture = language.Culture;
            _applied = new ResourceTextProvider(language.Culture);
        }
    }
}
