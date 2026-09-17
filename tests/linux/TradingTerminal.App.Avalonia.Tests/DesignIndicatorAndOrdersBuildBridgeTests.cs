using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class DesignIndicatorAndOrdersBuildBridgeTests
{
    [Fact]
    public void Adding_indicator_selects_it_and_period_input_edit_updates_canonical_token()
    {
        using var viewModel = CreateVm();

        viewModel.NewDesignIndicatorKind = "ema";
        viewModel.NewDesignIndicatorPeriodText = "20";
        viewModel.AddDesignIndicatorCommand.Execute(null);

        viewModel.SelectedDesignIndicator.Should().NotBeNull();
        viewModel.HasSelectedDesignIndicator.Should().BeTrue();
        viewModel.SelectedDesignIndicator!.CanonicalToken.Should().Be("ema(20)|input Close");

        viewModel.SelectedDesignIndicator.PeriodText = "50";
        viewModel.SelectedDesignIndicator.Input = "High";

        viewModel.SelectedDesignIndicator.DisplayLabel.Should().Be("ema(50)");
        viewModel.SelectedDesignIndicator.CanonicalToken.Should().Be("ema(50)|input High");
        viewModel.BuildDesignDraftContextBlock().Should().Contain("ema(50)|input High");
    }

    [Fact]
    public void Continue_to_Build_seeds_brief_and_composer_with_ORDERS_side()
    {
        using var viewModel = CreateVm();

        viewModel.DesignInstrumentText = "ES";
        viewModel.DesignTimeframeText = "5m";
        viewModel.DesignEntryRuleText = "close crosses above ema(20)";
        viewModel.DesignSizingRuleText = "1 contract";
        viewModel.DesignOrders.Side = DesignOrdersForm.SideShort;
        viewModel.DesignOrders.OrderType = DesignOrdersForm.OrderTypeLimit;
        viewModel.DesignOrders.TimeInForce = "Day";
        viewModel.DesignRiskLimits.Add(new DesignRiskLimitRow
        {
            Type = "Daily loss",
            ValueText = "2",
            Unit = "% of equity",
            Scope = "This strategy",
            Action = "Stop new entries",
        });

        viewModel.ReviewDesignRulesCommand.Execute(null);
        viewModel.DesignReviewHasRequired.Should().BeFalse();
        viewModel.DesignReviewWarningsAccepted = true;
        viewModel.CanContinueDesignToBuild.Should().BeTrue();

        viewModel.ContinueDesignToBuildCommand.Execute(null);

        viewModel.IsDesignReviewCurrent.Should().BeTrue();
        viewModel.Composer.Should().Contain("ORDERS:");
        viewModel.Composer.Should().Contain("Short");
        viewModel.Composer.Should().Contain("지정가");
        viewModel.Composer.Should().Contain("Build a runnable Paper strategy");
    }

    [Fact]
    public void Restoring_session_rehydrates_ORDERS_combo_from_summary_text()
    {
        var summary = "Both (flip) · Market · 시장가 · IOC";
        var sessions = new MemoryAuthoringSessionRepository();
        sessions.Save(new AuthoringSessionSnapshot(
            StrategyId: "orders-persist",
            DisplayName: "Orders persist",
            Chat: [],
            Thread: [],
            Files: [new StrategyFile(StrategyFile.DefaultName, "// draft")],
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow,
            DesignInstrumentText: "ES",
            DesignOrderRuleText: summary));

        using var restored = CreateVm(sessions);
        restored.SelectedSavedSession = sessions.List().First(s => s.StrategyId == "orders-persist");

        restored.DesignOrders.Side.Should().Be(DesignOrdersForm.SideBoth);
        restored.DesignOrders.OrderType.Should().Be(DesignOrdersForm.OrderTypeMarket);
        restored.DesignOrders.TimeInForce.Should().Be("IOC");
    }

    [Fact]
    public void Restoring_research_findings_populates_saved_condition_options()
    {
        var condition = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var reference = new ResearchAnalysisReferenceV1(
            ResearchAnalysisReferenceV1.CurrentSchemaVersion,
            "A",
            "Finding A",
            DateTimeOffset.UnixEpoch,
            condition,
            SearchResult: null,
            Selection: null,
            IndicatorBindings: [new ResearchIndicatorBindingV1("ema-20", "ema", 20)]);
        var json = ResearchAnalysisReferenceCanonicalJsonV1.SerializeMany([reference]);
        var sessions = new MemoryAuthoringSessionRepository();
        sessions.Save(new AuthoringSessionSnapshot(
            StrategyId: "saved-condition",
            DisplayName: "Saved condition",
            Chat: [],
            Thread: [],
            Files: [new StrategyFile(StrategyFile.DefaultName, "// draft")],
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow,
            ResearchAnalysisReferencesJson: json));

        using var restored = CreateVm(sessions);
        restored.SelectedSavedSession = sessions.List().First(s => s.StrategyId == "saved-condition");

        restored.HasDesignSignalOptions.Should().BeTrue();
        restored.DesignSignalOptions.Should().ContainSingle()
            .Which.Should().Contain("Finding A")
            .And.Contain(condition.ConditionId);
    }

    [Fact]
    public void Continue_to_Build_keeps_ORDERS_complete_for_intent_seed_mapping()
    {
        using var viewModel = CreateVm();

        viewModel.DesignInstrumentText = "ES";
        viewModel.DesignTimeframeText = "5m";
        viewModel.DesignEntryRuleText = "close crosses above ema(20)";
        viewModel.DesignSizingRuleText = "1 contract";
        viewModel.DesignOrders.Side = DesignOrdersForm.SideLong;
        viewModel.DesignOrders.OrderType = DesignOrdersForm.OrderTypeMarket;
        viewModel.DesignOrders.TimeInForce = "Day";
        viewModel.DesignRiskLimits.Add(new DesignRiskLimitRow
        {
            Type = "Daily loss",
            ValueText = "2",
            Unit = "% of equity",
            Scope = "This strategy",
            Action = "Stop new entries",
        });

        viewModel.ReviewDesignRulesCommand.Execute(null);
        viewModel.DesignReviewWarningsAccepted = true;
        viewModel.ContinueDesignToBuildCommand.Execute(null);

        viewModel.DesignOrders.IsComplete.Should().BeTrue();
        var seeds = DesignOrdersToIntentSeedV1.BuildSeeds(new DesignOrdersToIntentSeedV1.DesignOrdersSeedInput(
            viewModel.DesignOrders.Side,
            viewModel.DesignOrders.OrderType,
            viewModel.DesignOrders.TimeInForce,
            null));
        seeds.Should().ContainKey(DesignOrdersToIntentSeedV1.RequirementOrderType);
        seeds[DesignOrdersToIntentSeedV1.RequirementOrderType].Should().Contain("Market");
        viewModel.Status.Should().Contain("intent");
    }

    private static StrategyAuthoringViewModel CreateVm(IAuthoringSessionRepository? repository = null) =>
        new(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: repository ?? new MemoryAuthoringSessionRepository());

    private sealed class MemoryAuthoringSessionRepository(params AuthoringSessionSnapshot[] sessions)
        : IAuthoringSessionRepository
    {
        private readonly List<AuthoringSessionSnapshot> _sessions = [.. sessions];

        public IReadOnlyList<AuthoringSessionSnapshot> List() =>
            [.. _sessions.OrderByDescending(static session => session.UpdatedUtc)];

        public bool Save(AuthoringSessionSnapshot session)
        {
            _sessions.RemoveAll(existing => existing.StrategyId == session.StrategyId);
            _sessions.Add(session with { UpdatedUtc = DateTime.UtcNow });
            return true;
        }

        public void Delete(string strategyId) =>
            _sessions.RemoveAll(session => session.StrategyId == strategyId);
    }

    private sealed class StubCompiler : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) => StrategyCompileResult.Failed([]);
    }

    private sealed class StubRegistry : IBacktestStrategyRegistry
    {
        public IReadOnlyList<BacktestStrategyOption> All => [];
        public BacktestStrategyOption? Find(string id) => null;
        public void Register(BacktestStrategyOption option)
        {
        }

        public bool Remove(string id) => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
