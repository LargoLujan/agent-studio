using System.IO;
using System.Text;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// Composes <c>docs/evidencias/fase5-demostraciones.md</c> from the captures the demonstrations left in
/// <c>AGENTSTUDIO_CAPTURE_DIR\fase5</c> (<see cref="DemoReport"/>), and keeps a copy next to them. It spends no quota and
/// launches nothing, so it needs only <c>AGENTSTUDIO_PHASE5_DEMOS=1</c> and the folder of the captures; run it after the
/// demonstrations, as many times as wanted (each run replaces the document).
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class Phase5DemoReportTests
{
    [Fact]
    public async Task Writes_the_evidence_document_from_the_captures()
    {
        DemoSettings.RequireDemos();
        var folder = DemoSettings.CaptureDirectory;
        Assert.SkipWhen(folder is null, "Set AGENTSTUDIO_CAPTURE_DIR to the folder the demonstrations left their captures in.");
        var (captures, unreadable) = DemoCaptures.ReadAll(folder!);
        Assert.SkipWhen(captures.Count == 0 && unreadable.Count == 0, $"There are no captures in {DemoCaptures.Folder(folder!)}: run the demonstrations first.");

        var document = DemoReport.Compose(captures, unreadable, DateTimeOffset.UtcNow);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var target = Path.Combine(SourceTree.Root, "docs", "evidencias", DemoReport.FileName);
        await File.WriteAllTextAsync(target, document, encoding, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(DemoCaptures.Folder(folder!), DemoReport.FileName), document, encoding, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"{captures.Count} capturas, {unreadable.Count} ilegibles: {DemoScrub.Text(target)}");

        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), document, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(unreadable);
    }
}
