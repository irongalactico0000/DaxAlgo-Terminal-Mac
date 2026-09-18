using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Sandbox;

public enum PaperMarketDataBridgeFaultKind : byte
{
    InvalidQuote = 0,
    StaleOrDuplicateQuote = 1,
    NumericConversionFailed = 2,
    OmsVenueEventRejected = 3,
    SourceFailed = 4,
}

/// <summary>Observable fail-closed diagnostic from the L1-to-Paper execution boundary.</summary>
public sealed record PaperMarketDataBridgeFault(
    PaperMarketDataBridgeFaultKind Kind,
    InstrumentId InstrumentId,
    long Sequence,
    string Reason);

/// <summary>
/// Paper-only composition boundary from canonical L1 quotes to the deterministic venue and back
/// through the OMS callback ledger. It owns only subscriptions; it never starts a broker feed and
/// exposes no live execution adapter.
/// </summary>
public sealed class PaperMarketDataExecutionBridge : IDisposable
{
    public const byte DefaultPriceScale = 8;

    private readonly object _processGate = new();
    private readonly ReaderWriterLockSlim _callbackGate = new(LockRecursionPolicy.NoRecursion);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<InstrumentId, QuoteCursor> _cursors = [];
    private readonly DeterministicPaperVenue _venue;
    private readonly OrderManagementService _oms;
    private readonly byte _priceScale;
    private readonly Action<OmsCommandResult>? _resultObserver;
    private readonly Action<PaperMarketDataBridgeFault>? _faultObserver;
    private PaperMarketDataBridgeFault? _lastFault;
    private long _processedQuoteCount;
    private long _rejectedQuoteCount;
    private int _executionFaulted;
    private int _disposed;

    public PaperMarketDataExecutionBridge(
        IMarketDataHub hub,
        IEnumerable<InstrumentId> instruments,
        DeterministicPaperVenue venue,
        OrderManagementService oms,
        byte priceScale = DefaultPriceScale,
        Action<OmsCommandResult>? resultObserver = null,
        Action<PaperMarketDataBridgeFault>? faultObserver = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(instruments);
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _oms = oms ?? throw new ArgumentNullException(nameof(oms));

        // Use the public boundary itself to validate the configured scale without relying on Core
        // internals. Zero is valid at every supported scale and cannot overflow.
        try
        {
            _ = ExecutionNumericBoundary.PriceFromDouble(0, priceScale);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(priceScale), priceScale, exception.Message);
        }

        _priceScale = priceScale;
        _resultObserver = resultObserver;
        _faultObserver = faultObserver;

        var declared = instruments.Distinct().ToArray();
        if (declared.Length == 0 || declared.Any(static instrument => instrument.IsNone))
            throw new ArgumentException("At least one resolved Paper instrument is required.", nameof(instruments));

