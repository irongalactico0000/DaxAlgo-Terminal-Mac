using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Specification;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// A user-facing starting point for Strategy Builder. The prompt is editable copy; the normalized
/// specification supplies stable discovery facets without pretending that a family name is a
/// mutually exclusive strategy type.
/// </summary>
public sealed record StrategyStarterBrief(
    string Id,
    string Title,
    string Summary,
    string Prompt,
    StrategySpec Classification,
    IReadOnlyList<string> SearchAliases)
{
    /// <summary>Overlapping navigation lenses derived from the normalized specification.</summary>
    public IReadOnlyList<string> FamilyLabels =>
        StrategyStarterTaxonomy.GetFamilyLabels(Classification);

    /// <summary>Short labels suitable for filters, chips, and compact result cards.</summary>
    public StrategyStarterAxisLabels AxisLabels =>
        StrategyStarterAxisLabels.From(Classification);
}

/// <summary>Stable family names exposed to the authoring UI.</summary>
public static class StrategyStarterFamilies
{
    public const string TrendAndMomentum = "Trend & momentum";
    public const string ReversionAndRelativeValue = "Reversion & relative value";
    public const string ValueCarryAndQuality = "Value, carry & quality";
    public const string OrderFlowAndLiquidity = "Order flow & liquidity";
    public const string EventsAndCatalysts = "Events & catalysts";
    public const string VolatilityAndDerivatives = "Volatility & derivatives";
    public const string AllocationAndHedging = "Allocation & hedging";
    public const string Execution = "Execution";
    public const string AdaptiveAndMl = "Adaptive & ML";

    public static IReadOnlyList<string> All { get; } =
    [
        TrendAndMomentum,
        ReversionAndRelativeValue,
        ValueCarryAndQuality,
        OrderFlowAndLiquidity,
        EventsAndCatalysts,
        VolatilityAndDerivatives,
        AllocationAndHedging,
        Execution,
        AdaptiveAndMl,
    ];
}

/// <summary>Projects overlapping strategy families from canonical, orthogonal axes.</summary>
public static class StrategyStarterTaxonomy
{
    public static IReadOnlyList<string> GetFamilyLabels(StrategySpec specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var hypotheses = specification.Signal?.Hypotheses ?? [];
        var triggers = specification.Signal?.Triggers ?? [];
        var models = specification.Signal?.Models ?? [];
        var information = specification.Context?.Information ?? [];
        var assetClasses = specification.Context?.AssetClasses ?? [];
        var labels = new List<string>();

        if (hypotheses.Contains(ReturnHypothesisKind.Momentum))
            labels.Add(StrategyStarterFamilies.TrendAndMomentum);

        if (hypotheses.Any(static hypothesis =>
                hypothesis is ReturnHypothesisKind.Reversal or ReturnHypothesisKind.Convergence))
            labels.Add(StrategyStarterFamilies.ReversionAndRelativeValue);

        if (hypotheses.Any(static hypothesis =>
                hypothesis is ReturnHypothesisKind.Value
                    or ReturnHypothesisKind.Carry
                    or ReturnHypothesisKind.Quality
                    or ReturnHypothesisKind.Seasonality))
        {
            labels.Add(StrategyStarterFamilies.ValueCarryAndQuality);
        }

        if (specification.Objective == StrategyObjectiveKind.LiquidityProvision ||
            hypotheses.Any(static hypothesis =>
                hypothesis is ReturnHypothesisKind.StructuralFlow
                    or ReturnHypothesisKind.LiquidityProvision))
        {
            labels.Add(StrategyStarterFamilies.OrderFlowAndLiquidity);
        }

        if (hypotheses.Contains(ReturnHypothesisKind.CatalystInformation) ||
            triggers.Any(static trigger =>
                trigger is StrategyTriggerKind.StructuredExternalEvent or StrategyTriggerKind.NewsEvent) ||
            information.Any(static input =>
                input is StrategyInformationKind.CorporateEvent or StrategyInformationKind.NewsText))
        {
            labels.Add(StrategyStarterFamilies.EventsAndCatalysts);
        }

        if (hypotheses.Contains(ReturnHypothesisKind.VolatilityInsurance) ||
            assetClasses.Contains(AssetClass.Option) ||
            information.Contains(StrategyInformationKind.ImpliedVolatilitySurface) ||
            specification.Context?.Exposure is ExposureGeometryKind.DeltaNeutral
                or ExposureGeometryKind.VolatilityExposure)
        {
            labels.Add(StrategyStarterFamilies.VolatilityAndDerivatives);
        }

        if (specification.Objective is StrategyObjectiveKind.Allocation
                or StrategyObjectiveKind.Hedging
                or StrategyObjectiveKind.BenchmarkTracking ||
            hypotheses.Any(static hypothesis =>
                hypothesis is ReturnHypothesisKind.MarketRiskPremium or ReturnHypothesisKind.Defensive))
        {
            labels.Add(StrategyStarterFamilies.AllocationAndHedging);
        }

        if (specification.Objective == StrategyObjectiveKind.Execution)
            labels.Add(StrategyStarterFamilies.Execution);

        if (models.Any(static model =>
                model is SignalModelKind.SupervisedMachineLearning
                    or SignalModelKind.OnlineLearning
                    or SignalModelKind.ReinforcementLearning
                    or SignalModelKind.Ensemble))
        {
            labels.Add(StrategyStarterFamilies.AdaptiveAndMl);
        }

        return labels;
    }
}

