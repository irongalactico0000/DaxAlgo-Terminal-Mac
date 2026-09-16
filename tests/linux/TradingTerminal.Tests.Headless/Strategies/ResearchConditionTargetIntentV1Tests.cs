using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class ResearchConditionTargetIntentV1Tests
{
    [Fact]
    public void Live_meets_plans_meet_target()
    {
        var (action, units) = ResearchConditionTargetIntentV1.PlanTargetUnits(true, meetTargetUnits: 10);
        Assert.Equal(ResearchConditionTargetIntentV1.MeetAction, action);
        Assert.Equal(10d, units);
    }

    [Fact]
    public void Live_false_plans_flat()
    {
        var (action, units) = ResearchConditionTargetIntentV1.PlanTargetUnits(false, meetTargetUnits: 10);
        Assert.Equal(ResearchConditionTargetIntentV1.FlatAction, action);
        Assert.Equal(0d, units);
    }

    [Fact]
    public void Live_null_skips_without_inventing_exposure()
    {
        var (action, units) = ResearchConditionTargetIntentV1.PlanTargetUnits(null, meetTargetUnits: 10);
        Assert.Equal(ResearchConditionTargetIntentV1.SkipAction, action);
        Assert.Equal(0d, units);
    }

    [Fact]
    public void Evaluator_live_feeds_same_plan_as_monitor()
    {
        var bars = new List<(DateTimeOffset, double, double)>();
        var t0 = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 25; i++)
            bars.Add((t0.AddMinutes(i), Volume: 100, Close: 100 + i));
        bars[24] = (t0.AddMinutes(24), Volume: 250, Close: 130);

        var condition = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var search = ResearchConditionEvaluatorV1.SearchVolumeMultiple(
            condition, bars, "test", "TEST");
        var monitor = ResearchConditionEvaluatorV1.SearchVolumeMultiple(
            condition, bars, "test", "TEST");

        Assert.Equal(search.LiveMeetsCondition, monitor.LiveMeetsCondition);
        Assert.Equal(search.ConditionVersionHashSha256, monitor.ConditionVersionHashSha256);

        var (a1, u1) = ResearchConditionTargetIntentV1.PlanTargetUnits(
            search.LiveMeetsCondition, meetTargetUnits: 5);
        var (a2, u2) = ResearchConditionTargetIntentV1.PlanTargetUnits(
            monitor.LiveMeetsCondition, meetTargetUnits: 5);
        Assert.Equal(a1, a2);
        Assert.Equal(u1, u2);
        Assert.Equal(ResearchConditionTargetIntentV1.MeetAction, a1);
        Assert.Equal(5d, u1);
    }
}
