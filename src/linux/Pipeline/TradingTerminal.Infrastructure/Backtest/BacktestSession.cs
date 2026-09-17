using System.Reactive.Linq;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Risk;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest.Persistence;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Drives a single backtest end-to-end:
///   1. Set up the simulated clock, fill model, order book, and router.
///   2. Forward order events to the strategy and track fills for the trade ledger.
///   3. Iterate completed bars, a parquet stream, or the canonical local store: advance the clock,
///      run the order book, sample equity at most once per minute, and dispatch the matching quote,
///      trade, or completed-bar callback.
///   4. After the last replay event, flush the final equity point and close out the strategy.
///
/// Single-threaded by design — the engine has one logical timeline and we keep all state
/// transitions on the caller's task. Concurrency belongs at the parameter-sweep layer
/// (run N sessions in parallel), not inside a single session.
/// </summary>
public sealed class BacktestSession : IBacktestSession
{
    private readonly IMarketDataStore? _store;

    /// <summary>Parameterless ctor for parquet-only callers (CLI, existing tests).
    /// LocalStore sources will throw at run time if invoked through this ctor.</summary>
    public BacktestSession() : this(store: null) { }

    /// <summary>Preferred ctor for DI: supplies the canonical store so the engine can
    /// replay from it when <see cref="BacktestConfig.Source"/> is
    /// <see cref="BacktestDataSource.LocalStore"/>.</summary>
    public BacktestSession(IMarketDataStore? store)
    {
        _store = store;
    }

    public Task<BacktestResult> RunAsync(
        BacktestConfig config,
        IBacktestStrategy strategy,
        CancellationToken ct = default) => RunAsync(config, strategy, risk: null, ct);

