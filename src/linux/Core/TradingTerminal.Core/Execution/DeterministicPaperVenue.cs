using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Execution;

/// <summary>Exact quote and deterministic available quantity supplied to the Paper venue.</summary>
public sealed record PaperMarketSnapshot
{
    public PaperMarketSnapshot(
        InstrumentId instrumentId,
        ScaledPrice bid,
        ScaledPrice ask,
        ScaledQuantity availableQuantity,
        DateTimeOffset observedAtUtc,
        DepthSnapshot? depth = null)
        : this(
            instrumentId,
            bid,
            ask,
            availableQuantity,
            availableQuantity,
            observedAtUtc,
            depth)
    {
    }

    public PaperMarketSnapshot(
        InstrumentId instrumentId,
        ScaledPrice bid,
        ScaledPrice ask,
        ScaledQuantity bidAvailableQuantity,
        ScaledQuantity askAvailableQuantity,
        DateTimeOffset observedAtUtc,
        DepthSnapshot? depth = null)
    {
        if (instrumentId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(instrumentId));
        if (!bid.IsValid || bid.Coefficient <= 0) throw new ArgumentOutOfRangeException(nameof(bid));
        if (!ask.IsValid || ask.Coefficient <= 0 ||
            !ScaledValueMath.TryCompare(ask.Coefficient, ask.Scale, bid.Coefficient, bid.Scale, out var spread) ||
            spread < 0)
            throw new ArgumentOutOfRangeException(nameof(ask));
        if (!bidAvailableQuantity.IsValid || bidAvailableQuantity.Coefficient < 0)
            throw new ArgumentOutOfRangeException(nameof(bidAvailableQuantity));
        if (!askAvailableQuantity.IsValid || askAvailableQuantity.Coefficient < 0)
            throw new ArgumentOutOfRangeException(nameof(askAvailableQuantity));
        InstrumentId = instrumentId;
        Bid = bid;
        Ask = ask;
        BidAvailableQuantity = bidAvailableQuantity;
        AskAvailableQuantity = askAvailableQuantity;
        ObservedAtUtc = ExecutionValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        Depth = depth;
    }

    public InstrumentId InstrumentId { get; }
    public ScaledPrice Bid { get; }
    public ScaledPrice Ask { get; }
    public ScaledQuantity BidAvailableQuantity { get; }
    public ScaledQuantity AskAvailableQuantity { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    /// <summary>Optional explicit book for book-walk fills (Validate parity).</summary>
    public DepthSnapshot? Depth { get; }
}

public enum PaperVenueRecoveryFault : byte
{
    None = 0,
    VenueNotPristine = 1,
    LedgerStreamMissing = 2,
    LedgerProjectionInvalid = 3,
    ResourceMismatch = 4,
    MissingBrokerOrderId = 5,
    UnsafeLifecycleState = 6,
    AmbiguousStopActivation = 7,
    InvalidEconomicSnapshot = 8,
    IdentitySequenceInvalid = 9,
    PendingCommandEvidenceInvalid = 10,
}

/// <summary>Atomic outcome of reconstructing one Paper venue from immutable OMS evidence.</summary>
public sealed record PaperVenueRecoveryResult(
    PaperVenueRecoveryFault Fault,
    int WorkingOrderCount,
    int CompletedOrderCount,
    int FillCount,
    ClientOrderId? BlockedOrderId = null,
    string? Reason = null)
{
    public bool IsSuccess => Fault == PaperVenueRecoveryFault.None;
}

/// <summary>
/// Deterministic Paper order venue. It supports Market, Limit, Stop, StopLimit, Day/GTC/IOC/FOK,
/// partial fills, cancel, replace, expiry, idempotent command replay, and callback queuing.
/// Optional <see cref="PaperFillFidelityOptions"/> reuses Validate FIFO-ahead / book-walk helpers.
/// </summary>
public sealed class DeterministicPaperVenue : IPaperExecutionDispatcher, IExecutionReconciliationSnapshotProvider
{
    private readonly object _gate = new();
    private readonly PaperFillFidelityOptions _fidelity;
    private readonly Dictionary<ClientOrderId, PaperOrder> _orders = [];
    private readonly Dictionary<ClientOrderId, PaperOrder> _completedOrders = [];
    private readonly Dictionary<InstrumentId, PaperMarketSnapshot> _markets = [];
    private readonly List<ReconciliationFillSnapshot> _fills = [];
    private readonly Dictionary<InstrumentId, ScaledQuantity> _positions = [];
    private readonly Queue<PaperVenueEvent> _events = [];
    private ScaledMoney _cash = ScaledMoney.Zero;
    private DateTimeOffset? _lastEconomicObservation;
    private long _nextOrderSequence;
    private long _nextDispatchSequence;
    private long _nextEventSequence;
    private long _nextTradeSequence;
    private string? _recoveryIdentityEpoch;

    public DeterministicPaperVenue(PaperFillFidelityOptions? fidelity = null)
    {
        _fidelity = fidelity ?? PaperFillFidelityOptions.Default;
    }

    public PaperFillFidelityOptions FillFidelity => _fidelity;

