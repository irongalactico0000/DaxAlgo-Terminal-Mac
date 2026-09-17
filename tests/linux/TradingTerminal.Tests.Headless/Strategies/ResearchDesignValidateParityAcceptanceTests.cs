using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

/// <summary>
/// Acceptance: Research condition eval and Design chart preview share hit timestamps / metrics
/// on one bar dataset, and Design EMA/SMA levels match Charts' <see cref="Indicators"/> at those
/// hits. Validate is asserted only for honest L1 fill fidelity — it does not evaluate Design ENTRY
/// conditions yet.
/// </summary>
public sealed class ResearchDesignValidateParityAcceptanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Volume_path_Research_and_Design_share_hit_timestamps_and_metrics()
    {
        var bars = BuildVolumeBars();
        var condition = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);

        var research = ResearchConditionEvaluatorV1.SearchVolumeMultiple(
            condition, bars, dataSource: "acceptance", symbol: "TEST");
        Assert.True(
            DesignConditionChartPreviewEvaluatorV1.TryEvaluateVolumeCondition(
                condition, bars, "acceptance", "TEST", out var design));

        Assert.Equal(research.HitCount, design.HitCount);
        Assert.Equal(
            research.Hits.Select(static h => h.BarTimeUtc).ToArray(),
            design.Hits.Select(static h => h.BarTimeUtc).ToArray());
        Assert.Equal(
            research.Hits.Select(static h => h.ConditionMetric).ToArray(),
            design.Hits.Select(static h => h.ConditionMetric).ToArray());
        Assert.Equal(
            research.Hits.Select(static h => h.Threshold).ToArray(),
            design.Hits.Select(static h => h.Threshold).ToArray());
        Assert.Equal(
            research.Hits.Select(static h => h.ForwardReturn).ToArray(),
            design.Hits.Select(static h => h.ForwardReturn).ToArray());
        Assert.Equal(research.ConditionVersionHashSha256, design.ConditionVersionHashSha256);
        Assert.True(research.HitCount >= 1);
    }

    [Fact]
    public void Design_sma_hits_match_Indicators_SMA_levels_at_same_timestamps()
    {
        var bars = BuildCloseBars();
        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryEvaluateDesignOperands(
            "sma(3)",
            "is above",
            "20",
            bars,
            dataSource: "acceptance",
            symbol: "TEST",
            out var design));

        Assert.True(design.HitCount >= 1);

        var sma = new Indicators.SimpleMovingAverage(3);
        var byTime = new Dictionary<DateTimeOffset, double>();
        for (var i = 0; i < bars.Count; i++)
        {
            sma.Push(bars[i].Close);
            if (sma.IsReady)
                byTime[bars[i].TimeUtc] = sma.Value;
        }

        foreach (var hit in design.Hits)
        {
            Assert.True(byTime.TryGetValue(hit.BarTimeUtc, out var level), $"missing SMA at {hit.BarTimeUtc:u}");
            Assert.True(level > 20, $"SMA at hit must be above 20, got {level}");
            // ConditionMetric is left − right (SMA − 20).
            Assert.Equal(Math.Round(level - 20, 6, MidpointRounding.AwayFromZero), hit.ConditionMetric);
        }
    }

    [Fact]
    public void Design_ema_hits_match_Indicators_EMA_levels_at_same_timestamps()
    {
        var bars = BuildCloseBars();
        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryEvaluateDesignOperands(
            "ema(5)",
            "is above",
            "ema(20)",
            bars,
            dataSource: "acceptance",
            symbol: "TEST",
            out var design));

        Assert.True(design.HitCount >= 1, design.Note);

        var fast = new Indicators.ExponentialMovingAverage(5);
        var slow = new Indicators.ExponentialMovingAverage(20);
        var byTime = new Dictionary<DateTimeOffset, (double Fast, double Slow)>();
        for (var i = 0; i < bars.Count; i++)
        {
            fast.Push(bars[i].Close);
            slow.Push(bars[i].Close);
            if (fast.IsReady && slow.IsReady)
                byTime[bars[i].TimeUtc] = (fast.Value, slow.Value);
        }

        foreach (var hit in design.Hits)
        {
            Assert.True(byTime.TryGetValue(hit.BarTimeUtc, out var levels), $"missing EMA at {hit.BarTimeUtc:u}");
            Assert.True(levels.Fast > levels.Slow);
            Assert.Equal(
                Math.Round(levels.Fast - levels.Slow, 6, MidpointRounding.AwayFromZero),
                hit.ConditionMetric);
        }
    }

    [Fact]
    public void Validate_L1_fixture_is_fill_fidelity_not_condition_hits()
    {
        // Honest gap: Validate does not evaluate Design ENTRY / research conditions on bars.
        // This asserts the L1 fill-fidelity surface that Validate does attach today.
        var report = L1ExecutionLifecycleFixtureV1.RunTarget50PartialFillCancel();

        Assert.Equal(L1ExecutionLifecycleFixtureV1.DataModeToken, report.DataModeToken);
        Assert.Contains("not Nautilus", report.HonestyNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(25, report.FinalPosition);
        Assert.DoesNotContain(
            report.Events,
            e => e.State.Contains("condition", StringComparison.OrdinalIgnoreCase));
    }

    private static List<(DateTimeOffset TimeUtc, double Volume, double Close)> BuildVolumeBars()
    {
        var bars = new List<(DateTimeOffset, double, double)>();
        for (var i = 0; i < 40; i++)
        {
            var volume = 100d;
            if (i is 25 or 30 or 35)
                volume = 250d;
            bars.Add((T0.AddMinutes(i), volume, 100 + i));
        }

        return bars;
    }

    private static List<(DateTimeOffset TimeUtc, double Close)> BuildCloseBars()
    {
        var bars = new List<(DateTimeOffset, double)>();
        for (var i = 0; i < 15; i++)
            bars.Add((T0.AddMinutes(i), 10));
        for (var i = 15; i < 45; i++)
            bars.Add((T0.AddMinutes(i), 30));
        return bars;
    }
}
