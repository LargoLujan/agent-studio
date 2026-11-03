using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Observability.Logging;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Security;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

public sealed class LogRecordFormatterTests
{
    private const string Sentence = "Refactor the payroll module for TENANT-42";

    private static readonly DateTimeOffset At = new(2026, 10, 15, 9, 30, 15, 123, TimeSpan.Zero);

    private readonly FileLoggerOptions _options = new();

    private sealed record RequestStub(string Prompt, string Model);

    private readonly struct Wide
    {
        public override string ToString() => new('x', 300);
    }

    private readonly struct Bomb
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    private sealed class FakeStackException(string message, string stack) : Exception(message)
    {
        public override string? StackTrace => stack;
    }

    private sealed class MessageBombException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    private LogRecordFormatter Formatter(ISecretRedactor? redactor = null) =>
        new(_options, redactor ?? new SecretRedactor(@"C:\Users\testuser"));

    private static List<KeyValuePair<string, object?>> State(string template, params (string Name, object? Value)[] values)
    {
        var state = values.Select(v => KeyValuePair.Create(v.Name, v.Value)).ToList();
        state.Add(KeyValuePair.Create("{OriginalFormat}", (object?)template));
        return state;
    }

    private (JsonElement Record, string Line) Write(
        object? state,
        Exception? exception = null,
        LogLevel level = LogLevel.Information,
        EventId eventId = default,
        string category = "Tests.Category",
        Func<object?, Exception?, string>? formatter = null,
        DateTimeOffset? at = null)
    {
        var bytes = Formatter().Format(at ?? At, category, level, eventId, state, exception, formatter ?? ((s, _) => s?.ToString() ?? string.Empty));
        return Parse(bytes);
    }

    private static (JsonElement Record, string Line) Parse(byte[] bytes)
    {
        Assert.Equal((byte)'\n', bytes[^1]);
        var line = Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        using var document = JsonDocument.Parse(line);
        return (document.RootElement.Clone(), line);
    }

    private static string Hashed(string text) => $"[text len={text.Length} sha256={LogAssert.Fingerprint(text)}]";

    // ---- shape of a record ----

