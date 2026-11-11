using System.Diagnostics;
using System.IO;
using AgentStudio.Desktop.Controls.Editor;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Editor;

public sealed class EditorAssetsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "as-assets-" + Guid.NewGuid().ToString("N"));

    public EditorAssetsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Missing_monaco_reports_the_actionable_message()
    {
        var assets = new EditorAssets(_folder);

        Assert.False(assets.IsMonacoAvailable);
        Assert.Equal(
            "El editor no está disponible: faltan los archivos de Monaco. Ejecuta tools/assets/fetch-monaco.ps1.",
            assets.GetUnavailableReason());
    }

    [Fact]
    public void Present_resources_are_reported_available()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "monaco", "vs"));
        Directory.CreateDirectory(Path.Combine(_folder, "editor"));
        File.WriteAllText(Path.Combine(_folder, "monaco", "vs", "loader.js"), "//");
        File.WriteAllText(Path.Combine(_folder, "editor", "editor.html"), "<html/>");

        Assert.Null(new EditorAssets(_folder).GetUnavailableReason());
    }

    [Theory]
    [InlineData("https://agentstudio.editor/editor/editor.html", true)]
    [InlineData("https://AGENTSTUDIO.EDITOR/x", true)]
    [InlineData("http://agentstudio.editor/editor.html", false)]
    [InlineData("https://agentstudio.editor.evil.com/", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_virtual_host_is_navigable(string? uri, bool expected)
    {
        Assert.Equal(expected, EditorAssets.IsAllowedNavigation(uri));
    }

    [Fact]
    public void Shipped_page_has_a_strict_csp_without_remote_origins()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "editor", "editor.html"));

        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_services_resolve_from_the_container()
    {
        using var provider = new ServiceCollection().AddEditorServices().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IEditorDocumentFactory>());
        Assert.Same(EditorAssets.Default, provider.GetRequiredService<EditorAssets>());
    }

    [Fact]
    public async Task Fetch_script_parses_and_reports_its_plan_with_whatif()
    {
        var script = FindUpwards("tools", "assets", "fetch-monaco.ps1");
        var destination = Path.Combine(_folder, "monaco");
        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Destination", destination, "-WhatIf" })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, errors);
        Assert.Contains("monaco-editor 0.57.0", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(destination, "vs")));
    }

    private static string FindUpwards(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate " + string.Join('/', segments));
    }
}
