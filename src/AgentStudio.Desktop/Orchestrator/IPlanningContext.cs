using System.ComponentModel;
using AgentStudio.Desktop.Controls;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// The context of the orchestrator agent while it plans (docs/18 P3 and V33): the status bar shows it as it shows the context
/// of a task in course, so the context of what runs is always in view. Raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// on the UI thread when either member changes.
/// </summary>
public interface IPlanningContext : INotifyPropertyChanged
{
    /// <summary>The bar of the context of the planning; it stays after the planning ended, with its last reading.</summary>
    MetricBarReading? PlanningContext { get; }

    /// <summary>The orchestrator is planning now: from «Pedir plan» until its proposal, its failure or its cancellation.</summary>
    bool IsPlanningInProgress { get; }
}
