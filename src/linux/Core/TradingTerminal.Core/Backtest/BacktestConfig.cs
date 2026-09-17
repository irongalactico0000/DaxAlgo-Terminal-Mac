using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtest;

/// <summary>Where the engine pulls tick data from for a single backtest run.</summary>
public enum BacktestDataSource
{
    /// <summary>Replay a parquet file at <see cref="BacktestConfig.TickDataPath"/>
    /// (legacy / portable path). Used by recorder output, synth output, and shipped tape.</summary>
    ParquetFile = 0,

    /// <summary>Replay quotes from the canonical local store, scoped to
    /// <see cref="BacktestConfig.InstrumentId"/> + <see cref="BacktestConfig.FromUtc"/>/<see cref="BacktestConfig.ToUtc"/>.
    /// Lets you backtest whatever the live pipeline has captured without an explicit recording step.</summary>
    LocalStore = 1,
}

/// <summary>
/// Inputs for a single backtest run. <see cref="ContractMultiplier"/> scales price moves
/// to dollars (e.g. 50 for ES, 1 for stocks); <see cref="SlippageTicks"/> is added to the
/// touch on market fills to model crossing the spread under load. <see cref="FeeModel"/>
/// is consulted on every fill; defaults to <see cref="ZeroFeeModel"/> so legacy backtests
/// reproduce exactly.
///
/// Tick source: when <see cref="Source"/> is <see cref="BacktestDataSource.ParquetFile"/>
/// (default), the engine reads <see cref="TickDataPath"/>. When <see cref="BacktestDataSource.LocalStore"/>,
/// it streams quotes from the canonical store for <see cref="InstrumentId"/> in
/// [<see cref="FromUtc"/>, <see cref="ToUtc"/>) — both bounds are required for store mode.
///
/// <see cref="Broker"/> scopes <see cref="BacktestDataSource.LocalStore"/> reads to a single broker's
/// data when the store is split per broker; <c>null</c> reads every broker's data merged (the legacy
/// behaviour, and the only sensible default for the single-file backend).
///
/// <see cref="ReplayBars"/> bypasses the file/store source and replays the supplied completed OHLCV
/// bars using <see cref="ReplayBarSize"/>. The engine preserves each bar callback and derives only
/// the deterministic L1 observations required by its existing fill model.
/// <see cref="ReplayBarSeries"/> is the canonical multi-instrument form. Every series retains its
/// reviewed instrument and contract identity; the engine merges them on one UTC timeline without
/// forward-filling a missing bar.
/// </summary>
public sealed record BacktestConfig(
    Contract Contract,
    string TickDataPath,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    double TickSize = 0.25,
    int SlippageTicks = 0,
    double ContractMultiplier = 1.0,
    double StartingCash = 100_000d,
    IFeeModel? FeeModel = null,
    BacktestDataSource Source = BacktestDataSource.ParquetFile,
    InstrumentId InstrumentId = default,
    BrokerKind? Broker = null,
    string? TradeDataPath = null,
    IReadOnlyList<Bar>? ReplayBars = null,
    BarSize? ReplayBarSize = null,
    IReadOnlyList<BacktestBarSeries>? ReplayBarSeries = null,
    // Simulated delay after submit before L1 fills may occur (ms). 0 = immediate.
    double LatencyMs = 0,
    // When > 0, each L1 touch fills at most this many units (partials). 0 = no fixed ceiling.
    long MaxFillQuantityPerTouch = 0,
    // Cap each fill to opposite L1 size (AskSize buys / BidSize sells). Proxy only — not queue walk.
    bool CapToOppositeL1Size = false);

/// <summary>
/// One reviewed instrument's completed-bar history in a multi-series replay. Tick size and contract
/// multiplier are carried per instrument so fills and account valuation cannot borrow another leg's
/// market conventions.
/// </summary>
public sealed record BacktestBarSeries(
    InstrumentId InstrumentId,
    Contract Contract,
    BarSize BarSize,
    IReadOnlyList<Bar> Bars,
    double TickSize,
    double ContractMultiplier = 1d);