/// <summary>Human-readable projections of every canonical classification axis.</summary>
public sealed record StrategyStarterAxisLabels(
    string Objective,
    IReadOnlyList<string> AssetClasses,
    string Horizon,
    string Topology,
    string Exposure,
    IReadOnlyList<string> Data,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> Models,
    string Construction,
    IReadOnlyList<string> Risk,
    IReadOnlyList<string> Execution,
    IReadOnlyList<string> State,
    string Adaptation,
    string Regime)
{
    public static StrategyStarterAxisLabels From(StrategySpec specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return new StrategyStarterAxisLabels(
            StrategyStarterLabels.For(specification.Objective),
            specification.Context.AssetClasses.Select(StrategyStarterLabels.For).ToArray(),
            StrategyStarterLabels.For(specification.Context.Time.Horizon),
            StrategyStarterLabels.For(specification.Context.Topology),
            StrategyStarterLabels.For(specification.Context.Exposure),
            specification.Context.Information.Select(StrategyStarterLabels.For).ToArray(),
            specification.Signal.Hypotheses.Select(StrategyStarterLabels.For).ToArray(),
            specification.Signal.Triggers.Select(StrategyStarterLabels.For).ToArray(),
            specification.Signal.Models.Select(StrategyStarterLabels.For).ToArray(),
            StrategyStarterLabels.For(specification.Portfolio.Construction),
            specification.Risk.Rules.Select(StrategyStarterLabels.For).ToArray(),
            specification.Execution.Policies.Select(StrategyStarterLabels.For).ToArray(),
            specification.State.Policies.Select(StrategyStarterLabels.For).ToArray(),
            StrategyStarterLabels.For(specification.State.Adaptation),
            specification.State.Policies.Contains(StrategyStateKind.RegimeAware)
                ? "Regime-aware"
                : "Regime-agnostic");
    }
}

/// <summary>Concise display labels for normalized strategy values.</summary>
public static class StrategyStarterLabels
{
    public static string For(AssetClass value) => value switch
    {
        AssetClass.Equity => "Equities",
        AssetClass.Future => "Futures",
        AssetClass.Forex => "FX",
        AssetClass.Crypto => "Crypto",
        AssetClass.Option => "Options",
        AssetClass.Index => "Indexes",
        _ => "Unknown",
    };

    public static string For(StrategyObjectiveKind value) => value switch
    {
        StrategyObjectiveKind.ReturnSeeking => "Return seeking",
        StrategyObjectiveKind.Hedging => "Hedging",
        StrategyObjectiveKind.Allocation => "Allocation",
        StrategyObjectiveKind.BenchmarkTracking => "Benchmark tracking",
        StrategyObjectiveKind.LiquidityProvision => "Liquidity provision",
        StrategyObjectiveKind.Execution => "Execution",
        _ => value.ToString(),
    };

    public static string For(ReturnHypothesisKind value) => value switch
    {
        ReturnHypothesisKind.None => "No alpha thesis",
        ReturnHypothesisKind.MarketRiskPremium => "Market beta",
        ReturnHypothesisKind.Momentum => "Momentum",
        ReturnHypothesisKind.Reversal => "Mean reversion",
        ReturnHypothesisKind.Value => "Value",
        ReturnHypothesisKind.Carry => "Carry",
        ReturnHypothesisKind.Quality => "Quality",
        ReturnHypothesisKind.Defensive => "Defensive",
        ReturnHypothesisKind.Seasonality => "Seasonality",
        ReturnHypothesisKind.Convergence => "Convergence",
        ReturnHypothesisKind.CatalystInformation => "Catalyst",
        ReturnHypothesisKind.StructuralFlow => "Structural flow",
        ReturnHypothesisKind.LiquidityProvision => "Market making",
        ReturnHypothesisKind.VolatilityInsurance => "Volatility insurance",
        _ => value.ToString(),
    };

    public static string For(StrategyTriggerKind value) => value switch
    {
        StrategyTriggerKind.Quote => "Quotes",
        StrategyTriggerKind.Trade => "Trades",
        StrategyTriggerKind.Bar => "Bar close",
        StrategyTriggerKind.Depth => "Depth updates",
        StrategyTriggerKind.Schedule => "Schedule",
        StrategyTriggerKind.StructuredExternalEvent => "External events",
        StrategyTriggerKind.NewsEvent => "News",
        StrategyTriggerKind.OrderEvent => "Order events",
        StrategyTriggerKind.ContractLifecycle => "Contract lifecycle",
        _ => value.ToString(),
    };

    public static string For(StrategyHorizonKind value) => value switch
    {
        StrategyHorizonKind.Intraday => "Intraday",
        StrategyHorizonKind.MultiDay => "Multi-day",
        StrategyHorizonKind.MediumTerm => "Medium-term",
        StrategyHorizonKind.LongTerm => "Long-term",
        StrategyHorizonKind.Mixed => "Mixed horizon",
        _ => value.ToString(),
    };

    public static string For(MarketTopologyKind value) => value switch
    {
        MarketTopologyKind.SingleInstrument => "Single instrument",
        MarketTopologyKind.CrossSection => "Cross-section",
        MarketTopologyKind.Pair => "Pair",
        MarketTopologyKind.Basket => "Basket",
        MarketTopologyKind.CrossAsset => "Cross-asset",
        MarketTopologyKind.MultiVenue => "Multi-venue",
        MarketTopologyKind.UnderlyingAndDerivative => "Underlying + derivative",
        MarketTopologyKind.MultiLeg => "Multi-leg",
        _ => value.ToString(),
    };

    public static string For(ExposureGeometryKind value) => value switch
    {
        ExposureGeometryKind.LongOnly => "Long only",
        ExposureGeometryKind.DirectionalLongShort => "Directional long/short",
        ExposureGeometryKind.CrossSectionalLongShort => "Cross-sectional L/S",
        ExposureGeometryKind.MarketNeutral => "Market neutral",
        ExposureGeometryKind.Spread => "Spread",
        ExposureGeometryKind.Arbitrage => "Arbitrage",
        ExposureGeometryKind.DeltaNeutral => "Delta neutral",
        ExposureGeometryKind.VolatilityExposure => "Volatility",
        _ => value.ToString(),
    };

    public static string For(StrategyInformationKind value) => value switch
    {
        StrategyInformationKind.Quote => "Quotes",
        StrategyInformationKind.Trade => "Trades",
        StrategyInformationKind.Bar => "Bars",
        StrategyInformationKind.Depth => "Order-book depth",
        StrategyInformationKind.Fundamental => "Fundamentals",
        StrategyInformationKind.Macro => "Macro",
        StrategyInformationKind.CorporateEvent => "Corporate events",
        StrategyInformationKind.NewsText => "News / text",
        StrategyInformationKind.Alternative => "Alternative data",
        StrategyInformationKind.ImpliedVolatilitySurface => "Volatility surface",
        _ => value.ToString(),
    };

