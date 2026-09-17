namespace TradingTerminal.Charts;

/// <summary>One bar where a research/Design condition fired — drawn on <see cref="NativeChartSurface"/>.</summary>
public readonly record struct ChartConditionHitMarker(DateTimeOffset TimeUtc, bool ForwardPositive);
