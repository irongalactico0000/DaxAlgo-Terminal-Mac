using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class DesignConditionChartPreviewEvaluatorV1Tests
{
    [Fact]
    public void Sma_is_above_level_marks_bars()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<(DateTimeOffset, double)>();
        for (var i = 0; i < 10; i++)
            bars.Add((t0.AddMinutes(i), 10));
        for (var i = 10; i < 20; i++)
            bars.Add((t0.AddMinutes(i), 30));

        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryEvaluateDesignOperands(
            "sma(3)",
            "is above",
            "20",
            bars,
            dataSource: "test",
            symbol: "TEST",
            out var result));

        Assert.True(result.HitCount >= 1, $"expected level hits, got {result.HitCount}: {result.Note}");
        Assert.Contains("sma(3) is above 20", result.ConditionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sma_crosses_above_finds_crossover_bar()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        // Fast SMA(2) starts below slow SMA(4), then a step-up forces a cross.
        var closes = new double[] { 1, 1, 1, 1, 1, 1, 10, 10, 10, 10, 10, 10 };
        var bars = closes
            .Select((close, i) => (t0.AddMinutes(i), close))
            .ToList();

        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryEvaluateDesignOperands(
            "sma(2)",
            "crosses above",
            "sma(4)",
            bars,
            dataSource: "test",
            symbol: "TEST",
            out var result));

        Assert.True(result.HitCount >= 1, $"expected crossover hits, got {result.HitCount}: {result.Note}");
    }

    [Fact]
    public void Unsupported_operand_returns_false()
    {
        var bars = new List<(DateTimeOffset, double)>
        {
            (DateTimeOffset.UtcNow, 1),
        };
        Assert.False(DesignConditionChartPreviewEvaluatorV1.TryEvaluateDesignOperands(
            "volume",
            "is above",
            "avg",
            bars,
            "test",
            "TEST",
            out _));
    }

    [Fact]
    public void Parse_series_operand_accepts_ema_and_sma()
    {
        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryParseSeriesOperand("EMA(20)", out var kind, out var period));
        Assert.Equal("ema", kind);
        Assert.Equal(20, period);
        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryParseSeriesOperand("sma(50)", out kind, out period));
        Assert.Equal("sma", kind);
        Assert.Equal(50, period);
    }
}
