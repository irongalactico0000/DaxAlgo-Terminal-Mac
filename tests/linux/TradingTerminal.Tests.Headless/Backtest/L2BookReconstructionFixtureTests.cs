using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Headless.Backtest;

public sealed class L2BookReconstructionFixtureTests
{
    [Fact]
    public void Timeline_has_explicit_book_at_each_quote_timestamp()
    {
        var report = L2BookReconstructionFixtureV1.RunTwoTimestampTimeline();
        Assert.Equal(2, report.Timestamps.Count);
        Assert.All(report.Modes, m => Assert.Equal(L2BookReconstructionV1.ModeReconstructedLadder, m));
        Assert.Equal(100.01, report.BestAsks[0]);
        Assert.Equal(20, report.BestAskSizes[0]);
        Assert.Equal(88, report.RealSnapshotOverrideAskSize);
        Assert.Equal(35, report.SessionFillQuantity);
        Assert.Contains("reconstructed-l1-ladder-v1", report.DataModeToken, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_snapshot_mode_is_preferred_when_present()
    {
        var t0 = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        var quote = new Tick(t0, 10, 10.01, 5, 5);
        var real = new DepthSnapshot(t0, [new DepthLevel(10, 7)], [new DepthLevel(10.01, 9)]);
        var book = L2BookReconstructionV1.At(t0, quote, real);
        Assert.Equal(L2BookReconstructionV1.ModeRealSnapshot, book.Mode);
        Assert.Equal(9, book.Book.BestAskSize);
    }
}
