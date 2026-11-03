using System.ComponentModel;
using System.IO;
using AgentStudio.Desktop.Controls.Editor;

namespace AgentStudio.Tests.Desktop.Editor;

public sealed class EditorDocumentTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "as-editor-" + Guid.NewGuid().ToString("N"));

    public EditorDocumentTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Open_detects_bom_line_endings_and_language()
    {
        var path = Write("a.cs", [.. Bom, .. "x\r\ny\r\n"u8.ToArray()]);

        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("x\r\ny\r\n", document.SavedText);
        Assert.True(document.Format.HasBom);
        Assert.Equal(LineEnding.CrLf, document.Format.LineEnding);
        Assert.Equal("csharp", document.Language);
        Assert.Equal("a.cs", document.FileName);
        Assert.False(document.IsDirty);
    }

    [Theory]
    [InlineData(true, LineEnding.CrLf)]
    [InlineData(true, LineEnding.Lf)]
    [InlineData(false, LineEnding.CrLf)]
    [InlineData(false, LineEnding.Lf)]
    public async Task Save_preserves_bom_and_line_endings_byte_for_byte(bool hasBom, LineEnding lineEnding)
    {
        var eol = lineEnding == LineEnding.CrLf ? "\r\n" : "\n";
        var original = $"uno{eol}dos ñ €{eol}";
        var originalBytes = hasBom ? [.. Bom, .. System.Text.Encoding.UTF8.GetBytes(original)] : System.Text.Encoding.UTF8.GetBytes(original);
        var path = Write("f.txt", originalBytes);
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);

        // Monaco hands back text with its own line endings (here always LF); the file's style must win.
        await document.SaveAsync("uno\ndos ñ €\ntres\n", TestContext.Current.CancellationToken);

        var expected = $"uno{eol}dos ñ €{eol}tres{eol}";
        var expectedBytes = hasBom ? [.. Bom, .. System.Text.Encoding.UTF8.GetBytes(expected)] : System.Text.Encoding.UTF8.GetBytes(expected);
        Assert.Equal(expectedBytes, File.ReadAllBytes(path));
        Assert.Equal(expected, document.SavedText);
    }

    [Fact]
    public async Task Save_is_atomic_and_leaves_no_temporary_files()
    {
        var path = Write("g.json", "{}"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);

        await document.SaveAsync("{\"a\":1}", TestContext.Current.CancellationToken);

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task Failed_save_keeps_the_original_file_and_cleans_up()
    {
        var path = Write("h.txt", "original"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        document.SetDirty(true);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => document.SaveAsync("changed", TestContext.Current.CancellationToken));
        }

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
        Assert.True(document.IsDirty);
    }

    [Fact]
    public async Task Save_creates_a_new_file_with_the_platform_defaults()
    {
        var path = Path.Combine(_folder, "new.md");
        var document = new EditorDocument(path, string.Empty, TextFileFormat.Default);

        await document.SaveAsync("a\nb", TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith(Bom));
        Assert.Equal("a" + Environment.NewLine + "b", File.ReadAllText(path));
    }

    [Fact]
    public async Task Dirty_flag_notifies_and_clears_on_save()
    {
        var path = Write("d.py", "print(1)"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        var changes = new List<string?>();
        ((INotifyPropertyChanged)document).PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var saved = 0;
        document.Saved += (_, _) => saved++;

        document.SetDirty(true);
        Assert.True(document.IsDirty);
        document.SetDirty(true);
        await document.SaveAsync("print(2)", TestContext.Current.CancellationToken);

        Assert.False(document.IsDirty);
        Assert.Equal(1, saved);
        Assert.Equal(2, changes.Count(name => name == nameof(EditorDocument.IsDirty)));
        Assert.Contains(nameof(EditorDocument.SavedText), changes);
    }

    [Fact]
    public async Task Read_only_documents_refuse_to_save()
    {
        var path = Write("r.txt", "x"u8.ToArray());
        var document = new EditorDocument(path, "x", TextFileFormat.Default, isReadOnly: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => document.SaveAsync("y", TestContext.Current.CancellationToken));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public async Task Files_that_are_not_utf8_are_rejected_instead_of_corrupted()
    {
        var latin1 = Write("l.txt", [0x63, 0x61, 0x66, 0xE9]);
        var utf16 = Write("u.txt", [0xFF, 0xFE, 0x61, 0x00]);

        await Assert.ThrowsAsync<InvalidDataException>(() => EditorDocument.OpenAsync(latin1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => EditorDocument.OpenAsync(utf16, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", LineEnding.CrLf)]
    [InlineData("a\nb\nc", LineEnding.Lf)]
    [InlineData("a\r\nb\nc\n", LineEnding.Lf)]
    [InlineData("a\r\nb\r\nc\n", LineEnding.CrLf)]
    public void Predominant_line_ending_is_detected(string text, LineEnding expected)
    {
        Assert.Equal(expected, TextFileStore.DetectLineEnding(text));
    }

    [Fact]
    public async Task Save_refuses_to_overwrite_a_file_changed_on_disk()
    {
        var path = Write("e.txt", "uno"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "cambiado por un agente");
        document.SetDirty(true);

        var ex = await Assert.ThrowsAsync<FileChangedOnDiskException>(() => document.SaveAsync("mío", TestContext.Current.CancellationToken));

        Assert.False(ex.Deleted);
        Assert.Equal("cambiado por un agente", File.ReadAllText(path));
        Assert.True(document.IsDirty);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task Save_can_force_the_overwrite_and_then_tracks_the_new_file()
    {
        var path = Write("f2.txt", "uno"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "cambiado por un agente");

        await document.SaveAsync("mío", TestContext.Current.CancellationToken, overwriteExternalChanges: true);
        Assert.Equal("mío", File.ReadAllText(path));

        // After the forced save the fingerprint is the new file: a plain save works again, a new external edit is caught.
        await document.SaveAsync("mío 2", TestContext.Current.CancellationToken);
        File.WriteAllText(path, "otra vez");
        await Assert.ThrowsAsync<FileChangedOnDiskException>(() => document.SaveAsync("mío 3", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_file_deleted_on_disk_counts_as_changed()
    {
        var path = Write("del.txt", "uno"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        File.Delete(path);

        var ex = await Assert.ThrowsAsync<FileChangedOnDiskException>(() => document.SaveAsync("x", TestContext.Current.CancellationToken));
        Assert.True(ex.Deleted);
        Assert.False(File.Exists(path));

        await document.SaveAsync("x", TestContext.Current.CancellationToken, overwriteExternalChanges: true);
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public async Task Touching_the_file_without_changing_its_bytes_is_not_a_conflict()
    {
        var path = Write("t.txt", "uno"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        await document.SaveAsync("dos", TestContext.Current.CancellationToken);

        Assert.Equal("dos", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_new_file_that_appears_on_disk_before_the_first_save_is_a_conflict()
    {
        var path = Path.Combine(_folder, "nuevo.md");
        var document = new EditorDocument(path, string.Empty, TextFileFormat.Default);
        File.WriteAllText(path, "alguien llegó antes");

        await Assert.ThrowsAsync<FileChangedOnDiskException>(() => document.SaveAsync("x", TestContext.Current.CancellationToken));
        Assert.Equal("alguien llegó antes", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("a\r\nb\nc", true)]
    [InlineData("a\r\nb\r\nc", false)]
    [InlineData("a\nb\nc", false)]
    [InlineData("a\rb\nc", true)]
    [InlineData("a\r\nb\rc", true)]
    [InlineData("sin saltos", false)]
    public void Mixed_line_endings_are_detected(string text, bool expected)
    {
        Assert.Equal(expected, TextFileStore.HasMixedLineEndings(text));
    }

    [Fact]
    public async Task Mixed_line_endings_are_flagged_on_open_and_cleared_by_saving()
    {
        var path = Write("m.txt", "a\r\nb\nc\r\n"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        var changes = new List<string?>();
        ((INotifyPropertyChanged)document).PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(document.HasMixedLineEndings);
        Assert.Contains("CRLF y LF", document.MixedLineEndingsNotice);
        Assert.Contains("unificarán a CRLF", document.MixedLineEndingsNotice);

        await document.SaveAsync("a\nb\nc\n", TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\r\nc\r\n", File.ReadAllText(path));
        Assert.False(document.HasMixedLineEndings);
        Assert.Null(document.MixedLineEndingsNotice);
        Assert.Contains(nameof(EditorDocument.MixedLineEndingsNotice), changes);
    }

    [Fact]
    public async Task Uniform_files_have_no_mixed_line_endings_notice()
    {
        var path = Write("u2.txt", "a\nb\n"u8.ToArray());
        var document = await EditorDocument.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.False(document.HasMixedLineEndings);
        Assert.Null(document.MixedLineEndingsNotice);
    }
}