        foreach (var instrument in declared)
        {
            _cursors.Add(instrument, default);
            _subscriptions.Add(hub.Quotes(instrument).Subscribe(
                new QuoteObserver(
                    quote => OnQuote(instrument, quote),
                    error => LatchExecutionFault(new PaperMarketDataBridgeFault(
                        PaperMarketDataBridgeFaultKind.SourceFailed,
                        instrument,
                        0,
                        $"The canonical quote stream failed ({error.GetType().Name}).")))));
        }
    }

    public long ProcessedQuoteCount => Interlocked.Read(ref _processedQuoteCount);
    public long RejectedQuoteCount => Interlocked.Read(ref _rejectedQuoteCount);
    public bool IsExecutionFaulted => Volatile.Read(ref _executionFaulted) != 0;
    public PaperMarketDataBridgeFault? LastFault => Volatile.Read(ref _lastFault);

    /// <summary>
    /// Returns the latest accepted exact bid/ask midpoint for a strategy-to-Paper risk decision.
    /// The caller remains responsible for enforcing its maximum observation age.
    /// </summary>
    public bool TryGetLatestReferencePrice(
        InstrumentId instrumentId,
        out ScaledPrice price,
        out DateTimeOffset observedAtUtc)
    {
        price = default;
        observedAtUtc = default;
        if (Volatile.Read(ref _disposed) != 0 || IsExecutionFaulted) return false;
        lock (_processGate)
        {
            if (!_cursors.TryGetValue(instrumentId, out var cursor) || cursor.Sequence <= 0)
                return false;
            price = cursor.ReferencePrice;
            observedAtUtc = new DateTimeOffset(cursor.EventTimeUtc);
            return price.IsValid && price.Coefficient > 0 && observedAtUtc.Offset == TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _callbackGate.EnterWriteLock();
        try
        {
            List<Exception>? failures = null;
            foreach (var subscription in _subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            _subscriptions.Clear();
            if (failures is not null)
                throw new AggregateException("One or more Paper quote subscriptions failed to dispose.", failures);
        }
        finally
        {
            _callbackGate.ExitWriteLock();
            _callbackGate.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private void OnQuote(InstrumentId subscribedInstrument, Quote quote)
    {
        _callbackGate.EnterReadLock();
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || IsExecutionFaulted) return;

            lock (_processGate)
            {
                if (!TryCreateSnapshot(subscribedInstrument, quote, out var snapshot, out var fault))
                {
                    Interlocked.Increment(ref _rejectedQuoteCount);
                    RecordFault(fault!);
                    return;
                }

                _venue.OnMarket(snapshot!);
                var results = _oms.ProcessVenueEvents();
                foreach (var result in results)
                {
                    NotifyResult(result);
                    if (!result.IsSuccess)
                    {
                        LatchExecutionFault(new PaperMarketDataBridgeFault(
                            PaperMarketDataBridgeFaultKind.OmsVenueEventRejected,
                            quote.InstrumentId,
                            quote.Sequence,
                            result.Reason ?? $"The OMS rejected a Paper callback ({result.Fault})."));
                    }
                }

                var midpoint = Midpoint(snapshot!.Bid, snapshot.Ask);
                _cursors[subscribedInstrument] = new QuoteCursor(
                    quote.Sequence,
                    quote.EventTimeUtc,
                    midpoint);
                Interlocked.Increment(ref _processedQuoteCount);
            }
        }
        finally
        {
            _callbackGate.ExitReadLock();
        }
    }

    private bool TryCreateSnapshot(
        InstrumentId subscribedInstrument,
        Quote? quote,
        out PaperMarketSnapshot? snapshot,
        out PaperMarketDataBridgeFault? fault)
    {
        snapshot = null;
        fault = null;
        if (quote is null || quote.InstrumentId != subscribedInstrument ||
            quote.Sequence <= 0 || quote.EventTimeUtc.Kind != DateTimeKind.Utc ||
            !double.IsFinite(quote.Bid) || !double.IsFinite(quote.Ask) ||
            quote.Bid <= 0 || quote.Ask < quote.Bid || quote.BidSize < 0 || quote.AskSize < 0)
        {
            fault = new PaperMarketDataBridgeFault(
                PaperMarketDataBridgeFaultKind.InvalidQuote,
                quote?.InstrumentId ?? subscribedInstrument,
                quote?.Sequence ?? 0,
                "The quote has invalid identity, ordering metadata, prices, sizes, or UTC event time.");
            return false;
        }

        var cursor = _cursors[subscribedInstrument];
        if (quote.Sequence <= cursor.Sequence || quote.EventTimeUtc < cursor.EventTimeUtc)
        {
            fault = new PaperMarketDataBridgeFault(
                PaperMarketDataBridgeFaultKind.StaleOrDuplicateQuote,
                quote.InstrumentId,
                quote.Sequence,
                "The quote sequence or event time does not advance the Paper market cursor.");
            return false;
        }

        try
        {
            DepthSnapshot? depth = null;
            if (_venue.FillFidelity.EnableL2BookWalk)
            {
                // L1 quotes alone: reconstruct a finite ladder so book-walk has levels.
                // Explicit L2 remains preferred when a real DepthSnapshot is available upstream.
                depth = TradingTerminal.Core.Backtesting.L2BookReconstructionV1.FromL1(
                    quote.EventTimeUtc,
                    new Tick(quote.EventTimeUtc, quote.Bid, quote.Ask, quote.BidSize, quote.AskSize));
            }

            snapshot = new PaperMarketSnapshot(
                quote.InstrumentId,
                ExecutionNumericBoundary.PriceFromDouble(quote.Bid, _priceScale),
                ExecutionNumericBoundary.PriceFromDouble(quote.Ask, _priceScale),
                ScaledQuantity.FromWhole(quote.BidSize),
                ScaledQuantity.FromWhole(quote.AskSize),
                new DateTimeOffset(quote.EventTimeUtc),
                depth);
            return true;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            fault = new PaperMarketDataBridgeFault(
                PaperMarketDataBridgeFaultKind.NumericConversionFailed,
                quote.InstrumentId,
                quote.Sequence,
                exception.Message);
            return false;
        }
    }

    private void NotifyResult(OmsCommandResult result)
    {
        try
        {
            _resultObserver?.Invoke(result);
        }
        catch
        {
            // Observers are diagnostic only and cannot alter Paper execution or callback commits.
        }
    }

    private void RecordFault(PaperMarketDataBridgeFault fault)
    {
        Volatile.Write(ref _lastFault, fault);
        try
        {
            _faultObserver?.Invoke(fault);
        }
        catch
        {
            // Observers are diagnostic only and cannot interrupt the market-data subscription.
        }
    }

    private void LatchExecutionFault(PaperMarketDataBridgeFault fault)
    {
        Interlocked.Exchange(ref _executionFaulted, 1);
        RecordFault(fault);
    }

    private static ScaledPrice Midpoint(ScaledPrice bid, ScaledPrice ask)
    {
        var value = checked(
            (ExecutionNumericBoundary.ToDecimal(bid) + ExecutionNumericBoundary.ToDecimal(ask)) / 2m);
        if (!ExecutionNumericBoundary.TryPriceFromDecimal(value, out var midpoint) ||
            !midpoint.IsValid || midpoint.Coefficient <= 0)
        {
            throw new OverflowException("The exact Paper quote midpoint cannot be represented.");
        }
        return midpoint;
    }

    private readonly record struct QuoteCursor(
        long Sequence,
        DateTime EventTimeUtc,
        ScaledPrice ReferencePrice);

    private sealed class QuoteObserver(
        Action<Quote> onNext,
        Action<Exception> onError) : IObserver<Quote>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => onError(error);
        public void OnNext(Quote value) => onNext(value);
    }
}
