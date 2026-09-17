using System.Collections.ObjectModel;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// Holds working orders across every instrument and evaluates fills on each quote via an injected
/// <see cref="IFillModel"/>. Fully synchronous — driven by the engine's single-threaded replay loop.
/// A single required transition sink owns accounting/feedback effects. The legacy <see cref="Event"/>
/// surface is diagnostics-only: observer failures are captured and cannot change book behavior.
/// </summary>
internal sealed class SimulatedOrderBook
{
    private readonly SimClock _clock;
    private readonly IFillModel _fillModel;
    private readonly Func<InstrumentId, double> _tickSizeOf;
    private readonly TimeSpan _latency;
    private readonly Dictionary<string, WorkingOrder> _byClientId = new(StringComparer.Ordinal);
    private readonly List<SimulatedOrderBookDiagnosticFailure> _diagnosticFailures = [];
    private readonly ReadOnlyCollection<SimulatedOrderBookDiagnosticFailure> _readOnlyDiagnosticFailures;
    private Action<InstrumentId, OrderEvent>? _requiredTransitionSink;
    private long _nextBrokerId;
    private long _nextDiagnosticFailureSequence;

    public SimulatedOrderBook(
        SimClock clock,
        IFillModel fillModel,
        Func<InstrumentId, double> tickSizeOf,
        TimeSpan latency = default)
    {
        _clock = clock;
        _fillModel = fillModel;
        _tickSizeOf = tickSizeOf;
        _latency = latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
        _readOnlyDiagnosticFailures = _diagnosticFailures.AsReadOnly();
    }

    /// <summary>
    /// Best-effort legacy diagnostics raised after the required sink. Each observer is isolated;
    /// exceptions are captured in <see cref="DiagnosticFailures"/> and never escape.
    /// </summary>
    public event Action<InstrumentId, OrderEvent>? Event;

    public IReadOnlyList<SimulatedOrderBookDiagnosticFailure> DiagnosticFailures =>
        _readOnlyDiagnosticFailures;

    /// <summary>
    /// Binds the one effect-owning transition sink. Its exceptions propagate and abort the run;
    /// callers must never use the diagnostic event for accounting, reservation, or feedback state.
    /// </summary>
    public IDisposable BindRequiredTransitionSink(Action<InstrumentId, OrderEvent> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_requiredTransitionSink is not null)
            throw new InvalidOperationException("A required simulated-order transition sink is already bound.");
        _requiredTransitionSink = sink;
        return new RequiredTransitionSinkBinding(this, sink);
    }

    public OrderResult Submit(OrderRequest request, InstrumentId instrument)
    {
        if (_byClientId.TryGetValue(request.ClientOrderId, out var existing))
            return new OrderResult(request.ClientOrderId, existing.BrokerOrderId, existing.State);

        var requiredSink = RequireTransitionSink();
        var brokerId = $"BT-{Interlocked.Increment(ref _nextBrokerId)}";
        var order = new WorkingOrder
        {
            Request = request,
            Instrument = instrument,
            BrokerOrderId = brokerId,
            EarliestFillUtc = _clock.UtcNow.Add(_latency),
        };
        _byClientId.Add(request.ClientOrderId, order);

        PublishTransition(requiredSink, instrument, new OrderEvent(
            _clock.UtcNow, request.ClientOrderId, brokerId, request.Side, OrderState.Working,
            FilledQuantity: 0, AverageFillPrice: null));

        return new OrderResult(request.ClientOrderId, brokerId, OrderState.Working);
    }

    public void Cancel(string clientOrderId)
    {
        if (!_byClientId.TryGetValue(clientOrderId, out var order)) return;
        if (IsTerminal(order.State)) return;

        var requiredSink = RequireTransitionSink();
        order.State = OrderState.Cancelled;
        _byClientId.Remove(clientOrderId);

        PublishTransition(requiredSink, order.Instrument, new OrderEvent(
            _clock.UtcNow, clientOrderId, order.BrokerOrderId, order.Request.Side, OrderState.Cancelled,
            order.FilledQuantity, order.AveragePrice));
    }

    /// <summary>Evaluate fills for the orders resting on one instrument against its latest quote.</summary>
    public void OnQuote(InstrumentId instrument, Tick tick)
    {
        if (_byClientId.Count == 0) return;
        var tickSize = _tickSizeOf(instrument);

        foreach (var order in _byClientId.Values.ToList())
        {
            if (order.Instrument != instrument || IsTerminal(order.State)) continue;
            if (tick.TimestampUtc < order.EarliestFillUtc) continue;
            if (!_fillModel.TryFill(order, tick, tickSize, out var price, out var qty)) continue;

            var requiredSink = RequireTransitionSink();
            order.FilledQuantity += qty;
            order.TotalFillValue += price * qty;

            var newState = order.FilledQuantity >= order.Request.Quantity
                ? OrderState.Filled
                : OrderState.PartiallyFilled;
            order.State = newState;
            if (newState == OrderState.Filled)
                _byClientId.Remove(order.Request.ClientOrderId);

            var liquidity = order.Request.Type == OrderType.Limit ? LiquidityFlag.Maker : LiquidityFlag.Taker;

            PublishTransition(requiredSink, instrument, new OrderEvent(
                tick.TimestampUtc, order.Request.ClientOrderId, order.BrokerOrderId,
                order.Request.Side, newState,
                order.FilledQuantity, order.AveragePrice,
                LastFillQuantity: qty, LastFillPrice: price, Liquidity: liquidity));
        }
    }

    private Action<InstrumentId, OrderEvent> RequireTransitionSink() =>
        _requiredTransitionSink ?? throw new InvalidOperationException(
            "A required simulated-order transition sink must be bound before the book can transition.");

    private void PublishTransition(
        Action<InstrumentId, OrderEvent> requiredSink,
        InstrumentId instrument,
        OrderEvent orderEvent)
    {
        requiredSink(instrument, orderEvent);

        var observers = Event;
        if (observers is null) return;
        foreach (var handler in observers.GetInvocationList())
        {
            var observer = (Action<InstrumentId, OrderEvent>)handler;
            try
            {
                observer(instrument, orderEvent);
            }
            catch (Exception exception)
            {
                var method = observer.Method;
                _diagnosticFailures.Add(new SimulatedOrderBookDiagnosticFailure(
                    checked(++_nextDiagnosticFailureSequence),
                    instrument,
                    orderEvent.ClientOrderId,
                    orderEvent.State,
                    $"{method.DeclaringType?.FullName ?? "<unknown>"}.{method.Name}",
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message));
            }
        }
    }

    private void ReleaseRequiredTransitionSink(Action<InstrumentId, OrderEvent> sink)
    {
        if (ReferenceEquals(_requiredTransitionSink, sink)) _requiredTransitionSink = null;
    }

    private static bool IsTerminal(OrderState s) =>
        s is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected;

    private sealed class RequiredTransitionSinkBinding : IDisposable
    {
        private SimulatedOrderBook? _owner;
        private readonly Action<InstrumentId, OrderEvent> _sink;

        public RequiredTransitionSinkBinding(
            SimulatedOrderBook owner,
            Action<InstrumentId, OrderEvent> sink)
        {
            _owner = owner;
            _sink = sink;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseRequiredTransitionSink(_sink);
        }
    }
}

internal sealed record SimulatedOrderBookDiagnosticFailure(
    long Sequence,
    InstrumentId Instrument,
    string ClientOrderId,
    OrderState State,
    string Observer,
    string ExceptionType,
    string Message);
