using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Headless.Backtest;

public sealed class L2BookWalkIocFixtureTests
{
    [Fact]
    public void Canonical_ioc_math_matches_workstream_table()
    {
        var walk = L2BookWalkFillV1.CanonicalIocBuyLimit100At100_01();
        Assert.Equal(80, walk.FilledQuantity);
        Assert.Equal(20, walk.UnfilledQuantity);
        Assert.Equal(100.00625, walk.AverageFillPrice, precision: 9);
        Assert.Equal(2, walk.LevelsConsumed);
    }

    [Fact]
    public void Simulated_book_ioc_path_fills_80_cancels_20()
    {
        var report = L2BookWalkLifecycleFixtureV1.RunCanonicalIocBuy();
        Assert.Equal(80, report.FilledQuantity);
        Assert.Equal(20, report.CanceledRemaining);
        Assert.Equal(100.00625, report.AverageFillPrice, precision: 9);
        Assert.Contains("bookwalk-v1", report.DataModeToken, StringComparison.Ordinal);
        Assert.Contains("TradeLedger", report.HonestyNote, StringComparison.Ordinal);
        Assert.True(report.AccountingReconciled);
        Assert.Equal(80, report.Position);
        Assert.True(report.TotalFees > 0);
        Assert.Equal(report.StartingCash - (80 * report.AverageFillPrice) - report.TotalFees, report.EndingCash, precision: 6);
        Assert.Equal(report.StartingCash - report.TotalFees, report.EquityAtAverageFill, precision: 6);
    }

    [Fact]
    public void Walk_respects_sell_side_and_limit()
    {
        DepthLevel[] bids =
        [
            new(100.00, 40),
            new(99.99, 60),
            new(99.50, 1000),
        ];
        var walk = L2BookWalkFillV1.Walk(isBuy: false, quantity: 100, oppositeLevels: bids, limitPrice: 99.99);
        Assert.Equal(100, walk.FilledQuantity);
        Assert.Equal(0, walk.UnfilledQuantity);
        Assert.Equal((40 * 100.00 + 60 * 99.99) / 100.0, walk.AverageFillPrice, precision: 9);
    }
}

public sealed class FifoQueueAheadEstimatorTests
{
    [Fact]
    public void Join_and_consume_clear_when_opposite_size_falls()
    {
        var ahead = FifoQueueAheadEstimatorV1.Join(100);
        Assert.False(FifoQueueAheadEstimatorV1.IsCleared(ahead));
        ahead = FifoQueueAheadEstimatorV1.Consume(ahead, previousOppositeSize: 100, currentOppositeSize: 40);
        Assert.Equal(40, ahead);
        ahead = FifoQueueAheadEstimatorV1.Consume(ahead, previousOppositeSize: 40, currentOppositeSize: 0);
        Assert.True(FifoQueueAheadEstimatorV1.IsCleared(ahead));
    }

    [Fact]
    public void Size_increase_does_not_clear_ahead()
    {
        var ahead = FifoQueueAheadEstimatorV1.Join(50);
        ahead = FifoQueueAheadEstimatorV1.Consume(ahead, previousOppositeSize: 50, currentOppositeSize: 80);
        Assert.Equal(50, ahead);
    }
}
