using FluentAssertions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Specification;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class StrategyStarterCatalogTests
{
    [Fact]
    public void Catalog_is_structurally_valid_unique_and_searchable()
    {
        StrategyStarterCatalog.All.Should().HaveCount(23);
        StrategyStarterCatalog.ValidateAll().Should().BeEmpty();

        StrategyStarterCatalog.All.Select(static brief => brief.Id)
            .Should().OnlyHaveUniqueItems();
        StrategyStarterCatalog.All.Select(static brief => brief.Title)
            .Should().OnlyHaveUniqueItems();
        StrategyStarterCatalog.All.Should().OnlyContain(static brief =>
            brief.Classification.Id == brief.Id &&
            brief.Classification.Name == brief.Title &&
            !string.IsNullOrWhiteSpace(brief.Summary) &&
            !string.IsNullOrWhiteSpace(brief.Prompt) &&
            brief.SearchAliases.Count > 0 &&
            brief.FamilyLabels.Count > 0);

        StrategyStarterCatalog.All
            .SelectMany(static brief => StrategySpecValidator.Validate(brief.Classification))
            .Should().BeEmpty();
    }

    [Fact]
    public void QuoteL1_ema_starter_shows_user_facing_data_and_keeps_verified_prompt_boundary()
    {
        var starter = Find("starter.quote-l1-ema-smoke");

        starter.Title.Should().Contain("EMA crossover");
        starter.Title.Should().NotContain("smoke");
        starter.Summary.Should().Be(
            "Simple quote-driven EMA crossover for equities. Use when you need a minimal verified starter.");
        starter.Summary.Should().NotContain("TradeIR");
        starter.Summary.Should().NotContain("smoke");
        starter.Summary.Should().NotContain("in-process synthetic");
        starter.AxisLabels.Data.Should().Contain("Quotes");
        starter.Prompt.Should().Be(StrategyStarterCatalog.QuoteL1EmaSmokePrompt);
        starter.Prompt.Should().ContainAll(
            "ALPHA on XNAS in USD",
            "QuoteL1",
            "fast EMA 4",
            "slow EMA 12",
            "fixed target +5/-5 shares",
            "market day orders",
            "flatten on end",
            "host-owned canonical QuoteL1 schema",
            "do not add bars, tape, trailing risk, or unknown operators");
        starter.Classification.Context.Information.Should().Equal(StrategyInformationKind.Quote);
        starter.Classification.Signal.Triggers.Should().Equal(StrategyTriggerKind.Quote);
        starter.Classification.Signal.Models.Should().Equal(SignalModelKind.DeterministicRule);
        starter.Classification.Portfolio.Construction.Should().Be(PortfolioConstructionKind.FixedQuantity);
        starter.Classification.Execution.Policies.Should().Equal(StrategyExecutionPolicyKind.Market);
        starter.Classification.State.Adaptation.Should().Be(StrategyAdaptationKind.Fixed);

        StrategyStarterCatalog.Filter("QuoteL1 EMA")
            .Should().ContainSingle(candidate => candidate.Id == starter.Id);
    }

    [Fact]
    public void Five_minute_momentum_breakout_is_classified_as_long_only()
    {
        var starter = Find("starter.five-minute-momentum-breakout");

        starter.Prompt.Should().Be(StrategyStarterCatalog.FiveMinuteMomentumBreakoutPrompt);
        starter.Classification.Context.Exposure.Should().Be(ExposureGeometryKind.LongOnly);
    }

    [Fact]
    public void Catalog_spans_every_value_of_the_canonical_discovery_axes()
    {
        var specifications = StrategyStarterCatalog.All
            .Select(static brief => brief.Classification)
            .ToArray();

        specifications.Select(static spec => spec.Objective).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyObjectiveKind>());
        specifications.SelectMany(static spec => spec.Context.AssetClasses).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<AssetClass>().Except([AssetClass.Unknown]));
        specifications.Select(static spec => spec.Context.Time.Horizon).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyHorizonKind>());
        specifications.Select(static spec => spec.Context.Topology).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<MarketTopologyKind>());
        specifications.Select(static spec => spec.Context.Exposure).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<ExposureGeometryKind>());
        specifications.SelectMany(static spec => spec.Context.Information).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyInformationKind>());
        specifications.SelectMany(static spec => spec.Signal.Hypotheses).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<ReturnHypothesisKind>());
        specifications.SelectMany(static spec => spec.Signal.Triggers).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyTriggerKind>());
        specifications.SelectMany(static spec => spec.Signal.Models).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<SignalModelKind>());
        specifications.Select(static spec => spec.Portfolio.Construction).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<PortfolioConstructionKind>()
                .Except([PortfolioConstructionKind.NotApplicable]));
        specifications.SelectMany(static spec => spec.Risk.Rules).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyRiskExitKind>()
                .Except([StrategyRiskExitKind.NotApplicable]));
        specifications.SelectMany(static spec => spec.Execution.Policies).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyExecutionPolicyKind>()
                .Except([StrategyExecutionPolicyKind.NotApplicable]));
        specifications.SelectMany(static spec => spec.State.Policies).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyStateKind>());
        specifications.Select(static spec => spec.State.Adaptation).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<StrategyAdaptationKind>());
    }

    [Fact]
    public void Family_lens_is_computed_multi_match_and_covers_every_navigation_family()
    {
        StrategyStarterCatalog.All.SelectMany(static brief => brief.FamilyLabels).Distinct()
            .Should().BeEquivalentTo(StrategyStarterFamilies.All);
        StrategyStarterCatalog.All.Should().OnlyContain(static brief =>
            brief.FamilyLabels.Distinct(StringComparer.Ordinal).Count() == brief.FamilyLabels.Count);

        Find("starter.liquidity-sweep-fade").FamilyLabels.Should().Contain(
        [
            StrategyStarterFamilies.ReversionAndRelativeValue,
            StrategyStarterFamilies.OrderFlowAndLiquidity,
        ]);
        Find("starter.option-tail-risk-hedge").FamilyLabels.Should().Contain(
        [
            StrategyStarterFamilies.VolatilityAndDerivatives,
            StrategyStarterFamilies.AllocationAndHedging,
        ]);
        Find("starter.alternative-data-ml-momentum").FamilyLabels.Should().Contain(
        [
            StrategyStarterFamilies.TrendAndMomentum,
            StrategyStarterFamilies.AdaptiveAndMl,
        ]);
        Find("starter.adaptive-smart-router").FamilyLabels.Should().Contain(
        [
            StrategyStarterFamilies.Execution,
            StrategyStarterFamilies.AdaptiveAndMl,
        ]);
    }

    [Fact]
    public void Axis_projection_uses_concise_user_facing_labels()
    {
        var optionDispersion = Find("starter.option-dispersion").AxisLabels;
        optionDispersion.Horizon.Should().Be("Multi-day");
        optionDispersion.Topology.Should().Be("Multi-leg");
        optionDispersion.Exposure.Should().Be("Delta neutral");
        optionDispersion.Data.Should().Contain("Volatility surface");
        optionDispersion.Execution.Should().Contain("Coordinated legs");
        optionDispersion.Risk.Should().Contain("Greek cap");
        optionDispersion.Regime.Should().Be("Regime-agnostic");

        Find("starter.option-tail-risk-hedge").AxisLabels.Regime.Should().Be("Regime-aware");
        Find("starter.vwap-participation").AxisLabels.Execution.Should().Contain("VWAP");
        Find("starter.pov-volume-participation").AxisLabels.Execution.Should().Contain("POV");
        Find("starter.alternative-data-ml-momentum").AxisLabels.Models.Should().Contain("Supervised ML");
    }

    [Fact]
    public void Existing_empty_state_prompts_are_preserved_verbatim()
    {
        Find("starter.liquidity-sweep-fade").Prompt.Should().Be(
            "Fade liquidity sweeps at the prior day's low: enter when a stop-run through the level reverses within 3 bars on tape absorption, exit at VWAP with a stop below the sweep extreme.");
        Find("starter.five-minute-momentum-breakout").Prompt.Should().Be(
            "Momentum breakout on 5-minute bars: enter on a close above the last 20-bar high with a volume surge of at least 1.5× average, trail an ATR(14) stop.");
        Find("starter.cumulative-delta-divergence").Prompt.Should().Be(
            "Cumulative-delta divergence reversal: when price prints a new session low but cumulative delta holds above its own low, fade the move with a fixed 1.5R target.");
    }

    [Fact]
    public void Search_is_case_insensitive_supports_cross_field_terms_and_preserves_order()
    {
        StrategyStarterCatalog.Filter(null).Should().Equal(StrategyStarterCatalog.All);
        StrategyStarterCatalog.Filter("   ").Should().Equal(StrategyStarterCatalog.All);

        var futuresWithStops = StrategyStarterCatalog.Filter("FUTURES stop loss");
        futuresWithStops.Select(static brief => brief.Id).Should().Contain(
            "starter.liquidity-sweep-fade");

        var reversed = StrategyStarterCatalog.All.Reverse().ToArray();
        StrategyStarterCatalog.Filter(reversed, "regime-aware")
            .Should().Equal(reversed.Where(static brief =>
                brief.AxisLabels.Regime == "Regime-aware"));
    }

    [Theory]
    [InlineData("Quality momentum rotation", "starter.cross-sectional-quality-momentum")] // title
    [InlineData("failed stop-run", "starter.liquidity-sweep-fade")] // summary
    [InlineData("12-1 month", "starter.cross-sectional-quality-momentum")] // prompt
    [InlineData("Donchian", "starter.five-minute-momentum-breakout")] // alias
    [InlineData("relative value", "starter.pairs-spread-convergence")] // family
    [InlineData("Benchmark tracking", "starter.benchmark-tracking-rebalance")] // objective
    [InlineData("Futures", "starter.liquidity-sweep-fade")] // asset class
    [InlineData("Mixed horizon", "starter.option-tail-risk-hedge")] // horizon
    [InlineData("Underlying derivative", "starter.option-tail-risk-hedge")] // topology
    [InlineData("Delta neutral", "starter.option-dispersion")] // exposure
    [InlineData("Volatility surface", "starter.option-dispersion")] // data
    [InlineData("No alpha thesis", "starter.vwap-participation")] // hypothesis
    [InlineData("Contract lifecycle", "starter.seasonal-futures-spread")] // trigger
    [InlineData("Reinforcement learning", "starter.adaptive-smart-router")] // model
    [InlineData("Inventory target", "starter.queue-aware-market-making")] // construction
    [InlineData("Greek cap", "starter.option-tail-risk-hedge")] // risk
    [InlineData("Continuous quoting", "starter.queue-aware-market-making")] // execution
    [InlineData("Event lifecycle", "starter.news-catalyst-reaction")] // state
    [InlineData("Scheduled retraining", "starter.alternative-data-ml-momentum")] // adaptation
    [InlineData("Regime-aware", "starter.macro-risk-parity")] // regime
    public void Search_indexes_copy_families_and_every_canonical_axis(
        string query,
        string expectedStarterId)
    {
        StrategyStarterCatalog.Filter(query).Select(static brief => brief.Id)
            .Should().Contain(expectedStarterId);
        StrategyStarterCatalog.MatchesSearch(Find(expectedStarterId), query).Should().BeTrue();
    }

    private static StrategyStarterBrief Find(string id) =>
        StrategyStarterCatalog.All.Single(brief => brief.Id == id);
}