    public ExecutionDispatchResult Submit(
        SubmitOrderCommand command,
        OmsOrderProjection projection)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            if (_orders.TryGetValue(command.ClientOrderId, out var existing) ||
                _completedOrders.TryGetValue(command.ClientOrderId, out existing))
            {
                return existing.SubmitCommand == command
                    ? ExecutionDispatchResult.Dispatched(existing.SubmitReceipt)
                    : ExecutionDispatchResult.RejectedBeforeDispatch(
                        "The Paper client order id already belongs to different economics.");
            }

            if (command.Terms.Type == OrderType.Market &&
                !_markets.ContainsKey(command.Metadata.InstrumentId))
            {
                return ExecutionDispatchResult.RejectedBeforeDispatch(
                    "A Market order requires a current Paper quote before acceptance.");
            }

            var orderSequence = checked(++_nextOrderSequence);
            var brokerOrderId = new BrokerOrderId($"PAPER-{orderSequence}");
            var receipt = Receipt("submit", brokerOrderId, command.Metadata.CreatedAtUtc);
            var order = new PaperOrder(command, brokerOrderId, receipt, orderSequence);
            _orders.Add(command.ClientOrderId, order);
            Enqueue(
                PaperVenueEventKind.Acknowledged,
                order,
                command.Metadata.CreatedAtUtc,
                BrokerOrderId: brokerOrderId);
            BeginStopMonitoring(order, command.Metadata.CreatedAtUtc);

            if (_markets.TryGetValue(command.Metadata.InstrumentId, out var market))
                ConsumeStoredLiquidity(order, market);

