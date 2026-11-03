using AgentStudio.Application.Decisions;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Observability.Usage;

namespace AgentStudio.Tests.Observability.Context;

/// <summary>Catalogs of context windows learned from a synthetic, verified log, for the screens that judge the live context.</summary>
internal static class ModelWindowCatalogTestData
{
    /// <summary>The windows the CLI reported for each model in earlier runs of the project, with the base table under them.</summary>
    public static ModelWindowCatalog Learned(params (string Model, long Window)[] windows)
    {
        var log = new UsageLogBuilder();
        var attempt = 1;
        foreach (var (model, window) in windows)
        {
            log.Start("20261020-a1b2c3", "T1", attempt)
                .Finish("20261020-a1b2c3", "T1", attempt, session: "s" + attempt, usage: Snapshots.Claude(10, 0, 0, 1, 0m, Snapshots.Model(model, 10, 1, 0, 0, 0m, window: window)));
            attempt++;
        }

        return ModelWindowCatalog.FromLog(new DecisionLogState(log.Entries, AgentStudio.Domain.Decisions.DecisionLogVerification.Valid)).WithBaseline();
    }
}
