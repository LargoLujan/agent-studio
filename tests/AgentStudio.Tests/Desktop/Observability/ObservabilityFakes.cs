using System.IO;
using System.Text;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>A settings store that keeps one copy and records which writer saved it.</summary>
internal sealed class RecordingSettingsStore(AppSettings? initial = null) : IAppSettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? AppSettings.Default;

    public List<string> Writes { get; } = [];

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Writes.Add("save");
        Current = settings;
    }

    public void SavePreferences(AppSettings settings)
    {
        _ = settings.ContextThresholds();
        Writes.Add("preferences");
        Current = Current.WithContextThresholds(settings.ContextWarningPercent, settings.ContextCompressPercent, settings.ContextCriticalPercent)
            with { CodexQuotaReadEnabled = settings.CodexQuotaReadEnabled };
    }
}

/// <summary>The user's quota record: what <see cref="LatestAsync"/> answers, and what was appended.</summary>
internal sealed class FakeQuotaObservations : IQuotaObservations
{
    public List<QuotaObservation> Latest { get; } = [];

    public List<QuotaObservation> Appended { get; } = [];

    public Task AppendAsync(IReadOnlyList<QuotaObservation> observations, CancellationToken ct)
    {
        Appended.AddRange(observations);
        Latest.AddRange(observations);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QuotaObservation>> LatestAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<QuotaObservation>>([.. Latest]);
}

/// <summary>A Codex quota reader that answers what the test sets and counts the reads; no process behind it.</summary>
internal sealed class FakeQuotaReader(Func<QuotaReadResult>? result = null) : IProviderQuotaReader
{
    public ProviderId Provider => ProviderId.CodexCli;

    public int Reads { get; private set; }

    public Func<QuotaReadResult> Result { get; set; } = result ?? (() => new QuotaReadResult(QuotaReadStatus.Read, []));

    public Task<QuotaReadResult> ReadAsync(CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Result());
    }
}

/// <summary>A usage report that answers what the test sets and records the queries.</summary>
internal sealed class FakeUsageReport(Func<UsageReport>? report = null) : IUsageReport
{
    public List<(IReadOnlyList<string> Roots, UsageQuery Query)> Queries { get; } = [];

    public Func<UsageReport> Report { get; set; } = report ?? (() => UsageSamples.Empty);

    public Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct)
    {
        Queries.Add(([.. projectRoots], query));
        return Task.FromResult(Report());
    }
}

/// <summary>An exporter that writes a fixed text (UTF-8 with BOM, as the real CSV) and records which format was asked.</summary>
internal sealed class FakeUsageExporter : IUsageExporter
{
    public List<(string Format, UsageReport Report)> Calls { get; } = [];

    /// <summary>When set, writing throws it after a first chunk went out (a half-written export).</summary>
    public Exception? Failure { get; set; }

    public Task WriteCsvAsync(UsageReport report, Stream destination, CancellationToken ct) => WriteAsync("csv", "﻿attempt,input\r\n1,1200\r\n", report, destination, ct);

    public Task WriteJsonAsync(UsageReport report, Stream destination, CancellationToken ct) => WriteAsync("json", "{\"schema\":\"agentstudio-usage/1\"}", report, destination, ct);

    private async Task WriteAsync(string format, string content, UsageReport report, Stream destination, CancellationToken ct)
    {
        Calls.Add((format, report));
        var bytes = new UTF8Encoding(false).GetBytes(content);
        await destination.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), ct);
        if (Failure is not null)
        {
            throw Failure;
        }

        await destination.WriteAsync(bytes.AsMemory(bytes.Length / 2), ct);
    }
}

/// <summary>A «Guardar como» that answers a fixed path (null: the user cancelled) and records what it was asked.</summary>
internal sealed class FakeSaveFilePicker(string? answer) : ISaveFilePicker
{
    public List<(string Title, string FileName, string Filter)> Asked { get; } = [];

    public string? Answer { get; set; } = answer;

    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        Asked.Add((title, suggestedFileName, filter));
        return Answer;
    }
}
