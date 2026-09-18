using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// A single replay event the engine consumes: a quote update, trade print, completed bar,
/// or L2 depth snapshot. Modelled as a struct with nullable reference fields so the backtester
/// avoids per-event boxing/allocation at the scale of tens of millions of events per run.
/// Exactly one of Quote / Trade / Bar / Depth is non-null.
/// </summary>
internal readonly record struct BacktestEvent(
    InstrumentId InstrumentId,
    Contract Contract,
    DateTime TimestampUtc,
    Tick? Quote,
    TradePrint? Trade,
    Bar? Bar,
    BarSize? BarSize,
    DepthSnapshot? Depth = null)
{
    public static BacktestEvent FromQuote(InstrumentId instrument, Contract contract, Tick q) =>
        new(instrument, contract, q.TimestampUtc, q, null, null, null, null);

    public static BacktestEvent FromTrade(InstrumentId instrument, Contract contract, TradePrint t) =>
        new(instrument, contract, t.EventTimeUtc, null, t, null, null, null);

    public static BacktestEvent FromBar(
        InstrumentId instrument,
        Contract contract,
        BarSize barSize,
        Bar bar,
        DateTime completedAtUtc) =>
        new(instrument, contract, completedAtUtc, null, null, bar, barSize, null);

    public static BacktestEvent FromDepth(InstrumentId instrument, Contract contract, DepthSnapshot depth) =>
        new(instrument, contract, depth.TimestampUtc, null, null, null, null, depth);

    public BacktestInstrumentEvent ToPublic() => new(
        InstrumentId, Contract, TimestampUtc, Quote, Trade, Bar, BarSize, Depth);
}

/// <summary>
/// Internal seam over the replay sources the engine can consume: an exact completed-bar list,
/// a parquet file, or the canonical local store (quotes + trades merged by event time).
/// <see cref="BacktestSession"/> picks the right concrete via
/// <see cref="Resolve"/> based on the config — callers don't construct sources directly.
/// </summary>
internal static class BacktestTickSource
{
    /// <summary>Yields the event stream the engine should replay for this config. Completed-bar
    /// input preserves exact broker OHLCV callbacks and adds deterministic quotes for the existing
    /// fill model. Other sources replay quotes and optional trades by event time.</summary>
    public static IAsyncEnumerable<BacktestEvent> Resolve(BacktestConfig config, IMarketDataStore? store, CancellationToken ct)
    {
        if (config.ReplayBarSeries is { Count: > 0 } && config.ReplayBars is not null)
            throw new InvalidOperationException("Configure ReplayBars or ReplayBarSeries, not both.");
        if (config.ReplayBarSeries is { Count: > 0 } series)
            return ReadBarSeries(series, ct);
        if (config.ReplayBars is { } bars)
            return ReadBars(config, bars, ct);

        return config.Source switch
        {
            BacktestDataSource.LocalStore => ReadFromStore(config, store
                ?? throw new InvalidOperationException(
                    "BacktestConfig.Source = LocalStore but no IMarketDataStore was supplied to the session."), ct),
            _ => ReadFromParquet(config, ct),
        };
    }

    /// <summary>
    /// Replays exact historical bars while also producing the four deterministic synthetic L1
    /// observations used by the existing fill model. The completed bar follows its close quote,
    /// so a target emitted by <c>OnBarAsync</c> can fill only on subsequent market data.
    /// </summary>
    private static async IAsyncEnumerable<BacktestEvent> ReadBars(
        BacktestConfig config,
        IReadOnlyList<Bar> bars,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (config.ReplayBarSize is not { } barSize)
            throw new InvalidOperationException("ReplayBars requires ReplayBarSize.");

        var series = new BacktestBarSeries(
            config.InstrumentId,
            config.Contract,
            barSize,
            bars,
            config.TickSize,
            config.ContractMultiplier);
        await foreach (var replayEvent in ReadBarSeries([series], ct))
            yield return replayEvent;
    }

    private static async IAsyncEnumerable<BacktestEvent> ReadBarSeries(
        IReadOnlyList<BacktestBarSeries> series,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ValidateSeriesSet(series);
        var enumerators = series
            .OrderBy(value => value.InstrumentId.Value)
            .Select(item => EnumerateSeries(item).GetEnumerator())
            .ToArray();
        var active = new bool[enumerators.Length];
        try
        {
            for (var index = 0; index < enumerators.Length; index++)
                active[index] = enumerators[index].MoveNext();

            while (active.Any(value => value))
            {
                ct.ThrowIfCancellationRequested();
                var nextIndex = -1;
                for (var index = 0; index < enumerators.Length; index++)
                {
                    if (!active[index]) continue;
                    if (nextIndex < 0 || Compare(enumerators[index].Current, enumerators[nextIndex].Current) < 0)
                        nextIndex = index;
                }
                yield return enumerators[nextIndex].Current;
                active[nextIndex] = enumerators[nextIndex].MoveNext();
                await Task.Yield();
            }
        }
        finally
        {
            foreach (var enumerator in enumerators) enumerator.Dispose();
        }
    }