    public async Task<BacktestResult> RunAsync(
        BacktestConfig config,
        IBacktestStrategy strategy,
        IRiskManager? risk,
        CancellationToken ct = default)
    {
        if (config.ReplayBarSeries is { Count: > 1 } && strategy is not IInstrumentAwareBacktestStrategy)
        {
            throw new InvalidOperationException(
                "Multi-instrument replay requires an instrument-aware backtest strategy.");
        }
        if (config.LatencyMs < 0)
            throw new ArgumentOutOfRangeException(nameof(config), "LatencyMs must be ≥ 0.");
        if (config.MaxFillQuantityPerTouch < 0)
            throw new ArgumentOutOfRangeException(nameof(config), "MaxFillQuantityPerTouch must be ≥ 0.");

        var clock = new SimulatedClock();
        var fillModel = new L1FillModel(
            config.TickSize,
            config.SlippageTicks,
            config.MaxFillQuantityPerTouch,
            config.CapToOppositeL1Size);
        var latency = TimeSpan.FromMilliseconds(config.LatencyMs);
        var orderBook = new SimulatedOrderBook(clock, fillModel, latency);
        var router = new BacktestOrderRouter(orderBook, risk, clock);

        var ledger = new TradeLedger(config.ContractMultiplier, config.StartingCash, config.FeeModel);
        var equity = new List<EquityPoint>();
        var fills = new List<FillRecord>();
        DateTime? lastSample = null;
        var lastTicks = new Dictionary<Contract, Tick>();
        var marks = new Dictionary<Contract, double>();
        var multipliers = (config.ReplayBarSeries ?? [])
            .ToDictionary(series => series.Contract, series => series.ContractMultiplier);
        if (!multipliers.ContainsKey(config.Contract))
            multipliers[config.Contract] = config.ContractMultiplier;

        var orderEventTask = Task.CompletedTask;
        using var sub = router.OrderEvents.Subscribe(evt =>
        {
            if (evt.LastFillQuantity > 0 && evt.LastFillPrice is { } px)
            {
                if (!router.TryGetContract(evt.ClientOrderId, out var contract) || contract is null)
                    throw new InvalidOperationException($"No contract is bound to fill {evt.ClientOrderId}.");
                var multiplier = multipliers.TryGetValue(contract, out var configuredMultiplier)
                    ? configuredMultiplier
                    : config.ContractMultiplier;
                ledger.OnFill(contract, evt.TimestampUtc, evt.Side, evt.LastFillQuantity, px, evt.Liquidity, multiplier);
                var mid = lastTicks.TryGetValue(contract, out var lt) ? (lt.Bid + lt.Ask) * 0.5 : px;
                fills.Add(new FillRecord(
                    TimestampUtc: evt.TimestampUtc,
                    ClientOrderId: evt.ClientOrderId,
                    Side: evt.Side,
                    Quantity: evt.LastFillQuantity,
                    Price: px,
                    MidAtFill: mid,
                    Liquidity: evt.Liquidity,
                    Symbol: contract.Symbol));
            }

            orderEventTask = orderEventTask.ContinueWith(
                _ => strategy.OnOrderEventAsync(evt, ct),
                ct,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        });

        await strategy.OnStartAsync(clock, router, ct).ConfigureAwait(false);

        var batch = new List<BacktestEvent>();
        DateTime? batchTimestamp = null;
        await foreach (var evt in BacktestTickSource.Resolve(config, _store, ct))
        {
            if (batchTimestamp is { } timestamp && evt.TimestampUtc != timestamp)
            {
                await ProcessBatchAsync(batch, timestamp).ConfigureAwait(false);
                batch.Clear();
            }
            batchTimestamp = evt.TimestampUtc;
            batch.Add(evt);
        }
        if (batchTimestamp is { } finalTimestamp)
            await ProcessBatchAsync(batch, finalTimestamp).ConfigureAwait(false);

        await orderEventTask.ConfigureAwait(false);
        await strategy.OnEndAsync(clock, router, ct).ConfigureAwait(false);

        if (lastTicks.Count > 0 && batchTimestamp is { } finalEventTime)
            equity.Add(new EquityPoint(finalEventTime, ledger.Equity(marks)));

        var endingCash = lastTicks.Count > 0 ? ledger.Equity(marks) : config.StartingCash;

        var bare = new BacktestResult(
            ledger.Trades, equity, config.StartingCash, endingCash,
            TotalFees: ledger.TotalFees,
            Fills: fills,
            Signals: router.Signals,
            EndingPositions: ledger.Positions);
        return bare with { Stats = StatisticsCalculator.Calculate(bare) };

        async Task ProcessBatchAsync(IReadOnlyList<BacktestEvent> events, DateTime timestamp)
        {
            clock.SetTo(timestamp);
            await orderEventTask.ConfigureAwait(false);

            var sawQuote = false;
            foreach (var replayEvent in events.Where(item => item.Quote is not null))
            {
                var tick = replayEvent.Quote!;
                lastTicks[replayEvent.Contract] = tick;
                marks[replayEvent.Contract] = (tick.Bid + tick.Ask) * 0.5d;
                orderBook.OnTick(replayEvent.Contract, tick);
                sawQuote = true;
            }
            await orderEventTask.ConfigureAwait(false);

            if (strategy is IInstrumentAwareBacktestStrategy aware)
            {
                await aware.OnMarketEventBatchAsync(
                    events.Select(item => item.ToPublic()).ToArray(),
                    clock,
                    router,
                    ct).ConfigureAwait(false);
            }
            else
            {
                foreach (var replayEvent in events)
                {
                    if (replayEvent.Quote is { } tick)
                        await strategy.OnTickAsync(tick, clock, router, ct).ConfigureAwait(false);
                    else if (replayEvent.Trade is { } trade)
                        await strategy.OnTradeAsync(trade, clock, router, ct).ConfigureAwait(false);
                    else if (replayEvent.Bar is { } bar)
                        await strategy.OnBarAsync(bar, clock, router, ct).ConfigureAwait(false);
                }
            }

            if (sawQuote && (lastSample is null || (timestamp - lastSample.Value).TotalSeconds >= 60))
            {
                equity.Add(new EquityPoint(timestamp, ledger.Equity(marks)));
                lastSample = timestamp;
            }
        }
    }
}
