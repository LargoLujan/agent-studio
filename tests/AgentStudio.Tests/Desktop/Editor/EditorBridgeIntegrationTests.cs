using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Desktop.Controls.Editor;

namespace AgentStudio.Tests.Desktop.Editor;

/// <summary>
/// F1-2, end to end: <see cref="CodeEditorControl"/> loads the shipped editor page and Monaco into the installed
/// WebView2 Runtime and talks to it only through <see cref="EditorBridge"/> (open, changed, requestContent, content,
/// markSaved). The window is never shown (see <see cref="HiddenEditorHost"/>). The tests read Monaco's state with
/// ExecuteScriptAsync and stand in for the user's typing with Monaco's own edit API, never with simulated input.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class EditorBridgeIntegrationTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The document in Monaco (language and text) and how many token colours its view renders: one colour means
    /// plain text, two or more means the language's highlighting is on screen.
    /// </summary>
    private const string EditorStateScript = """
        (() => {
            const editor = window.monaco && monaco.editor.getEditors()[0];
            const model = editor && editor.getModel();
            if (!model) {
                return null;
            }
            const colours = new Set();
            for (const span of document.querySelectorAll('#editor .view-lines span')) {
                for (const name of span.classList) {
                    if (/^mtk\d+$/.test(name)) {
                        colours.add(name);
                    }
                }
            }
            return { language: model.getLanguageId(), text: model.getValue(), colours: colours.size };
        })()
        """;

    private const string UndoScript = "monaco.editor.getEditors()[0].getModel().undo(); null";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "agentstudio-editor-bridge-" + Guid.NewGuid().ToString("N"));

    public EditorBridgeIntegrationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort on a temp folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort on a temp folder.
        }
    }

    [Theory]
    [InlineData("Programa.cs", "csharp", "public sealed class Saludo\r\n{\r\n    // Comentario con eñe: año\r\n    public string Hola(int veces) => \"hola\" + veces;\r\n}\r\n")]
    [InlineData("script.py", "python", "def hola(veces: int) -> str:\n    # Comentario con eñe: año\n    return 'hola' * veces\n")]
    [InlineData("consulta.sql", "sql", "SELECT id, nombre\r\nFROM usuarios\r\nWHERE id = 42; -- año\r\n")]
    [InlineData("ajustes.json", "json", "{\n  \"nombre\": \"año\",\n  \"total\": 3,\n  \"activo\": true\n}\n")]
    [InlineData("pipeline.yaml", "yaml", "nombre: agente # año\nelementos:\n  - uno\n  - dos\n")]
    [InlineData("LEEME.md", "markdown", "# Título\r\n\r\nTexto con **negrita** y `código`.\r\n")]
    public async Task Opening_a_file_shows_its_exact_text_with_its_language_highlighted(string fileName, string language, string text)
    {
        HiddenEditorHost.SkipUnlessAvailable();
        var path = WriteFile(fileName, text, withBom: false);

        var state = await HiddenEditorHost.RunAsync(path, _folder, editor =>
            editor.WaitForAsync(EditorStateScript, s => HasText(s, text) && s.GetProperty("colours").GetInt32() >= 2));

        Assert.True(state.ValueKind == JsonValueKind.Object, "The editor page never showed a document.");
        Assert.Equal(text, state.GetProperty("text").GetString());
        Assert.Equal(language, state.GetProperty("language").GetString());
        Assert.True(state.GetProperty("colours").GetInt32() >= 2, $"Monaco rendered {fileName} in a single colour: no syntax highlighting.");
    }

    [Theory]
    [InlineData("Programa.cs", true, "public sealed class Saludo\r\n{\r\n    // año\r\n}\r\n")]
    [InlineData("script.py", false, "def hola():\n    return 'año'\n")]
    public async Task An_edit_in_the_page_marks_the_document_dirty_and_is_saved_with_the_original_encoding(string fileName, bool withBom, string text)
    {
        HiddenEditorHost.SkipUnlessAvailable();
        var path = WriteFile(fileName, text, withBom);
        var token = TestContext.Current.CancellationToken;

        var (dirtyBefore, dirtyAfterEdit, dirtyAfterSave, saved) = await HiddenEditorHost.RunAsync(path, _folder, async editor =>
        {
            await editor.WaitForAsync(EditorStateScript, s => HasText(s, text));
            var before = editor.Document.IsDirty;
            await editor.EvaluateAsync(InsertAtStartScript("ñ "));
            var edited = await editor.WaitUntilAsync(() => editor.Document.IsDirty);
            await editor.Control.SaveAsync(token);
            return (before, edited, editor.Document.IsDirty, await File.ReadAllBytesAsync(path, token));
        });

        Assert.False(dirtyBefore);
        Assert.True(dirtyAfterEdit, "The page never reported the edit: the document did not become dirty.");
        Assert.False(dirtyAfterSave);
        byte[] expected = [.. withBom ? Utf8Bom : [], .. Encoding.UTF8.GetBytes("ñ " + text)];
        Assert.Equal(expected, saved);
    }

    [Fact]
    public async Task After_a_save_undoing_back_to_the_saved_text_clears_the_dirty_flag()
    {
        HiddenEditorHost.SkipUnlessAvailable();
        const string text = "x = 1\n";
        var path = WriteFile("deshacer.py", text, withBom: false);
        var token = TestContext.Current.CancellationToken;

        var (dirtyAfterSecondEdit, cleanAfterUndo) = await HiddenEditorHost.RunAsync(path, _folder, async editor =>
        {
            await editor.WaitForAsync(EditorStateScript, s => HasText(s, text));
            await editor.EvaluateAsync(InsertAtStartScript("# uno\n"));
            Assert.True(await editor.WaitUntilAsync(() => editor.Document.IsDirty), "The page never reported the first edit.");
            await editor.Control.SaveAsync(token);

            await editor.EvaluateAsync(InsertAtStartScript("# dos\n"));
            var dirty = await editor.WaitUntilAsync(() => editor.Document.IsDirty);
            await editor.EvaluateAsync(UndoScript);
            return (dirty, await editor.WaitUntilAsync(() => !editor.Document.IsDirty));
        });

        Assert.True(dirtyAfterSecondEdit, "The page never reported the edit made after saving.");
        Assert.True(cleanAfterUndo, "Undoing back to the saved text left the document dirty: the page did not take the save (markSaved).");
    }

    private static bool HasText(JsonElement state, string text) =>
        state.ValueKind == JsonValueKind.Object && state.GetProperty("text").GetString() == text;

    /// <summary>What a keystroke at the start of the document does, through Monaco's edit API, as its own undo step.</summary>
    private static string InsertAtStartScript(string inserted) => $$"""
        (() => {
            const editor = monaco.editor.getEditors()[0];
            editor.pushUndoStop();
            editor.executeEdits('test', [{ range: new monaco.Range(1, 1, 1, 1), text: {{JsonSerializer.Serialize(inserted)}} }]);
            editor.pushUndoStop();
            return null;
        })()
        """;

    private string WriteFile(string fileName, string text, bool withBom)
    {
        var path = Path.Combine(_folder, fileName);
        File.WriteAllBytes(path, [.. withBom ? Utf8Bom : [], .. Encoding.UTF8.GetBytes(text)]);
        return path;
    }
}
