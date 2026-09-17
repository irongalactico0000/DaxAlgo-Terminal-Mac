using System.Text.Json;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Deterministic first-party L1 execution lifecycle proof used by Validate.
/// Target +50 → market 50 → one touch fills 25 (max-per-touch) → cancel → position +25.
/// Not Nautilus queue/liquidity matching; CSP/VibeQuant are unrelated lanes.
/// </summary>
public static class L1ExecutionLifecycleFixtureV1
{
    public const string ReportId = "target50-fill25-cancel";
    public const string DataModeToken =
        "L1TouchFillModel|book=L1|queue=off|liq=off|partials=max25|latencyMs=0|fixture=target50-fill25-cancel";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static L1ExecutionLifecycleReportV1 RunTarget50PartialFillCancel()
    {
        var clock = new SimulatedClock();
        var t0 = new DateTime(2026, 3, 1, 14, 30, 0, DateTimeKind.Utc);
        clock.SetTo(t0);

        var fillModel = new L1FillModel(tickSize: 0.01, slippageTicks: 0, maxFillQuantityPerTouch: 25);
        var book = new SimulatedOrderBook(clock, fillModel);
        var contract = Contract.UsStock("MSFT");
        var events = new List<OrderEvent>();
        using var _ = book.Events.Subscribe(events.Add);

        const long target = 50;
        book.Submit(
            new OrderRequest("c-target50", contract, OrderSide.Buy, OrderType.Market, Quantity: target));

        var tick = new Tick(t0.AddMilliseconds(5), Bid: 410.0, Ask: 410.05, BidSize: 200, AskSize: 200);
        book.OnTick(contract, tick);

        var partial = events.Last(e => e.State == OrderState.PartiallyFilled);
        if (partial.FilledQuantity != 25 || partial.LastFillQuantity != 25)
        {
            throw new InvalidOperationException(
                $"Expected first touch fill of 25, got filled={partial.FilledQuantity} last={partial.LastFillQuantity}.");
        }

        book.Cancel("c-target50");
        var canceled = events.Last(e => e.State == OrderState.Cancelled);
        if (canceled.FilledQuantity != 25)
        {
            throw new InvalidOperationException(
                $"Expected canceled order with filled=25, got {canceled.FilledQuantity}.");
        }

        return new L1ExecutionLifecycleReportV1(
            TargetPosition: target,
            FinalPosition: canceled.FilledQuantity,
            FilledQuantity: canceled.FilledQuantity,
            CanceledRemaining: target - canceled.FilledQuantity,
            DataModeToken: DataModeToken,
            HonestyNote:
                "First-party L1FillModel / SimulatedOrderBook with max-per-touch partials. " +
                "Queue position and liquidity consumption are off — not Nautilus matching. " +
                "CSP/VibeQuant Compare panels are research/native lanes, not this execution path.",
            Events: events
                .Select(e => new L1ExecutionLifecycleEventV1(
                    e.TimestampUtc,
                    e.ClientOrderId,
                    e.State.ToString(),
                    e.FilledQuantity,
                    e.LastFillQuantity,
                    e.LastFillPrice))
                .ToArray());
    }

    public static string Serialize(L1ExecutionLifecycleReportV1 report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string Summary(L1ExecutionLifecycleReportV1 report) =>
        $"Target +{report.TargetPosition} → fill {report.FilledQuantity} → cancel → position +{report.FinalPosition} · L1FillModel (not Nautilus)";
}

public sealed record L1ExecutionLifecycleReportV1(
    long TargetPosition,
    long FinalPosition,
    long FilledQuantity,
    long CanceledRemaining,
    string DataModeToken,
    string HonestyNote,
    IReadOnlyList<L1ExecutionLifecycleEventV1> Events);

public sealed record L1ExecutionLifecycleEventV1(
    DateTime TimestampUtc,
    string ClientOrderId,
    string State,
    long FilledQuantity,
    long LastFillQuantity,
    double? LastFillPrice);