    public static string For(SignalModelKind value) => value switch
    {
        SignalModelKind.DeterministicRule => "Rules",
        SignalModelKind.Ranking => "Ranking",
        SignalModelKind.Statistical => "Statistical",
        SignalModelKind.Econometric => "Econometric",
        SignalModelKind.Optimization => "Optimization",
        SignalModelKind.SupervisedMachineLearning => "Supervised ML",
        SignalModelKind.OnlineLearning => "Online learning",
        SignalModelKind.ReinforcementLearning => "Reinforcement learning",
        SignalModelKind.Ensemble => "Ensemble",
        _ => value.ToString(),
    };

    public static string For(PortfolioConstructionKind value) => value switch
    {
        PortfolioConstructionKind.NotApplicable => "Not applicable",
        PortfolioConstructionKind.FixedQuantity => "Fixed quantity",
        PortfolioConstructionKind.EqualWeight => "Equal weight",
        PortfolioConstructionKind.TopK => "Top K",
        PortfolioConstructionKind.VolatilityTarget => "Volatility target",
        PortfolioConstructionKind.RiskBudget => "Risk budget",
        PortfolioConstructionKind.Optimized => "Optimized",
        PortfolioConstructionKind.ExposureNeutral => "Exposure neutral",
        PortfolioConstructionKind.InventoryTarget => "Inventory target",
        _ => value.ToString(),
    };

    public static string For(StrategyExecutionPolicyKind value) => value switch
    {
        StrategyExecutionPolicyKind.NotApplicable => "Not applicable",
        StrategyExecutionPolicyKind.Market => "Market",
        StrategyExecutionPolicyKind.Limit => "Limit",
        StrategyExecutionPolicyKind.Stop => "Stop",
        StrategyExecutionPolicyKind.Passive => "Passive",
        StrategyExecutionPolicyKind.Aggressive => "Aggressive",
        StrategyExecutionPolicyKind.Twap => "TWAP",
        StrategyExecutionPolicyKind.Vwap => "VWAP",
        StrategyExecutionPolicyKind.Pov => "POV",
        StrategyExecutionPolicyKind.SmartRouting => "Smart routing",
        StrategyExecutionPolicyKind.CoordinatedLegs => "Coordinated legs",
        StrategyExecutionPolicyKind.ContinuousQuoting => "Continuous quoting",
        _ => value.ToString(),
    };

    public static string For(StrategyStateKind value) => value switch
    {
        StrategyStateKind.Stateless => "Stateless",
        StrategyStateKind.PositionAware => "Position-aware",
        StrategyStateKind.EventLifecycle => "Event lifecycle",
        StrategyStateKind.InventoryAware => "Inventory-aware",
        StrategyStateKind.Cooldown => "Cooldown",
        StrategyStateKind.FiniteState => "State machine",
        StrategyStateKind.RegimeAware => "Regime-aware",
        _ => value.ToString(),
    };

    public static string For(StrategyRiskExitKind value) => value switch
    {
        StrategyRiskExitKind.NotApplicable => "Not applicable",
        StrategyRiskExitKind.SignalReversal => "Signal reversal",
        StrategyRiskExitKind.StopLoss => "Stop loss",
        StrategyRiskExitKind.TakeProfit => "Take profit",
        StrategyRiskExitKind.TrailingStop => "Trailing stop",
        StrategyRiskExitKind.TimeExit => "Time exit",
        StrategyRiskExitKind.EventResolution => "Event resolution",
        StrategyRiskExitKind.ExposureCap => "Exposure cap",
        StrategyRiskExitKind.GreekCap => "Greek cap",
        StrategyRiskExitKind.LiquidityCap => "Liquidity cap",
        StrategyRiskExitKind.DrawdownKillSwitch => "Drawdown kill switch",
        _ => value.ToString(),
    };

    public static string For(StrategyAdaptationKind value) => value switch
    {
        StrategyAdaptationKind.Fixed => "Fixed",
        StrategyAdaptationKind.OfflineSelected => "Offline selected",
        StrategyAdaptationKind.PeriodicRecalibration => "Periodic recalibration",
        StrategyAdaptationKind.RollingRefit => "Rolling refit",
        StrategyAdaptationKind.ScheduledRetraining => "Scheduled retraining",
        StrategyAdaptationKind.OnlineLearning => "Online learning",
        StrategyAdaptationKind.ReinforcementLearning => "Reinforcement learning",
        _ => value.ToString(),
    };
}

public sealed record StrategyStarterCatalogIssue(
    string StarterId,
    string Code,
    string Path,
    string Message);

/// <summary>Curated, normalized discovery corpus for the New Strategy empty state.</summary>
public static class StrategyStarterCatalog
{
    public const string QuoteL1EmaSmokePrompt =
        "Build a deterministic single-instrument equity QuoteL1 EMA crossover for ALPHA on XNAS in USD. Use quote mid, fast EMA 4, slow EMA 12, a greater-than decision, fixed target +5/-5 shares, market day orders, and flatten on end. For Typed Graph, use only the installed QuoteL1 smoke-supported operators and the host-owned canonical QuoteL1 schema; do not add bars, tape, trailing risk, or unknown operators.";

    public const string LiquiditySweepFadePrompt =
        "Fade liquidity sweeps at the prior day's low: enter when a stop-run through the level reverses within 3 bars on tape absorption, exit at VWAP with a stop below the sweep extreme.";

    public const string FiveMinuteMomentumBreakoutPrompt =
        "Momentum breakout on 5-minute bars: enter on a close above the last 20-bar high with a volume surge of at least 1.5× average, trail an ATR(14) stop.";

    public const string CumulativeDeltaDivergencePrompt =
        "Cumulative-delta divergence reversal: when price prints a new session low but cumulative delta holds above its own low, fade the move with a fixed 1.5R target.";

