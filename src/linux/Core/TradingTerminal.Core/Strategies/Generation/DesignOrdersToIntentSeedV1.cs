using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Maps Design ORDERS (Side · Market/Limit · TIF · price rule) into ConfirmedStrategyIntent
/// requirement answers. TradeIR today only compiles Market + Day/GTC/IOC — Limit/FOK stay
/// intent prose with an explicit gap note.
/// </summary>
public static class DesignOrdersToIntentSeedV1
{
    public const string RequirementOrderType = "execution.order_type_selection";
    public const string RequirementTimeInForce = "execution.time_in_force";
    public const string RequirementMarketPolicy = "execution.market_policy";
    public const string RequirementLimitPolicy = "execution.limit_policy";
    public const string RequirementOrderPolicy = "execution.order_policy";

    public sealed record DesignOrdersSeedInput(
        string Side,
        string OrderType,
        string TimeInForce,
        string? PriceRule);

    /// <summary>RequirementId → CanonicalValue prose for Applicable rows.</summary>
    public static IReadOnlyDictionary<string, string> BuildSeeds(DesignOrdersSeedInput input)
    {
        var seeds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(input.Side) ||
            string.IsNullOrWhiteSpace(input.OrderType) ||
            string.IsNullOrWhiteSpace(input.TimeInForce))
            return seeds;

        var isLimit = input.OrderType.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
                      input.OrderType.Contains("지정가", StringComparison.Ordinal);
        var isMarket = !isLimit &&
                       (input.OrderType.Contains("Market", StringComparison.OrdinalIgnoreCase) ||
                        input.OrderType.Contains("시장가", StringComparison.Ordinal));

        var tifTradeIr = MapTimeInForceForTradeIr(input.TimeInForce, out var tifGap);
        var sideClause = MapSideClause(input.Side);

        seeds[RequirementOrderType] = isLimit
            ? $"Design ORDERS: Limit (지정가) for {sideClause}. " +
              "TradeIR market-node cannot encode Limit yet — keep as intent until Limit IR exists."
            : $"Design ORDERS: Market (시장가) for {sideClause}. " +
              "TradeIR uses execution.market.";

        seeds[RequirementTimeInForce] =
            $"Design ORDERS TIF={input.TimeInForce.Trim()}" +
            (string.IsNullOrWhiteSpace(tifTradeIr)
                ? " (no TradeIR literal — intent only)."
                : $" → TradeIR time_in_force={tifTradeIr}.") +
            (tifGap is null ? "" : $" {tifGap}");

        seeds[RequirementOrderPolicy] =
            $"Side {input.Side.Trim()}; order {input.OrderType.Trim()}; TIF {input.TimeInForce.Trim()}" +
            (string.IsNullOrWhiteSpace(input.PriceRule) ? "" : $"; price rule {input.PriceRule.Trim()}") +
            ".";

        if (isMarket)
        {
            seeds[RequirementMarketPolicy] =
                $"Market orders allowed for {sideClause}; TIF {input.TimeInForce.Trim()}" +
                (tifTradeIr is null ? "." : $" (TradeIR {tifTradeIr}).");
        }

        if (isLimit)
        {
            var price = string.IsNullOrWhiteSpace(input.PriceRule) ? "unspecified" : input.PriceRule.Trim();
            seeds[RequirementLimitPolicy] =
                $"Limit (지정가) price rule={price}; TIF {input.TimeInForce.Trim()}. " +
                "Not lowered into TradeIR market node — intent-only until Limit IR ships.";
        }

        return seeds;
    }

    /// <summary>TradeIR-accepted TIF literal, or null when unsupported.</summary>
    public static string? MapTimeInForceForTradeIr(string timeInForce, out string? gapNote)
    {
        gapNote = null;
        var t = timeInForce.Trim();
        if (string.Equals(t, "Day", StringComparison.OrdinalIgnoreCase))
            return "day";
        if (string.Equals(t, "GTC", StringComparison.OrdinalIgnoreCase))
            return "good_til_cancelled";
        if (string.Equals(t, "IOC", StringComparison.OrdinalIgnoreCase))
            return "immediate_or_cancel";
        if (string.Equals(t, "FOK", StringComparison.OrdinalIgnoreCase))
        {
            gapNote = "FOK has no TradeIR literal yet — prefer IOC in graph or keep intent-only.";
            return null;
        }

        gapNote = $"Unrecognized TIF '{t}' for TradeIR.";
        return null;
    }

    public static string MapSideClause(string side)
    {
        var s = side.Trim();
        if (s.Contains("Short", StringComparison.OrdinalIgnoreCase))
            return "short-only entries";
        if (s.Contains("Both", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("flip", StringComparison.OrdinalIgnoreCase))
            return "long and short (flip) entries";
        return "long-only entries";
    }

    public static string HashSeeds(IReadOnlyDictionary<string, string> seeds) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(
            seeds.OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => kv.Key + "=" + kv.Value)
                .ToArray());
}
