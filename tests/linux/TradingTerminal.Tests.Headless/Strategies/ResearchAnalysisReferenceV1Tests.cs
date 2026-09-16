using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

/// <summary>
/// Canon R13 slice: two named in-app references keep distinct condition versions after serialize/restore.
/// </summary>
public sealed class ResearchAnalysisReferenceV1Tests
{
    [Fact]
    public void References_A_and_B_round_trip_with_distinct_conditions()
    {
        var conditionA = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var conditionB = ResearchConditionDefinitionV1.VolumeMultiple(3, 20);
        Assert.NotEqual(conditionA.VersionHashSha256, conditionB.VersionHashSha256);

        var bars = BuildBars();
        var searchA = ResearchConditionEvaluatorV1.SearchVolumeMultiple(conditionA, bars, "test", "BTCUSDT");
        var searchB = ResearchConditionEvaluatorV1.SearchVolumeMultiple(conditionB, bars, "test", "BTCUSDT");
        Assert.True(searchA.HitCount > searchB.HitCount);

        var selectionA = new ResearchChartSelectionV1(
            new InstrumentId(1),
            "BTCUSDT",
            BarSize.OneMinute,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            DateTimeOffset.UnixEpoch.AddHours(1),
            DateTimeOffset.UnixEpoch.AddHours(2),
            StrategyDataRequirement.Bars);
        var selectionB = new ResearchChartSelectionV1(
            new InstrumentId(2),
            "ETHUSDT",
            BarSize.FiveMinutes,
            DateTimeOffset.UnixEpoch.AddDays(1),
            DateTimeOffset.UnixEpoch.AddDays(1).AddHours(1),
            DateTimeOffset.UnixEpoch.AddDays(1).AddHours(1),
            DateTimeOffset.UnixEpoch.AddDays(1).AddHours(2),
            StrategyDataRequirement.Bars);

        var refA = new ResearchAnalysisReferenceV1(
            ResearchAnalysisReferenceV1.CurrentSchemaVersion,
            "A",
            "A",
            DateTimeOffset.UnixEpoch,
            conditionA,
            searchA,
            selectionA,
            [new ResearchIndicatorBindingV1("ema-21", "ema", 21)]);
        var refB = new ResearchAnalysisReferenceV1(
            ResearchAnalysisReferenceV1.CurrentSchemaVersion,
            "B",
            "B",
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            conditionB,
            searchB,
            selectionB,
            [new ResearchIndicatorBindingV1("ema-50", "ema", 50)]);

        var json = ResearchAnalysisReferenceCanonicalJsonV1.SerializeMany([refA, refB]);
        var restored = ResearchAnalysisReferenceCanonicalJsonV1.DeserializeMany(json);
        Assert.Equal(2, restored.Count);

        var a = Assert.Single(restored, r => r.ReferenceId == "A");
        var b = Assert.Single(restored, r => r.ReferenceId == "B");
        Assert.Equal(conditionA.VersionHashSha256, a.Condition.VersionHashSha256);
        Assert.Equal(conditionB.VersionHashSha256, b.Condition.VersionHashSha256);
        Assert.Equal(searchA.HitCount, a.SearchResult!.HitCount);
        Assert.Equal(searchB.HitCount, b.SearchResult!.HitCount);
        Assert.Equal(21, a.IndicatorBindings[0].Period);
        Assert.Equal(50, b.IndicatorBindings[0].Period);
        Assert.Equal("BTCUSDT", a.Selection!.CanonicalSymbol);
        Assert.Equal("ETHUSDT", b.Selection!.CanonicalSymbol);
        Assert.Equal(BarSize.OneMinute, a.Selection.Timeframe);
        Assert.Equal(BarSize.FiveMinutes, b.Selection.Timeframe);
    }

    private static List<(DateTimeOffset, double, double)> BuildBars()
    {
        var bars = new List<(DateTimeOffset, double, double)>();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        for (var i = 0; i < 40; i++)
        {
            var volume = 100d;
            if (i is 25 or 30 or 35)
                volume = 250d;
            if (i == 36)
                volume = 400d;
            bars.Add((t0.AddMinutes(i), volume, 100 + i));
        }

        return bars;
    }
}
