using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// One simulated fill for Validate chart overlay — distinct from condition-hit markers.
/// Sourced from QuickBacktest <see cref="Trade"/>s at accept time, not from evidence aggregates.
/// </summary>
public sealed record ValidationChartFillV1(
    DateTimeOffset TimeUtc,
    double Price,
    bool IsEntry,
    bool IsBuy);

public static class ValidationChartFillMapperV1
{
    public static IReadOnlyList<ValidationChartFillV1> FromTrades(IEnumerable<Trade>? trades)
    {
        if (trades is null)
            return Array.Empty<ValidationChartFillV1>();

        var list = new List<ValidationChartFillV1>();
        foreach (var trade in trades)
        {
            var buyEntry = trade.Side == OrderSide.Buy;
            list.Add(new ValidationChartFillV1(
                new DateTimeOffset(DateTime.SpecifyKind(trade.EntryUtc, DateTimeKind.Utc)),
                trade.EntryPrice,
                IsEntry: true,
                IsBuy: buyEntry));
            list.Add(new ValidationChartFillV1(
                new DateTimeOffset(DateTime.SpecifyKind(trade.ExitUtc, DateTimeKind.Utc)),
                trade.ExitPrice,
                IsEntry: false,
                IsBuy: !buyEntry));
        }

        return list;
    }
}
