using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class AuthoredChartChoiceCatalogV1Tests
{
    [Fact]
    public void Famous_indicator_ids_enable_matching_native_toggles()
    {
        var state = NativeChartOverlaySelectionV1.FromHostOverlayIds(["ema-20", "rsi-14", "bollinger-20"]);

        Assert.False(state.ShowSma);
        Assert.True(state.ShowEma);
        Assert.Equal(20, state.EmaPeriod);
        Assert.True(state.ShowRsi);
        Assert.False(state.ShowMacd);
        Assert.True(state.ShowBollinger);
    }

    [Fact]
    public void Candles_only_clears_default_overlays()
    {
        var state = NativeChartOverlaySelectionV1.FromHostOverlayIds(["candles"]);

        Assert.False(state.ShowSma);
        Assert.False(state.ShowEma);
        Assert.False(state.ShowRsi);
        Assert.False(state.ShowMacd);
        Assert.False(state.ShowBollinger);
    }

    [Fact]
    public void Host_visualizer_from_catalog_overlays_is_launch_valid()
    {
        var candidate = new AuthoredUnitInstrumentCandidateV1(
            new InstrumentId(7),
            "AAPL",
            AssetClass.Equity,
            "NASDAQ",
            "USD",
            [BrokerKind.InteractiveBrokers]);
        var overlays = AuthoredChartChoiceCatalogV1.Overlays
            .Where(static item => item.Id is "ema-20" or "rsi-14")
            .ToArray();

        var specification = AuthoredChartChoiceCatalogV1.TryCreateHostVisualizerSpecification(
            "aapl-overlays",
            "Show AAPL with EMA and RSI",
            [candidate],
            overlays);

        Assert.NotNull(specification);
        Assert.Empty(AuthoredUnitSpecificationValidatorV1.ValidateForLaunch(specification));
        Assert.Equal(AuthoredUnitKindV1.Visualizer, specification!.Kind);
        Assert.Contains(specification.Drawing.Layers, static layer => layer.TypeId == "indicator.ema@1");
        Assert.Contains(specification.Drawing.Layers, static layer => layer.TypeId == "indicator.rsi@1");
    }

    [Fact]
    public void Vague_indicator_follow_up_asks_for_host_catalog_even_with_plus_five_context()
    {
        var resolution = AuthoredChartChoiceCatalogV1.Resolve(
            "Show chart that goes 5% higher in 1day from S&P500\nUser clarification or retry instruction: yes want to see indicators but you have some?",
            "yes want to see indicators but you have some?");

        Assert.True(resolution.NeedsClarification);
        Assert.Contains("RSI 14", resolution.ClarificationQuestion, StringComparison.Ordinal);
        Assert.Contains(resolution.ResearchScans, static scan => scan.Id == "next-day-plus-5");
    }

    [Fact]
    public void Host_preview_event_can_request_research_capture()
    {
        var args = new HostChartOverlayPreviewRequestedEventArgs(["ema-20"], startResearchCapture: true);

        Assert.Equal(["ema-20"], args.OverlayIds);
        Assert.True(args.StartResearchCapture);
    }

    [Fact]
    public void Famous_indicators_without_names_ask_for_a_host_catalog_choice()
    {
        var resolution = AuthoredChartChoiceCatalogV1.Resolve("Show me famous indicators on the chart");

        Assert.True(resolution.NeedsClarification);
        Assert.Contains("SMA 20", resolution.ClarificationQuestion, StringComparison.Ordinal);
        Assert.Contains("RSI 14", resolution.ClarificationQuestion, StringComparison.Ordinal);
        Assert.Contains("MACD", resolution.ClarificationQuestion, StringComparison.Ordinal);
        Assert.Contains("Bollinger", resolution.ClarificationQuestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbered_clarification_reply_selects_exact_overlays()
    {
        var resolution = AuthoredChartChoiceCatalogV1.Resolve(
            "Show me famous indicators on the chart\nUser clarification or retry instruction: 3 5",
            "3 5");

        Assert.False(resolution.NeedsClarification);
        Assert.Equal(["ema-20", "rsi-14"], resolution.Overlays.Select(static item => item.Id).ToArray());
    }

    [Fact]
    public void Named_indicators_compose_launchable_drawing_layers()
    {
        var resolution = AuthoredChartChoiceCatalogV1.Resolve("Chart AAPL with EMA and Bollinger bands");
        var drawing = AuthoredChartChoiceCatalogV1.ComposeDrawing(resolution.Overlays);

        Assert.Contains(drawing.Layers, static layer => layer.TypeId == "price.candles@1");
        Assert.Contains(drawing.Layers, static layer => layer.TypeId == "indicator.ema@1");
        Assert.Contains(drawing.Layers, static layer => layer.TypeId == "indicator.bollinger@1");
        Assert.Contains(drawing.Panes, static pane => pane.Role == AuthoredChartPaneRoleV1.Price);
    }

    [Fact]
    public void Next_day_plus_five_is_a_research_scan_not_a_strategy_overlay()
    {
        var resolution = AuthoredChartChoiceCatalogV1.Resolve(
            "Find S&P charts that rose at least +5% the next day");

        Assert.False(resolution.NeedsClarification);
        Assert.True(resolution.HasResearchScans);
        Assert.Contains(resolution.ResearchScans, static scan => scan.Id == "next-day-plus-5");
        Assert.Empty(resolution.Overlays);
        Assert.Contains("gallery", resolution.ResearchScans[0].ClarificationHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stochastic_and_vwap_resolve_from_catalog()
    {
        var resolution = AuthoredChartChoiceCatalogV1.Resolve("Show Stochastic and VWAP on the chart");

        Assert.Contains(resolution.Overlays, static item => item.Id == "stochastic-14-3-3");
        Assert.Contains(resolution.Overlays, static item => item.Id == "vwap");
        var state = NativeChartOverlaySelectionV1.FromHostOverlayIds(
            resolution.Overlays.Select(static item => item.Id));
        Assert.True(state.ShowStochastic);
        Assert.True(state.ShowVwap);
    }

    [Fact]
    public void Default_research_capture_overlay_ids_are_ema_rsi_atr()
    {
        Assert.Equal(
            ["ema-20", "rsi-14", "atr-14"],
            NativeChartOverlaySelectionV1.DefaultResearchCaptureOverlayIds);
        var state = NativeChartOverlaySelectionV1.FromHostOverlayIds(
            NativeChartOverlaySelectionV1.DefaultResearchCaptureOverlayIds);
        Assert.True(state.ShowEma);
        Assert.Equal(20, state.EmaPeriod);
        Assert.True(state.ShowRsi);
        Assert.True(state.ShowAtr);
    }

    [Fact]
    public void User_indicator_json_merges_into_catalog()
    {
        var user = UserChartIndicatorCatalogV1.Parse(
            """{"indicators":[{"id":"sma-200","displayName":"SMA 200","kind":"sma","period":200}]}""");
        var merged = AuthoredChartChoiceCatalogV1.MergeWithUserIndicators(user);

        Assert.Contains(merged, static item => item.Id == "sma-200");
        Assert.Contains(merged, static item => item.Id == "rsi-14");
    }

    [Theory]
    [InlineData("Why did EMA 20 fail here?", true)]
    [InlineData("Compare VWAP across these charts", true)]
    [InlineData("Add EMA 20", false)]
    [InlineData("Show RSI which I use for divergence", false)]
    [InlineData("Find similar pre-breakout charts", false)]
    public void Analytical_research_questions_are_detected(string text, bool expected) =>
        Assert.Equal(expected, AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(text));
}
