using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Holds working orders and evaluates fills on every tick using an injected
/// <see cref="IFillModel"/>. Optional FIFO-ahead updates and L2 depth snapshots
/// support Validate queue / liquidity-walk v1. Fully synchronous.
/// </summary>
public sealed class SimulatedOrderBook
{
    private readonly IClock _clock;
    private readonly IFillModel _fillModel;
    private readonly TimeSpan _latency;
    private readonly bool _enableFifoQueueAhead;
    private readonly Subject<OrderEvent> _events = new();
    private readonly Dictionary<string, PendingOrder> _byClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<Contract, DepthSnapshot> _lastDepth = new();
    private long _nextBrokerId;

    public SimulatedOrderBook(
        IClock clock,
        IFillModel fillModel,
        TimeSpan latency = default,
        bool enableFifoQueueAhead = false)
    {
        _clock = clock;
        _fillModel = fillModel;
        _latency = latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
        _enableFifoQueueAhead = enableFifoQueueAhead ||
            (fillModel is L1FillModel l1 && l1.EnableFifoQueueAhead);
    }

    public IObservable<OrderEvent> Events => _events.AsObservable();

    public DepthSnapshot? LastDepth(Contract contract) =>
        _lastDepth.TryGetValue(contract, out var d) ? d : null;

    /// <summary>Store latest L2 snapshot for book-walk fills (per-snapshot; not depleting across time).</summary>
    public void OnDepth(Contract contract, DepthSnapshot depth)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(depth);
        _lastDepth[contract] = depth;
    }

    public OrderResult Submit(OrderRequest request)
    {
        if (_byClientId.ContainsKey(request.ClientOrderId))
        {
            var existing = _byClientId[request.ClientOrderId];
            return new OrderResult(request.ClientOrderId, existing.BrokerOrderId, existing.State);
        }

        var brokerId = $"BT-{Interlocked.Increment(ref _nextBrokerId)}";
        var pending = new PendingOrder
        {
            Request = request,
            BrokerOrderId = brokerId,
            EarliestFillUtc = _clock.UtcNow.Add(_latency),
        };
        _byClientId.Add(request.ClientOrderId, pending);

        _events.OnNext(new OrderEvent(
            _clock.UtcNow, request.ClientOrderId, brokerId, request.Side, OrderState.Working,
            FilledQuantity: 0, AverageFillPrice: null));

        return new OrderResult(request.ClientOrderId, brokerId, OrderState.Working);
    }

    public void Cancel(string clientOrderId)
    {
        if (!_byClientId.TryGetValue(clientOrderId, out var order)) return;
        if (IsTerminal(order.State)) return;

        order.State = OrderState.Cancelled;
        _byClientId.Remove(clientOrderId);

        _events.OnNext(new OrderEvent(
            _clock.UtcNow, clientOrderId, order.BrokerOrderId, order.Request.Side, OrderState.Cancelled,
            order.FilledQuantity, order.AveragePrice));
    }

    /// <summary>
    /// Evaluates only orders for <paramref name="contract"/>. Multi-asset replay must never let one
    /// leg's quote fill another leg's working order.
    /// </summary>
    public void OnTick(Contract contract, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (_byClientId.Count == 0) return;

        var orders = _byClientId.Values.ToList();
        foreach (var order in orders)
        {
            if (IsTerminal(order.State)) continue;
            if (order.Request.Contract != contract) continue;
            if (tick.TimestampUtc < order.EarliestFillUtc) continue;

            UpdateFifoQueue(order, tick);

            if (!_fillModel.TryFill(order, tick, out var price, out var qty))
            {
                MaybeCancelIocUnfilled(order, tick.TimestampUtc);
                continue;
            }

            order.FilledQuantity += qty;
            order.TotalFillValue += price * qty;

            var newState = order.FilledQuantity >= order.Request.Quantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            order.State = newState;

            // Book-walk fills take liquidity. Resting L1 limits stay maker.
            var liquidity = _fillModel is L2BookWalkFillModel
                ? LiquidityFlag.Taker
                : order.Request.Type == OrderType.Limit
                    ? LiquidityFlag.Maker
                    : LiquidityFlag.Taker;
            ConsumeWalkedLiquidity(contract, order, qty);

            _events.OnNext(new OrderEvent(
                tick.TimestampUtc, order.Request.ClientOrderId, order.BrokerOrderId,
                order.Request.Side, newState,
                order.FilledQuantity, order.AveragePrice,
                LastFillQuantity: qty, LastFillPrice: price,
                Liquidity: liquidity));

            if (newState == OrderState.Filled)
            {
                _byClientId.Remove(order.Request.ClientOrderId);
                continue;
            }

            MaybeCancelIocUnfilled(order, tick.TimestampUtc);
        }
    }

    /// <summary>Legacy single-contract entry point retained for direct callers.</summary>
    public void OnTick(Tick tick)
    {
        if (_byClientId.Count == 0) return;
        var contracts = _byClientId.Values.Select(order => order.Request.Contract).Distinct().ToArray();
        if (contracts.Length > 1)
            throw new InvalidOperationException("An unscoped tick cannot evaluate a multi-contract order book.");
        if (contracts.Length == 1)
            OnTick(contracts[0], tick);
    }

    private void ConsumeWalkedLiquidity(Contract contract, PendingOrder order, long filledQty)
    {
        if (_fillModel is not L2BookWalkFillModel)
            return;
        if (!_lastDepth.TryGetValue(contract, out var depth))
            return;

        var isBuy = order.Request.Side == OrderSide.Buy;
        double? limit = order.Request.Type is OrderType.Limit or OrderType.StopLimit
            ? order.Request.LimitPrice
            : null;
        _lastDepth[contract] = isBuy
            ? depth with
            {
                Asks = L2BookWalkFillV1.Consume(filledQty, depth.Asks, limit, isBuy: true),
            }
            : depth with
            {
                Bids = L2BookWalkFillV1.Consume(filledQty, depth.Bids, limit, isBuy: false),
            };
    }

    private void UpdateFifoQueue(PendingOrder order, Tick tick)
    {
        if (!_enableFifoQueueAhead)
            return;
        if (order.Request.Type != OrderType.Limit)
            return;

        var isBuy = order.Request.Side == OrderSide.Buy;
        var opposite = isBuy ? tick.AskSize : tick.BidSize;
        if (!order.QueueJoined)
        {
            order.QueueAhead = FifoQueueAheadEstimatorV1.Join(opposite);
            order.LastOppositeSize = opposite;
            order.QueueJoined = true;
            return;
        }

        order.QueueAhead = FifoQueueAheadEstimatorV1.Consume(
            order.QueueAhead,
            order.LastOppositeSize,
            opposite);
        order.LastOppositeSize = opposite;
    }

    private void MaybeCancelIocUnfilled(PendingOrder order, DateTime timestampUtc)
    {
        if (order.Request.TimeInForce != TimeInForce.Ioc)
            return;
        if (IsTerminal(order.State))
            return;
        if (order.FilledQuantity >= order.Request.Quantity)
            return;

        // IOC: after first evaluable tick attempt, cancel remainder (filled or not).
        order.State = OrderState.Cancelled;
        _byClientId.Remove(order.Request.ClientOrderId);
        _events.OnNext(new OrderEvent(
            timestampUtc, order.Request.ClientOrderId, order.BrokerOrderId, order.Request.Side,
            OrderState.Cancelled, order.FilledQuantity, order.AveragePrice));
    }

    private static bool IsTerminal(OrderState s) =>
        s is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;
}
