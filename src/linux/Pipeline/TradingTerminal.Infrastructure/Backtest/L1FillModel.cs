using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Strategy for deciding whether a pending order fills against the current L1 quote and
/// at what price. Optional max-per-touch enables quantity-limited partials; optional
/// opposite-L1-size cap uses BidSize/AskSize as a queue/liquidity proxy — not full book walk.
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
/// many units (partial lifecycle); 0 means no fixed per-touch ceiling.
/// When <paramref name="capToOppositeL1Size"/> is true, each fill is also capped by the
/// opposite touch size (AskSize for buys, BidSize for sells). Zero opposite size → no fill.
/// That is an L1 size proxy — not Nautilus queue position or multi-level liquidity walk.
/// </summary>
public sealed class L1FillModel : IFillModel
{
    private readonly double _tickSize;
    private readonly int _slippageTicks;
    private readonly long _maxFillQuantityPerTouch;
    private readonly bool _capToOppositeL1Size;

    public L1FillModel(
        double tickSize,
        int slippageTicks,
        long maxFillQuantityPerTouch = 0,
        bool capToOppositeL1Size = false)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        if (maxFillQuantityPerTouch < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFillQuantityPerTouch));
        _tickSize = tickSize;
        _slippageTicks = slippageTicks;
        _maxFillQuantityPerTouch = maxFillQuantityPerTouch;
        _capToOppositeL1Size = capToOppositeL1Size;
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
                fillQty = CapFill(remaining, tick, isBuy);
                return fillQty > 0;

            case OrderType.Limit:
            {
                var lp = o.Request.LimitPrice!.Value;
                if (isBuy && tick.Ask <= lp)
                {
                    fillPrice = Math.Min(tick.Ask, lp);
                    fillQty = CapFill(remaining, tick, isBuy);
                    return fillQty > 0;
                }
                if (!isBuy && tick.Bid >= lp)
                {
                    fillPrice = Math.Max(tick.Bid, lp);
                    fillQty = CapFill(remaining, tick, isBuy);
                    return fillQty > 0;
                }
                return false;
            }

            case OrderType.Stop:
            {
                var sp = o.Request.StopPrice!.Value;
                if (isBuy && tick.Ask >= sp)
                {
                    fillPrice = tick.Ask + slip;
                    fillQty = CapFill(remaining, tick, isBuy);
                    return fillQty > 0;
                }
                if (!isBuy && tick.Bid <= sp)
                {
                    fillPrice = tick.Bid - slip;
                    fillQty = CapFill(remaining, tick, isBuy);
                    return fillQty > 0;
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

    private long CapFill(long remaining, Tick tick, bool isBuy)
    {
        var qty = remaining;
        if (_maxFillQuantityPerTouch > 0)
            qty = Math.Min(qty, _maxFillQuantityPerTouch);
        if (_capToOppositeL1Size)
        {
            var opposite = isBuy ? tick.AskSize : tick.BidSize;
            if (opposite <= 0)
                return 0;
            qty = Math.Min(qty, opposite);
        }

        return qty;
    }
}
