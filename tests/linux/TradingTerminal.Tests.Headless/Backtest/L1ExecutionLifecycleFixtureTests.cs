using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Headless.Backtest;

/// <summary>
/// Honest L1 execution lifecycle via the shared Validate fixture (Infrastructure L1FillModel) —
/// not Nautilus queue/liquidity matching.
/// </summary>
public sealed class L1ExecutionLifecycleFixtureTests
{
    [Fact]
    public void Target_plus_50_partial_fill_then_cancel_leaves_position_plus_25()
    {
        var report = L1ExecutionLifecycleFixtureV1.RunTarget50PartialFillCancel();

        Assert.Equal(50, report.TargetPosition);
        Assert.Equal(25, report.FinalPosition);
        Assert.Equal(25, report.FilledQuantity);
        Assert.Equal(25, report.CanceledRemaining);
        Assert.Contains(report.Events, e => e.State == "Working");
        Assert.Contains(report.Events, e => e.State == "PartiallyFilled");
        Assert.Contains(report.Events, e => e.State == "Cancelled");
        Assert.Equal(L1ExecutionLifecycleFixtureV1.DataModeToken, report.DataModeToken);
        Assert.Contains("not Nautilus", report.HonestyNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finalPosition\":25", L1ExecutionLifecycleFixtureV1.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_touches_without_cancel_reach_target_plus_50()
    {
        var clock = new SimulatedClock();
        var t0 = new DateTime(2026, 3, 1, 14, 0, 0, DateTimeKind.Utc);
        clock.SetTo(t0);

        var fillModel = new L1FillModel(tickSize: 0.01, slippageTicks: 0, maxFillQuantityPerTouch: 25);
        var book = new SimulatedOrderBook(clock, fillModel);
        var contract = Contract.UsStock("ES");
        var events = new List<OrderEvent>();
        using var _ = book.Events.Subscribe(events.Add);

        book.Submit(
            new OrderRequest("c-full", contract, OrderSide.Buy, OrderType.Market, Quantity: 50));

        var tick1 = new Tick(t0.AddMilliseconds(1), Bid: 100, Ask: 100.01, BidSize: 100, AskSize: 100);
        book.OnTick(contract, tick1);
        Assert.Equal(OrderState.PartiallyFilled, events[^1].State);
        Assert.Equal(25, events[^1].FilledQuantity);

        var tick2 = new Tick(t0.AddMilliseconds(2), Bid: 100, Ask: 100.01, BidSize: 100, AskSize: 100);
        book.OnTick(contract, tick2);
        Assert.Equal(OrderState.Filled, events[^1].State);
        Assert.Equal(50, events[^1].FilledQuantity);
    }
}