    [Fact]
    public void A_record_has_exactly_the_required_fields_in_a_stable_order()
    {
        var (record, _) = Write(State("Started"));

        Assert.Equal(["timestamp", "level", "category", "eventId", "message"], record.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Started", record.GetProperty("message").GetString());
        Assert.Equal("Tests.Category", record.GetProperty("category").GetString());
        Assert.Equal(0, record.GetProperty("eventId").GetInt32());
    }

    [Fact]
    public void Event_name_properties_and_exception_follow_the_message_when_present()
    {
        var (record, _) = Write(
            State("Saving {Path}", ("Path", "a.cs")),
            new InvalidOperationException("boom"),
            eventId: new EventId(7, "Save"));

        Assert.Equal(
            ["timestamp", "level", "category", "eventId", "eventName", "message", "properties", "exception"],
            record.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, record.GetProperty("eventId").GetInt32());
        Assert.Equal("Save", record.GetProperty("eventName").GetString());
    }

    [Fact]
    public void The_timestamp_is_iso_8601_utc_with_milliseconds()
    {
        var (utc, _) = Write(State("x"));
        var (shifted, _) = Write(State("x"), at: new DateTimeOffset(2026, 10, 15, 11, 30, 15, 123, TimeSpan.FromHours(2)));

        Assert.Equal("2026-10-15T09:30:15.123Z", utc.GetProperty("timestamp").GetString());
        Assert.Equal("2026-10-15T09:30:15.123Z", shifted.GetProperty("timestamp").GetString());
    }

    [Theory]
    [InlineData(LogLevel.Trace, "Trace")]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Information, "Information")]
    [InlineData(LogLevel.Warning, "Warning")]
    [InlineData(LogLevel.Error, "Error")]
    [InlineData(LogLevel.Critical, "Critical")]
    public void The_level_is_written_by_name(LogLevel level, string expected)
    {
        var (record, _) = Write(State("x"), level: level);

        Assert.Equal(expected, record.GetProperty("level").GetString());
    }

    // ---- message templates ----

    [Fact]
    public void The_message_is_the_template_with_the_values_in_place()
    {
        var (record, _) = Write(State("Saving {Path} failed after {Attempts} tries", ("Path", "a.cs"), ("Attempts", 3)));

        Assert.Equal("Saving a.cs failed after 3 tries", record.GetProperty("message").GetString());
        var properties = record.GetProperty("properties");
        Assert.Equal("a.cs", properties.GetProperty("Path").GetString());
        Assert.Equal(3, properties.GetProperty("Attempts").GetInt32());
        Assert.False(properties.TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public void Format_specifiers_alignment_and_escaped_braces_work_like_in_the_framework()
    {
        var (record, _) = Write(State("Took {Elapsed:N1} s [{Name,8}] {{literal}} {Count:D3}", ("Elapsed", 12.345), ("Name", "ab"), ("Count", 7)));

        Assert.Equal("Took 12.3 s [      ab] {literal} 007", record.GetProperty("message").GetString());
    }

    [Fact]
    public void Numbers_in_the_message_always_use_the_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            var (record, _) = Write(State("Size {Bytes:N1}", ("Bytes", 1234.5)));

            Assert.Equal("Size 1,234.5", record.GetProperty("message").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_name_used_twice_keeps_both_values_under_distinct_property_names()
    {
        var (record, _) = Write(State("{X} and {X}", ("X", 1), ("X", 2)));

        Assert.Equal("1 and 2", record.GetProperty("message").GetString());
        Assert.Equal(1, record.GetProperty("properties").GetProperty("X").GetInt32());
        Assert.Equal(2, record.GetProperty("properties").GetProperty("X_2").GetInt32());
    }

    [Fact]
    public void A_null_value_is_null_in_json_and_null_in_the_message()
    {
        var (record, _) = Write(State("Value is {Value}", ("Value", null)));

        Assert.Equal("Value is (null)", record.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("properties").GetProperty("Value").ValueKind);
    }

    [Fact]
    public void A_template_with_more_holes_than_values_is_written_as_it_is()
    {
        var (record, _) = Write(State("{A} then {B}", ("A", 1)));

        Assert.Equal("{A} then {B}", record.GetProperty("message").GetString());
    }

    [Fact]
    public void Without_a_template_the_message_comes_from_the_formatter_redacted()
    {
        var (record, _) = Write("plain state", formatter: (_, _) => "token=abc123 done");

        Assert.Equal("token=[REDACTED] done", record.GetProperty("message").GetString());
        Assert.False(record.TryGetProperty("properties", out _));
    }

    [Fact]
    public void A_list_of_pairs_without_a_template_does_not_use_the_caller_formatter()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            KeyValuePair.Create("Count", (object?)3),
            KeyValuePair.Create("Prompt", (object?)Sentence),
        };

        var (record, line) = Write(state, formatter: (_, _) => "raw: " + Sentence);

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        Assert.Equal("Count=3 Prompt=" + Hashed(Sentence), record.GetProperty("message").GetString());
    }

    [Fact]
    public void A_template_that_already_contains_a_secret_is_redacted()
    {
        var (record, line) = Write(State("api_key=abc123xyz was refused"));

        Assert.Equal("api_key=[REDACTED] was refused", record.GetProperty("message").GetString());
        Assert.DoesNotContain("abc123xyz", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_only_becomes_a_secret_in_the_message_context_is_not_written_as_a_property_either()
    {
        var (record, line) = Write(State("password={Value}", ("Value", "hunter2")));

        Assert.Equal("password=[REDACTED]", record.GetProperty("message").GetString());
        Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
        var value = record.GetProperty("properties").GetProperty("Value");
        Assert.Equal(7, value.GetProperty("length").GetInt32());
        Assert.Equal(LogAssert.Fingerprint("hunter2"), value.GetProperty("sha256").GetString());
    }

    [Fact]
    public void A_message_longer_than_the_limit_is_written_as_length_and_hash_only()
    {
        _options.MaxMessageLength = 50;
        var text = new string('m', 51);

        var (record, line) = Write("plain state", formatter: (_, _) => text);

        Assert.Equal(Hashed(text), record.GetProperty("message").GetString());
        Assert.DoesNotContain(text, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_at_the_limit_is_kept()
    {
        _options.MaxMessageLength = 50;
        var text = new string('m', 50);

        var (record, _) = Write("plain state", formatter: (_, _) => text);

        Assert.Equal(text, record.GetProperty("message").GetString());
    }

    // ---- property values ----

    [Fact]
    public void Scalars_keep_their_json_types()
    {
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var when = new DateTime(2026, 10, 15, 9, 30, 0, DateTimeKind.Utc);
        var offset = new DateTimeOffset(2026, 10, 15, 11, 30, 0, TimeSpan.FromHours(2));
        var span = TimeSpan.FromSeconds(62.5);

        var (record, _) = Write(State(
            "{I} {L} {U} {D} {F} {M} {B} {C} {G} {E} {W} {O} {S}",
            ("I", 42),
            ("L", long.MaxValue),
            ("U", ulong.MaxValue),
            ("D", 0.25),
            ("F", 1.5f),
            ("M", 12.50m),
            ("B", true),
            ("C", 'x'),
            ("G", guid),
            ("E", LogLevel.Warning),
            ("W", when),
            ("O", offset),
            ("S", span)));

        var p = record.GetProperty("properties");
        Assert.Equal(42, p.GetProperty("I").GetInt32());
        Assert.Equal(long.MaxValue, p.GetProperty("L").GetInt64());
        Assert.Equal(ulong.MaxValue, p.GetProperty("U").GetUInt64());
        Assert.Equal(0.25, p.GetProperty("D").GetDouble());
        Assert.Equal(1.5, p.GetProperty("F").GetDouble());
        Assert.Equal(12.50m, p.GetProperty("M").GetDecimal());
        Assert.True(p.GetProperty("B").GetBoolean());
        Assert.Equal("x", p.GetProperty("C").GetString());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", p.GetProperty("G").GetString());
        Assert.Equal("Warning", p.GetProperty("E").GetString());
        Assert.Equal(when.ToString("O", CultureInfo.InvariantCulture), p.GetProperty("W").GetString());
        Assert.Equal(offset.ToString("O", CultureInfo.InvariantCulture), p.GetProperty("O").GetString());
        Assert.Equal(span.ToString("c", CultureInfo.InvariantCulture), p.GetProperty("S").GetString());
    }

    [Fact]
    public void Non_finite_numbers_are_written_as_text()
    {
        var (record, _) = Write(State("{A} {B} {C}", ("A", double.NaN), ("B", double.PositiveInfinity), ("C", float.NegativeInfinity)));

        var p = record.GetProperty("properties");
        Assert.Equal("NaN", p.GetProperty("A").GetString());
        Assert.Equal("Infinity", p.GetProperty("B").GetString());
        Assert.Equal("-Infinity", p.GetProperty("C").GetString());
    }

    [Theory]
    [InlineData("Prompt")]
    [InlineData("SystemInstructions")]
    [InlineData("FinalText")]
    [InlineData("Response")]
    [InlineData("Content")]
    [InlineData("prompt")]
    [InlineData("system_prompt")]
    [InlineData("UserPrompt")]
    [InlineData("modelOutput")]
    [InlineData("AssistantResponse")]
    [InlineData("JSONContent")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Password")]
    [InlineData("ConnectionString")]
    [InlineData("StdErr")]
    [InlineData("AccessToken")]
    [InlineData("Authorization")]
    public void A_text_under_a_sensitive_name_is_written_as_length_and_hash_only(string name)
    {
        var (record, line) = Write(State("Sending {" + name + "}", (name, Sentence)));

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        Assert.DoesNotContain("TENANT-42", line, StringComparison.Ordinal);
        Assert.Equal("Sending " + Hashed(Sentence), record.GetProperty("message").GetString());
        var value = record.GetProperty("properties").GetProperty(name);
        Assert.Equal(Sentence.Length, value.GetProperty("length").GetInt32());
        Assert.Equal(LogAssert.Fingerprint(Sentence), value.GetProperty("sha256").GetString());
        Assert.Equal(["length", "sha256"], value.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("Context")]
    [InlineData("ContextWindow")]
    [InlineData("Path")]
    [InlineData("Provider")]
    [InlineData("Reason")]
    [InlineData("Message")]
    [InlineData("Title")]
    [InlineData("Detail")]
    [InlineData("Key")]
    [InlineData("Tokens")]
    public void A_short_text_under_an_ordinary_name_is_kept(string name)
    {
        var (record, _) = Write(State("Got {" + name + "}", (name, "short value")));

        Assert.Equal("Got short value", record.GetProperty("message").GetString());
        Assert.Equal("short value", record.GetProperty("properties").GetProperty(name).GetString());
    }

    [Theory]
    [InlineData("ContentRoot")]
    [InlineData("ContentType")]
    [InlineData("OutputPath")]
    [InlineData("OutputDirectory")]
    [InlineData("InputFile")]
    [InlineData("PromptName")]
    [InlineData("PromptId")]
    [InlineData("ResponseCode")]
    [InlineData("ResponseUrl")]
    [InlineData("TextEncoding")]
    [InlineData("TokenType")]
    [InlineData("SecretName")]
    public void A_name_that_ends_in_a_word_for_something_other_than_text_is_not_sensitive(string name)
    {
        var (record, _) = Write(State("Got {" + name + "}", (name, "short value")));

        Assert.Equal("short value", record.GetProperty("properties").GetProperty(name).GetString());
    }

    [Theory]
    [InlineData("PromptText")]
    [InlineData("PromptPreview")]
    [InlineData("Prompt2")]
    [InlineData("Prompt_v2")]
    [InlineData("AuthorizationHeader")]
    [InlineData("ApiKeyValue")]
    [InlineData("ResponseSnippet")]
    public void A_sensitive_word_still_counts_when_the_name_ends_in_something_else(string name)
    {
        var (record, line) = Write(State("Got {" + name + "}", (name, Sentence)));

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        Assert.Equal(Sentence.Length, record.GetProperty("properties").GetProperty(name).GetProperty("length").GetInt32());
    }

    [Fact]
    public void Extra_exempt_words_can_be_added_to_the_options()
    {
        _options.ExemptNameWords.Add("snippet");

        var (record, _) = Write(State("Got {ResponseSnippet}", ("ResponseSnippet", "short value")));

        Assert.Equal("short value", record.GetProperty("properties").GetProperty("ResponseSnippet").GetString());
    }

    [Fact]
    public void Extra_sensitive_words_can_be_added_to_the_options()
    {
        _options.SensitiveNameWords.Add("diff");

        var (record, line) = Write(State("Changed {FileDiff}", ("FileDiff", Sentence)));

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        Assert.Equal(Sentence.Length, record.GetProperty("properties").GetProperty("FileDiff").GetProperty("length").GetInt32());
    }

    [Fact]
    public void A_long_text_is_hashed_whatever_its_name()
    {
        var text = string.Concat(Enumerable.Repeat("long free text ", 30));

        var (record, line) = Write(State("Saw {Detail}", ("Detail", text)));

        Assert.DoesNotContain("free text", line, StringComparison.Ordinal);
        Assert.Equal("Saw " + Hashed(text), record.GetProperty("message").GetString());
        Assert.Equal(text.Length, record.GetProperty("properties").GetProperty("Detail").GetProperty("length").GetInt32());
    }

    [Fact]
    public void The_length_limit_is_inclusive()
    {
        _options.MaxValueLength = 20;
        var atLimit = new string('a', 20);
        var overLimit = new string('b', 21);

        var (record, _) = Write(State("{Fits} {TooLong}", ("Fits", atLimit), ("TooLong", overLimit)));

        var p = record.GetProperty("properties");
        Assert.Equal(atLimit, p.GetProperty("Fits").GetString());
        Assert.Equal(21, p.GetProperty("TooLong").GetProperty("length").GetInt32());
    }

    [Fact]
    public void Strings_are_redacted_before_they_are_written()
    {
        var key = FakeSecrets.OpenAiKey();

        var (record, line) = Write(State("Header {Header} for {Path}", ("Header", "x-key: " + key), ("Path", @"C:\Users\testuser\repo\a.cs")));

        Assert.DoesNotContain(key, line, StringComparison.Ordinal);
        Assert.DoesNotContain("testuser", line, StringComparison.Ordinal);
        var p = record.GetProperty("properties");
        Assert.Equal("x-key: [REDACTED]", p.GetProperty("Header").GetString());
        Assert.Equal(@"~\repo\a.cs", p.GetProperty("Path").GetString());
        Assert.Equal(@"Header x-key: [REDACTED] for ~\repo\a.cs", record.GetProperty("message").GetString());
    }

    [Fact]
    public void An_object_is_written_by_its_type_name_and_never_by_its_content()
    {
        var (record, line) = Write(State("Starting {Request}", ("Request", new RequestStub(Sentence, "model-x"))));

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        Assert.DoesNotContain("model-x", line, StringComparison.Ordinal);
        Assert.Equal("Starting [RequestStub]", record.GetProperty("message").GetString());
        var value = record.GetProperty("properties").GetProperty("Request");
        Assert.Equal("RequestStub", value.GetProperty("type").GetString());
        Assert.False(value.TryGetProperty("count", out _));
    }

    [Fact]
    public void A_collection_is_written_by_its_type_and_count_and_never_by_its_items()
    {
        var (record, line) = Write(State("Paths {Paths} and {Names}", ("Paths", new List<string> { "alpha-secret", "beta", "gamma" }), ("Names", new[] { "one", "two" })));

        Assert.DoesNotContain("alpha-secret", line, StringComparison.Ordinal);
        Assert.Equal("Paths [List<String> (3 items)] and [String[] (2 items)]", record.GetProperty("message").GetString());
        var p = record.GetProperty("properties");
        Assert.Equal("List<String>", p.GetProperty("Paths").GetProperty("type").GetString());
        Assert.Equal(3, p.GetProperty("Paths").GetProperty("count").GetInt32());
        Assert.Equal(2, p.GetProperty("Names").GetProperty("count").GetInt32());
    }

    [Fact]
    public void A_struct_such_as_a_typed_id_is_written_through_its_text()
    {
        var (record, _) = Write(State("Agent {AgentId} started", ("AgentId", new AgentId("Exec-1"))));

        Assert.Equal("Agent Exec-1 started", record.GetProperty("message").GetString());
        Assert.Equal("Exec-1", record.GetProperty("properties").GetProperty("AgentId").GetString());
    }

    private readonly record struct Snapshot(string Prompt, int Tokens);

    [Fact]
    public void A_record_struct_prints_its_members_so_it_is_written_by_type_only()
    {
        var (record, line) = Write(State("Saw {Snapshot}", ("Snapshot", new Snapshot(Sentence, 5))));

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        Assert.Equal("Saw [Snapshot]", record.GetProperty("message").GetString());
        Assert.Equal("Snapshot", record.GetProperty("properties").GetProperty("Snapshot").GetProperty("type").GetString());
    }

    [Fact]
    public void Tuples_pairs_and_other_structs_without_their_own_text_are_written_by_type_only()
    {
        var (record, line) = Write(State(
            "{Tuple} {Pair} {Token}",
            ("Tuple", (Sentence, 1)),
            ("Pair", KeyValuePair.Create("Prompt", Sentence)),
            ("Token", CancellationToken.None)));

        Assert.DoesNotContain("payroll", line, StringComparison.Ordinal);
        var p = record.GetProperty("properties");
        Assert.Equal("ValueTuple<String,Int32>", p.GetProperty("Tuple").GetProperty("type").GetString());
        Assert.Equal("KeyValuePair<String,String>", p.GetProperty("Pair").GetProperty("type").GetString());
        Assert.Equal("CancellationToken", p.GetProperty("Token").GetProperty("type").GetString());
    }

    [Fact]
    public void A_struct_with_a_long_text_is_hashed()
    {
        var text = new string('x', 300);

        var (record, line) = Write(State("Saw {Item}", ("Item", new Wide())));

        Assert.DoesNotContain(text, line, StringComparison.Ordinal);
        Assert.Equal(300, record.GetProperty("properties").GetProperty("Item").GetProperty("length").GetInt32());
    }

    [Fact]
    public void A_value_whose_text_cannot_be_obtained_is_survived()
    {
        var (record, _) = Write(State("Saw {Item}", ("Item", new Bomb())));

        Assert.Equal("Saw [ToString failed: InvalidOperationException]", record.GetProperty("message").GetString());
        Assert.Equal("[ToString failed: InvalidOperationException]", record.GetProperty("properties").GetProperty("Item").GetString());
    }

    [Fact]
    public void An_exception_used_as_a_value_is_written_by_type_only()
    {
        var (record, line) = Write(State("Failed with {Error}", ("Error", new InvalidOperationException("secret detail"))));

        Assert.DoesNotContain("secret detail", line, StringComparison.Ordinal);
        Assert.Equal("InvalidOperationException", record.GetProperty("properties").GetProperty("Error").GetProperty("type").GetString());
    }

    // ---- exceptions ----

    private static Exception Thrown()
    {
        try
        {
            try
            {
                throw new IOException("inner failure");
            }
            catch (IOException inner)
            {
                throw new InvalidOperationException("outer failure", inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            return outer;
        }
    }

    [Fact]
    public void An_exception_is_summarised_with_type_message_frames_and_the_inner_chain()
    {
        var (record, _) = Write(State("Failed"), Thrown());

        var exception = record.GetProperty("exception");
        Assert.Equal("System.InvalidOperationException", exception.GetProperty("type").GetString());
        Assert.Equal("outer failure", exception.GetProperty("message").GetString());
        var frames = exception.GetProperty("stackTrace").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Assert.NotEmpty(frames);
        Assert.StartsWith("at ", frames[0], StringComparison.Ordinal);
        var inner = exception.GetProperty("inner");
        Assert.Equal("System.IO.IOException", inner.GetProperty("type").GetString());
        Assert.Equal("inner failure", inner.GetProperty("message").GetString());
    }

    [Fact]
    public void The_stack_is_limited_to_the_configured_number_of_frames()
    {
        _options.MaxStackFrames = 2;
        var stack = "   at A.One() in x.cs:line 1\n   at A.Two() in x.cs:line 2\n   at A.Three() in x.cs:line 3\n   at A.Four()";

        var (record, _) = Write(State("Failed"), new FakeStackException("boom", stack));

        var frames = record.GetProperty("exception").GetProperty("stackTrace").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Equal(["at A.One() in x.cs:line 1", "at A.Two() in x.cs:line 2"], frames);
    }

    [Fact]
    public void Stack_frames_and_messages_are_redacted()
    {
        var key = FakeSecrets.OpenAiKey();
        var stack = @"   at Demo.Run() in C:\Users\testuser\src\Demo.cs:line 12" + "\n   at Demo.Main()";

        var (record, line) = Write(State("Failed"), new FakeStackException("rejected " + key + @" for C:\Users\testuser\x", stack));

        Assert.DoesNotContain(key, line, StringComparison.Ordinal);
        Assert.DoesNotContain("testuser", line, StringComparison.Ordinal);
        var exception = record.GetProperty("exception");
        Assert.Equal(@"rejected [REDACTED] for ~\x", exception.GetProperty("message").GetString());
        Assert.Equal(@"at Demo.Run() in ~\src\Demo.cs:line 12", exception.GetProperty("stackTrace")[0].GetString());
    }

    [Fact]
    public void A_long_exception_message_is_written_as_length_and_hash_only()
    {
        var text = string.Concat(Enumerable.Repeat("the model said this ", 100));

        var (record, line) = Write(State("Failed"), new InvalidOperationException(text));

        Assert.DoesNotContain("the model said", line, StringComparison.Ordinal);
        Assert.Equal(Hashed(text), record.GetProperty("exception").GetProperty("message").GetString());
    }

    [Fact]
    public void An_exception_whose_message_throws_is_survived()
    {
        var (record, _) = Write(State("Failed"), new MessageBombException());

        Assert.Equal("[message unavailable: InvalidOperationException]", record.GetProperty("exception").GetProperty("message").GetString());
    }

    [Fact]
    public void The_inner_chain_stops_after_three_levels()
    {
        var chain = new Exception("one", new Exception("two", new Exception("three", new Exception("four", new Exception("five")))));

        var (record, _) = Write(State("Failed"), chain);

        var depth = 0;
        var current = record.GetProperty("exception");
        while (current.TryGetProperty("inner", out var inner))
        {
            depth++;
            current = inner;
        }

        Assert.Equal(3, depth);
        Assert.Equal("four", current.GetProperty("message").GetString());
    }

    // ---- text hygiene ----

    [Fact]
    public void Control_characters_and_line_separators_never_split_the_record()
    {
        var value = "a\nb\r\nc\td\u2028e\u2029f\u0085g\0h";

        var (record, line) = Write(State("Saw {Detail}", ("Detail", value)));

        foreach (var separator in new[] { '\u2028', '\u2029', '\u0085', '\0', '\t' })
        {
            Assert.DoesNotContain(separator, line);
        }

        Assert.Equal(value, record.GetProperty("properties").GetProperty("Detail").GetString());
    }

    [Fact]
    public void Accented_text_stays_readable()
    {
        var (_, line) = Write(State("Operación {Estado}: ¿ñandú?", ("Estado", "terminada")));

        Assert.Contains("Operación terminada: ¿ñandú?", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Characters_a_json_encoder_likes_to_escape_are_written_as_they_are()
    {
        var credential = FakeSecrets.BearerCredential();

        var (_, line) = Write("plain state", formatter: (_, _) => "<a href='x'>&" + credential);

        Assert.Contains("<a href='x'>&" + credential, line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpaired_surrogate_does_not_break_the_record()
    {
        var (record, _) = Write(State("Saw {Detail}", ("Detail", "ab\uD800cd")));

        var text = record.GetProperty("properties").GetProperty("Detail").GetString()!;
        Assert.StartsWith("ab", text, StringComparison.Ordinal);
        Assert.EndsWith("cd", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Emoji_survive_as_valid_text()
    {
        var (record, _) = Write(State("Saw {Detail}", ("Detail", "ok \U0001F600")));

        Assert.Equal("ok \U0001F600", record.GetProperty("properties").GetProperty("Detail").GetString());
    }

    // ---- notices and failures ----

    [Fact]
    public void A_notice_is_a_valid_record_with_numbers()
    {
        var bytes = Formatter().FormatNotice(At, LogLevel.Warning, "AgentStudio.Observability.FileLogger", "3 entries were dropped", ("Dropped", 3), ("Failed", 0));

        var (record, _) = Parse(bytes);
        Assert.Equal("Warning", record.GetProperty("level").GetString());
        Assert.Equal("3 entries were dropped", record.GetProperty("message").GetString());
        Assert.Equal(3, record.GetProperty("properties").GetProperty("Dropped").GetInt64());
        Assert.Equal(0, record.GetProperty("properties").GetProperty("Failed").GetInt64());
    }

    [Fact]
    public void A_failure_record_keeps_the_identity_of_the_entry_and_none_of_its_content()
    {
        var bytes = Formatter().FormatFailure(At, "Tests.Category", LogLevel.Error, new EventId(9, "Nine"), new InvalidOperationException("secret detail"));

        var (record, line) = Parse(bytes);
        Assert.DoesNotContain("secret detail", line, StringComparison.Ordinal);
        Assert.Equal("Error", record.GetProperty("level").GetString());
        Assert.Equal("Tests.Category", record.GetProperty("category").GetString());
        Assert.Equal(9, record.GetProperty("eventId").GetInt32());
        Assert.Equal("[log entry could not be formatted: InvalidOperationException]", record.GetProperty("message").GetString());
    }
}
