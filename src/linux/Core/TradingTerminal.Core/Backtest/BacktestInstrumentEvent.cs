using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// A replay payload whose canonical instrument and broker contract remain attached. Exactly one of
/// <see cref="Quote"/>, <see cref="Trade"/>, <see cref="Bar"/>, or <see cref="Depth"/> is present.
/// </summary>
public sealed record BacktestInstrumentEvent(
    InstrumentId InstrumentId,
    Contract Contract,
    DateTime TimestampUtc,
    Tick? Quote = null,
    TradePrint? Trade = null,
    Bar? Bar = null,
    BarSize? BarSize = null,
    DepthSnapshot? Depth = null);

/// <summary>
/// Optional engine contract for strategies that require canonical identity or multiple replay
/// instruments. The engine supplies every event at one timestamp as a batch. Implementations may
/// preload the complete batch before invoking strategy callbacks, so all completed bars at the same
/// UTC boundary are visible together without exposing a later timestamp.
/// </summary>
public interface IInstrumentAwareBacktestStrategy
{
    Task OnMarketEventBatchAsync(
        IReadOnlyList<BacktestInstrumentEvent> events,
        Time.IClock clock,
        Trading.IOrderRouter router,
        CancellationToken ct);
}
