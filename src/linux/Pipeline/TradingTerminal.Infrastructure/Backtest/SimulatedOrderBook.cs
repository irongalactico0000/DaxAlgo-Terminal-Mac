using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Holds working orders and evaluates fills on every tick using an injected
/// <see cref="IFillModel"/>. Fully synchronous — driven by the engine's replay loop on a
/// single thread. Exposes <see cref="Events"/> for the order router (and indirectly the
/// strategy) to subscribe to.
/// </summary>
public sealed class SimulatedOrderBook
{
    private readonly IClock _clock;
    private readonly IFillModel _fillModel;
    private readonly TimeSpan _latency;
    private readonly Subject<OrderEvent> _events = new();
    private readonly Dictionary<string, PendingOrder> _byClientId = new(StringComparer.Ordinal);
    private long _nextBrokerId;

    public SimulatedOrderBook(IClock clock, IFillModel fillModel, TimeSpan latency = default)
    {
        _clock = clock;
        _fillModel = fillModel;
        _latency = latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
    }

    public IObservable<OrderEvent> Events => _events.AsObservable();

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

        // Snapshot to allow removals while iterating.
        var orders = _byClientId.Values.ToList();
        foreach (var order in orders)
        {
            if (IsTerminal(order.State)) continue;
            if (order.Request.Contract != contract) continue;
            if (tick.TimestampUtc < order.EarliestFillUtc) continue;
            if (!_fillModel.TryFill(order, tick, out var price, out var qty)) continue;

            order.FilledQuantity += qty;
            order.TotalFillValue += price * qty;

            var newState = order.FilledQuantity >= order.Request.Quantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            order.State = newState;

            var liquidity = order.Request.Type == OrderType.Limit
                ? LiquidityFlag.Maker
                : LiquidityFlag.Taker;

            _events.OnNext(new OrderEvent(
                tick.TimestampUtc, order.Request.ClientOrderId, order.BrokerOrderId,
                order.Request.Side, newState,
                order.FilledQuantity, order.AveragePrice,
                LastFillQuantity: qty, LastFillPrice: price,
                Liquidity: liquidity));

            if (newState == OrderState.Filled)
                _byClientId.Remove(order.Request.ClientOrderId);
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

    private static bool IsTerminal(OrderState s) =>
        s is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;
}
