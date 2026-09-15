using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class ResearchConditionEvaluatorV1Tests
{
    [Fact]
    public void Volume_multiple_finds_spike_and_reports_live()
    {
        var bars = new List<(DateTimeOffset, double, double)>();
        var t0 = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 25; i++)
            bars.Add((t0.AddMinutes(i), Volume: 100, Close: 100 + i));
        // Spike at index 24 relative to prior 20 bars of 100
        bars[24] = (t0.AddMinutes(24), Volume: 250, Close: 130);

        var condition = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var result = ResearchConditionEvaluatorV1.SearchVolumeMultiple(
            condition,
            bars,
            dataSource: "test",
            symbol: "TEST");

        Assert.True(result.HitCount >= 1);
        Assert.Contains(result.Hits, hit => hit.ConditionMetric >= 2);
        Assert.True(result.LiveMeetsCondition);
        Assert.Equal(condition.VersionHashSha256, result.ConditionVersionHashSha256);
    }

    [Fact]
    public void MapSymbolForTsd_defaults_equity_to_btc_for_wire_proof()
    {
        Assert.Equal(
            "BTCUSDT",
            TradingTerminal.Infrastructure.Research.ResearchConditionSearchV1.MapSymbolForTsd("AAPL"));
        Assert.Equal(
            "ETHUSDT",
            TradingTerminal.Infrastructure.Research.ResearchConditionSearchV1.MapSymbolForTsd("ETH-USD"));
    }
}