    public static IReadOnlyList<StrategyStarterBrief> All { get; } =
    [
        Create(
            "starter.quote-l1-ema-smoke",
            "EMA crossover (equity quotes)",
            "Simple quote-driven EMA crossover for equities. Use when you need a minimal verified starter.",
            QuoteL1EmaSmokePrompt,
            ["equity quotes", "EMA crossover", "ALPHA XNAS"],
            assetClasses: [AssetClass.Equity],
            information: [StrategyInformationKind.Quote],
            hypotheses: [ReturnHypothesisKind.Momentum],
            triggers: [StrategyTriggerKind.Quote],
            models: [SignalModelKind.DeterministicRule],
            construction: PortfolioConstructionKind.FixedQuantity,
            risks: [StrategyRiskExitKind.SignalReversal],
            execution: [StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware],
            adaptation: StrategyAdaptationKind.Fixed,
            holdingPeriod: TimeSpan.FromHours(1)),

        Create(
            "starter.liquidity-sweep-fade",
            "Liquidity sweep fade",
            "Fade a failed stop-run after tape absorption confirms the reversal.",
            LiquiditySweepFadePrompt,
            ["stop run", "liquidity grab", "tape absorption", "VWAP"],
            assetClasses: [AssetClass.Future],
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Trade, StrategyInformationKind.Bar],
            hypotheses: [ReturnHypothesisKind.Reversal, ReturnHypothesisKind.StructuralFlow],
            triggers: [StrategyTriggerKind.Trade, StrategyTriggerKind.Bar, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.DeterministicRule],
            risks: [StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TakeProfit, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.Cooldown],
            cadence: TimeSpan.FromMinutes(1),
            holdingPeriod: TimeSpan.FromMinutes(30)),

        Create(
            "starter.five-minute-momentum-breakout",
            "5-minute momentum breakout",
            "Trade a volume-confirmed range break with an ATR trailing stop.",
            FiveMinuteMomentumBreakoutPrompt,
            ["Donchian", "20-bar high", "volume surge", "ATR trail"],
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Bar],
            hypotheses: [ReturnHypothesisKind.Momentum],
            triggers: [StrategyTriggerKind.Bar, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.DeterministicRule],
            risks: [StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TrailingStop],
            execution: [StrategyExecutionPolicyKind.Stop, StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.OfflineSelected,
            cadence: TimeSpan.FromMinutes(5),
            holdingPeriod: TimeSpan.FromHours(4)),

