using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class StrategyResearchDatasetAuthoringTests
{
    [Fact]
    public void Host_chart_selection_becomes_a_persisted_leakage_safe_event_sample()
    {
        var repository = new MemorySessionRepository();
        using (var viewModel = Create(repository))
        {
            viewModel.SetResearchChartSelection(Selection());

            viewModel.IsResearchStage.Should().BeTrue();
            viewModel.HasResearchChartSelection.Should().BeTrue();
            viewModel.MarkPreBreakoutCommand.CanExecute(null).Should().BeTrue();

            viewModel.MarkPreBreakoutCommand.Execute(null);

            viewModel.HasResearchChartSelection.Should().BeFalse();
            viewModel.ResearchEventSamples.Should().ContainSingle();
            viewModel.ResearchEventSamples[0].Label.Should().Be(ResearchEventLabelKindV1.PreBreakout);
            viewModel.ResearchDatasetDefinition!.LeakagePolicy.ExcludeOutcomeWindowFromFeatures.Should().BeTrue();
            viewModel.ResearchDatasetDefinition.LeakagePolicy.FitTransformsOnTrainingOnly.Should().BeTrue();
            viewModel.StrategyWorkspace.Bindings.DatasetDefinitionHashSha256.Should().Be(
                ResearchDatasetCanonicalJsonV1.Hash(viewModel.ResearchDatasetDefinition));
            viewModel.StrategyWorkspace.Stage(StrategyWorkspaceStageV1.Validate).State
                .Should().Be(StrategyWorkspaceStageStateV1.Unavailable);
            repository.Saved!.ResearchDatasetJson.Should().NotBeNullOrWhiteSpace();
            repository.Saved.StrategyWorkspaceJson.Should().NotBeNullOrWhiteSpace();
        }

        using var restored = Create(repository);
        restored.SelectedSavedSession = repository.Saved;
        restored.IsResearchStage.Should().BeTrue();
        restored.ResearchEventSamples.Should().ContainSingle();
        restored.ResearchEventSamples[0].EventSampleId.Should().StartWith("event-");
        restored.StrategyWorkspace.Bindings.DatasetDefinitionHashSha256.Should().Be(
            ResearchDatasetCanonicalJsonV1.Hash(restored.ResearchDatasetDefinition!));
    }

    [Fact]
    public void Same_exact_selection_and_label_is_deduplicated()
    {
        using var viewModel = Create(new MemorySessionRepository());

        viewModel.SetResearchChartSelection(Selection());
        viewModel.MarkNeutralCommand.Execute(null);
        viewModel.SetResearchChartSelection(Selection());
        viewModel.MarkNeutralCommand.Execute(null);

        viewModel.ResearchEventSamples.Should().ContainSingle();
        viewModel.Status.Should().Contain("already exist");
    }

    private static StrategyAuthoringViewModel Create(IAuthoringSessionRepository repository) => new(
        new StubCompiler(),
        new StubRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        sessionRepository: repository);

    private static ResearchChartSelectionV1 Selection()
    {
        var start = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
        return new ResearchChartSelectionV1(
            new InstrumentId(42),
            "BTC-USD",
            BarSize.OneMinute,
            start,
            start.AddMinutes(10),
            start.AddMinutes(10),
            start.AddMinutes(15),
            StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape);
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
