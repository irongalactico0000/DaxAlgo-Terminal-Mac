using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// Snapshot book-walk fill (Validate liquidity-walk v1). Walks opposite levels until
/// quantity or limit is exhausted. Not Nautilus matching, not MBO/queue priority.
/// </summary>
public static class L2BookWalkFillV1
{
    public readonly record struct Result(
        long FilledQuantity,
        long UnfilledQuantity,
        double AverageFillPrice,
        int LevelsConsumed);

    /// <summary>
    /// Walk asks (buys) or bids (sells). Asks must be ascending; bids descending (best first).
    /// When <paramref name="limitPrice"/> is set, skip levels beyond the limit.
    /// </summary>
    public static Result Walk(
        bool isBuy,
        long quantity,
        IReadOnlyList<DepthLevel> oppositeLevels,
        double? limitPrice = null)
    {
        if (quantity <= 0 || oppositeLevels is null || oppositeLevels.Count == 0)
            return new Result(0, Math.Max(0, quantity), 0, 0);

        long filled = 0;
        double notional = 0;
        var levels = 0;

        foreach (var level in oppositeLevels)
        {
            if (level.Size <= 0 || level.Price <= 0)
                continue;

            if (limitPrice is { } limit)
            {
                if (isBuy && level.Price > limit)
                    break;
                if (!isBuy && level.Price < limit)
                    break;
            }

            var take = Math.Min(level.Size, quantity - filled);
            if (take <= 0)
                break;

            filled += take;
            notional += take * level.Price;
            levels++;
            if (filled >= quantity)
                break;
        }

        var avg = filled > 0 ? notional / filled : 0d;
        return new Result(filled, quantity - filled, avg, levels);
    }

    /// <summary>
    /// Remove <paramref name="filledQuantity"/> from the opposite side so a later order
    /// cannot re-consume the same snapshot liquidity. Levels beyond the limit are kept.
    /// </summary>
    public static IReadOnlyList<DepthLevel> Consume(
        long filledQuantity,
        IReadOnlyList<DepthLevel> oppositeLevels,
        double? limitPrice,
        bool isBuy)
    {
        if (oppositeLevels is null || oppositeLevels.Count == 0)
            return Array.Empty<DepthLevel>();
        if (filledQuantity <= 0)
            return oppositeLevels;

        var remaining = filledQuantity;
        var kept = new List<DepthLevel>(oppositeLevels.Count);
        foreach (var level in oppositeLevels)
        {
            if (remaining <= 0 || level.Size <= 0)
            {
                kept.Add(level);
                continue;
            }

            if (limitPrice is { } limit)
            {
                var beyond = isBuy ? level.Price > limit : level.Price < limit;
                if (beyond)
                {
                    kept.Add(level);
                    continue;
                }
            }

            if (level.Size <= remaining)
            {
                remaining -= level.Size;
                continue;
            }

            kept.Add(level with { Size = level.Size - remaining });
            remaining = 0;
        }

        return kept;
    }

    /// <summary>Canonical IOC fixture from Validate workstream docs.</summary>
    public static Result CanonicalIocBuyLimit100At100_01()
    {
        // Asks: 30 @ 100.00, 50 @ 100.01, more above limit (ignored).
        DepthLevel[] asks =
        [
            new(100.00, 30),
            new(100.01, 50),
            new(100.02, 1000),
        ];
        return Walk(isBuy: true, quantity: 100, oppositeLevels: asks, limitPrice: 100.01);
    }
}
