using AgentStudio.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

public sealed class FileLoggerOptionsTests
{
    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData("None", LogLevel.None)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData(" Error ", LogLevel.Error)]
    [InlineData("1", LogLevel.Debug)]
    public void A_level_is_read_from_its_name_or_number(string text, LogLevel expected)
    {
        Assert.Equal(expected, FileLoggerOptions.ParseLevel(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("verbose")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Debug,Warning")]
    public void Anything_else_gives_the_fallback_so_a_typo_cannot_stop_the_app(string? text)
    {
        Assert.Equal(LogLevel.Information, FileLoggerOptions.ParseLevel(text));
        Assert.Equal(LogLevel.Warning, FileLoggerOptions.ParseLevel(text, LogLevel.Warning));
    }

    [Fact]
    public void The_defaults_are_the_production_settings()
    {
        var options = new FileLoggerOptions();

        Assert.Equal(LogLevel.Information, options.MinLevel);
        Assert.Equal("agentstudio", options.FileNamePrefix);
        Assert.Equal(10L * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Equal(14, options.RetentionDays);
        Assert.Equal(4096, options.QueueCapacity);
        Assert.Equal(256, options.MaxValueLength);
        Assert.Equal(1024, options.MaxMessageLength);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ShutdownTimeout);
    }

    [Fact]
    public void The_sensitive_words_start_with_the_documented_defaults_and_can_be_extended_per_instance()
    {
        var first = new FileLoggerOptions();
        var second = new FileLoggerOptions();

        first.SensitiveNameWords.Add("diff");

        Assert.Contains("prompt", first.SensitiveNameWords);
        Assert.Contains("PROMPT", first.SensitiveNameWords); // case-insensitive
        Assert.Contains("diff", first.SensitiveNameWords);
        Assert.DoesNotContain("diff", second.SensitiveNameWords);
        Assert.Contains("instructions", FileLoggerOptions.DefaultSensitiveNameWords);
        Assert.DoesNotContain("diff", FileLoggerOptions.DefaultSensitiveNameWords);
    }

    [Fact]
    public void The_exempt_words_start_with_the_documented_defaults_and_can_be_extended_per_instance()
    {
        var first = new FileLoggerOptions();
        var second = new FileLoggerOptions();

        first.ExemptNameWords.Add("snippet");

        Assert.Contains("root", first.ExemptNameWords);
        Assert.Contains("PATH", first.ExemptNameWords); // case-insensitive
        Assert.Contains("snippet", first.ExemptNameWords);
        Assert.DoesNotContain("snippet", second.ExemptNameWords);
        Assert.DoesNotContain("text", FileLoggerOptions.DefaultExemptNameWords);
    }
}
