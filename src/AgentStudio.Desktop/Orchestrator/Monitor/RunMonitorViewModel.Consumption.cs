using AgentStudio.Desktop.Observability.Analysis;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

// Phase 13 (docs/17 D-34 and D-35, F13-15, WP8): the analysis of the consumption of the run on screen. The deterministic one runs when
// the run ends, as the auditor does, and when the person asks; the one with an agent only with their OK. Neither applies anything.

public sealed partial class RunMonitorViewModel
{
    private ConsumptionAnalysisViewModel? _consumption;

    /// <summary>
    /// The analysis of the consumption of the run followed: its findings, its button and the OK of the analysis with an agent. The shell gives
    /// it the analyzers once (<see cref="ConsumptionAnalysisViewModel.Analyzer"/> and <see cref="ConsumptionAnalysisViewModel.Assisted"/>);
    /// without them the section is not shown.
    /// </summary>
    public ConsumptionAnalysisViewModel Consumption => _consumption ??= new ConsumptionAnalysisViewModel(_time, _ui);

    /// <summary>Completes once the deterministic analysis that the end of the run started has finished and been applied.</summary>
    public Task ConsumptionCheck { get; private set; } = Task.CompletedTask;

    /// <summary>A run ends: its consumption is analysed at once, free and over the verified log, like the auditor (D-34). A run only read back from its log waits for the button.</summary>
    private void AnalyzeConsumptionAfterEnd(bool isFinal)
    {
        if (isFinal && !IsInspecting && _consumption is { IsAvailable: true } analysis)
        {
            ConsumptionCheck = analysis.RunDeterministicAsync();
        }
    }
}
