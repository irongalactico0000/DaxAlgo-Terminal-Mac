using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>
/// Step 4 / V04: selection → condition → named refs A/B survive session JSON round-trip
/// (quit/reopen stand-in) without chart re-upload.
/// </summary>
public sealed class ResearchReferenceContinuityTests
{
    private static readonly JsonSerializerOptions SessionJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Selection_condition_and_refs_A_B_restore_after_session_reload()
    {
        var repository = new MemorySessionRepository();
        var selectionA = Selection("BTC-USD", minutesOffset: 0);
        var selectionB = Selection("ETH-USD", minutesOffset: 60);

        using (var viewModel = Create(repository))
        {
            viewModel.SetResearchChartSelection(
                selectionA,
                indicatorBindings: [new ResearchIndicatorBindingV1("ema-21", "ema", 21)]);
            viewModel.PendingConditionMultipleText = "2";
            viewModel.PendingConditionLookbackText = "20";
            viewModel.ApplyPendingResearchConditionCommand.Execute(null);
            viewModel.SaveResearchReferenceACommand.Execute(null);

            viewModel.SetResearchChartSelection(
                selectionB,
                indicatorBindings: [new ResearchIndicatorBindingV1("ema-50", "ema", 50)]);
            viewModel.PendingConditionMultipleText = "3";
            viewModel.ApplyPendingResearchConditionCommand.Execute(null);
            viewModel.SaveResearchReferenceBCommand.Execute(null);

            viewModel.HasResearchReferenceA.Should().BeTrue();
            viewModel.HasResearchReferenceB.Should().BeTrue();
            repository.Saved.Should().NotBeNull();
            repository.Saved!.ResearchAnalysisReferencesJson.Should().NotBeNullOrWhiteSpace();
            repository.Saved.ResearchChartSelectionJson.Should().NotBeNullOrWhiteSpace();
            repository.Saved.ResearchConditionJson.Should().NotBeNullOrWhiteSpace();
            repository.Saved.ResearchIndicatorBindingsJson.Should().NotBeNullOrWhiteSpace();
        }

        // Disk-shaped round-trip (same options as AuthoringSessionStore).
        var diskJson = JsonSerializer.Serialize(repository.Saved!, SessionJson);
        var fromDisk = JsonSerializer.Deserialize<AuthoringSessionSnapshot>(diskJson, SessionJson)
            ?? throw new InvalidOperationException("Session JSON did not deserialize.");
        repository.Saved = fromDisk with { UpdatedUtc = DateTime.UtcNow };

        using var restored = Create(repository);
        restored.SelectedSavedSession = repository.Saved;
        restored.HasResearchReferenceA.Should().BeTrue();
        restored.HasResearchReferenceB.Should().BeTrue();
        restored.HasResearchChartSelection.Should().BeTrue(
            "pending brush must restore without re-selecting on the chart");
        restored.PendingResearchChartSelection!.CanonicalSymbol.Should().Be("ETH-USD");
        restored.PendingResearchIndicatorBindings.Should().ContainSingle(b => b.Period == 50);
        restored.PendingResearchCondition!.Threshold.Should().Be(3);

        restored.RestoreResearchReferenceACommand.Execute(null);
        restored.PendingResearchCondition!.Threshold.Should().Be(2);
        restored.PendingResearchChartSelection!.CanonicalSymbol.Should().Be("BTC-USD");
        restored.PendingResearchIndicatorBindings.Should().ContainSingle(b => b.Period == 21);

        restored.RestoreResearchReferenceBCommand.Execute(null);
        restored.PendingResearchCondition!.Threshold.Should().Be(3);
        restored.PendingResearchChartSelection!.CanonicalSymbol.Should().Be("ETH-USD");
        restored.PendingResearchIndicatorBindings.Should().ContainSingle(b => b.Period == 50);

        var hashA = ResearchConditionDefinitionV1.VolumeMultiple(2, 20).VersionHashSha256;
        var hashB = ResearchConditionDefinitionV1.VolumeMultiple(3, 20).VersionHashSha256;
        hashA.Should().NotBe(hashB);
        restored.PendingResearchCondition.VersionHashSha256.Should().Be(hashB);
    }

    [Fact]
    public void Pending_selection_alone_persists_without_labeled_sample()
    {
        var repository = new MemorySessionRepository();
        using (var viewModel = Create(repository))
        {
            viewModel.SetResearchChartSelection(
                Selection("SOL-USD", minutesOffset: 0),
                indicatorBindings: [new ResearchIndicatorBindingV1("rsi-14", "rsi", 14)]);
            repository.Saved.Should().NotBeNull();
            repository.Saved!.ResearchChartSelectionJson.Should().NotBeNullOrWhiteSpace();
            repository.Saved.ResearchDatasetJson.Should().BeNull(
                "brush alone must not require a labeled dataset sample");
        }

        using var restored = Create(repository);
        restored.SelectedSavedSession = repository.Saved;
        restored.HasResearchChartSelection.Should().BeTrue();
        restored.PendingResearchChartSelection!.CanonicalSymbol.Should().Be("SOL-USD");
        restored.PendingResearchIndicatorBindings.Should().ContainSingle(b => b.Kind == "rsi" && b.Period == 14);
    }

    private static StrategyAuthoringViewModel Create(IAuthoringSessionRepository repository) => new(
        new StubCompiler(),
        new StubRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        sessionRepository: repository);

    private static ResearchChartSelectionV1 Selection(string symbol, int minutesOffset)
    {
        var start = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutesOffset);
        return new ResearchChartSelectionV1(
            new InstrumentId(42),
            symbol,
            BarSize.OneMinute,
            start,
            start.AddMinutes(10),
            start.AddMinutes(10),
            start.AddMinutes(15),
            StrategyDataRequirement.Bars);
    }

    private sealed class MemorySessionRepository : IAuthoringSessionRepository
    {
        public AuthoringSessionSnapshot? Saved { get; set; }

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
