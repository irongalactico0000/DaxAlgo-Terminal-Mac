using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// Decides whether a working order fills against the current quote and at what price.
/// <paramref name="tickSize"/> is passed per call because it varies per instrument in a portfolio
/// run. Optional <see cref="L1TouchFillModel"/> max-per-touch enables quantity-limited partials.
/// </summary>
internal interface IFillModel
{
    bool TryFill(WorkingOrder order, Tick quote, double tickSize, out double fillPrice, out long fillQty);
}

/// <summary>
/// Level-1 fill model. Market orders cross the spread plus <c>slippageTicks * tickSize</c>; limits
/// fill when the opposite touch crosses the limit; stops trigger when the relevant touch crosses the
/// stop, then fill like a market order. Conservative: buys pay the ask, sells hit the bid.
/// When <paramref name="maxFillQuantityPerTouch"/> is &gt; 0, each touch fills at most that many
/// units (partial lifecycle); 0 means fill full remaining quantity.
/// </summary>
internal sealed class L1TouchFillModel : IFillModel
{
    private readonly int _slippageTicks;
    private readonly long _maxFillQuantityPerTouch;

    public L1TouchFillModel(int slippageTicks, long maxFillQuantityPerTouch = 0)
    {
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        if (maxFillQuantityPerTouch < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFillQuantityPerTouch));
        _slippageTicks = slippageTicks;
        _maxFillQuantityPerTouch = maxFillQuantityPerTouch;
    }

    public bool TryFill(WorkingOrder o, Tick tick, double tickSize, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var slip = _slippageTicks * tickSize;
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
                if (isBuy && tick.Ask <= lp) { fillPrice = Math.Min(tick.Ask, lp); fillQty = CapFill(remaining); return true; }
                if (!isBuy && tick.Bid >= lp) { fillPrice = Math.Max(tick.Bid, lp); fillQty = CapFill(remaining); return true; }
                return false;
            }

            case OrderType.Stop:
            {
                var sp = o.Request.StopPrice!.Value;
                if (isBuy && tick.Ask >= sp) { fillPrice = tick.Ask + slip; fillQty = CapFill(remaining); return true; }
                if (!isBuy && tick.Bid <= sp) { fillPrice = tick.Bid - slip; fillQty = CapFill(remaining); return true; }
                return false;
            }

            case OrderType.StopLimit:
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
