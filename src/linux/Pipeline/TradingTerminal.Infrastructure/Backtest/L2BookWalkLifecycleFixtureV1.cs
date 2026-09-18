using System.Text.Json;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Canonical IOC book-walk fixture (Validate workstream): buy 100 @ 100.01 → fill 80 / cancel 20 / avg 100.00625.
/// Snapshot walk only — not Nautilus matching.
/// </summary>
public static class L2BookWalkLifecycleFixtureV1
{
    public const string ReportId = "ioc-bookwalk-100-at-100.01";
    public const string DataModeToken =
        "L2BookWalkFillV1|book=L2-snapshot|queue=off|liq=bookwalk-v1|fixture=ioc-100@100.01";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static L2BookWalkLifecycleReportV1 RunCanonicalIocBuy()
    {
        var walk = L2BookWalkFillV1.CanonicalIocBuyLimit100At100_01();
        if (walk.FilledQuantity != 80 ||
            walk.UnfilledQuantity != 20 ||
            Math.Abs(walk.AverageFillPrice - 100.00625) > 1e-9)
        {
            throw new InvalidOperationException(
                $"IOC fixture mismatch: filled={walk.FilledQuantity} unfilled={walk.UnfilledQuantity} avg={walk.AverageFillPrice}");
        }

        // Also prove through SimulatedOrderBook + L2BookWalkFillModel + IOC TIF.
        var clock = new SimulatedClock();
        var t0 = new DateTime(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);
        clock.SetTo(t0);
        var contract = Contract.UsStock("MSFT");
        var bookHolder = new SimulatedOrderBook?[1];
        var fillModel = new L2BookWalkFillModel(c => bookHolder[0]?.LastDepth(c));
        var book = new SimulatedOrderBook(clock, fillModel);
        bookHolder[0] = book;
        var events = new List<OrderEvent>();
        using var _ = book.Events.Subscribe(events.Add);

        book.OnDepth(
            contract,
            new DepthSnapshot(
                t0,
                Bids: [new DepthLevel(99.99, 100)],
                Asks:
                [
                    new DepthLevel(100.00, 30),
                    new DepthLevel(100.01, 50),
                    new DepthLevel(100.02, 1000),
                ]));

        book.Submit(new OrderRequest(
            "c-ioc-100",
            contract,
            OrderSide.Buy,
            OrderType.Limit,
            Quantity: 100,
            LimitPrice: 100.01,
            TimeInForce: TimeInForce.Ioc));

        var tick = new Tick(t0.AddMilliseconds(1), Bid: 99.99, Ask: 100.00, BidSize: 100, AskSize: 30);
        book.OnTick(contract, tick);

        var filled = events.Where(e => e.LastFillQuantity > 0).Sum(e => e.LastFillQuantity);
        var canceled = events.Last(e => e.State == OrderState.Cancelled);
        if (filled != 80 || canceled.FilledQuantity != 80)
        {
            throw new InvalidOperationException(
                $"Book IOC path mismatch: filledSum={filled} canceledFilled={canceled.FilledQuantity}");
        }

        const double startingCash = 100_000d;
        const double bps = 1d;
        var ledger = new TradeLedger(multiplier: 1d, startingCash, new BpsFeeModel(bps));
        foreach (var evt in events)
        {
            if (evt.LastFillQuantity <= 0 || evt.LastFillPrice is not { } px)
                continue;
            ledger.OnFill(contract, evt.TimestampUtc, evt.Side, evt.LastFillQuantity, px, evt.Liquidity);
        }

        var notional = 80d * walk.AverageFillPrice;
        var expectedFee = notional * (bps * 1e-4);
        var expectedCash = startingCash - notional - expectedFee;
        var equityAtFill = ledger.Equity(walk.AverageFillPrice);
        var reconciled =
            ledger.NetPosition == 80 &&
            Math.Abs(ledger.TotalFees - expectedFee) < 1e-6 &&
            Math.Abs(ledger.Cash - expectedCash) < 1e-6 &&
            Math.Abs(equityAtFill - (startingCash - expectedFee)) < 1e-6 &&
            ledger.Trades.Count == 0;
        if (!reconciled)
        {
            throw new InvalidOperationException(
                $"Accounting mismatch: pos={ledger.NetPosition} cash={ledger.Cash} fees={ledger.TotalFees} equity={equityAtFill}");
        }

        // Consumed asks must not refill a second IOC at the same limit.
        book.Submit(new OrderRequest(
            "c-ioc-second",
            contract,
            OrderSide.Buy,
            OrderType.Limit,
            Quantity: 100,
            LimitPrice: 100.01,
            TimeInForce: TimeInForce.Ioc));
        book.OnTick(contract, tick);
        var secondFills = events.Where(e => e.ClientOrderId == "c-ioc-second" && e.LastFillQuantity > 0).Sum(e => e.LastFillQuantity);
        if (secondFills != 0)
        {
            throw new InvalidOperationException(
                $"Second IOC reused consumed liquidity: filled {secondFills}.");
        }

        return new L2BookWalkLifecycleReportV1(
            SubmittedQuantity: 100,
            FilledQuantity: 80,
            CanceledRemaining: 20,
            AverageFillPrice: walk.AverageFillPrice,
            DataModeToken: DataModeToken,
            HonestyNote:
                "L2BookWalkFillV1 snapshot walk + IOC remainder cancel + TradeLedger cash/fees/position. " +
                "Walked size is removed from this snapshot. Not Nautilus matching or MBO.",
            Events: events
                .Select(e => new L2BookWalkLifecycleEventV1(
                    e.TimestampUtc,
                    e.ClientOrderId,
                    e.State.ToString(),
                    e.FilledQuantity,
                    e.LastFillQuantity,
                    e.LastFillPrice))
                .ToArray(),
            StartingCash: startingCash,
            EndingCash: ledger.Cash,
            Position: ledger.NetPosition,
            TotalFees: ledger.TotalFees,
            EquityAtAverageFill: equityAtFill,
            AccountingReconciled: true);
    }

    public static string Serialize(L2BookWalkLifecycleReportV1 report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string Summary(L2BookWalkLifecycleReportV1 report) =>
        $"IOC book-walk · filled {report.FilledQuantity} / cancel {report.CanceledRemaining} · avg {report.AverageFillPrice:F5} · pos {report.Position} · fees {report.TotalFees:F4}";
}

public sealed record L2BookWalkLifecycleReportV1(
    long SubmittedQuantity,
    long FilledQuantity,
    long CanceledRemaining,
    double AverageFillPrice,
    string DataModeToken,
    string HonestyNote,
    IReadOnlyList<L2BookWalkLifecycleEventV1> Events,
    double StartingCash = 0,
    double EndingCash = 0,
    long Position = 0,
    double TotalFees = 0,
    double EquityAtAverageFill = 0,
    bool AccountingReconciled = false);

public sealed record L2BookWalkLifecycleEventV1(
    DateTime TimestampUtc,
    string ClientOrderId,
    string State,
    long FilledQuantity,
    long LastFillQuantity,
    double? LastFillPrice);
