using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodifySpike.Harness;

/// <summary>One verified behaviour of the scripted run; <see cref="Detail"/> says what was observed.</summary>
public sealed record CheckResult(string Area, string Name, bool Passed, string Detail);

public sealed record EnvironmentInfo(
    string OperatingSystem,
    string Runtime,
    int LogicalProcessors,
    int RenderTier,
    double DpiScale,
    string WindowSizePixels,
    string NodifyVersion);

public sealed record FrameStats(
    string Scenario,
    int Frames,
    double Seconds,
    double AverageFps,
    double MeanFrameMs,
    double MedianFrameMs,
    double P95FrameMs,
    double MaxFrameMs,
    int FramesOver20Ms,
    int FramesOver33Ms);

public sealed record ThemeReport(
    int NodifyColourKeys,
    int RemappedFromTokens,
    int TransparentOnPurpose,
    IReadOnlyList<string> UnmappedKeys,
    IReadOnlyList<string> LiveElementMismatches,
    string CanvasBackgroundToken,
    string CanvasBackgroundPixel,
    string NodeHeaderToken,
    string NodeHeaderPixel);

/// <summary>Milliseconds since the load started: the view model is built, a first frame is drawn, every wire has its anchors.</summary>
public sealed record RenderTimes(double ViewModelBuildMs, double FirstFrameMs, double FullyDrawnMs);

public sealed record BenchmarkReport(
    int Nodes,
    int Connections,
    int ContainersRendered,
    int WiresRendered,
    RenderTimes PlanSizeLoad20Nodes,
    RenderTimes FirstLoad,
    IReadOnlyList<RenderTimes> WarmReloads,
    double DomainGraphRebuildMs,
    double CycleProbeMs,
    double WorkingSetMbBefore,
    double WorkingSetMbAfter,
    double FitZoom,
    IReadOnlyList<FrameStats> Frames);

public sealed class HarnessReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string GeneratedAt { get; set; } = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

    public EnvironmentInfo? Environment { get; set; }

    /// <summary>Whole-machine CPU load sampled when the run and the benchmark start and end (the machine is shared with other agents).</summary>
    public List<string> MachineLoad { get; } = [];

    public ThemeReport? Theme { get; set; }

    /// <summary>The 12-node sample, loaded first in a fresh process: includes template parsing and JIT.</summary>
    public RenderTimes? SmallGraphColdLoad { get; set; }

    public BenchmarkReport? Benchmark { get; set; }

    /// <summary>Only filled by <c>--only exp</c>: the measurement-method comparison.</summary>
    public IReadOnlyList<FrameStats>? Experiment { get; set; }

    public List<CheckResult> Checks { get; } = [];

    /// <summary>Stages that did not run, and why (an empty list means the run is complete).</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Every time the harness window became the foreground window (all of them were handed back at once); empty when it never did.</summary>
    public List<string> ForegroundTakeovers { get; } = [];

    public List<string> Screenshots { get; } = [];

    public string? Fatal { get; set; }

    public int Passed => Checks.Count(c => c.Passed);

    [JsonIgnore]
    public int Failures => Checks.Count(c => !c.Passed);

    public int Failed => Failures;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
