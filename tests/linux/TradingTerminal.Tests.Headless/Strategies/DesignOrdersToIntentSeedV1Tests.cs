using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class DesignOrdersToIntentSeedV1Tests
{
    [Fact]
    public void Market_day_seeds_tradeir_compatible_execution_rows()
    {
        var seeds = DesignOrdersToIntentSeedV1.BuildSeeds(new DesignOrdersToIntentSeedV1.DesignOrdersSeedInput(
            "Long",
            "Market · 시장가",
            "Day",
            null));

        Assert.Contains(DesignOrdersToIntentSeedV1.RequirementOrderType, seeds.Keys);
        Assert.Contains(DesignOrdersToIntentSeedV1.RequirementTimeInForce, seeds.Keys);
        Assert.Contains(DesignOrdersToIntentSeedV1.RequirementMarketPolicy, seeds.Keys);
        Assert.Contains(DesignOrdersToIntentSeedV1.RequirementOrderPolicy, seeds.Keys);
        Assert.DoesNotContain(DesignOrdersToIntentSeedV1.RequirementLimitPolicy, seeds.Keys);
        Assert.Contains("Market", seeds[DesignOrdersToIntentSeedV1.RequirementOrderType], StringComparison.Ordinal);
        Assert.Contains("time_in_force=day", seeds[DesignOrdersToIntentSeedV1.RequirementTimeInForce], StringComparison.Ordinal);
        Assert.Contains("long-only", seeds[DesignOrdersToIntentSeedV1.RequirementMarketPolicy], StringComparison.Ordinal);
    }

    [Fact]
    public void Limit_fok_stays_intent_only_with_gap_notes()
    {
        var seeds = DesignOrdersToIntentSeedV1.BuildSeeds(new DesignOrdersToIntentSeedV1.DesignOrdersSeedInput(
            "Both (flip)",
            "Limit · 지정가",
            "FOK",
            "Mid"));

        Assert.Contains("Limit", seeds[DesignOrdersToIntentSeedV1.RequirementOrderType], StringComparison.Ordinal);
        Assert.Contains("TradeIR", seeds[DesignOrdersToIntentSeedV1.RequirementOrderType], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOK", seeds[DesignOrdersToIntentSeedV1.RequirementTimeInForce], StringComparison.Ordinal);
        Assert.Contains("intent", seeds[DesignOrdersToIntentSeedV1.RequirementLimitPolicy], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flip", seeds[DesignOrdersToIntentSeedV1.RequirementOrderPolicy], StringComparison.OrdinalIgnoreCase);
        Assert.Null(DesignOrdersToIntentSeedV1.MapTimeInForceForTradeIr("FOK", out var gap));
        Assert.Contains("FOK", gap, StringComparison.Ordinal);
    }
}
