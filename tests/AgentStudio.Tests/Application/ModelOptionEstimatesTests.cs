using AgentStudio.Application.Decisions;
using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The expected consumption of each option of a model (docs/17 D-24, F13-11): the estimator of the system (docs/16 D-20,
/// brought forward; WP3 implements it) is asked once per option, with only the model changed. It reads the log only: no agent
/// and no quota. «Desconocida» stays unknown, never a number made up.
/// </summary>
public sealed class ModelOptionEstimatesTests
{
    private static readonly UsageEstimateRequest Request = new(
        new ProjectContext("p1", "C:\\repo"), new AgentId("ejecutor"), ProviderId.ClaudeCode, "sonnet", AgentRunPurpose.Executor, WorkSize.Medium);

    [Fact]
    public async Task Each_option_is_estimated_once_in_its_order_with_only_its_model_changed()
    {
        var estimator = new FakeEstimator();

        var options = await ModelOptionEstimates.CompareAsync(estimator, Request, ["claude-sonnet-5-5", "OPUS", null, "claude-sonnet-5-5", " opus ", ""], TestContext.Current.CancellationToken);

        Assert.Equal(["claude-sonnet-5-5", "OPUS", null], options.Select(o => o.Model));
        Assert.Equal([Request with { Model = "claude-sonnet-5-5" }, Request with { Model = "OPUS" }, Request with { Model = null }], estimator.Asked);
        Assert.True(options[0].Estimate.IsKnown);
        Assert.False(options[1].Estimate.IsKnown);
        Assert.False(options[1].Estimate.InputTokens.IsKnown);
        Assert.Equal(MetricOrigin.Missing, options[1].Estimate.InputTokens.Low.Origin);
    }

    /// <summary>A fake estimator: three references for the full name of sonnet, two for anything else.</summary>
    private sealed class FakeEstimator : IUsageEstimator
    {
        public List<UsageEstimateRequest> Asked { get; } = [];

        public Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            if (request.Model != "claude-sonnet-5-5")
            {
                return Task.FromResult(UsageEstimate.Unknown(2));
            }

            static EstimateInterval<T> Interval<T>(T low, T high)
                where T : struct => new(MetricValue<T>.EstimatedBySystem(low, "p25"), MetricValue<T>.EstimatedBySystem(high, "p75"));
            return Task.FromResult(new UsageEstimate(3, Interval(1000L, 2000L), Interval(100L, 200L), Interval(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)), Interval(0.1m, 0.2m), [EstimateAssumptions.SameAgentAndModel]));
        }

        public Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