        Create(
            "starter.cumulative-delta-divergence",
            "Cumulative-delta divergence",
            "Fade price extremes that are not confirmed by aggressive trade flow.",
            CumulativeDeltaDivergencePrompt,
            ["CVD", "order flow", "delta divergence", "1.5R"],
            assetClasses: [AssetClass.Future],
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Trade],
            hypotheses: [ReturnHypothesisKind.Reversal, ReturnHypothesisKind.StructuralFlow],
            triggers: [StrategyTriggerKind.Trade, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.Statistical],
            risks: [StrategyRiskExitKind.SignalReversal, StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TakeProfit],
            execution: [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.Cooldown],
            holdingPeriod: TimeSpan.FromHours(2)),

        Create(
            "starter.cross-sectional-quality-momentum",
            "Quality momentum rotation",
            "Rank liquid equities by quality and medium-term momentum, then hold the strongest cohort.",
            "Build a cross-sectional equity rotation: rank the liquid universe by 12-1 month momentum and a profitability-quality score, hold the top decile and short the bottom decile, neutralize net exposure, and stop new entries after a portfolio drawdown limit.",
            ["factor rotation", "quality", "cross-sectional momentum", "top decile"],
            topology: MarketTopologyKind.CrossSection,
            exposure: ExposureGeometryKind.CrossSectionalLongShort,
            information: [StrategyInformationKind.Fundamental, StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.MediumTerm,
            hypotheses: [ReturnHypothesisKind.Momentum, ReturnHypothesisKind.Quality],
            triggers: [StrategyTriggerKind.Schedule, StrategyTriggerKind.Bar],
            models: [SignalModelKind.Ranking],
            construction: PortfolioConstructionKind.TopK,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
            execution: [StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            cadence: TimeSpan.FromDays(7),
            holdingPeriod: TimeSpan.FromDays(30)),

        Create(
            "starter.pairs-spread-convergence",
            "Pairs spread convergence",
            "Trade a stationary relative-value spread while keeping pair exposure neutral.",
            "Create a pairs strategy for two cointegrated equities: enter when the rolling spread z-score exceeds ±2, size legs beta-neutral, exit inside ±0.5, and force a time exit when convergence stalls.",
            ["pairs trading", "cointegration", "z-score", "relative value"],
            topology: MarketTopologyKind.Pair,
            exposure: ExposureGeometryKind.Spread,
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.Convergence, ReturnHypothesisKind.Reversal],
            triggers: [StrategyTriggerKind.Quote, StrategyTriggerKind.Bar, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.Statistical, SignalModelKind.Econometric],
            construction: PortfolioConstructionKind.ExposureNeutral,
            risks: [StrategyRiskExitKind.SignalReversal, StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.CoordinatedLegs],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.RollingRefit,
            cadence: TimeSpan.FromHours(1),
            holdingPeriod: TimeSpan.FromDays(3)),

        Create(
            "starter.fx-carry-basket",
            "FX carry basket",
            "Allocate across currencies using rate differentials with a macro risk filter.",
            "Build an FX carry basket: rank liquid currencies by forward-implied carry, go long the top group and short the bottom group, scale each leg to a shared risk budget, and reduce exposure when volatility or funding stress rises.",
            ["currency carry", "forward points", "G10", "risk budget"],
            assetClasses: [AssetClass.Forex],
            topology: MarketTopologyKind.Basket,
            information: [StrategyInformationKind.Macro, StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.Carry],
            triggers: [StrategyTriggerKind.Schedule, StrategyTriggerKind.Bar],
            models: [SignalModelKind.Econometric, SignalModelKind.Ranking],
            construction: PortfolioConstructionKind.RiskBudget,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.Market, StrategyExecutionPolicyKind.Twap],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            cadence: TimeSpan.FromDays(1),
            holdingPeriod: TimeSpan.FromDays(10)),

        Create(
            "starter.deep-value-quality-allocation",
            "Deep value quality allocation",
            "Rebalance into inexpensive, financially resilient companies.",
            "Create a long-only equity allocation that ranks stocks by value and balance-sheet quality using point-in-time fundamentals, equal-weights the qualifying names, rebalances monthly, and caps sector exposure and portfolio drawdown.",
            ["value factor", "quality factor", "fundamentals", "monthly rebalance"],
            objective: StrategyObjectiveKind.Allocation,
            topology: MarketTopologyKind.CrossSection,
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Fundamental, StrategyInformationKind.CorporateEvent, StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.LongTerm,
            hypotheses: [ReturnHypothesisKind.Value, ReturnHypothesisKind.Quality, ReturnHypothesisKind.MarketRiskPremium],
            triggers: [StrategyTriggerKind.Schedule, StrategyTriggerKind.StructuredExternalEvent],
            models: [SignalModelKind.Ranking],
            construction: PortfolioConstructionKind.EqualWeight,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
            execution: [StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            cadence: TimeSpan.FromDays(30),
            holdingPeriod: TimeSpan.FromDays(180)),

        Create(
            "starter.benchmark-tracking-rebalance",
            "Benchmark tracking rebalance",
            "Minimize tracking error with constrained, low-turnover rebalances.",
            "Build a long-only benchmark tracker for a broad equity index: optimize constituent weights to minimize tracking error and turnover, rebalance on schedule, cap single-name exposure, and execute the rebalance with VWAP orders.",
            ["index tracking", "tracking error", "passive", "rebalance"],
            objective: StrategyObjectiveKind.BenchmarkTracking,
            assetClasses: [AssetClass.Equity, AssetClass.Index],
            topology: MarketTopologyKind.Basket,
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.LongTerm,
            hypotheses: [ReturnHypothesisKind.MarketRiskPremium, ReturnHypothesisKind.Defensive],
            triggers: [StrategyTriggerKind.Schedule],
            models: [SignalModelKind.Optimization],
            construction: PortfolioConstructionKind.Optimized,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
            execution: [StrategyExecutionPolicyKind.Vwap],
            state: [StrategyStateKind.Stateless],
            cadence: TimeSpan.FromDays(30),
            holdingPeriod: TimeSpan.FromDays(365)),

        Create(
            "starter.option-tail-risk-hedge",
            "Option tail-risk hedge",
            "Maintain convex protection while controlling premium spend and Greek exposure.",
            "Design an index-option tail hedge that buys convex downside protection when macro stress and implied volatility conditions warrant it, rolls before expiry, caps Greek exposure and premium budget, and exits protection as the risk regime normalizes.",
            ["tail hedge", "protective puts", "convexity", "risk-off"],
            objective: StrategyObjectiveKind.Hedging,
            assetClasses: [AssetClass.Option, AssetClass.Index],
            topology: MarketTopologyKind.UnderlyingAndDerivative,
            exposure: ExposureGeometryKind.VolatilityExposure,
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Macro, StrategyInformationKind.ImpliedVolatilitySurface],
            horizon: StrategyHorizonKind.Mixed,
            hypotheses: [ReturnHypothesisKind.VolatilityInsurance, ReturnHypothesisKind.Defensive],
            triggers: [StrategyTriggerKind.Quote, StrategyTriggerKind.Schedule, StrategyTriggerKind.ContractLifecycle],
            models: [SignalModelKind.Statistical],
            construction: PortfolioConstructionKind.VolatilityTarget,
            risks: [StrategyRiskExitKind.GreekCap, StrategyRiskExitKind.DrawdownKillSwitch, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.CoordinatedLegs],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware, StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            cadence: TimeSpan.FromHours(1),
            holdingPeriod: TimeSpan.FromDays(30)),

        Create(
            "starter.queue-aware-market-making",
            "Queue-aware market making",
            "Continuously quote around fair value while managing queue position and inventory.",
            "Create a two-sided crypto market maker that estimates microprice from depth and trades, adjusts spreads for volatility and inventory, models queue position and partial fills, and stops quoting at inventory or liquidity limits.",
            ["market making", "microprice", "queue position", "inventory skew"],
            objective: StrategyObjectiveKind.LiquidityProvision,
            assetClasses: [AssetClass.Crypto],
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Depth, StrategyInformationKind.Trade],
            hypotheses: [ReturnHypothesisKind.LiquidityProvision, ReturnHypothesisKind.StructuralFlow],
            triggers: [StrategyTriggerKind.Depth, StrategyTriggerKind.Trade, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.Optimization, SignalModelKind.OnlineLearning],
            construction: PortfolioConstructionKind.InventoryTarget,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.LiquidityCap],
            execution: [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.Passive, StrategyExecutionPolicyKind.ContinuousQuoting],
            state: [StrategyStateKind.InventoryAware, StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.OnlineLearning,
            holdingPeriod: TimeSpan.FromMinutes(5)),

        Create(
            "starter.adaptive-smart-router",
            "Adaptive smart router",
            "Choose venues and urgency from live depth while learning execution quality.",
            "Build a multi-venue equity order router that observes quotes and depth, selects passive or aggressive child orders, learns from fills and slippage, and respects time and liquidity limits until the parent order completes.",
            ["SOR", "venue selection", "execution policy", "slippage"],
            objective: StrategyObjectiveKind.Execution,
            topology: MarketTopologyKind.MultiVenue,
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Depth],
            hypotheses: [ReturnHypothesisKind.None],
            triggers: [StrategyTriggerKind.Quote, StrategyTriggerKind.Depth, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.ReinforcementLearning],
            risks: [StrategyRiskExitKind.LiquidityCap, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.SmartRouting, StrategyExecutionPolicyKind.Passive, StrategyExecutionPolicyKind.Aggressive],
            state: [StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.ReinforcementLearning,
            holdingPeriod: TimeSpan.FromMinutes(30)),

        Create(
            "starter.vwap-participation",
            "VWAP participation",
            "Schedule child orders against observed market volume.",
            "Execute a large equity order against intraday VWAP: estimate the volume curve from finalized bars and trades, schedule child orders, adapt urgency when behind target, and stop when the time window or liquidity cap is reached.",
            ["VWAP", "volume curve", "parent order", "implementation shortfall"],
            objective: StrategyObjectiveKind.Execution,
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Trade, StrategyInformationKind.Bar, StrategyInformationKind.Quote],
            hypotheses: [ReturnHypothesisKind.None],
            triggers: [StrategyTriggerKind.Schedule, StrategyTriggerKind.Trade, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.DeterministicRule],
            risks: [StrategyRiskExitKind.LiquidityCap, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.Vwap],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            holdingPeriod: TimeSpan.FromHours(6)),

        Create(
            "starter.twap-schedule",
            "TWAP schedule",
            "Spread a parent order evenly across a fixed trading window.",
            "Create a TWAP executor for a large FX order: divide the parent quantity into scheduled slices, use passive limits when spread conditions allow, catch up safely after missed slices, and finish or cancel at the deadline.",
            ["TWAP", "time slicing", "parent order", "deadline"],
            objective: StrategyObjectiveKind.Execution,
            assetClasses: [AssetClass.Forex],
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Quote],
            hypotheses: [ReturnHypothesisKind.None],
            triggers: [StrategyTriggerKind.Schedule, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.DeterministicRule],
            risks: [StrategyRiskExitKind.LiquidityCap, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.Twap, StrategyExecutionPolicyKind.Limit],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            holdingPeriod: TimeSpan.FromHours(2)),

        Create(
            "starter.pov-volume-participation",
            "POV volume participation",
            "Track a target share of live traded volume without overwhelming liquidity.",
            "Build a futures POV executor that targets 10% of observed market volume, updates after each trade and fill, uses aggressive orders only when participation falls behind, and enforces liquidity and completion-time caps.",
            ["POV", "participation rate", "traded volume", "child orders"],
            objective: StrategyObjectiveKind.Execution,
            assetClasses: [AssetClass.Future],
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Trade, StrategyInformationKind.Quote],
            hypotheses: [ReturnHypothesisKind.None],
            triggers: [StrategyTriggerKind.Trade, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.DeterministicRule],
            risks: [StrategyRiskExitKind.LiquidityCap, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.Pov, StrategyExecutionPolicyKind.Aggressive],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            holdingPeriod: TimeSpan.FromHours(1)),

        Create(
            "starter.news-catalyst-reaction",
            "News catalyst reaction",
            "Trade structured news surprises through a bounded event lifecycle.",
            "Create an event-driven equity strategy that classifies timestamped news and corporate events, trades only high-confidence surprises, sizes a single position, and exits on event resolution, a stop, or a strict time limit.",
            ["news sentiment", "event driven", "NLP", "surprise"],
            information: [StrategyInformationKind.NewsText, StrategyInformationKind.CorporateEvent, StrategyInformationKind.Quote],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.CatalystInformation],
            triggers: [StrategyTriggerKind.NewsEvent, StrategyTriggerKind.StructuredExternalEvent, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.Ensemble],
            risks: [StrategyRiskExitKind.EventResolution, StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.Aggressive, StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.EventLifecycle, StrategyStateKind.PositionAware],
            adaptation: StrategyAdaptationKind.ScheduledRetraining,
            holdingPeriod: TimeSpan.FromDays(2)),

        Create(
            "starter.post-earnings-drift",
            "Post-earnings drift",
            "Rank earnings surprises and hold the strongest delayed reactions.",
            "Build a cross-sectional post-earnings-announcement drift strategy using point-in-time results and price reaction: rank standardized surprises, trade the strongest long and short cohorts, trail winners, and exit after the event window.",
            ["PEAD", "earnings surprise", "event study", "drift"],
            topology: MarketTopologyKind.CrossSection,
            exposure: ExposureGeometryKind.CrossSectionalLongShort,
            information: [StrategyInformationKind.CorporateEvent, StrategyInformationKind.Fundamental, StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.CatalystInformation, ReturnHypothesisKind.Momentum],
            triggers: [StrategyTriggerKind.StructuredExternalEvent, StrategyTriggerKind.Bar],
            models: [SignalModelKind.Econometric, SignalModelKind.Ranking],
            construction: PortfolioConstructionKind.TopK,
            risks: [StrategyRiskExitKind.EventResolution, StrategyRiskExitKind.TrailingStop, StrategyRiskExitKind.TimeExit, StrategyRiskExitKind.ExposureCap],
            execution: [StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.EventLifecycle, StrategyStateKind.PositionAware],
            adaptation: StrategyAdaptationKind.RollingRefit,
            cadence: TimeSpan.FromDays(1),
            holdingPeriod: TimeSpan.FromDays(20)),

        Create(
            "starter.seasonal-futures-spread",
            "Seasonal futures spread",
            "Trade recurring calendar-spread dislocations around contract rolls.",
            "Create a seasonal futures calendar-spread strategy: model the normal term-structure spread by day-of-year, enter convergence trades at historical extremes, coordinate both legs, roll contracts explicitly, and close by signal or time limit.",
            ["calendar spread", "seasonality", "term structure", "contract roll"],
            assetClasses: [AssetClass.Future],
            topology: MarketTopologyKind.Pair,
            exposure: ExposureGeometryKind.Spread,
            information: [StrategyInformationKind.Bar, StrategyInformationKind.Macro],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.Seasonality, ReturnHypothesisKind.Carry, ReturnHypothesisKind.Convergence],
            triggers: [StrategyTriggerKind.Schedule, StrategyTriggerKind.Bar, StrategyTriggerKind.ContractLifecycle],
            models: [SignalModelKind.Econometric],
            construction: PortfolioConstructionKind.ExposureNeutral,
            risks: [StrategyRiskExitKind.SignalReversal, StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.TimeExit],
            execution: [StrategyExecutionPolicyKind.CoordinatedLegs],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            cadence: TimeSpan.FromDays(1),
            holdingPeriod: TimeSpan.FromDays(14)),

        Create(
            "starter.macro-risk-parity",
            "Macro risk parity",
            "Allocate risk across asset classes and de-risk in hostile regimes.",
            "Build a cross-asset risk-parity allocation across equity indexes, rates futures, and defensive assets: estimate rolling covariance, equalize risk contributions, apply volatility targeting, and reduce total exposure after a drawdown or risk-off macro regime.",
            ["risk parity", "all weather", "covariance", "risk contribution"],
            objective: StrategyObjectiveKind.Allocation,
            assetClasses: [AssetClass.Equity, AssetClass.Future, AssetClass.Index],
            topology: MarketTopologyKind.CrossAsset,
            exposure: ExposureGeometryKind.LongOnly,
            information: [StrategyInformationKind.Macro, StrategyInformationKind.Bar],
            horizon: StrategyHorizonKind.LongTerm,
            hypotheses: [ReturnHypothesisKind.MarketRiskPremium, ReturnHypothesisKind.Defensive, ReturnHypothesisKind.Carry],
            triggers: [StrategyTriggerKind.Schedule],
            models: [SignalModelKind.Optimization],
            construction: PortfolioConstructionKind.RiskBudget,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
            execution: [StrategyExecutionPolicyKind.Market, StrategyExecutionPolicyKind.Twap],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            cadence: TimeSpan.FromDays(7),
            holdingPeriod: TimeSpan.FromDays(90)),

        Create(
            "starter.option-dispersion",
            "Option dispersion",
            "Trade index-versus-component volatility while holding delta near neutral.",
            "Design a delta-neutral option dispersion strategy: compare index implied volatility with weighted component surfaces, enter only when the spread clears costs, coordinate every leg, rebalance delta, and enforce Greek and gross-exposure caps.",
            ["dispersion", "implied correlation", "delta hedge", "volatility spread"],
            assetClasses: [AssetClass.Option, AssetClass.Index],
            topology: MarketTopologyKind.MultiLeg,
            exposure: ExposureGeometryKind.DeltaNeutral,
            information: [StrategyInformationKind.ImpliedVolatilitySurface, StrategyInformationKind.Quote],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.Convergence, ReturnHypothesisKind.VolatilityInsurance],
            triggers: [StrategyTriggerKind.Quote, StrategyTriggerKind.ContractLifecycle, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.Statistical],
            construction: PortfolioConstructionKind.ExposureNeutral,
            risks: [StrategyRiskExitKind.GreekCap, StrategyRiskExitKind.ExposureCap],
            execution: [StrategyExecutionPolicyKind.CoordinatedLegs],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            adaptation: StrategyAdaptationKind.PeriodicRecalibration,
            holdingPeriod: TimeSpan.FromDays(7)),

        Create(
            "starter.alternative-data-ml-momentum",
            "Alternative-data ML momentum",
            "Blend alternative observations with price momentum under strict model governance.",
            "Create a crypto momentum strategy that combines point-in-time alternative data with quotes, trains a supervised ensemble on walk-forward windows, volatility-targets exposure, retrains on schedule, and halts after an exposure or drawdown breach.",
            ["machine learning", "alternative data", "walk forward", "model artifact"],
            assetClasses: [AssetClass.Crypto],
            information: [StrategyInformationKind.Alternative, StrategyInformationKind.Quote],
            horizon: StrategyHorizonKind.MultiDay,
            hypotheses: [ReturnHypothesisKind.Momentum],
            triggers: [StrategyTriggerKind.Quote, StrategyTriggerKind.Schedule],
            models: [SignalModelKind.SupervisedMachineLearning, SignalModelKind.Ensemble],
            construction: PortfolioConstructionKind.VolatilityTarget,
            risks: [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
            execution: [StrategyExecutionPolicyKind.Market],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware],
            adaptation: StrategyAdaptationKind.ScheduledRetraining,
            cadence: TimeSpan.FromHours(1),
            holdingPeriod: TimeSpan.FromDays(3)),

        Create(
            "starter.online-basket-stat-arb",
            "Online basket stat-arb",
            "Continuously refit a market-neutral basket as relationships evolve.",
            "Build an online statistical-arbitrage equity basket: update a neutral residual model from quotes and trades, enter only liquid dislocations, optimize exposure-neutral weights, and kill risk on signal reversal, exposure breach, or portfolio drawdown.",
            ["stat arb", "online learning", "residual", "market neutral"],
            topology: MarketTopologyKind.Basket,
            exposure: ExposureGeometryKind.MarketNeutral,
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Trade, StrategyInformationKind.Alternative],
            hypotheses: [ReturnHypothesisKind.Convergence, ReturnHypothesisKind.StructuralFlow],
            triggers: [StrategyTriggerKind.Trade, StrategyTriggerKind.Quote],
            models: [SignalModelKind.Statistical, SignalModelKind.OnlineLearning],
            construction: PortfolioConstructionKind.Optimized,
            risks: [StrategyRiskExitKind.SignalReversal, StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
            execution: [StrategyExecutionPolicyKind.CoordinatedLegs],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware],
            adaptation: StrategyAdaptationKind.OnlineLearning,
            holdingPeriod: TimeSpan.FromHours(8)),

        Create(
            "starter.cross-venue-crypto-arbitrage",
            "Cross-venue crypto arbitrage",
            "Capture executable price gaps across venues with coordinated, liquidity-aware orders.",
            "Create a cross-venue crypto arbitrage strategy that compares executable depth, enters only after fees and latency buffers, coordinates offsetting legs, routes to the best venues, and stops at liquidity or gross-exposure caps.",
            ["cross exchange", "arbitrage", "best execution", "latency buffer"],
            assetClasses: [AssetClass.Crypto],
            topology: MarketTopologyKind.MultiVenue,
            exposure: ExposureGeometryKind.Arbitrage,
            information: [StrategyInformationKind.Quote, StrategyInformationKind.Depth],
            hypotheses: [ReturnHypothesisKind.Convergence, ReturnHypothesisKind.StructuralFlow],
            triggers: [StrategyTriggerKind.Quote, StrategyTriggerKind.Depth, StrategyTriggerKind.OrderEvent],
            models: [SignalModelKind.DeterministicRule],
            construction: PortfolioConstructionKind.ExposureNeutral,
            risks: [StrategyRiskExitKind.LiquidityCap, StrategyRiskExitKind.ExposureCap],
            execution: [StrategyExecutionPolicyKind.SmartRouting, StrategyExecutionPolicyKind.CoordinatedLegs],
            state: [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState],
            holdingPeriod: TimeSpan.FromSeconds(10)),
    ];

    /// <summary>
    /// Matches every whitespace-delimited query term against user copy, aliases, overlapping
    /// families, and every normalized axis label. Terms may match different fields, which makes a
    /// query such as <c>futures stop loss</c> useful without inventing denormalized search tags.
    /// </summary>
    public static bool MatchesSearch(StrategyStarterBrief brief, string? query)
    {
        ArgumentNullException.ThrowIfNull(brief);

        var terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return true;

        var searchableText = string.Join('\n', SearchValues(brief));
        return terms.All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Filters the built-in catalog while retaining its curated display order.</summary>
    public static IReadOnlyList<StrategyStarterBrief> Filter(string? query) =>
        Filter(All, query);

    /// <summary>Filters any starter sequence while retaining source order.</summary>
    public static IReadOnlyList<StrategyStarterBrief> Filter(
        IEnumerable<StrategyStarterBrief> source,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(brief => MatchesSearch(brief, query)).ToArray();
    }

    /// <summary>Returns structural catalog problems without throwing, for tests and startup diagnostics.</summary>
    public static IReadOnlyList<StrategyStarterCatalogIssue> ValidateAll()
    {
        var issues = new List<StrategyStarterCatalogIssue>();

        foreach (var duplicate in All.GroupBy(static brief => brief.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            issues.Add(new StrategyStarterCatalogIssue(
                duplicate.Key,
                "starter.id.duplicate",
                "id",
                $"Starter id '{duplicate.Key}' is used more than once."));
        }

        foreach (var duplicate in All.GroupBy(static brief => brief.Title, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            issues.Add(new StrategyStarterCatalogIssue(
                duplicate.First().Id,
                "starter.title.duplicate",
                "title",
                $"Starter title '{duplicate.Key}' is used more than once."));
        }

        foreach (var brief in All)
        {
            Required(brief.Id, brief.Id, "starter.id.required", "id", "Starter id is required.", issues);
            Required(brief.Title, brief.Id, "starter.title.required", "title", "Starter title is required.", issues);
            Required(brief.Summary, brief.Id, "starter.summary.required", "summary", "Starter summary is required.", issues);
            Required(brief.Prompt, brief.Id, "starter.prompt.required", "prompt", "Starter prompt is required.", issues);

            if (!string.Equals(brief.Id, brief.Classification.Id, StringComparison.Ordinal))
            {
                issues.Add(new StrategyStarterCatalogIssue(
                    brief.Id,
                    "starter.classification.id_mismatch",
                    "classification.id",
                    "Starter and classification ids must match."));
            }

            if (!string.Equals(brief.Title, brief.Classification.Name, StringComparison.Ordinal))
            {
                issues.Add(new StrategyStarterCatalogIssue(
                    brief.Id,
                    "starter.classification.name_mismatch",
                    "classification.name",
                    "Starter title and classification name must match."));
            }

            if (brief.SearchAliases is null || brief.SearchAliases.Count == 0 ||
                brief.SearchAliases.Any(string.IsNullOrWhiteSpace))
            {
                issues.Add(new StrategyStarterCatalogIssue(
                    brief.Id,
                    "starter.search_alias.required",
                    "search_aliases",
                    "At least one non-empty search alias is required."));
            }

            if (brief.FamilyLabels.Count == 0)
            {
                issues.Add(new StrategyStarterCatalogIssue(
                    brief.Id,
                    "starter.family.required",
                    "classification",
                    "The classification must project into at least one discovery family."));
            }

            foreach (var issue in StrategySpecValidator.Validate(brief.Classification))
            {
                issues.Add(new StrategyStarterCatalogIssue(
                    brief.Id,
                    issue.Code,
                    $"classification.{issue.Path}",
                    issue.Message));
            }
        }

        return issues;
    }

    private static IEnumerable<string> SearchValues(StrategyStarterBrief brief)
    {
        yield return brief.Title;
        yield return brief.Summary;
        yield return brief.Prompt;

        foreach (var value in brief.SearchAliases)
            yield return value;
        foreach (var value in brief.FamilyLabels)
            yield return value;

        var axes = brief.AxisLabels;
        yield return axes.Objective;
        foreach (var value in axes.AssetClasses)
            yield return value;
        yield return axes.Horizon;
        yield return axes.Topology;
        yield return axes.Exposure;
        foreach (var value in axes.Data)
            yield return value;
        foreach (var value in axes.Hypotheses)
            yield return value;
        foreach (var value in axes.Triggers)
            yield return value;
        foreach (var value in axes.Models)
            yield return value;
        yield return axes.Construction;
        foreach (var value in axes.Risk)
            yield return value;
        foreach (var value in axes.Execution)
            yield return value;
        foreach (var value in axes.State)
            yield return value;
        yield return axes.Adaptation;
        yield return axes.Regime;
    }

    private static void Required(
        string? value,
        string starterId,
        string code,
        string path,
        string message,
        ICollection<StrategyStarterCatalogIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new StrategyStarterCatalogIssue(starterId, code, path, message));
    }

    private static StrategyStarterBrief Create(
        string id,
        string title,
        string summary,
        string prompt,
        IReadOnlyList<string> aliases,
        StrategyObjectiveKind objective = StrategyObjectiveKind.ReturnSeeking,
        IReadOnlyList<AssetClass>? assetClasses = null,
        MarketTopologyKind topology = MarketTopologyKind.SingleInstrument,
        ExposureGeometryKind exposure = ExposureGeometryKind.DirectionalLongShort,
        IReadOnlyList<StrategyInformationKind>? information = null,
        StrategyHorizonKind horizon = StrategyHorizonKind.Intraday,
        IReadOnlyList<ReturnHypothesisKind>? hypotheses = null,
        IReadOnlyList<StrategyTriggerKind>? triggers = null,
        IReadOnlyList<SignalModelKind>? models = null,
        PortfolioConstructionKind construction = PortfolioConstructionKind.FixedQuantity,
        IReadOnlyList<StrategyRiskExitKind>? risks = null,
        IReadOnlyList<StrategyExecutionPolicyKind>? execution = null,
        IReadOnlyList<StrategyStateKind>? state = null,
        StrategyAdaptationKind adaptation = StrategyAdaptationKind.Fixed,
        TimeSpan? cadence = null,
        TimeSpan? holdingPeriod = null) =>
        new(
            id,
            title,
            summary,
            prompt,
            new StrategySpec(
                id,
                title,
                objective,
                new StrategyContextSpec(
                    assetClasses ?? [AssetClass.Equity],
                    topology,
                    exposure,
                    information ?? [StrategyInformationKind.Quote],
                    new StrategyTimeSemantics(horizon, cadence, holdingPeriod)),
                new StrategySignalSpec(
                    hypotheses ?? [ReturnHypothesisKind.Momentum],
                    triggers ?? [StrategyTriggerKind.Quote],
                    models ?? [SignalModelKind.DeterministicRule]),
                new StrategyPortfolioSpec(construction),
                new StrategyRiskSpec(risks ?? [StrategyRiskExitKind.SignalReversal]),
                new StrategyExecutionSpec(execution ?? [StrategyExecutionPolicyKind.Market]),
                new StrategyStateSpec(
                    state ?? [StrategyStateKind.PositionAware],
                    adaptation),
                []),
            aliases);
}