    private static IEnumerable<BacktestEvent> EnumerateSeries(BacktestBarSeries item)
    {
        var span = item.BarSize.ToTimeSpan();
        if (span <= TimeSpan.Zero)
            throw new InvalidOperationException("Every replay bar size must resolve to a positive duration.");
        var halfSpread = Math.Max(item.TickSize, 1e-9d) * 0.5d;
        var step = span / 4;
        DateTime? previousTimestamp = null;
        foreach (var bar in item.Bars)
        {
            ValidateReplayBar(bar, previousTimestamp);
            previousTimestamp = bar.TimestampUtc;
            var path = bar.Close >= bar.Open
                ? new[] { bar.Open, bar.Low, bar.High, bar.Close }
                : new[] { bar.Open, bar.High, bar.Low, bar.Close };
            var size = Math.Max(1L, bar.Volume / 4L);
            DateTime closeTimestamp = bar.TimestampUtc;
            for (var index = 0; index < path.Length; index++)
            {
                var timestamp = bar.TimestampUtc + (step * index);
                closeTimestamp = timestamp;
                var price = path[index];
                yield return BacktestEvent.FromQuote(
                    item.InstrumentId,
                    item.Contract,
                    new Tick(timestamp, price - halfSpread, price + halfSpread, size, size));
            }
            yield return BacktestEvent.FromBar(
                item.InstrumentId, item.Contract, item.BarSize, bar, closeTimestamp);
        }
    }

    private static int Compare(BacktestEvent left, BacktestEvent right)
    {
        var timestamp = left.TimestampUtc.CompareTo(right.TimestampUtc);
        if (timestamp != 0) return timestamp;
        var kind = EventKindOrder(left).CompareTo(EventKindOrder(right));
        if (kind != 0) return kind;
        var instrument = left.InstrumentId.Value.CompareTo(right.InstrumentId.Value);
        return instrument != 0
            ? instrument
            : string.Compare(left.Contract.Symbol, right.Contract.Symbol, StringComparison.Ordinal);
    }

    private static void ValidateSeriesSet(IReadOnlyList<BacktestBarSeries> series)
    {
        if (series.Count == 0)
            throw new InvalidOperationException("ReplayBarSeries cannot be empty.");
        if (series.Count > 1 && series.Any(item => item.InstrumentId.IsNone))
            throw new InvalidOperationException("Every replay series requires a canonical instrument.");
        if (series.Any(item => item.Contract is null || item.Bars is null || item.Bars.Count == 0))
            throw new InvalidOperationException("Every replay series requires a contract and at least one bar.");
        if (series.Any(item => !double.IsFinite(item.TickSize) || item.TickSize <= 0d))
            throw new InvalidOperationException("Every replay series requires a finite positive tick size.");
        if (series.Any(item => !double.IsFinite(item.ContractMultiplier) || item.ContractMultiplier <= 0d))
            throw new InvalidOperationException("Every replay series requires a finite positive contract multiplier.");
        if (series.Select(item => item.InstrumentId).Distinct().Count() != series.Count)
            throw new InvalidOperationException("Replay series instruments must be unique.");
        if (series.Select(item => item.Contract).Distinct().Count() != series.Count)
            throw new InvalidOperationException("Replay series contracts must be unique.");
    }

    private static int EventKindOrder(BacktestEvent value) =>
        value.Depth is not null ? 0 :
        value.Quote is not null ? 1 :
        value.Trade is not null ? 2 : 3;

    private static void ValidateReplayBar(Bar bar, DateTime? previousTimestamp)
    {
        if (bar.TimestampUtc.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("Replay bars must use UTC timestamps.");
        if (previousTimestamp is { } previous && bar.TimestampUtc <= previous)
            throw new InvalidOperationException("Replay bars must be strictly increasing with no duplicate timestamps.");
        if (!double.IsFinite(bar.Open) || !double.IsFinite(bar.High) ||
            !double.IsFinite(bar.Low) || !double.IsFinite(bar.Close) ||
            bar.Open <= 0d || bar.High <= 0d || bar.Low <= 0d || bar.Close <= 0d)
        {
            throw new InvalidOperationException("Replay bars require finite positive OHLC prices.");
        }
        if (bar.High < Math.Max(bar.Open, bar.Close) ||
            bar.Low > Math.Min(bar.Open, bar.Close) ||
            bar.Low > bar.High)
        {
            throw new InvalidOperationException("Replay bar OHLC values are inconsistent.");
        }
        if (bar.Volume < 0L)
            throw new InvalidOperationException("Replay bar volume cannot be negative.");
    }

    private static IAsyncEnumerable<BacktestEvent> ReadFromParquet(
        BacktestConfig config, CancellationToken ct)
    {
        // Quote-only (legacy) unless an optional trade tape is supplied alongside; then merge both by
        // event time so trade-tape-primary strategies replay genuine prints (not synthetic L1).
        return string.IsNullOrWhiteSpace(config.TradeDataPath)
            ? ReadQuotesOnly(config, ct)
            : ReadQuotesAndTrades(config, ct);
    }

    private static async IAsyncEnumerable<BacktestEvent> ReadQuotesOnly(
        BacktestConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var t in ReadQuotes(config.TickDataPath, config.FromUtc, config.ToUtc, ct))
            yield return BacktestEvent.FromQuote(config.InstrumentId, config.Contract, t);
    }

