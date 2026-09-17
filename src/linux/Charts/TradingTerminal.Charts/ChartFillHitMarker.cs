namespace TradingTerminal.Charts;

/// <summary>
/// Simulated fill marker for Validate overlay — drawn at price (circle), separate from condition triangles.
/// </summary>
public readonly record struct ChartFillHitMarker(
    DateTimeOffset TimeUtc,
    double Price,
    bool IsEntry,
    bool IsBuy);
