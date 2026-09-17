using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Strategy for deciding whether a pending order fills against the current L1 quote and
/// at what price. Optional max-per-touch enables quantity-limited partials; queue position
/// and liquidity walk remain out of scope.
/// </summary>
public interface IFillModel
{
    bool TryFill(PendingOrder order, Tick tick, out double fillPrice, out long fillQty);
}

/// <summary>
/// Level-1 fill model. Market orders cross the spread plus <c>slippageTicks * tickSize</c>;
/// limits fill when the opposite touch crosses the limit; stops trigger when the relevant
/// touch crosses the stop, then fill at touch + slippage like a market order.
///
/// Conservative: we use the side of the book that pays the spread (buy-at-ask, sell-at-bid).
/// When <paramref name="maxFillQuantityPerTouch"/> is &gt; 0, each touch fills at most that
/// many units (partial lifecycle); 0 means fill full remaining quantity.
/// </summary>
public sealed class L1FillModel : IFillModel
{
    private readonly double _tickSize;
    private readonly int _slippageTicks;
    private readonly long _maxFillQuantityPerTouch;

    public L1FillModel(double tickSize, int slippageTicks, long maxFillQuantityPerTouch = 0)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        if (maxFillQuantityPerTouch < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFillQuantityPerTouch));
        _tickSize = tickSize;
        _slippageTicks = slippageTicks;
        _maxFillQuantityPerTouch = maxFillQuantityPerTouch;
    }

    public bool TryFill(PendingOrder o, Tick tick, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var slip = _slippageTicks * _tickSize;
        var isBuy = o.Request.Side == OrderSide.Buy;

        switch (o.Request.Type)
        {
            case OrderType.Market:
                fillPrice = isBuy ? tick.Ask + slip : tick.Bid - slip;
                fillQty = CapFill(remaining);
                return true;

            case OrderType.Limit:
            {
                var lp = o.Request.LimitPrice!.Value;
                if (isBuy && tick.Ask <= lp)
                {
                    fillPrice = Math.Min(tick.Ask, lp);
                    fillQty = CapFill(remaining);
                    return true;
                }
                if (!isBuy && tick.Bid >= lp)
                {
                    fillPrice = Math.Max(tick.Bid, lp);
                    fillQty = CapFill(remaining);
                    return true;
                }
                return false;
            }

            case OrderType.Stop:
            {
                var sp = o.Request.StopPrice!.Value;
                if (isBuy && tick.Ask >= sp)
                {
                    fillPrice = tick.Ask + slip;
                    fillQty = CapFill(remaining);
                    return true;
                }
                if (!isBuy && tick.Bid <= sp)
                {
                    fillPrice = tick.Bid - slip;
                    fillQty = CapFill(remaining);
                    return true;
                }
                return false;
            }

            case OrderType.StopLimit:
                // Out of scope for the first cut — treat as a limit immediately.
                goto case OrderType.Limit;

            default:
                return false;
        }
    }

    private long CapFill(long remaining) =>
        _maxFillQuantityPerTouch > 0
            ? Math.Min(remaining, _maxFillQuantityPerTouch)
            : remaining;
}
