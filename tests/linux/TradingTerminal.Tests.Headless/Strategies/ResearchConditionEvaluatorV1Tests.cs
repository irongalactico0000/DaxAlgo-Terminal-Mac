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
    public void Custom_threshold_changes_version_and_hit_count()
    {
        var bars = new List<(DateTimeOffset, double, double)>();
        var t0 = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 40; i++)
        {
            var volume = 100d;
            if (i is 25 or 30 or 35)
                volume = 250d; // 2.5× → hits k=2, not k=3
            if (i == 36)
                volume = 400d; // 4× → hits both
            bars.Add((t0.AddMinutes(i), volume, 100 + i));
        }

        var c2 = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var c3 = ResearchConditionDefinitionV1.VolumeMultiple(3, 20);
        Assert.NotEqual(c2.VersionHashSha256, c3.VersionHashSha256);

        var r2 = ResearchConditionEvaluatorV1.SearchVolumeMultiple(c2, bars, "test", "TEST");
        var r3 = ResearchConditionEvaluatorV1.SearchVolumeMultiple(c3, bars, "test", "TEST");
        Assert.True(r2.HitCount > r3.HitCount, $"expected k=2 hits ({r2.HitCount}) > k=3 hits ({r3.HitCount})");
        Assert.True(r3.HitCount >= 1);
    }

    [Fact]
    public void MapSymbolForTsd_maps_crypto_aliases_without_remapping_equities()
    {
        Assert.Equal(
            "BTCUSDT",
            TradingTerminal.Infrastructure.Research.ResearchConditionSearchV1.MapSymbolForTsd("BTCUSD"));
        Assert.Equal(
            "ETHUSDT",
            TradingTerminal.Infrastructure.Research.ResearchConditionSearchV1.MapSymbolForTsd("ETH-USD"));
        Assert.Equal(
            "AAPL",
            TradingTerminal.Infrastructure.Research.ResearchConditionSearchV1.MapSymbolForTsd("AAPL"));
    }
}