    // Route by extension: .csv → the portable CSV readers (external/Python-sourced data), else the
    // native parquet readers (recorder / synth / C#-written tape).
    private static bool IsCsv(string? path) =>
        path is not null && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static IAsyncEnumerable<Tick> ReadQuotes(string path, DateTime? from, DateTime? to, CancellationToken ct) =>
        IsCsv(path) ? CsvTickReader.ReadAsync(path, from, to, ct) : ParquetTickReader.ReadAsync(path, from, to, ct);

    private static IAsyncEnumerable<TradePrint> ReadTrades(string path, DateTime? from, DateTime? to, CancellationToken ct) =>
        IsCsv(path) ? CsvTradeReader.ReadAsync(path, from, to, ct) : ParquetTradeReader.ReadAsync(path, from, to, ct);

    /// <summary>
    /// Merges the quote parquet and an optional trade parquet by event time, mirroring the store
    /// merge (<see cref="ReadFromStore"/>). On a tie the quote is yielded first so the strategy's
    /// view of the spread is current when it sees the trade.
    /// </summary>
    private static async IAsyncEnumerable<BacktestEvent> ReadQuotesAndTrades(
        BacktestConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var qe = ReadQuotes(config.TickDataPath, config.FromUtc, config.ToUtc, ct).GetAsyncEnumerator(ct);
        await using var te = ReadTrades(config.TradeDataPath!, config.FromUtc, config.ToUtc, ct).GetAsyncEnumerator(ct);

        var hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
        var hasT = await te.MoveNextAsync().ConfigureAwait(false);
        while (hasQ || hasT)
        {
            if (hasQ && (!hasT || qe.Current.TimestampUtc <= te.Current.EventTimeUtc))
            {
                yield return BacktestEvent.FromQuote(config.InstrumentId, config.Contract, qe.Current);
                hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                yield return BacktestEvent.FromTrade(config.InstrumentId, config.Contract, te.Current);
                hasT = await te.MoveNextAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads quotes, trades, and depth from the store and merges them by event time.
    /// Depth is yielded before quotes at the same timestamp so the book is current for fills.
    /// </summary>
    private static async IAsyncEnumerable<BacktestEvent> ReadFromStore(
        BacktestConfig config, IMarketDataStore store,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (config.InstrumentId.IsNone)
            throw new InvalidOperationException("LocalStore backtest requires BacktestConfig.InstrumentId.");
        if (config.FromUtc is not { } from || config.ToUtc is not { } to)
            throw new InvalidOperationException("LocalStore backtest requires both FromUtc and ToUtc.");
        if (to <= from)
            throw new InvalidOperationException("LocalStore backtest requires ToUtc > FromUtc.");

        await using var qe = store.ReadQuotesAsync(config.InstrumentId, from, to, config.Broker, ct).GetAsyncEnumerator(ct);
        await using var te = store.ReadTradesAsync(config.InstrumentId, from, to, config.Broker, ct).GetAsyncEnumerator(ct);
        await using var de = store.ReadDepthAsync(config.InstrumentId, from, to, ct).GetAsyncEnumerator(ct);

        var hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
        var hasT = await te.MoveNextAsync().ConfigureAwait(false);
        var hasD = await de.MoveNextAsync().ConfigureAwait(false);
        while (hasQ || hasT || hasD)
        {
            var qTime = hasQ ? qe.Current.EventTimeUtc : DateTime.MaxValue;
            var tTime = hasT ? te.Current.EventTimeUtc : DateTime.MaxValue;
            var dTime = hasD ? de.Current.TimestampUtc : DateTime.MaxValue;
            var next = qTime;
            if (dTime <= next) next = dTime;
            if (tTime < next) next = tTime;

            if (hasD && dTime == next)
            {
                yield return BacktestEvent.FromDepth(config.InstrumentId, config.Contract, de.Current);
                hasD = await de.MoveNextAsync().ConfigureAwait(false);
            }
            else if (hasQ && qTime == next)
            {
                var q = qe.Current;
                yield return BacktestEvent.FromQuote(
                    config.InstrumentId,
                    config.Contract,
                    new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize));
                hasQ = await qe.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                yield return BacktestEvent.FromTrade(config.InstrumentId, config.Contract, te.Current);
                hasT = await te.MoveNextAsync().ConfigureAwait(false);
            }
        }
    }
}
