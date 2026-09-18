using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Strategy for deciding whether a pending order fills against the current L1 quote and
/// at what price. Optional max-per-touch enables quantity-limited partials; optional
/// opposite-L1-size cap uses BidSize/AskSize as a liquidity proxy; optional FIFO-ahead
/// estimate delays passive fills until opposite-size decreases clear the join queue.
/// Not Nautilus matching / MBO.
/// </summary>
public interface IFillModel
{
    bool TryFill(PendingOrder order, Tick tick, out double fillPrice, out long fillQty);
}

/// <inheritdoc cref="IFillModel"/>
public sealed class L1FillModel : IFillModel
{
    private readonly double _tickSize;
    private readonly int _slippageTicks;
    private readonly long _maxFillQuantityPerTouch;
    private readonly bool _capToOppositeL1Size;
    private readonly bool _enableFifoQueueAhead;

    public L1FillModel(
        double tickSize,
        int slippageTicks,
        long maxFillQuantityPerTouch = 0,
        bool capToOppositeL1Size = false,
        bool enableFifoQueueAhead = false)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        if (maxFillQuantityPerTouch < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFillQuantityPerTouch));
        _tickSize = tickSize;
        _slippageTicks = slippageTicks;
        _maxFillQuantityPerTouch = maxFillQuantityPerTouch;
        _capToOppositeL1Size = capToOppositeL1Size;
        _enableFifoQueueAhead = enableFifoQueueAhead;
    }

    public bool EnableFifoQueueAhead => _enableFifoQueueAhead;

    public bool TryFill(PendingOrder o, Tick tick, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var slip = _slippageTicks * _tickSize;
        var isBuy = o.Request.Side == OrderSide.Buy;

        // Passive join estimate: at-touch limits wait for FIFO-ahead to clear.
        // Crossing limits (price through the touch) take immediately as taker.
        if (_enableFifoQueueAhead && o.Request.Type == OrderType.Limit)
        {
            var lp = o.Request.LimitPrice!.Value;
            var crossing = isBuy ? tick.Ask < lp : tick.Bid > lp;
            if (!crossing && !FifoQueueAheadEstimatorV1.IsCleared(o.QueueAhead))
                return false;
        }

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

/// <summary>
/// Multi-level snapshot book walk for Validate liquidity-walk v1.
/// Uses last <see cref="DepthSnapshot"/> when present; otherwise synthesizes one L1 level
/// from the tick (honest <c>l1-proxy</c> token path).
/// </summary>
public sealed class L2BookWalkFillModel : IFillModel
{
    private readonly Func<Contract, DepthSnapshot?> _depthFor;
    private readonly long _maxFillQuantityPerTouch;

    public L2BookWalkFillModel(
        Func<Contract, DepthSnapshot?> depthFor,
        long maxFillQuantityPerTouch = 0)
    {
        _depthFor = depthFor ?? throw new ArgumentNullException(nameof(depthFor));
        if (maxFillQuantityPerTouch < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFillQuantityPerTouch));
        _maxFillQuantityPerTouch = maxFillQuantityPerTouch;
    }

    public bool TryFill(PendingOrder o, Tick tick, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;
        if (_maxFillQuantityPerTouch > 0)
            remaining = Math.Min(remaining, _maxFillQuantityPerTouch);

        var isBuy = o.Request.Side == OrderSide.Buy;
        double? limit = o.Request.Type is OrderType.Limit or OrderType.StopLimit
            ? o.Request.LimitPrice
            : null;

        // Marketable check for limits: must cross or touch.
        if (limit is { } lp)
        {
            if (isBuy && tick.Ask > lp) return false;
            if (!isBuy && tick.Bid < lp) return false;
        }
        else if (o.Request.Type is OrderType.Stop)
        {
            var sp = o.Request.StopPrice!.Value;
            if (isBuy && tick.Ask < sp) return false;
            if (!isBuy && tick.Bid > sp) return false;
        }

        var depth = _depthFor(o.Request.Contract);
        IReadOnlyList<DepthLevel> levels;
        if (depth is not null)
        {
            levels = isBuy ? depth.Asks : depth.Bids;
        }
        else
        {
            // L1 proxy: single opposite touch as one book level.
            levels = isBuy
                ? [new DepthLevel(tick.Ask, Math.Max(1, tick.AskSize))]
                : [new DepthLevel(tick.Bid, Math.Max(1, tick.BidSize))];
        }

        if (levels.Count == 0)
            return false;

        var walk = L2BookWalkFillV1.Walk(isBuy, remaining, levels, limit);
        if (walk.FilledQuantity <= 0)
            return false;

        fillQty = walk.FilledQuantity;
        fillPrice = walk.AverageFillPrice;
        return true;
    }
}
