using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Execution;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Headless.Backtest;

/// <summary>
/// Honest L1 execution lifecycle: target +50 → market order 50 → one touch fills 25 (max-per-touch)
/// → cancel remainder → final position +25. This is first-party L1TouchFillModel behavior —
/// not Nautilus queue/liquidity matching. Queue/liquidity remain unavailable until a real adapter.
/// </summary>
public sealed class L1ExecutionLifecycleFixtureTests
{
    public const string DataModeToken =
        "L1TouchFillModel|book=L1|queue=off|liq=off|partials=max25|latencyMs=0|fixture=target50-fill25-cancel";

    [Fact]
    public void Target_plus_50_partial_fill_then_cancel_leaves_position_plus_25()
    {
        var report = RunTarget50PartialFillCancelFixture();

        Assert.Equal(50, report.TargetPosition);
        Assert.Equal(25, report.FinalPosition);
        Assert.Equal(25, report.FilledQuantity);
        Assert.Equal(25, report.CanceledRemaining);
        Assert.Contains(report.Events, e => e.State == OrderState.Working);
        Assert.Contains(report.Events, e => e.State == OrderState.PartiallyFilled);
        Assert.Contains(report.Events, e => e.State == OrderState.Cancelled);
        Assert.Equal(DataModeToken, report.DataModeToken);
        Assert.Contains("not Nautilus", report.HonestyNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_touches_without_cancel_reach_target_plus_50()
    {
        var clock = new SimClock();
        var t0 = new DateTime(2026, 3, 1, 14, 0, 0, DateTimeKind.Utc);
        clock.SetTo(t0);

        var fillModel = new L1TouchFillModel(slippageTicks: 0, maxFillQuantityPerTouch: 25);
        var book = new SimulatedOrderBook(clock, fillModel, _ => 0.01);
        var instrument = new InstrumentId(42);
        var contract = Contract.UsStock("ES");
        var events = new List<OrderEvent>();
        using var _ = book.BindRequiredTransitionSink((_, e) => events.Add(e));

        book.Submit(
            new OrderRequest("c-full", contract, OrderSide.Buy, OrderType.Market, Quantity: 50),
            instrument);

        var tick1 = new Tick(t0.AddMilliseconds(1), Bid: 100, Ask: 100.01, BidSize: 100, AskSize: 100);
        book.OnQuote(instrument, tick1);
        Assert.Equal(OrderState.PartiallyFilled, events[^1].State);
        Assert.Equal(25, events[^1].FilledQuantity);

        var tick2 = new Tick(t0.AddMilliseconds(2), Bid: 100, Ask: 100.01, BidSize: 100, AskSize: 100);
        book.OnQuote(instrument, tick2);
        Assert.Equal(OrderState.Filled, events[^1].State);
        Assert.Equal(50, events[^1].FilledQuantity);
    }

    /// <summary>Shared runner for Builder result attachment tests.</summary>
    public static L1ExecutionLifecycleReport RunTarget50PartialFillCancelFixture()
    {
        var clock = new SimClock();
        var t0 = new DateTime(2026, 3, 1, 14, 30, 0, DateTimeKind.Utc);
        clock.SetTo(t0);

        // max 25 per touch: first quote fills half of a 50-share market buy toward target +50.
        var fillModel = new L1TouchFillModel(slippageTicks: 0, maxFillQuantityPerTouch: 25);
        var book = new SimulatedOrderBook(clock, fillModel, _ => 0.01);
        var instrument = new InstrumentId(7);
        var contract = Contract.UsStock("MSFT");
        var events = new List<OrderEvent>();
        using var _ = book.BindRequiredTransitionSink((_, e) => events.Add(e));

        const long target = 50;
        book.Submit(
            new OrderRequest("c-target50", contract, OrderSide.Buy, OrderType.Market, Quantity: target),
            instrument);

        var tick = new Tick(t0.AddMilliseconds(5), Bid: 410.0, Ask: 410.05, BidSize: 200, AskSize: 200);
        book.OnQuote(instrument, tick);

        var partial = events.Last(e => e.State == OrderState.PartiallyFilled);
        Assert.Equal(25, partial.FilledQuantity);
        Assert.Equal(25, partial.LastFillQuantity);

        book.Cancel("c-target50");
        var canceled = events.Last(e => e.State == OrderState.Cancelled);
        Assert.Equal(25, canceled.FilledQuantity);

        return new L1ExecutionLifecycleReport(
            TargetPosition: target,
            FinalPosition: canceled.FilledQuantity,
            FilledQuantity: canceled.FilledQuantity,
            CanceledRemaining: target - canceled.FilledQuantity,
            DataModeToken: DataModeToken,
            HonestyNote:
                "First-party L1TouchFillModel with max-per-touch partials. " +
                "Queue position and liquidity consumption are off — not Nautilus matching. " +
                "CSP/VibeQuant Compare panels are research/native lanes, not this execution path.",
            Events: events
                .Select(e => new L1ExecutionLifecycleEvent(
                    e.TimestampUtc,
                    e.ClientOrderId,
                    e.State,
                    e.FilledQuantity,
                    e.LastFillQuantity,
                    e.LastFillPrice))
                .ToArray());
    }
}

public sealed record L1ExecutionLifecycleReport(
    long TargetPosition,
    long FinalPosition,
    long FilledQuantity,
    long CanceledRemaining,
    string DataModeToken,
    string HonestyNote,
    IReadOnlyList<L1ExecutionLifecycleEvent> Events);

public sealed record L1ExecutionLifecycleEvent(
    DateTime TimestampUtc,
    string ClientOrderId,
    OrderState State,
    long FilledQuantity,
    long LastFillQuantity,
    double? LastFillPrice);
