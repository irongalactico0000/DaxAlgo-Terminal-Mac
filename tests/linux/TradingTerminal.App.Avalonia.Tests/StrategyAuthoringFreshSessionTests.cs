using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class StrategyAuthoringFreshSessionTests
{
    [Fact]
    public void Generation_lane_row_exposes_real_phases_and_rejects_late_regression()
    {
        var row = new StrategyGenerationLaneProgressRow(StrategyGenerationLaneV1.TypedGraph);

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.PreparingRequest));
        row.StateLabel.Should().Be("PREPARING");
        row.PipelineText.Should().Contain("● PREPARE");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.WaitingForModel));
        row.StateLabel.Should().Be("WAITING FOR MODEL");
        row.StateDetail.Should().Contain("waiting for the model response");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.ValidatingArtifact));
        row.StateDetail.Should().Contain("installed package check");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.Completed,
            "Installed package validation passed; nothing was tested or run."));
        row.StateLabel.Should().Be("READY");
        row.PipelineText.Should().Be("✓ PREPARE   ✓ MODEL   ✓ PARSE   ✓ CHECK");

        row.Apply(new StrategyGenerationLaneProgressV1(
            row.Lane,
            StrategyGenerationLaneProgressStateV1.ParsingResponse,
            "late callback"));
        row.State.Should().Be(StrategyGenerationLaneProgressStateV1.Completed);
        row.StateDetail.Should().Be("Installed package validation passed; nothing was tested or run.");
    }

    [Fact]
    public void Generation_lane_row_exposes_terminal_artifact_and_raw_failure_for_inspection()
    {
        const string source = "def on_event(state, event):\n    return state\n";
        var lane = StrategyGenerationLaneV1.VibePython;
        var candidate = new StrategyGenerationCandidateV1(
            StrategyGenerationCandidateV1.CurrentSchemaVersion,
            "preview/vibe-python",
            lane,
            new string('a', 64),
            StrategyGenerationPackageCatalogV1.RequireBinding(lane),
            "Preview candidate",
            "Inspect the exact source before choosing it.",
            [],
            [],
            [],
            [],
            new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.VibePythonSource,
                "strategy.py",
                "python",
                source,
                null),
            "Review-only fixture.",
            []);
        var generated = new StrategyGenerationLaneResultV1(
            lane,
            StrategyGenerationReadinessV1.Generated,
            candidate,
            StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate),
            [],
            new StrategyGenerationAgentRunV1(
                "vibe-agent",
                "test-provider",
                null,
                true,
                null,
                null,
                CodegenUsage.None));
        var generatedRow = new StrategyGenerationLaneProgressRow(lane);

        generatedRow.Apply(new StrategyGenerationLaneProgressV1(
            lane,
            StrategyGenerationLaneProgressStateV1.Completed,
            "Artifact ready for review.",
            generated));

        generatedRow.HasResult.Should().BeTrue();
        generatedRow.ResultOption.Should().NotBeNull();
        generatedRow.ResultOption!.Result.Should().BeSameAs(generated);
        generatedRow.InspectablePreview.Should().Be(source);
        generatedRow.PreviewHeading.Should().Be("strategy.py · exact generated artifact");

        const string rawResponse = "{ broken model response";
        var issue = new StrategyCandidateGenerationIssueV1(
            StrategyCandidateGenerationIssueSeverityV1.Error,
            "LANE_JSON_INVALID",
            "candidate",
            "The candidate envelope could not be parsed.");
        var failed = new StrategyGenerationLaneResultV1(
            StrategyGenerationLaneV1.TypedGraph,
            StrategyGenerationReadinessV1.Failed,
            null,
            null,
            [issue],
            new StrategyGenerationAgentRunV1(
                "graph-agent",
                "test-provider",
                null,
                false,
                "The model returned invalid JSON.",
                rawResponse,
                CodegenUsage.None));
        var failedRow = new StrategyGenerationLaneProgressRow(failed.Lane);

        failedRow.Apply(new StrategyGenerationLaneProgressV1(
            failed.Lane,
            StrategyGenerationLaneProgressStateV1.Failed,
            "Candidate envelope invalid.",
            failed));

        failedRow.HasResult.Should().BeTrue();
        failedRow.ResultOption!.IsFailed.Should().BeTrue();
        failedRow.ResultOption.FirstIssue.Should().BeSameAs(issue);
        failedRow.InspectablePreview.Should().Be(rawResponse,
            "the exact failed provider response is more inspectable than a flattened status string");
        failedRow.PreviewHeading.Should().Be("Raw model response · candidate envelope invalid");

        var whitespaceArtifact = candidate with
        {
            Artifact = candidate.Artifact with { Source = "   " },
        };
        var whitespaceResult = failed with
        {
            Lane = lane,
            Candidate = whitespaceArtifact,
            CandidateHashSha256 = StrategyGenerationCandidateCanonicalJsonV1.Hash(whitespaceArtifact),
        };
        var whitespaceOption = new StrategyGenerationCandidateOption(whitespaceResult);
        whitespaceOption.InspectablePreview.Should().Be(rawResponse);
        whitespaceOption.PreviewHeading.Should().Be("Raw model response · candidate envelope invalid",
            "the heading must describe the content actually displayed, not a blank artifact shell");
    }

    [Fact]
    public void New_strategy_clears_session_transients_without_reselecting_the_saved_chat()
    {
        var saved = new AuthoringSessionSnapshot(
            StrategyId: "saved-strategy",
            DisplayName: "Saved strategy",
            Chat:
            [
                new AuthoringChatEntry(
                    AuthoringChatEntry.User,
                    "Keep this saved conversation",
                    DateTime.Now),
            ],
            Thread: [],
            Files: [new StrategyFile("Saved.cs", "// saved")],
            InputTokens: 120,
            OutputTokens: 30,
            GenerateCandidateFirst: false,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var sessions = new MemoryAuthoringSessionRepository(saved);
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);

        viewModel.SelectedSavedSession = sessions.List().First();
        viewModel.SelectedSavedSession.Should().NotBeNull();
        viewModel.Composer = "unsent follow-up";
        viewModel.StarterSearchText = "futures";
        viewModel.SelectedStarterFamily = viewModel.StarterFamilyOptions[1];
        viewModel.SelectedStarterHorizon = viewModel.StarterHorizonOptions[1];
        viewModel.SelectedStarterData = viewModel.StarterDataOptions[1];
        viewModel.InputTokens = 321;
        viewModel.OutputTokens = 123;
        viewModel.CachedTokens = 99;
        viewModel.WorkbenchTab = 3;
        viewModel.Activity.Add("old activity");
        viewModel.Tasks.Add(new BuildTask("old task"));
        viewModel.AiStatus = "old provider status";
        viewModel.ElapsedText = "2m elapsed";
        viewModel.ElapsedCompact = "2:00";
        viewModel.WorkingVerb = "Generating…";
        viewModel.StepText = "step 3 of 4";
        viewModel.CompiledOk = true;
        viewModel.IsRegistered = true;
        viewModel.AwaitingAnswer = true;
        viewModel.ReviewOpen = true;
        var rawResult = new StrategyGenerationLaneResultV1(
            StrategyGenerationLaneV1.TypedGraph,
            StrategyGenerationReadinessV1.Failed,
            null,
            null,
            [new StrategyCandidateGenerationIssueV1(
                StrategyCandidateGenerationIssueSeverityV1.Error,
                "RAW_FAILURE",
                "candidate",
                "Transient raw response fixture.")],
            new StrategyGenerationAgentRunV1(
                "graph-agent",
                "test-provider",
                null,
                false,
                "Transient failure.",
                "transient raw provider payload",
                CodegenUsage.None));
        var rawRow = new StrategyGenerationLaneProgressRow(rawResult.Lane);
        rawRow.Apply(new StrategyGenerationLaneProgressV1(
            rawResult.Lane,
            StrategyGenerationLaneProgressStateV1.Failed,
            "Transient failure.",
            rawResult));
        viewModel.GenerationLaneProgressRows.Add(rawRow);
        viewModel.SelectedGenerationLaneProgressRow = rawRow;

        viewModel.NewChatCommand.Execute(null);

        sessions.SaveCalls.Should().Be(1, "the outgoing saved conversation must remain available");
        viewModel.SavedSessions.Should().ContainSingle(session => session.StrategyId == "saved-strategy");
        viewModel.SelectedSavedSession.Should().BeNull("a new strategy is not a restored session");
        viewModel.StrategyId.Should().Be("myStrategy");
        viewModel.DisplayName.Should().Be("My custom strategy");
        viewModel.GenerateCandidateFirst.Should().BeTrue();
        viewModel.Composer.Should().BeEmpty();
        viewModel.StarterSearchText.Should().BeEmpty();
        viewModel.SelectedStarterFamily.Should().Be(viewModel.StarterFamilyOptions[0]);
        viewModel.SelectedStarterHorizon.Should().Be(viewModel.StarterHorizonOptions[0]);
        viewModel.SelectedStarterData.Should().Be(viewModel.StarterDataOptions[0]);
        viewModel.VisibleStarterBriefs.Should().HaveCount(viewModel.AllStarterBriefs.Count - 1,
            "QuoteL1 EMA smoke is Build-only and must not appear in Design/Research starters");
        viewModel.VisibleStarterBriefs.Should().NotContain(brief =>
            string.Equals(brief.Id, "starter.quote-l1-ema-smoke", StringComparison.Ordinal));
        viewModel.CanOpenDesignScreen.Should().BeTrue(
            "Strategy Builder starts on Design; Research Studio is the separate investigation workspace");
        viewModel.HasResearchDesignHandoff.Should().BeFalse();
        viewModel.InputTokens.Should().Be(0);
        viewModel.OutputTokens.Should().Be(0);
        viewModel.CachedTokens.Should().Be(0);
        viewModel.WorkbenchTab.Should().Be(3, "a fresh Design screen must select the visible Request tab");
        viewModel.IsDesignScreen.Should().BeTrue();
        viewModel.IsChartDesignStage.Should().BeTrue();
        viewModel.IsResearchStage.Should().BeFalse();
        viewModel.ActiveScreenTitle.Should().Be("Design");
        viewModel.CanOpenResearchScreen.Should().BeTrue();
        viewModel.ShowResearchWorkspace.Should().BeFalse();
        viewModel.IsResearchStudioShell.Should().BeFalse();
        viewModel.ExecutionFillModelOptions.Should().ContainSingle()
            .Which.Should().Be(StrategyAuthoringViewModel.SupportedExecutionFillModel);
        viewModel.ExecutionBookTypeOptions.Should().ContainSingle()
            .Which.Should().Be(StrategyAuthoringViewModel.SupportedExecutionBookType);
        viewModel.TryGetAppliedExecutionFidelity(out var applied, out var rejection).Should().BeTrue(rejection);
        applied.DataModeToken.Should().Contain("L1TouchFillModel");
        applied.DataModeToken.Should().Contain("applied");
        viewModel.ExecutionEnableQueuePosition = true;
        viewModel.TryGetAppliedExecutionFidelity(out _, out rejection).Should().BeFalse();
        rejection.Should().Contain("Queue");
        viewModel.ExecutionEnableQueuePosition = false;
        viewModel.ExecutionEnablePartialFills = true;
        viewModel.ExecutionLatencyMs = 25;
        viewModel.TryGetAppliedExecutionFidelity(out var withPartials, out rejection).Should().BeTrue(rejection);
        withPartials.PartialsEnabled.Should().BeTrue();
        withPartials.LatencyMs.Should().Be(25);
        withPartials.DataModeToken.Should().Contain("partials=max4");
        withPartials.DataModeToken.Should().Contain("latencyMs=25");
        viewModel.ExecutionEnablePartialFills = false;
        viewModel.ExecutionLatencyMs = 0;
        viewModel.ShowImplementationTabs.Should().BeFalse(
            "Design must not expose Strategy.cs / Code — that belongs in Build");
        viewModel.ActiveArtifactKindText.Should().NotBeNullOrWhiteSpace();
        viewModel.ShowDesignInspector.Should().BeTrue(
            "Design must open the rule workbench immediately");
        viewModel.ShowDesignRuleEditor.Should().BeTrue();
        viewModel.ShowWorkbenchPanel.Should().BeTrue();
        viewModel.DesignInspectorWidth.Should().Be(390);
        viewModel.DesignInspectorMinWidth.Should().Be(0);
        viewModel.ConversationColumnMaxWidth.Should().Be(double.PositiveInfinity);
        viewModel.MainWorkspaceColumnDefinitions.Should().Be("Auto,*,4,Auto");
        viewModel.ResearchStageState.Should().Be("PENDING");
        viewModel.DesignStageState.Should().Be("PENDING");
        viewModel.ResearchStageState.Should().NotBe("OPTIONAL");
        viewModel.DesignStageState.Should().NotBe("OPTIONAL");
        viewModel.StrategyWorkspace.WorkspaceId.Should().Be("myStrategy");
        viewModel.StrategyWorkspace.Stages.Should().HaveCount(6);
        viewModel.ValidateStageState.Should().Be("LOCKED");
        viewModel.PaperStageState.Should().Be("LOCKED");
        viewModel.Messages.Should().BeEmpty();
        viewModel.Activity.Should().BeEmpty();
        viewModel.Tasks.Should().BeEmpty();
        viewModel.Diagnostics.Should().BeEmpty();
        viewModel.AiStatus.Should().BeNull();
        viewModel.ElapsedText.Should().BeNull();
        viewModel.ElapsedCompact.Should().BeNull();
        viewModel.WorkingVerb.Should().BeNull();
        viewModel.StepText.Should().BeNull();
        viewModel.CompiledOk.Should().BeFalse();
        viewModel.IsRegistered.Should().BeFalse();
        viewModel.AwaitingAnswer.Should().BeFalse();
        viewModel.ReviewOpen.Should().BeFalse();
        viewModel.GenerationLaneProgressRows.Should().BeEmpty();
        viewModel.SelectedGenerationLaneProgressRow.Should().BeNull(
            "starting a new strategy must release transient raw provider output");
        viewModel.Files.Should().ContainSingle(file => file.Name == StrategyFile.DefaultName);
        viewModel.SelectedFile.Should().BeSameAs(viewModel.Files[0]);
        viewModel.Status.Should().Contain("rules");
        viewModel.CandidateEmptyTitle.Should().Be("Trading rules");
        viewModel.AuthoredUnitSpecification.Should().BeNull();
        viewModel.ConfirmedStrategyIntent.Should().BeNull();
        viewModel.CanUseObservationInDesign.Should().BeFalse(
            "a blank project has no observation to promote yet");
    }

    [Fact]
    public void Opening_ranked_screen_row_binds_Hyperion_to_that_symbol()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.IsResearchStudioShell = true;
        viewModel.SetBoundResearchChartInstrument("BTCUSD");
        viewModel.ActiveResearchContextText.Should().Contain("BTCUSD");

        var from = new DateTime(2026, 9, 15, 7, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(24);
        var row = new ResearchMarketScreenRowV1(
            Rank: 1,
            CanonicalSymbol: "C",
            DisplayName: "Citigroup",
            MetricValue: 700_123,
            MetricLabel: "estimated traded value",
            MetricDefinition: "Σ(volume × close)",
            BarsUsed: 24,
            BarSizeLabel: "1h",
            WindowFromUtc: from,
            WindowToUtcExclusive: to,
            VolumeSum: 1,
            TradedValueSum: 700_123,
            PercentChange: null);

        string? previewedSymbol = null;
        viewModel.HostChartOverlayPreviewRequested += (_, args) =>
            previewedSymbol = args.PreferredSymbol;

        viewModel.OpenResearchScreenRowCommand.Execute(row);

        previewedSymbol.Should().Be("C");
        viewModel.SelectedResearchScreenRow!.CanonicalSymbol.Should().Be("C");
        viewModel.ResearchChartInstrumentText.Should().Be("C");
        viewModel.ActiveResearchContextText.Should().StartWith("#1 C");
        viewModel.ResearchLinkedContextText.Should().Contain("linked");
        viewModel.ResearchIndicatorInspectText.Should().Contain("C");
        viewModel.ResearchScreenUniverseOptions.Should().Contain("S&P 100");
        viewModel.ResearchScreenUniverseOptions.Should().Contain("Available instruments");
    }

    [Fact]
    public void Open_chart_instrument_enables_market_structure_without_ranking()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.IsResearchStudioShell = true;
        viewModel.SelectedResearchScreenRow.Should().BeNull();
        viewModel.CanOpenResearchMarketStructure.Should().BeFalse();

        viewModel.SetBoundResearchChartInstrument("BTCUSD");

        viewModel.CanOpenResearchMarketStructure.Should().BeTrue(
            "an open chart is enough — Rank is not required to inspect Order book / Footprint / Bookmap");
        viewModel.OpenResearchOrderBookCommand.CanExecute(null).Should().BeTrue();

        string? opened = null;
        viewModel.HostResearchMarketStructureRequested += (_, args) =>
            opened = $"{args.ViewKind}:{args.CanonicalSymbol}";
        viewModel.OpenResearchOrderBookCommand.Execute(null);
        opened.Should().Be("OrderBook:BTCUSD");
    }

    [Fact]
    public void Research_led_path_saves_observation_before_any_strategy_artifact()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.IsResearchStudioShell = true;
        viewModel.NewChatCommand.Execute(null);
        viewModel.IsResearchStage.Should().BeTrue();
        viewModel.ShowResearchWorkspace.Should().BeTrue(
            "Research Studio shell is the only host for the Research workspace chrome");
        viewModel.AuthoredUnitSpecification.Should().BeNull();
        viewModel.CompiledOk.Should().BeFalse();
        viewModel.IsRegistered.Should().BeFalse();
        viewModel.DesignStageState.Should().Be("PENDING");
        viewModel.BuildStageState.Should().Be("PENDING");

        var starter = viewModel.AllStarterBriefs.First(brief =>
            !string.Equals(brief.Id, "starter.quote-l1-ema-smoke", StringComparison.Ordinal));
        viewModel.UseStarterPromptCommand.Execute(starter);
        viewModel.IsResearchStage.Should().BeTrue();
        viewModel.Composer.Should().StartWith("Investigate before writing trading rules:");
        viewModel.ConfirmedStrategyIntent.Should().BeNull(
            "templates seed a research question, not a confirmed strategy intent");
        viewModel.AuthoredUnitSpecification.Should().BeNull();
        viewModel.DesignStageState.Should().Be("PENDING");
        viewModel.BuildStageState.Should().Be("PENDING");

        var start = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
        viewModel.SetResearchChartSelection(
            new ResearchChartSelectionV1(
                new InstrumentId(7),
                "MSFT",
                BarSize.OneHour,
                start,
                start.AddHours(4),
                start.AddHours(4),
                start.AddHours(8),
                StrategyDataRequirement.Bars),
            indicatorBindings:
            [
                new ResearchIndicatorBindingV1("ema-50", "ema", 50),
                new ResearchIndicatorBindingV1("rsi-14", "rsi", 14),
            ]);
        viewModel.PendingConditionMultipleText = "2";
        viewModel.PendingConditionLookbackText = "20";
        viewModel.ApplyPendingResearchConditionCommand.Execute(null);
        viewModel.SaveResearchReferenceACommand.Execute(null);

        viewModel.AuthoredUnitSpecification.Should().BeNull(
            "saving a research observation must not freeze a visualizer or strategy specification");
        viewModel.CompiledOk.Should().BeFalse();
        viewModel.IsRegistered.Should().BeFalse();
        viewModel.CanUseObservationInDesign.Should().BeTrue();
        viewModel.DesignStageState.Should().Be("PENDING",
            "Design stays pending until Use in Design promotes evidence into rules");
        viewModel.BuildStageState.Should().Be("PENDING");
        viewModel.ShowImplementationTabs.Should().BeFalse();

        viewModel.UseObservationInDesignCommand.Execute(null);

        viewModel.IsChartDesignStage.Should().BeTrue();
        viewModel.HasResearchDesignHandoff.Should().BeTrue();
        viewModel.CanOpenDesignScreen.Should().BeTrue();
        viewModel.WorkingFlowMapText.Should().Contain("2 Design ✓");
        viewModel.Composer.Should().Contain("Use this saved Research finding in Design");
        viewModel.Composer.Should().Contain("MSFT");
        viewModel.Composer.Should().Contain("ema");
        viewModel.Composer.Should().Contain("Saved finding");
        viewModel.AuthoredUnitSpecification.Should().BeNull(
            "Use in Design attaches evidence only — no generate/compile/register");
        viewModel.CompiledOk.Should().BeFalse();
        viewModel.IsRegistered.Should().BeFalse();
        viewModel.BuildStageState.Should().Be("PENDING");
        viewModel.Status.Should().Match(s =>
            s.Contains("No compile or register", StringComparison.Ordinal) ||
            s.Contains("Strategy Builder", StringComparison.Ordinal));
    }

    [Fact]
    public void Research_overlays_are_analysis_only_and_do_not_complete_Design_or_Build()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.NewChatCommand.Execute(null);
        viewModel.PendingResearchOverlayIds = ["ema-20", "rsi-14"];

        viewModel.AuthoredUnitSpecification.Should().BeNull();
        viewModel.DesignStageState.Should().Be("PENDING");
        viewModel.BuildStageState.Should().Be("PENDING");
        viewModel.CanUseObservationInDesign.Should().BeFalse(
            "analysis overlays alone are not Design evidence — need a chart selection, reference, or condition");
        viewModel.HasResearchDesignHandoff.Should().BeFalse();
        viewModel.StrategyWorkspace.Bindings.AuthoredUnitSpecificationHashSha256.Should().BeNull();
        viewModel.StrategyWorkspace.Bindings.DrawingSemanticsHashSha256.Should().BeNull();
    }

    [Fact]
    public void Constructor_starts_blank_Research_without_restoring_latest_strategy_session()
    {
        var saved = new AuthoringSessionSnapshot(
            StrategyId: "alpha-quote-l1",
            DisplayName: "ALPHA QuoteL1",
            Chat:
            [
                new AuthoringChatEntry(
                    AuthoringChatEntry.User,
                    "ALPHA on XNAS QuoteL1 EMA 4/12 targets +5/-5",
                    DateTime.Now),
            ],
            Thread: [],
            Files: [new StrategyFile("Saved.cs", "// saved")],
            InputTokens: 50,
            OutputTokens: 10,
            GenerateCandidateFirst: true,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow);
        var sessions = new MemoryAuthoringSessionRepository(saved);

        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: sessions);

        viewModel.SavedSessions.Should().ContainSingle(session => session.StrategyId == "alpha-quote-l1");
        viewModel.SelectedSavedSession.Should().BeNull();
        viewModel.StrategyId.Should().Be("myStrategy");
        viewModel.IsChartDesignStage.Should().BeTrue();
        viewModel.IsResearchStage.Should().BeFalse();
        viewModel.Messages.Should().BeEmpty();
        viewModel.Composer.Should().BeEmpty();
        viewModel.AuthoredUnitSpecification.Should().BeNull();
        viewModel.CanOpenDesignScreen.Should().BeTrue();
        viewModel.DesignStageState.Should().Be("PENDING");
        viewModel.BuildStageState.Should().Be("PENDING");
    }

    private sealed class MemoryAuthoringSessionRepository(params AuthoringSessionSnapshot[] sessions)
        : IAuthoringSessionRepository
    {
        private readonly List<AuthoringSessionSnapshot> _sessions = [.. sessions];

        public int SaveCalls { get; private set; }

        public IReadOnlyList<AuthoringSessionSnapshot> List() =>
            [.. _sessions.OrderByDescending(static session => session.UpdatedUtc)];

        public bool Save(AuthoringSessionSnapshot session)
        {
            SaveCalls++;
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
