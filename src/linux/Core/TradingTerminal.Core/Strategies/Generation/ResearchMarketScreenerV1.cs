using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Cross-sectional ranking metric for Research Studio market screening.
/// Volume = instrument quantity; TradedValue = estimated money exchanged (Σ volume × close).
/// Exact traded value requires transaction values or a provider trade-value field.
/// </summary>
public enum ResearchScreenMetricV1
{
    TradingVolume = 0,
    TradedValue = 1,
    PercentChange = 2,
}

/// <summary>Research Studio market screen request.</summary>
/// <param name="UniverseId">Catalog id (e.g. sp100, registry).</param>
/// <param name="Metric">Ranking metric with an explicit definition.</param>
/// <param name="TopN">Maximum ranked results to return.</param>
/// <param name="LookbackBars">Completed bars required in the shared ranking window.</param>
/// <param name="RequiredBarSize">
/// Single bar size for every instrument. Mixing sizes (e.g. 1h with 1D) is not allowed —
/// instruments without enough history at this size are excluded with a reason.
/// </param>
/// <param name="HydrateMissingHistory">
/// When true, request connected-broker history for instruments that lack local bars
/// (same pattern as chart-pattern search).
/// </param>
/// <param name="MaxRemoteHydrations">Safety cap on remote history requests per Rank.</param>
public sealed record ResearchMarketScreenRequestV1(
    string UniverseId,
    ResearchScreenMetricV1 Metric,
    int TopN,
    int LookbackBars,
    BarSize RequiredBarSize = BarSize.OneHour,
    bool HydrateMissingHistory = true,
    int MaxRemoteHydrations = 120);

/// <summary>Why an instrument was omitted from the ranked results.</summary>
public sealed record ResearchMarketScreenExclusionV1(
    string CanonicalSymbol,
    string Reason);

/// <summary>One ranked instrument from a market screen.</summary>
public sealed record ResearchMarketScreenRowV1(
    int Rank,
    string CanonicalSymbol,
    string DisplayName,
    double MetricValue,
    string MetricLabel,
    string MetricDefinition,
    int BarsUsed,
    string BarSizeLabel,
    DateTime WindowFromUtc,
    DateTime WindowToUtcExclusive,
    double? VolumeSum,
    double? TradedValueSum,
    double? PercentChange);

/// <summary>Full screen result with coverage so Top N never invents missing instruments.</summary>
public sealed record ResearchMarketScreenResultV1(
    string UniverseId,
    string UniverseLabel,
    int RequestedUniverseSize,
    int InstrumentsInRegistry,
    int InstrumentsWithUsableHistory,
    int InstrumentsNotInRegistry,
    int HydrationAttempts,
    int HydrationSuccesses,
    ResearchScreenMetricV1 Metric,
    string MetricDefinition,
    int TopNRequested,
    DateTime CalculatedUtc,
    string BarSizeLabel,
    int LookbackBars,
    DateTime WindowFromUtc,
    DateTime WindowToUtcExclusive,
    string ResultHeader,
    IReadOnlyList<ResearchMarketScreenRowV1> Rows,
    IReadOnlyList<ResearchMarketScreenExclusionV1> Exclusions,
    string CoverageSummary,
    string DataProvenanceSummary);

public interface IResearchMarketScreenerV1
{
    Task<ResearchMarketScreenResultV1> ScreenAsync(
        ResearchMarketScreenRequestV1 request,
        CancellationToken cancellationToken = default);
}
