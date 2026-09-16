using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class StrategyDraftAuthoringTests
{
    [Fact]
    public void Host_chart_draft_shows_stop_and_target_summary_without_orders()
    {
        using var viewModel = Create();

        viewModel.SetStrategyDraft(Draft());

        viewModel.IsResearchStage.Should().BeTrue();
        viewModel.HasStrategyDraft.Should().BeTrue();
        viewModel.StrategyDraftSummaryText.Should().Contain("stop 100.25");
        viewModel.StrategyDraftSummaryText.Should().Contain("target 112.5");
        viewModel.StrategyDraftSummaryText.Should().Contain("unlocked");
        viewModel.Status.Should().Contain("does not place orders");
    }

    [Fact]
    public void Clear_draft_removes_summary()
    {
        using var viewModel = Create();
        viewModel.SetStrategyDraft(Draft());

        viewModel.ClearStrategyDraftCommand.Execute(null);

        viewModel.HasStrategyDraft.Should().BeFalse();
        viewModel.StrategyDraftSummaryText.Should().Contain("No chart strategy draft");
    }

    [Fact]
    public void Chart_strategy_draft_survives_session_restart()
    {
        var repository = new MemorySessionRepository();
        using (var viewModel = Create(repository))
        {
            viewModel.SetStrategyDraft(Draft());
            repository.Saved.Should().NotBeNull();
            repository.Saved!.StrategyDraftJson.Should().NotBeNullOrWhiteSpace();
        }

        using var restored = Create(repository);
        restored.SelectedSavedSession = repository.Saved;
        restored.HasStrategyDraft.Should().BeTrue();
        restored.StrategyDraftSummaryText.Should().Contain("stop 100.25");
        restored.StrategyDraftSummaryText.Should().Contain("target 112.5");
        restored.PendingStrategyDraft!.DraftId.Should().Be("draft-test");
    }

    private static StrategyAuthoringViewModel Create(IAuthoringSessionRepository? repository = null) => new(
        new StubCompiler(),
        new StubRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        sessionRepository: repository ?? new MemorySessionRepository());

    private static StrategyDraftV1 Draft()
    {
        var scope = new StrategyDraftScopeV1(new InstrumentId(42), "AAPL", BarSize.OneHour);
        var draft = StrategyDraftV1.Create(scope, draftId: "draft-test");
        draft = StrategyDraftGestureApplierV1.UpsertStop(draft, "chart-stop", 100.25m);
        return StrategyDraftGestureApplierV1.UpsertTarget(draft, "chart-target", 112.5m);
    }

    private sealed class MemorySessionRepository : IAuthoringSessionRepository
    {
        public AuthoringSessionSnapshot? Saved { get; private set; }

        public IReadOnlyList<AuthoringSessionSnapshot> List() => Saved is null ? [] : [Saved];

        public bool Save(AuthoringSessionSnapshot session)
        {
            Saved = session with { UpdatedUtc = DateTime.UtcNow };
            return true;
        }

        public void Delete(string strategyId)
        {
            if (Saved?.StrategyId == strategyId) Saved = null;
        }
    }

    private sealed class StubCompiler : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) => StrategyCompileResult.Failed([]);
    }

    private sealed class StubRegistry : IBacktestStrategyRegistry
    {
        public IReadOnlyList<BacktestStrategyOption> All => [];
        public BacktestStrategyOption? Find(string id) => null;
        public void Register(BacktestStrategyOption option) { }
        public bool Remove(string id) => false;
        public event EventHandler? Changed { add { } remove { } }
    }
}
