using System.Text.Json;
using AgentStudio.Desktop.Controls.Editor;

namespace AgentStudio.Tests.Desktop.Editor;

public sealed class EditorMessageCodecTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Open_is_serialized_with_camel_case_fields_and_type_first()
    {
        var json = EditorMessageCodec.Serialize(new OpenMessage("a\r\n\"b\" <x>", "csharp", true));

        Assert.StartsWith("{\"type\":\"open\"", json, StringComparison.Ordinal);
        var root = Parse(json);
        Assert.Equal("open", root.GetProperty("type").GetString());
        Assert.Equal("a\r\n\"b\" <x>", root.GetProperty("content").GetString());
        Assert.Equal("csharp", root.GetProperty("language").GetString());
        Assert.True(root.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public void Show_diff_request_content_focus_hide_diff_and_mark_saved_are_serialized()
    {
        var diff = Parse(EditorMessageCodec.Serialize(new ShowDiffMessage("old", "new", "json")));
        Assert.Equal("showDiff", diff.GetProperty("type").GetString());
        Assert.Equal("old", diff.GetProperty("original").GetString());
        Assert.Equal("new", diff.GetProperty("modified").GetString());
        Assert.Equal("json", diff.GetProperty("language").GetString());

        var request = Parse(EditorMessageCodec.Serialize(new RequestContentMessage("r1")));
        Assert.Equal("requestContent", request.GetProperty("type").GetString());
        Assert.Equal("r1", request.GetProperty("requestId").GetString());

        Assert.Equal("{\"type\":\"focus\"}", EditorMessageCodec.Serialize(new FocusMessage()));
        Assert.Equal("{\"type\":\"hideDiff\"}", EditorMessageCodec.Serialize(new HideDiffMessage()));

        var saved = Parse(EditorMessageCodec.Serialize(new MarkSavedMessage(42)));
        Assert.Equal("markSaved", saved.GetProperty("type").GetString());
        Assert.Equal(42, saved.GetProperty("versionId").GetInt64());
    }

    [Fact]
    public void Ready_and_save_requested_are_parsed()
    {
        Assert.True(EditorMessageCodec.TryParse("{\"type\":\"ready\"}", out var ready, out var error1));
        Assert.IsType<ReadyMessage>(ready);
        Assert.Null(error1);

        Assert.True(EditorMessageCodec.TryParse("{\"type\":\"saveRequested\"}", out var save, out _));
        Assert.IsType<SaveRequestedMessage>(save);
    }

    [Fact]
    public void Changed_is_parsed()
    {
        Assert.True(EditorMessageCodec.TryParse("{\"type\":\"changed\",\"versionId\":7,\"isDirty\":true}", out var message, out _));
        var changed = Assert.IsType<ChangedMessage>(message);
        Assert.Equal(7, changed.VersionId);
        Assert.True(changed.IsDirty);
    }

    [Fact]
    public void Content_is_parsed()
    {
        Assert.True(EditorMessageCodec.TryParse("{\"type\":\"content\",\"requestId\":\"abc\",\"text\":\"x\\ny\",\"versionId\":3}", out var message, out _));
        var content = Assert.IsType<ContentMessage>(message);
        Assert.Equal("abc", content.RequestId);
        Assert.Equal("x\ny", content.Text);
        Assert.Equal(3, content.VersionId);
    }

    [Fact]
    public void Error_is_parsed()
    {
        Assert.True(EditorMessageCodec.TryParse("{\"type\":\"error\",\"message\":\"boom\"}", out var message, out _));
        Assert.Equal("boom", Assert.IsType<ErrorMessage>(message).Message);
    }

    [Fact]
    public void Unknown_type_is_rejected_with_a_reason()
    {
        Assert.False(EditorMessageCodec.TryParse("{\"type\":\"launchMissiles\"}", out var message, out var error));
        Assert.Null(message);
        Assert.Contains("launchMissiles", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":")]
    [InlineData("[1,2,3]")]
    [InlineData("\"ready\"")]
    [InlineData("{}")]
    [InlineData("{\"type\":5}")]
    [InlineData("{\"type\":\"changed\"}")]
    [InlineData("{\"type\":\"changed\",\"versionId\":\"7\",\"isDirty\":true}")]
    [InlineData("{\"type\":\"changed\",\"versionId\":7,\"isDirty\":\"yes\"}")]
    [InlineData("{\"type\":\"content\",\"requestId\":\"a\",\"versionId\":1}")]
    [InlineData("{\"type\":\"content\",\"requestId\":1,\"text\":\"t\",\"versionId\":1}")]
    public void Malformed_messages_are_rejected_without_throwing(string? json)
    {
        Assert.False(EditorMessageCodec.TryParse(json, out var message, out var error));
        Assert.Null(message);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