            return ExecutionDispatchResult.Dispatched(receipt);
        }
    }

    public ExecutionDispatchResult Cancel(
        CancelOrderCommand command,
        OmsOrderProjection projection)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            if (!_orders.TryGetValue(projection.ClientOrderId, out var order))
                return ExecutionDispatchResult.RejectedBeforeDispatch("The Paper order is no longer working.");
            if (order.SubmitCommand.OrderId != command.OrderId)
                return ExecutionDispatchResult.RejectedBeforeDispatch("The Paper order identity does not match.");

            var receipt = Receipt("cancel", order.BrokerOrderId, command.Metadata.CreatedAtUtc);
            CompleteOrder(order, OrderLifecycleState.Cancelled);
            Enqueue(
                PaperVenueEventKind.Cancelled,
                order,
                command.Metadata.CreatedAtUtc,
                ResolveCausation(command),
                BrokerOrderId: order.BrokerOrderId);
            return ExecutionDispatchResult.Dispatched(receipt);
        }
    }

    public ExecutionDispatchResult Replace(
        ReplaceOrderCommand command,
        OmsOrderProjection projection)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            if (!_orders.TryGetValue(projection.ClientOrderId, out var order))
                return ExecutionDispatchResult.RejectedBeforeDispatch("The Paper order is no longer working.");
            if (order.SubmitCommand.OrderId != command.OrderId)
                return ExecutionDispatchResult.RejectedBeforeDispatch("The Paper order identity does not match.");
            if (CompareQuantity(command.ReplacementTerms.Quantity, order.FilledQuantity) < 0)
                return ExecutionDispatchResult.RejectedBeforeDispatch(
                    "Replacement quantity cannot be below accepted Paper fills.");

            order.Terms = command.ReplacementTerms;
            order.StopActivated = false;
            order.CausationId = ResolveCausation(command);
            order.State = order.FilledQuantity.Coefficient > 0
                ? OrderLifecycleState.PartiallyFilled
                : OrderLifecycleState.Working;
            var receipt = Receipt("replace", order.BrokerOrderId, command.Metadata.CreatedAtUtc);
            Enqueue(
                PaperVenueEventKind.Replaced,
                order,
                command.Metadata.CreatedAtUtc,
                order.CausationId,
                BrokerOrderId: order.BrokerOrderId,
                ReplacementTerms: command.ReplacementTerms);
            BeginStopMonitoring(order, command.Metadata.CreatedAtUtc);
            if (_markets.TryGetValue(command.Metadata.InstrumentId, out var market))
                ConsumeStoredLiquidity(order, market);
            return ExecutionDispatchResult.Dispatched(receipt);
        }
    }

    /// <summary>Publishes a quote and evaluates all accepted orders in submission order.</summary>
    public void OnMarket(PaperMarketSnapshot market)
    {
        ArgumentNullException.ThrowIfNull(market);
        lock (_gate)
        {
            _markets[market.InstrumentId] = market;
            var remainingBidLiquidity = market.BidAvailableQuantity;
            var remainingAskLiquidity = market.AskAvailableQuantity;
            foreach (var order in _orders.Values
                         .Where(order => order.SubmitCommand.Metadata.InstrumentId == market.InstrumentId)
                         .OrderBy(order => order.SubmissionSequence)
                         .ToArray())
            {
                // Refresh from store so book-walk depletion is visible to later orders this tick.
                var currentMarket = _markets.TryGetValue(market.InstrumentId, out var stored)
                    ? stored
                    : market;
                if (order.Terms.Side == OrderSide.Buy)
                {
                    if (remainingAskLiquidity.Coefficient <= 0) continue;
                    remainingAskLiquidity = SubtractQuantity(
                        remainingAskLiquidity,
                        Evaluate(order, currentMarket, remainingAskLiquidity));
                }
                else
                {
                    if (remainingBidLiquidity.Coefficient <= 0) continue;
                    remainingBidLiquidity = SubtractQuantity(
                        remainingBidLiquidity,
                        Evaluate(order, currentMarket, remainingBidLiquidity));
                }
            }

            var depletedDepth = _markets.TryGetValue(market.InstrumentId, out var afterWalk)
                ? afterWalk.Depth ?? market.Depth
                : market.Depth;
            _markets[market.InstrumentId] = new PaperMarketSnapshot(
                market.InstrumentId,
                market.Bid,
                market.Ask,
                remainingBidLiquidity,
                remainingAskLiquidity,
                market.ObservedAtUtc,
                depletedDepth);
        }
    }

    /// <summary>Expires accepted Day orders at a deterministic injected UTC instant.</summary>
    public void ExpireDayOrders(DateTimeOffset expiredAtUtc)
    {
        var timestamp = ExecutionValidation.RequireUtc(expiredAtUtc, nameof(expiredAtUtc));
        lock (_gate)
        {
            foreach (var order in _orders.Values
                         .Where(static order => order.Terms.TimeInForce == TimeInForce.Day)
                         .ToArray())
            {
                CompleteOrder(order, OrderLifecycleState.Expired);
                Enqueue(
                    PaperVenueEventKind.Expired,
                    order,
                    timestamp,
                    order.CausationId,
                    BrokerOrderId: order.BrokerOrderId);
            }
        }
    }

    public IReadOnlyList<PaperVenueEvent> DrainEvents()
    {
        lock (_gate)
        {
            if (_events.Count == 0) return Array.Empty<PaperVenueEvent>();
            var copy = _events.ToArray();
            _events.Clear();
            return Array.AsReadOnly(copy);
        }
    }

    /// <summary>
    /// Reconstructs broker-side Paper truth from a verified ledger. The operation is all-or-nothing:
    /// uncertain external outcomes and non-evented stop activation reject recovery before venue state
    /// changes. A successful caller must still run startup reconciliation before admitting new orders.
    /// </summary>
    public PaperVenueRecoveryResult RestoreFromLedger(
        ExecutionResource resource,
        IOrderEventStore eventStore,
        DateTimeOffset recoveredAtUtc)
    {
        if (!resource.IsValid) throw new ArgumentException("The execution resource is invalid.", nameof(resource));
        ArgumentNullException.ThrowIfNull(eventStore);
        var recoveredAt = ExecutionValidation.RequireUtc(recoveredAtUtc, nameof(recoveredAtUtc));

        lock (_gate)
        {
            if (_orders.Count != 0 || _completedOrders.Count != 0 || _markets.Count != 0 ||
                _fills.Count != 0 || _positions.Count != 0 || _events.Count != 0 ||
                _cash != ScaledMoney.Zero || _nextOrderSequence != 0 || _nextDispatchSequence != 0 ||
                _nextEventSequence != 0 || _nextTradeSequence != 0)
            {
                return RecoveryFailed(
                    PaperVenueRecoveryFault.VenueNotPristine,
                    null,
                    "Paper recovery requires a newly constructed venue.");
            }

            var outbox = eventStore.ReadOutbox();
            var dispatched = outbox
                .Where(entry => entry.Event.Kind == OrderEventKind.SubmissionRecorded)
                .GroupBy(entry => entry.Event.AggregateId)
                .Select(group => new
                {
                    ClientOrderId = group.Key,
                    SubmissionSequence = group.Min(entry => entry.OutboxSequence),
                })
                .OrderBy(item => item.SubmissionSequence)
                .ToArray();
            var recoveredOrders = new Dictionary<ClientOrderId, PaperOrder>();
            var recoveredCompleted = new Dictionary<ClientOrderId, PaperOrder>();
            long nextOrderSequence = 0;
            long nextBrokerSequence = 0;

            foreach (var item in dispatched)
            {
                var events = eventStore.Read(item.ClientOrderId);
                var storedProjection = eventStore.ReadProjection(item.ClientOrderId);
                if (events.Count == 0 || storedProjection is null)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.LedgerStreamMissing,
                        item.ClientOrderId,
                        "A dispatched Paper order is missing its event stream or projection.");
                }

                var rebuilt = OmsOrderProjection.Rebuild(events);
                if (!rebuilt.IsSuccess || rebuilt.Projection != storedProjection)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.LedgerProjectionInvalid,
                        item.ClientOrderId,
                        "The stored Paper projection does not equal verified event replay.");
                }

                var projection = rebuilt.Projection;
                var metadata = projection.SubmitCommand.Metadata;
                if (metadata.VenueId != resource.VenueId ||
                    metadata.TradingAccountId != resource.TradingAccountId ||
                    metadata.Environment != resource.Environment)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.ResourceMismatch,
                        item.ClientOrderId,
                        "A dispatched order belongs to a different venue, account, or environment.");
                }
                if (projection.BrokerOrderId is not { } brokerOrderId)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.MissingBrokerOrderId,
                        item.ClientOrderId,
                        "A dispatched Paper order has no broker order identity.");
                }
                if (!TryParsePaperSequence(brokerOrderId.Value, "PAPER-", out var brokerSequence))
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.IdentitySequenceInvalid,
                        item.ClientOrderId,
                        "A restored Paper broker order id is not canonical.");
                }

                var recoveryAction = projection.State switch
                {
                    OrderLifecycleState.Acknowledging => PaperRecoveryAction.Acknowledge,
                    OrderLifecycleState.PendingCancel => PaperRecoveryAction.ConfirmCancel,
                    OrderLifecycleState.PendingReplace => PaperRecoveryAction.ConfirmReplace,
                    _ => PaperRecoveryAction.None,
                };
                var pendingReplacement = projection.PendingReplacementTerms;
                if (recoveryAction == PaperRecoveryAction.ConfirmReplace && pendingReplacement is null)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.PendingCommandEvidenceInvalid,
                        item.ClientOrderId,
                        "PendingReplace has no durable replacement terms.");
                }

                var restoredState = recoveryAction switch
                {
                    PaperRecoveryAction.ConfirmCancel => OrderLifecycleState.Cancelled,
                    PaperRecoveryAction.ConfirmReplace when projection.FilledQuantity.Coefficient > 0 =>
                        OrderLifecycleState.PartiallyFilled,
                    PaperRecoveryAction.ConfirmReplace => OrderLifecycleState.Working,
                    _ => projection.State,
                };
                var isCompleted = OrderLifecycle.IsTerminal(restoredState);
                if (!isCompleted && projection.State is not (
                        OrderLifecycleState.Acknowledging or
                        OrderLifecycleState.Working or
                        OrderLifecycleState.PartiallyFilled or
                        OrderLifecycleState.PendingReplace))
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.UnsafeLifecycleState,
                        item.ClientOrderId,
                        $"Paper order state {projection.State} has an uncertain dispatch outcome.");
                }
                var stopActivation = recoveryAction == PaperRecoveryAction.ConfirmReplace
                    ? new OrderStopActivationProjectionResult(
                        true,
                        OrderStopActivationProjector.Initial(pendingReplacement!),
                        -1)
                    : OrderStopActivationProjector.Rebuild(events);
                if (recoveryAction != PaperRecoveryAction.ConfirmCancel && !stopActivation.IsSuccess)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.AmbiguousStopActivation,
                        item.ClientOrderId,
                        $"Stop activation evidence is invalid at event {stopActivation.EventIndex}.");
                }
                if (recoveryAction == PaperRecoveryAction.None &&
                    !isCompleted &&
                    stopActivation.State == OrderStopActivationState.Unknown)
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.AmbiguousStopActivation,
                        item.ClientOrderId,
                        "Zero-fill Stop activation is not represented by durable OMS evidence.");
                }

                var submission = events.Single(orderEvent => orderEvent.Kind == OrderEventKind.SubmissionRecorded);
                var submissionSequence = checked(++nextOrderSequence);
                var receipt = new ExecutionDispatchReceipt(
                    new DispatchAttemptId($"paper-recovered-submit-{submissionSequence}"),
                    submission.OccurredAtUtc,
                    brokerOrderId,
                    projection.ExchangeOrderId);
                var order = new PaperOrder(
                    projection.SubmitCommand,
                    brokerOrderId,
                    receipt,
                    submissionSequence)
                {
                    Terms = pendingReplacement ?? projection.Terms,
                    FilledQuantity = projection.FilledQuantity,
                    StopActivated = stopActivation.State == OrderStopActivationState.Activated,
                    CausationId = projection.LastCausationId,
                    State = restoredState,
                    RecoveryAction = recoveryAction,
                    RecoveryDispatchWasRecorded = events.Any(orderEvent => orderEvent.Kind ==
                        (recoveryAction == PaperRecoveryAction.ConfirmCancel
                            ? OrderEventKind.CancelDispatchRecorded
                            : OrderEventKind.ReplaceDispatchRecorded)),
                };
                (isCompleted ? recoveredCompleted : recoveredOrders).Add(item.ClientOrderId, order);
                nextBrokerSequence = Math.Max(nextBrokerSequence, brokerSequence);
            }

            ExecutionReconciliationSnapshot economicSnapshot;
            try
            {
                economicSnapshot = ExecutionReconciliationSnapshotBuilder.FromLedger(
                    resource,
                    recoveredAt,
                    eventStore);
            }
            catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentException)
            {
                return RecoveryFailed(
                    PaperVenueRecoveryFault.InvalidEconomicSnapshot,
                    null,
                    $"Paper economic state could not be rebuilt: {exception.GetType().Name}.");
            }

            long nextTradeSequence = 0;
            foreach (var fill in economicSnapshot.Fills)
            {
                if (!TryParsePaperSequence(fill.TradeId.Value, "PAPER-TRADE-", out var tradeSequence))
                {
                    return RecoveryFailed(
                        PaperVenueRecoveryFault.IdentitySequenceInvalid,
                        fill.ClientOrderId,
                        "A restored Paper trade id is not canonical.");
                }
                nextTradeSequence = Math.Max(nextTradeSequence, tradeSequence);
            }

            var epochSeed = string.Join('|', outbox.Select(entry => entry.Event.EventHash));
            var recoveryEpoch = ExecutionCanonicalJson.Sha256(epochSeed)[..16];
            foreach (var pair in recoveredOrders) _orders.Add(pair.Key, pair.Value);
            foreach (var pair in recoveredCompleted) _completedOrders.Add(pair.Key, pair.Value);
            _fills.AddRange(economicSnapshot.Fills);
            foreach (var position in economicSnapshot.Positions)
                _positions.Add(position.InstrumentId, position.Quantity);
            _cash = economicSnapshot.Cash.Single(item =>
                string.Equals(item.Currency, "SIM", StringComparison.Ordinal)).Total;
            _lastEconomicObservation = economicSnapshot.Fills.Count == 0
                ? null
                : economicSnapshot.Fills.Max(fill => fill.OccurredAtUtc);
            _nextOrderSequence = Math.Max(nextOrderSequence, nextBrokerSequence);
            _nextTradeSequence = nextTradeSequence;
            _recoveryIdentityEpoch = recoveryEpoch;

            foreach (var order in _orders.Values.Concat(_completedOrders.Values)
                         .OrderBy(order => order.SubmissionSequence))
            {
                switch (order.RecoveryAction)
                {
                    case PaperRecoveryAction.Acknowledge:
                        Enqueue(
                            PaperVenueEventKind.Acknowledged,
                            order,
                            recoveredAt,
                            order.CausationId,
                            BrokerOrderId: order.BrokerOrderId,
                            Reason: "Paper acknowledgement was reconstructed from durable submission evidence.");
                        break;
                    case PaperRecoveryAction.ConfirmCancel:
                        Enqueue(
                            PaperVenueEventKind.Cancelled,
                            order,
                            recoveredAt,
                            order.CausationId,
                            BrokerOrderId: order.BrokerOrderId,
                            Reason: order.RecoveryDispatchWasRecorded
                                ? "Paper cancellation crossed the venue boundary before restart."
                                : "Committed Paper cancellation was replayed on the pristine recovered venue.");
                        break;
                    case PaperRecoveryAction.ConfirmReplace:
                        Enqueue(
                            PaperVenueEventKind.Replaced,
                            order,
                            recoveredAt,
                            order.CausationId,
                            BrokerOrderId: order.BrokerOrderId,
                            ReplacementTerms: order.Terms,
                            Reason: order.RecoveryDispatchWasRecorded
                                ? "Paper replacement crossed the venue boundary before restart."
                                : "Committed Paper replacement was replayed on the pristine recovered venue.");
                        BeginStopMonitoring(order, recoveredAt);
                        break;
                }
            }

            return new PaperVenueRecoveryResult(
                PaperVenueRecoveryFault.None,
                _orders.Count,
                _completedOrders.Count,
                _fills.Count);
        }
    }

    public ExecutionReconciliationSnapshot CaptureReconciliationSnapshot(
        ExecutionResource resource,
        DateTimeOffset capturedAtUtc)
    {
        if (!resource.IsValid) throw new ArgumentException("The execution resource is invalid.", nameof(resource));
        var captured = ExecutionValidation.RequireUtc(capturedAtUtc, nameof(capturedAtUtc));
        lock (_gate)
        {
            if (_lastEconomicObservation is { } observed && observed > captured)
                throw new ArgumentOutOfRangeException(nameof(capturedAtUtc), "Capture cannot precede Paper fills.");
            var orders = _orders.Values.Concat(_completedOrders.Values)
                .OrderBy(item => item.SubmissionSequence)
                .Select(item => new ReconciliationOrderSnapshot(
                    item.SubmitCommand.CanonicalInstruction,
                    CanonicalOrderInstructionMapper.ToCanonicalTerms(item.Terms),
                    item.State,
                    WasDispatched: true,
                    item.BrokerOrderId,
                    ExchangeOrderId: null,
                    item.FilledQuantity))
                .ToArray();
            var observedAt = _lastEconomicObservation ?? captured;
            var positions = _positions
                .OrderBy(item => item.Key.Value)
                .Select(item => new ReconciliationPositionSnapshot(item.Key, item.Value, observedAt))
                .ToArray();
            return new ExecutionReconciliationSnapshot(
                resource,
                captured,
                Array.AsReadOnly(orders),
                Array.AsReadOnly(_fills.OrderBy(item => item.TradeId.Value, StringComparer.Ordinal).ToArray()),
                Array.AsReadOnly(positions),
                Array.AsReadOnly(new[] { new ReconciliationCashSnapshot("SIM", _cash, _cash, observedAt) }));
        }
    }

    private void ConsumeStoredLiquidity(PaperOrder order, PaperMarketSnapshot market)
    {
        var available = AvailableForSide(order.Terms.Side, market);
        var consumed = Evaluate(order, market, available);
        if (consumed.Coefficient <= 0) return;

        // Prefer depth depleted by book-walk inside Evaluate.
        var depth = _markets.TryGetValue(market.InstrumentId, out var stored)
            ? stored.Depth ?? market.Depth
            : market.Depth;

        var bidAvailable = market.BidAvailableQuantity;
        var askAvailable = market.AskAvailableQuantity;
        if (order.Terms.Side == OrderSide.Buy)
            askAvailable = SubtractQuantity(askAvailable, consumed);
        else
            bidAvailable = SubtractQuantity(bidAvailable, consumed);

        _markets[market.InstrumentId] = new PaperMarketSnapshot(
            market.InstrumentId,
            market.Bid,
            market.Ask,
            bidAvailable,
            askAvailable,
            market.ObservedAtUtc,
            depth);
    }

    private ScaledQuantity Evaluate(
        PaperOrder order,
        PaperMarketSnapshot market,
        ScaledQuantity? availableQuantity = null)
    {
        if (!_orders.ContainsKey(order.SubmitCommand.ClientOrderId)) return ScaledQuantity.Zero;

        var remaining = SubtractQuantity(order.Terms.Quantity, order.FilledQuantity);
        if (remaining.Coefficient <= 0) return ScaledQuantity.Zero;

        UpdateFifoQueue(order, market);
        if (_fidelity.EnableFifoQueueAhead &&
            order.Terms.Type == OrderType.Limit &&
            !TradingTerminal.Core.Backtesting.FifoQueueAheadEstimatorV1.IsCleared(order.QueueAhead))
        {
            var limit = order.Terms.LimitPrice!.Value;
            var crossing = order.Terms.Side == OrderSide.Buy
                ? ComparePrice(market.Ask, limit) < 0
                : ComparePrice(market.Bid, limit) > 0;
            if (!crossing)
                return ScaledQuantity.Zero;
        }

        var available = MinQuantity(
            remaining,
            availableQuantity ?? AvailableForSide(order.Terms.Side, market));

        ScaledPrice price;
        var executable = ResolveExecutionPrice(order, market, out price);
        if (_fidelity.EnableL2BookWalk &&
            market.Depth is { } depth &&
            TryBookWalkFill(order, market, depth, remaining, available, out var walkQty, out var walkPrice))
        {
            available = walkQty;
            price = walkPrice;
            executable = walkQty.Coefficient > 0;
            // Deplete walked levels on the stored book for subsequent orders.
            var isBuy = order.Terms.Side == OrderSide.Buy;
            double? limitPx = order.Terms.Type is OrderType.Limit or OrderType.StopLimit
                ? (double)ExecutionNumericBoundary.ToDecimal(order.Terms.LimitPrice!.Value)
                : null;
            var filledLong = (long)ExecutionNumericBoundary.ToDecimal(walkQty);
            var updatedDepth = isBuy
                ? depth with
                {
                    Asks = TradingTerminal.Core.Backtesting.L2BookWalkFillV1.Consume(
                        filledLong, depth.Asks, limitPx, isBuy: true),
                }
                : depth with
                {
                    Bids = TradingTerminal.Core.Backtesting.L2BookWalkFillV1.Consume(
                        filledLong, depth.Bids, limitPx, isBuy: false),
                };
            _markets[market.InstrumentId] = new PaperMarketSnapshot(
                market.InstrumentId,
                market.Bid,
                market.Ask,
                market.BidAvailableQuantity,
                market.AskAvailableQuantity,
                market.ObservedAtUtc,
                updatedDepth);
            market = _markets[market.InstrumentId];
        }

        if (!executable)
        {
            if (order.Terms.TimeInForce is TimeInForce.Ioc or TimeInForce.Fok)
            {
                CompleteOrder(order, OrderLifecycleState.Cancelled);
                Enqueue(
                    PaperVenueEventKind.Cancelled,
                    order,
                    market.ObservedAtUtc,
                    order.CausationId,
                    BrokerOrderId: order.BrokerOrderId,
                    Reason: $"{order.Terms.TimeInForce} order was not immediately executable.");
            }
            return ScaledQuantity.Zero;
        }

        if (_fidelity.MaxFillQuantityPerTouch > 0)
        {
            var maxTouch = ScaledQuantity.FromWhole(_fidelity.MaxFillQuantityPerTouch);
            available = MinQuantity(available, maxTouch);
        }

        if (order.Terms.TimeInForce == TimeInForce.Fok && CompareQuantity(available, remaining) < 0)
        {
            CompleteOrder(order, OrderLifecycleState.Cancelled);
            Enqueue(
                PaperVenueEventKind.Cancelled,
                order,
                market.ObservedAtUtc,
                order.CausationId,
                BrokerOrderId: order.BrokerOrderId,
                Reason: "FOK liquidity was insufficient.");
            return ScaledQuantity.Zero;
        }

        if (available.Coefficient > 0)
        {
            if (!ScaledValueMath.TryAddQuantity(order.FilledQuantity, available, out var filledQuantity))
                throw new OverflowException("Paper filled quantity cannot be represented exactly.");
            order.FilledQuantity = filledQuantity;
            var fill = new OrderFill(
                new TradeId($"PAPER-TRADE-{checked(++_nextTradeSequence)}"),
                available,
                price,
                ScaledMoney.Zero,
                market.ObservedAtUtc);
            var reconciliationFill = new ReconciliationFillSnapshot(
                fill.TradeId,
                order.SubmitCommand.ClientOrderId,
                order.BrokerOrderId,
                null,
                order.SubmitCommand.Metadata.InstrumentId,
                order.Terms.Side,
                fill.Quantity,
                fill.Price,
                fill.Fee,
                fill.OccurredAtUtc);
            ExecutionReconciliationSnapshotBuilder.ApplyFill(
                reconciliationFill,
                _positions,
                ref _cash);
            _fills.Add(reconciliationFill);
            _lastEconomicObservation = market.ObservedAtUtc;
            Enqueue(
                PaperVenueEventKind.Fill,
                order,
                market.ObservedAtUtc,
                order.CausationId,
                BrokerOrderId: order.BrokerOrderId,
                Fill: fill);
        }

        if (CompareQuantity(order.FilledQuantity, order.Terms.Quantity) >= 0)
            CompleteOrder(order, OrderLifecycleState.Filled);
        else if (order.Terms.TimeInForce == TimeInForce.Ioc)
        {
            CompleteOrder(order, OrderLifecycleState.Cancelled);
            Enqueue(
                PaperVenueEventKind.Cancelled,
                order,
                market.ObservedAtUtc,
                order.CausationId,
                BrokerOrderId: order.BrokerOrderId,
                Reason: "IOC remainder cancelled.");
        }
        else if (order.FilledQuantity.Coefficient > 0)
        {
            order.State = OrderLifecycleState.PartiallyFilled;
        }

        return available;
    }

    private void UpdateFifoQueue(PaperOrder order, PaperMarketSnapshot market)
    {
        if (!_fidelity.EnableFifoQueueAhead || order.Terms.Type != OrderType.Limit)
            return;

        var opposite = order.Terms.Side == OrderSide.Buy
            ? (long)ExecutionNumericBoundary.ToDecimal(market.AskAvailableQuantity)
            : (long)ExecutionNumericBoundary.ToDecimal(market.BidAvailableQuantity);
        if (!order.QueueJoined)
        {
            order.QueueAhead = TradingTerminal.Core.Backtesting.FifoQueueAheadEstimatorV1.Join(opposite);
            order.LastOppositeSize = opposite;
            order.QueueJoined = true;
            return;
        }

        order.QueueAhead = TradingTerminal.Core.Backtesting.FifoQueueAheadEstimatorV1.Consume(
            order.QueueAhead,
            order.LastOppositeSize,
            opposite);
        order.LastOppositeSize = opposite;
    }

    private bool TryBookWalkFill(
        PaperOrder order,
        PaperMarketSnapshot market,
        DepthSnapshot depth,
        ScaledQuantity remaining,
        ScaledQuantity availableCap,
        out ScaledQuantity fillQty,
        out ScaledPrice fillPrice)
    {
        fillQty = ScaledQuantity.Zero;
        fillPrice = market.Ask;
        var isBuy = order.Terms.Side == OrderSide.Buy;
        var levels = isBuy ? depth.Asks : depth.Bids;
        if (levels.Count == 0)
            return false;

        double? limitPx = null;
        if (order.Terms.Type is OrderType.Limit or OrderType.StopLimit)
            limitPx = (double)ExecutionNumericBoundary.ToDecimal(order.Terms.LimitPrice!.Value);

        var want = (long)ExecutionNumericBoundary.ToDecimal(remaining);
        var cap = (long)ExecutionNumericBoundary.ToDecimal(availableCap);
        if (want <= 0 || cap <= 0)
            return false;
        want = Math.Min(want, cap);

        var walk = TradingTerminal.Core.Backtesting.L2BookWalkFillV1.Walk(isBuy, want, levels, limitPx);
        if (walk.FilledQuantity <= 0)
            return false;

        fillQty = ScaledQuantity.FromWhole(walk.FilledQuantity);
        // Average can need more fractional digits than the L1 quote scale (e.g. 100.00625).
        fillPrice = ExecutionNumericBoundary.PriceFromDecimal((decimal)walk.AverageFillPrice);
        return true;
    }

    private bool ResolveExecutionPrice(
        PaperOrder order,
        PaperMarketSnapshot market,
        out ScaledPrice price)
    {
        var terms = order.Terms;
        price = terms.Side == OrderSide.Buy ? market.Ask : market.Bid;

        if (terms.Type is OrderType.Stop or OrderType.StopLimit && !order.StopActivated)
        {
            order.StopActivated = terms.Side == OrderSide.Buy
                ? ComparePrice(market.Ask, terms.StopPrice!.Value) >= 0
                : ComparePrice(market.Bid, terms.StopPrice!.Value) <= 0;
            if (!order.StopActivated) return false;
            Enqueue(
                PaperVenueEventKind.StopActivated,
                order,
                market.ObservedAtUtc,
                order.CausationId,
                BrokerOrderId: order.BrokerOrderId,
                Reason: "The Paper stop trigger crossed its configured price.");
        }

        if (terms.Type is OrderType.Limit or OrderType.StopLimit)
        {
            return terms.Side == OrderSide.Buy
                ? ComparePrice(market.Ask, terms.LimitPrice!.Value) <= 0
                : ComparePrice(market.Bid, terms.LimitPrice!.Value) >= 0;
        }

        return true;
    }

    private static ScaledQuantity MinQuantity(ScaledQuantity left, ScaledQuantity right) =>
        CompareQuantity(left, right) <= 0 ? left : right;

    private static ScaledQuantity AvailableForSide(OrderSide side, PaperMarketSnapshot market) =>
        side == OrderSide.Buy ? market.AskAvailableQuantity : market.BidAvailableQuantity;

    private static ScaledQuantity SubtractQuantity(ScaledQuantity left, ScaledQuantity right)
    {
        if (!ScaledValueMath.TrySubtractQuantity(left, right, out var difference) || difference.Coefficient < 0)
            throw new OverflowException("Paper liquidity arithmetic cannot be represented exactly.");
        return difference;
    }

    private static int CompareQuantity(ScaledQuantity left, ScaledQuantity right)
    {
        if (!ScaledValueMath.TryCompare(left.Coefficient, left.Scale, right.Coefficient, right.Scale, out var comparison))
            throw new OverflowException("Paper quantity comparison cannot be represented exactly.");
        return comparison;
    }

    private void BeginStopMonitoring(PaperOrder order, DateTimeOffset occurredAtUtc)
    {
        if (order.Terms.Type is not (OrderType.Stop or OrderType.StopLimit)) return;
        Enqueue(
            PaperVenueEventKind.StopMonitoringStarted,
            order,
            occurredAtUtc,
            order.CausationId,
            BrokerOrderId: order.BrokerOrderId,
            Reason: "The Paper venue is durably tracking stop activation.");
    }

    private static int ComparePrice(ScaledPrice left, ScaledPrice right)
    {
        if (!ScaledValueMath.TryCompare(left.Coefficient, left.Scale, right.Coefficient, right.Scale, out var comparison))
            throw new OverflowException("Paper price comparison cannot be represented exactly.");
        return comparison;
    }

    private ExecutionDispatchReceipt Receipt(
        string operation,
        BrokerOrderId brokerOrderId,
        DateTimeOffset occurredAtUtc) =>
        new(
            new DispatchAttemptId(
                _recoveryIdentityEpoch is null
                    ? $"paper-{operation}-{checked(++_nextDispatchSequence)}"
                    : $"paper-{operation}-{_recoveryIdentityEpoch}-{checked(++_nextDispatchSequence)}"),
            ExecutionValidation.RequireUtc(occurredAtUtc, nameof(occurredAtUtc)),
            brokerOrderId);

    private void Enqueue(
        PaperVenueEventKind kind,
        PaperOrder order,
        DateTimeOffset occurredAtUtc,
        CausationId? causationId = null,
        BrokerOrderId? BrokerOrderId = null,
        ExchangeOrderId? ExchangeOrderId = null,
        OrderFill? Fill = null,
        OrderTerms? ReplacementTerms = null,
        string? Reason = null)
    {
        var eventId = new ExecutionEventId(
            _recoveryIdentityEpoch is null
                ? $"paper-event-{checked(++_nextEventSequence)}"
                : $"paper-event-{_recoveryIdentityEpoch}-{checked(++_nextEventSequence)}");
        var cause = causationId ?? order.CausationId;
        _events.Enqueue(new PaperVenueEvent(
            eventId,
            kind,
            order.SubmitCommand.ClientOrderId,
            occurredAtUtc,
            cause,
            BrokerOrderId,
            ExchangeOrderId,
            Fill,
            ReplacementTerms,
            Reason));
    }

    private static CausationId ResolveCausation(ExecutionCommand command) =>
        command.Metadata.CausationId ?? new CausationId(command.Metadata.CommandId.Value);

    private static bool TryParsePaperSequence(string value, string prefix, out long sequence)
    {
        sequence = 0;
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(
                   value.AsSpan(prefix.Length),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out sequence) && sequence > 0;
    }

    private static PaperVenueRecoveryResult RecoveryFailed(
        PaperVenueRecoveryFault fault,
        ClientOrderId? blockedOrderId,
        string reason) =>
        new(fault, 0, 0, 0, blockedOrderId, reason);

    private void CompleteOrder(PaperOrder order, OrderLifecycleState state)
    {
        order.State = state;
        _orders.Remove(order.SubmitCommand.ClientOrderId);
        _completedOrders[order.SubmitCommand.ClientOrderId] = order;
    }

    private sealed class PaperOrder
    {
        public PaperOrder(
            SubmitOrderCommand submitCommand,
            BrokerOrderId brokerOrderId,
            ExecutionDispatchReceipt submitReceipt,
            long submissionSequence)
        {
            SubmitCommand = submitCommand;
            Terms = submitCommand.Terms;
            BrokerOrderId = brokerOrderId;
            SubmitReceipt = submitReceipt;
            SubmissionSequence = submissionSequence;
            CausationId = ResolveCausation(submitCommand);
        }

        public SubmitOrderCommand SubmitCommand { get; }
        public BrokerOrderId BrokerOrderId { get; }
        public ExecutionDispatchReceipt SubmitReceipt { get; }
        public long SubmissionSequence { get; }
        public OrderTerms Terms { get; set; }
        public ScaledQuantity FilledQuantity { get; set; } = ScaledQuantity.Zero;
        public bool StopActivated { get; set; }
        public CausationId CausationId { get; set; }
        public OrderLifecycleState State { get; set; } = OrderLifecycleState.Working;
        public PaperRecoveryAction RecoveryAction { get; set; }
        public bool RecoveryDispatchWasRecorded { get; set; }

        /// <summary>FIFO-ahead estimate remaining (Validate parity). 0 = cleared / unused.</summary>
        public long QueueAhead { get; set; }
        public bool QueueJoined { get; set; }
        public long LastOppositeSize { get; set; }
    }

    private enum PaperRecoveryAction : byte
    {
        None = 0,
        Acknowledge = 1,
        ConfirmCancel = 2,
        ConfirmReplace = 3,
    }
}
