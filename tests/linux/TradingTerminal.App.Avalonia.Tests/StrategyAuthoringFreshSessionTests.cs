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
        viewModel.ExecutionUnsupportedOptionsExplanation.Should().Contain("Nautilus-class");
        viewModel.ExecutionUnsupportedOptionsExplanation.Should().Contain("L1");
        viewModel.ExecutionUnsupportedOptionsAvailable.Should().BeFalse();
        viewModel.ExecutionEnablePartialFills = false;
        viewModel.ExecutionLatencyMs = 0;
        viewModel.DesignInstrumentText = "ES";
        viewModel.DesignTimeframeText = "5m";
        viewModel.DesignEvaluationTimingText = "Completed bar";
        viewModel.DesignEntryRuleText = "close crosses above EMA 20";
        viewModel.DesignExitRuleText = "close crosses below EMA 20";
        viewModel.DesignUnresolvedChecklistText.Should().Contain("sizing");
        viewModel.CanPromoteDesignRulesToRequest.Should().BeTrue();
        viewModel.PromoteDesignRulesToRequestCommand.Execute(null);
        viewModel.Composer.Should().Contain("composer prompt");
        viewModel.Composer.Should().Contain("INSTRUMENT: ES");
        viewModel.Composer.Should().Contain("ENTRY: close crosses above EMA 20");
        viewModel.Composer.Should().Contain("Accept before Design fields change");
        viewModel.AwaitingHyperionDesignProposal.Should().BeTrue();
        viewModel.DesignEntryRuleText.Should().Be("close crosses above EMA 20",
            "Ask Hyperion must not mutate Design fields until Accept");
        viewModel.ReviewDesignRulesCommand.Execute(null);
        viewModel.Status.Should().Contain("Working draft rules reviewed");
        viewModel.DesignRulesReviewText.Should().Contain("ENTRY: close crosses above EMA 20");
        viewModel.CanStageLastHyperionAsDesignProposal.Should().BeFalse();
        viewModel.Composer = "";
        viewModel.AwaitingHyperionDesignProposal = false;
        viewModel.ShowImplementationTabs.Should().BeFalse(
            "Design must not expose Strategy.cs / Code — that belongs in Build");
        viewModel.ActiveArtifactKindText.Should().NotBeNullOrWhiteSpace();
        viewModel.ShowDesignInspector.Should().BeTrue(
            "Design must open the rule workbench immediately");
        viewModel.ShowDesignRuleEditor.Should().BeTrue();
        viewModel.ShowWorkbenchPanel.Should().BeTrue();
        viewModel.DesignInspectorWidth.Should().Be(double.NaN,
            "Design rules fill the center column");
        viewModel.DesignInspectorMinWidth.Should().Be(420);
        viewModel.ConversationColumnMaxWidth.Should().Be(360);
        viewModel.MainWorkspaceColumnDefinitions.Should().Be("Auto,Auto,4,*",
            "Design: Hyperion Auto, rules star — not chat-dominated center");
        viewModel.ResearchStageState.Should().Be("OPTIONAL",
            "Research is optional Studio work, not a numbered Builder stage");
        viewModel.DesignStageState.Should().Be("PENDING");
        viewModel.StrategyWorkspace.WorkspaceId.Should().Be("myStrategy");
        viewModel.WorkspaceStageBindingCount.Should().Be(6,
            "workspace aggregate keeps Brief+Research for hash bindings");
        viewModel.StrategyWorkspace.Stages.Should().HaveCount(6);
        StrategyAuthoringViewModel.BuilderRailStageCount.Should().Be(4);
        viewModel.WorkingFlowMapText.Should().Contain("Research (optional)");
        viewModel.WorkingFlowMapText.Should().Contain("1 Design");
        viewModel.WorkingFlowMapText.Should().NotContain("1 Research");
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
    public void JourneyA_Investigate_then_Back_preserves_Design_without_finding()
    {
        using var viewModelA = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModelA.DisplayName = "Momentum";
        viewModelA.DesignEntryRuleText = "A entry: EMA 20 cross";
        viewModelA.OpenDesignScreenCommand.Execute(null);
        viewModelA.IsChartDesignStage.Should().BeTrue();
        viewModelA.CanInvestigateInResearchStudio.Should().BeTrue();

        var handoffCount = 0;
        viewModelA.StrategyBuilderHandoffRequested += (_, _) => handoffCount++;
        var studioRequested = 0;
        viewModelA.ResearchStudioRequested += (_, _) => studioRequested++;

        // Plan Journey A uses Design → Investigate (not the chrome Research Studio pill alone).
        viewModelA.InvestigateInResearchStudioCommand.Execute(null);
        studioRequested.Should().Be(1);
        viewModelA.ResearchOpenedFromBuilder.Should().BeTrue();
        viewModelA.Composer.Should().Contain("Investigate the working Design draft");
        viewModelA.Composer.Should().Contain("A entry: EMA 20 cross");
        viewModelA.Composer.Should().NotContain(
            "Add this saved Research finding",
            "Investigate must not attach finding-link evidence");
        viewModelA.IsResearchStudioShell = true;
        viewModelA.CanReturnToStrategyBuilder.Should().BeTrue(
            "Back must work without selecting a chart period or saving an observation");
        viewModelA.CanUseObservationInDesign.Should().BeFalse(
            "Add finding stays disabled until there is something to transfer");
        viewModelA.ReturnToStrategyBuilderText.Should().Contain("Momentum");
        viewModelA.UseInStrategyBuilderText.Should().Contain("Add finding to Momentum");
        viewModelA.HasResearchDesignHandoff.Should().BeFalse();

        viewModelA.ReturnToStrategyBuilderCommand.Execute(null);
        handoffCount.Should().Be(1);
        viewModelA.IsChartDesignStage.Should().BeTrue();
        viewModelA.DesignEntryRuleText.Should().Be("A entry: EMA 20 cross");
        viewModelA.HasResearchDesignHandoff.Should().BeFalse(
            "Back must not attach a finding");
        viewModelA.HasPendingAddFindingReview.Should().BeFalse();

        // Second trip via Investigate — still preserves the draft.
        viewModelA.InvestigateInResearchStudioCommand.Execute(null);
        viewModelA.IsResearchStudioShell = true;
        viewModelA.ReturnToStrategyBuilderCommand.Execute(null);
        viewModelA.DesignEntryRuleText.Should().Be("A entry: EMA 20 cross");

        using var viewModelB = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModelB.DisplayName = "Strategy B";
        viewModelB.DesignEntryRuleText = "B entry: RSI oversold";
        viewModelB.MarkResearchOpenedFromBuilder();
        viewModelB.IsResearchStudioShell = true;
        viewModelB.ReturnToStrategyBuilderCommand.Execute(null);
        viewModelB.DesignEntryRuleText.Should().Be("B entry: RSI oversold");
        viewModelA.DesignEntryRuleText.Should().Be("A entry: EMA 20 cross",
            "each strategy keeps its own draft");
    }

    [Fact]
    public void Build_shows_Design_blockers_and_validation_result_survives_restore()
    {
        var repository = new MemoryAuthoringSessionRepository();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: repository);

        viewModel.ActiveScreen = StrategyAuthoringScreen.Build;
        viewModel.IsBuildScreen.Should().BeTrue();
        viewModel.ShowBuildDesignBlockers.Should().BeTrue(
            "Build without instrument/entry must show blockers");
        viewModel.BuildDesignBlockerText.Should().Contain("Entry");
        viewModel.ReturnToDesignFromBuildCommand.Execute(null);
        viewModel.IsChartDesignStage.Should().BeTrue();

        viewModel.DesignInstrumentText = "ES";
        viewModel.DesignEntryRuleText = "EMA 20 crosses above EMA 50";
        viewModel.ActiveScreen = StrategyAuthoringScreen.Build;
        viewModel.ShowBuildDesignBlockers.Should().BeFalse();

        var buildHash = new string('b', 64);
        var evidence = new HistoricalValidationEvidenceV1(
            HistoricalValidationEvidenceV1.CurrentSchemaVersion,
            new HistoricalValidationContextV1(
                viewModel.StrategyWorkspace.WorkspaceId,
                new string('a', 64),
                buildHash,
                null),
            new string('c', 64),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            "L1TouchFillModel",
            "synthetic",
            TradeCount: 3,
            StartingCash: 100_000,
            EndingCash: 100_150,
            TotalFees: 12,
            CompletedUtc: new DateTime(2026, 1, 2, 1, 0, 0, DateTimeKind.Utc));

        viewModel.UpsertHistoricalValidationResult(evidence);
        viewModel.HasStrategyVersionResults.Should().BeTrue();
        viewModel.StrategyVersionResults.Should().ContainSingle()
            .Which.Kind.Should().Be(StrategyVersionResultKind.HistoricalValidate);

        var json = HistoricalValidationEvidenceCanonicalJsonV1.Serialize(evidence);
        repository.Save(new AuthoringSessionSnapshot(
            StrategyId: viewModel.StrategyId,
            DisplayName: viewModel.DisplayName,
            Chat: [],
            Thread: [],
            Files: [new StrategyFile(StrategyFile.DefaultName, "// draft")],
            ActiveScreen: StrategyAuthoringScreen.Build,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow,
            DesignInstrumentText: "ES",
            DesignEntryRuleText: "EMA 20 crosses above EMA 50",
            HistoricalValidationEvidenceJson: json,
            StrategyVersionResultsJson: System.Text.Json.JsonSerializer.Serialize(
                new[]
                {
                    StrategyVersionResultSnapshot.FromItem(viewModel.StrategyVersionResults[0]),
                })));

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: repository);

        restored.SelectedSavedSession = repository.List().Single(s => s.StrategyId == viewModel.StrategyId);
        restored.HasStrategyVersionResults.Should().BeTrue();
        restored.StrategyVersionResults[0].Kind.Should().Be(StrategyVersionResultKind.HistoricalValidate);
        restored.HistoricalValidationEvidence.Should().NotBeNull();
        restored.OpenStrategyVersionResultCommand.Execute(restored.StrategyVersionResults[0]);
        restored.HistoricalValidationEvidence!.TradeCount.Should().Be(3);
        restored.DesignEntryRuleText.Should().Be("EMA 20 crosses above EMA 50");
    }

    [Fact]
    public void L1_execution_lifecycle_report_attaches_to_strategy_version_results()
    {
        var repository = new MemoryAuthoringSessionRepository();
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: repository);

        viewModel.DisplayName = "Liquidity sweep demo";
        viewModel.CanAttachL1ExecutionLifecycleDemo.Should().BeTrue();
        viewModel.AttachL1ExecutionLifecycleDemoCommand.CanExecute(null).Should().BeTrue();
        viewModel.AttachL1ExecutionLifecycleDemoCommand.Execute(null);

        viewModel.HasStrategyVersionResults.Should().BeTrue();
        var item = viewModel.StrategyVersionResults.Should().ContainSingle().Subject;
        item.Kind.Should().Be(StrategyVersionResultKind.ExecutionLifecycle);
        item.Summary.Should().Contain("+25");
        item.Summary.Should().Contain("not Nautilus");
        item.EvidenceJson.Should().Contain("finalPosition\":25");
        viewModel.Status.Should().Contain("Fixed fixture");
        viewModel.NativeStrategyEvidencePanels.Should().Contain(p =>
            p.Title.Contains("CSP", StringComparison.Ordinal) &&
            p.Authority.Contains("not a trading backtest", StringComparison.OrdinalIgnoreCase));
        viewModel.NativeStrategyEvidencePanels.Should().Contain(p =>
            p.Title.Contains("VibeQuant", StringComparison.Ordinal) &&
            p.Authority.Contains("not Nautilus", StringComparison.OrdinalIgnoreCase));

        viewModel.OpenStrategyVersionResultCommand.Execute(item);
        viewModel.ActiveScreen.Should().Be(StrategyAuthoringScreen.Validate);
        viewModel.Status.Should().Contain("L1FillModel");
        viewModel.Status.Should().Contain("not claimed");

        repository.Save(new AuthoringSessionSnapshot(
            StrategyId: viewModel.StrategyId,
            DisplayName: viewModel.DisplayName,
            Chat: [],
            Thread: [],
            Files: [new StrategyFile(StrategyFile.DefaultName, "// draft")],
            ActiveScreen: StrategyAuthoringScreen.Validate,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow,
            StrategyVersionResultsJson: System.Text.Json.JsonSerializer.Serialize(
                new[]
                {
                    StrategyVersionResultSnapshot.FromItem(viewModel.StrategyVersionResults[0]),
                })));

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: repository);
        restored.SelectedSavedSession = repository.List().Single(s => s.StrategyId == viewModel.StrategyId);
        restored.HasStrategyVersionResults.Should().BeTrue();
        restored.StrategyVersionResults[0].Kind.Should().Be(StrategyVersionResultKind.ExecutionLifecycle);
        restored.StrategyVersionResults[0].EvidenceJson.Should().Contain("finalPosition\":25");
    }

    [Fact]
    public void Strategy_template_creates_Design_draft_and_Investigate_is_separate()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.IsResearchStudioShell.Should().BeFalse();
        var starter = viewModel.AllStarterBriefs.First(brief =>
            !string.Equals(brief.Id, "starter.quote-l1-ema-smoke", StringComparison.Ordinal));

        var researchRequested = 0;
        viewModel.ResearchStudioRequested += (_, _) => researchRequested++;

        viewModel.UseStarterPromptCommand.Execute(starter);

        viewModel.IsChartDesignStage.Should().BeTrue(
            "templates create a Design draft — they must not open Research");
        viewModel.HasDesignRuleDraft.Should().BeTrue();
        viewModel.DesignEntryRuleText.Should().NotBeNullOrWhiteSpace();
        viewModel.LastAppliedStarterId.Should().Be(starter.Id);
        researchRequested.Should().Be(0);
        viewModel.CanInvestigateInResearchStudio.Should().BeTrue();

        viewModel.InvestigateInResearchStudioCommand.Execute(null);
        researchRequested.Should().Be(1);
        viewModel.ResearchOpenedFromBuilder.Should().BeTrue();
        viewModel.Composer.Should().StartWith("Investigate before writing trading rules:");
        viewModel.DesignEntryRuleText.Should().NotBeNullOrWhiteSpace(
            "Investigate must not clear the Design draft");
    }

    [Fact]
    public void Restoring_saved_strategy_reopens_Design_rules_and_stage()
    {
        var repository = new MemoryAuthoringSessionRepository();
        repository.Save(new AuthoringSessionSnapshot(
            StrategyId: "strategyA",
            DisplayName: "Strategy A",
            Chat: [],
            Thread: [],
            Files: [new StrategyFile(StrategyFile.DefaultName, "// draft")],
            ActiveScreen: StrategyAuthoringScreen.Design,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            UpdatedUtc: DateTime.UtcNow,
            DesignInstrumentText: "ES",
            DesignEntryRuleText: "A entry preserved"));

        using var restored = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: repository);

        restored.SelectedSavedSession = repository.List().Single(s => s.StrategyId == "strategyA");
        restored.DesignEntryRuleText.Should().Be("A entry preserved");
        restored.DesignInstrumentText.Should().Be("ES");
        restored.IsChartDesignStage.Should().BeTrue();
        restored.DisplayName.Should().Be("Strategy A");
    }

    [Fact]
    public void Hyperion_design_proposal_requires_Accept_before_overwriting_fields()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.DesignInstrumentText = "ES";
        viewModel.DesignTimeframeText = "5m";
        viewModel.DesignEvaluationTimingText = "Completed bar";
        viewModel.DesignEntryRuleText = "close crosses above EMA 20";
        viewModel.DesignExitRuleText = "close crosses below EMA 20";
        viewModel.PromoteDesignRulesToRequestCommand.Execute(null);
        viewModel.DesignEntryRuleText.Should().Be("close crosses above EMA 20");
        viewModel.AwaitingHyperionDesignProposal.Should().BeTrue();

        viewModel.Messages.Add(new AuthoringMessage(
            CodegenRole.Assistant,
            "INSTRUMENT: NQ\nTIMEFRAME: 15m\nEVALUATION: Completed bar\nENTRY: EMA 20 crosses above EMA 50\nEXIT: EMA 20 crosses below EMA 50\nSIZING: 1 contract\nRISK: 1% daily stop\nORDERS: market IOC"));
        viewModel.CanStageLastHyperionAsDesignProposal.Should().BeTrue();
        // Ask Hyperion sets Awaiting — the assistant reply auto-stages into the Design proposal panel.
        viewModel.HasPendingHyperionDesignProposal.Should().BeTrue(
            "chat result must connect to Design automatically when awaiting a rules reply");
        viewModel.AwaitingHyperionDesignProposal.Should().BeFalse();
        viewModel.DesignEntryRuleText.Should().Be("close crosses above EMA 20",
            "auto-stage must not overwrite Design fields until Accept");

        viewModel.AcceptHyperionDesignProposalCommand.Execute(null);
        viewModel.DesignInstrumentText.Should().Be("NQ");
        viewModel.DesignTimeframeText.Should().Be("15m");
        viewModel.DesignEntryRuleText.Should().Be("EMA(20) crosses above EMA(50)");
        viewModel.DesignSizing.QuantityText.Should().Be("1");
        viewModel.DesignSizing.Unit.Should().Be("contracts");
        viewModel.DesignSizingRuleText.Should().Contain("1");
        viewModel.DesignRisk.IsComplete.Should().BeTrue();
        viewModel.DesignOrders.OrderType.Should().Be("Market");
        viewModel.DesignOrders.TimeInForce.Should().Be("IOC");
        viewModel.HasPendingHyperionDesignProposal.Should().BeFalse();
        viewModel.DesignUnresolvedChecklistText.Should().Contain("All Design fields have text");
        viewModel.DesignInstrumentProvenance.Should().Be(DesignValueProvenance.HyperionAccepted);
        viewModel.DesignTimeframeProvenance.Should().Be(DesignValueProvenance.HyperionAccepted);
        viewModel.DesignIndicators.Should().Contain(i =>
            i.Kind.Equals("EMA", StringComparison.OrdinalIgnoreCase) && i.Period == 20);
        viewModel.DesignIndicators.Should().Contain(i =>
            i.Kind.Equals("EMA", StringComparison.OrdinalIgnoreCase) && i.Period == 50);
        viewModel.DesignEntryCondition.IsComplete.Should().BeTrue();
        viewModel.DesignEntryCondition.OperatorKey.Should().Be("crosses above");
        viewModel.DesignEntryCondition.Provenance.Should().Be(DesignValueProvenance.HyperionAccepted);

        viewModel.DesignEntryRuleText = "manual EMA 30 cross";
        viewModel.Messages.Add(new AuthoringMessage(
            CodegenRole.Assistant,
            "ENTRY: should not apply without Accept"));
        viewModel.StageLastHyperionAsDesignProposalCommand.Execute(null);
        viewModel.DiscardHyperionDesignProposalCommand.Execute(null);
        viewModel.DesignEntryRuleText.Should().Be("manual EMA 30 cross");
        viewModel.HasPendingHyperionDesignProposal.Should().BeFalse();
    }

    [Fact]
    public void Hyperion_Accept_parses_INDICATORS_and_CONDITION_into_editable_controls()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.Messages.Add(new AuthoringMessage(
            CodegenRole.Assistant,
            "INSTRUMENT: MSFT\nTIMEFRAME: 15m\nEVALUATION: Completed bar\n" +
            "INDICATORS: ema(20), ema(50)\n" +
            "CONDITION: ema(20) crosses above ema(50)\n" +
            "SIZING: target 10 shares\n" +
            "EXIT: ema(20) crosses below ema(50)"));
        viewModel.StageLastHyperionAsDesignProposalCommand.Execute(null);
        viewModel.DesignInstrumentText.Should().BeNullOrEmpty("stage must not mutate");
        viewModel.AcceptHyperionDesignProposalCommand.Execute(null);

        viewModel.DesignInstrumentText.Should().Be("MSFT");
        viewModel.DesignTimeframeText.Should().Be("15m");
        viewModel.DesignTimeframeProvenanceLabel.Should().Contain("Hyperion");
        viewModel.DesignIndicators.Should().HaveCount(2);
        viewModel.DesignEntryCondition.LeftOperand.Should().Be("ema(20)");
        viewModel.DesignEntryCondition.RightOperand.Should().Be("ema(50)");
        viewModel.DesignEntryCondition.OperatorKey.Should().Be("crosses above");
        viewModel.DesignEntryRuleText.Should().Be("ema(20) crosses above ema(50)");
        viewModel.DesignSizingRuleText.Should().Be("target 10 shares");

        viewModel.DesignEntryCondition.LeftOperand = "ema(30)";
        viewModel.DesignEntryRuleText.Should().Be("ema(30) crosses above ema(50)");
        viewModel.DesignEntryCondition.Provenance.Should().Be(DesignValueProvenance.Operator);
    }

    [Fact]
    public void Strategic_form_sizing_range_and_exit_sync_into_draft_summaries()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.DesignExitCondition.LeftOperand = "ema(20)";
        viewModel.DesignExitCondition.OperatorKey = "crosses below";
        viewModel.DesignExitCondition.RightOperand = "ema(50)";
        viewModel.DesignExitRuleText.Should().Be("ema(20) crosses below ema(50)");

        viewModel.DesignSizing.QuantityText = "10";
        viewModel.DesignSizing.Unit = "shares";
        viewModel.DesignSizing.RangeMinText = "5";
        viewModel.DesignSizing.RangeMaxText = "15";
        viewModel.DesignSizing.SummaryText.Should().Contain("range 5–15");
        viewModel.DesignSizingRuleText.Should().Contain("10");
        viewModel.DesignSizingRuleText.Should().Contain("5");

        viewModel.DesignRisk.MaxLossText = "1";
        viewModel.DesignRisk.MaxLossUnit = "%";
        viewModel.DesignRisk.DailyStopText = "2";
        viewModel.DesignRisk.IsComplete.Should().BeTrue();

        viewModel.DesignOrders.OrderType = "Market";
        viewModel.DesignOrders.TimeInForce = "IOC";
        viewModel.DesignOrderRuleText.Should().Be("Market IOC");

        viewModel.Messages.Add(new AuthoringMessage(
            CodegenRole.Assistant,
            "SIZING: target 10 shares (range 5-15)\n" +
            "RISK: max loss 1% · daily stop 2%\n" +
            "ORDERS: Limit Day · price mid\n" +
            "EXIT: ema(20) crosses below ema(50)"));
        viewModel.StageLastHyperionAsDesignProposalCommand.Execute(null);
        viewModel.AcceptHyperionDesignProposalCommand.Execute(null);
        viewModel.DesignSizing.HasRange.Should().BeTrue();
        viewModel.DesignSizing.RangeMinText.Should().Be("5");
        viewModel.DesignSizing.RangeMaxText.Should().Be("15");
        viewModel.DesignOrders.OrderType.Should().Be("Limit");
        viewModel.DesignOrders.TimeInForce.Should().Be("Day");
        viewModel.DesignOrders.PriceRule.Should().Be("mid");
        viewModel.DesignExitCondition.OperatorKey.Should().Be("crosses below");
    }

    [Fact]
    public void Add_design_indicator_does_not_create_entry_rule()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.NewDesignIndicatorKind = "ema";
        viewModel.NewDesignIndicatorPeriodText = "20";
        viewModel.AddDesignIndicatorCommand.Execute(null);
        viewModel.DesignIndicators.Should().ContainSingle(i => i.Period == 20);
        viewModel.DesignEntryRuleText.Should().BeNullOrEmpty();
        viewModel.DesignEntryCondition.IsComplete.Should().BeFalse();
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
        viewModel.HasDesignRuleDraft.Should().BeTrue(
            "templates also seed Design rules even when already in Research Studio");
        viewModel.DesignEntryRuleText.Should().NotBeNullOrWhiteSpace();
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
        viewModel.SaveResearchFinding1Command.Execute(null);

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
        viewModel.HasPendingAddFindingReview.Should().BeTrue(
            "Add finding stages a review — Design is unchanged until Confirm");
        viewModel.HasResearchDesignHandoff.Should().BeFalse();
        viewModel.Composer.Should().NotContain("Add this saved Research finding");
        var entryBeforeConfirm = viewModel.DesignEntryRuleText;

        viewModel.ConfirmAddFindingToStrategyCommand.Execute(null);

        viewModel.HasPendingAddFindingReview.Should().BeFalse();
        viewModel.IsChartDesignStage.Should().BeTrue();
        viewModel.HasResearchDesignHandoff.Should().BeTrue();
        viewModel.LinkedResearchSummaryText.Should().Contain("Finding linked");
        viewModel.LinkedResearchSummaryText.Should().Contain("ema");
        viewModel.HasStrategyDraft.Should().BeTrue(
            "Confirm creates a research-scoped draft and binds condition id/hash");
        viewModel.PendingStrategyDraft!.LinkedConditionId.Should().Be(
            viewModel.PendingResearchCondition!.ConditionId);
        viewModel.PendingStrategyDraft.LinkedConditionVersionHashSha256.Should().Be(
            viewModel.PendingResearchCondition.VersionHashSha256);
        viewModel.CanOpenDesignScreen.Should().BeTrue();
        viewModel.WorkingFlowMapText.Should().Contain("1 Design ✓");
        viewModel.Composer.Should().Contain("Add this saved Research finding");
        viewModel.Composer.Should().Contain("MSFT");
        viewModel.Composer.Should().Contain("ema");
        viewModel.Composer.Should().Contain("Saved finding");
        viewModel.DesignEntryRuleText.Should().Be(entryBeforeConfirm,
            "Confirm links evidence — it must not invent or overwrite Design entry rules");
        viewModel.HasPendingFindingDesignProposal.Should().BeTrue(
            "Confirm stages finding → Design rules for review before Apply");
        viewModel.PendingFindingDesignProposalText.Should().Contain("ENTRY:");
        viewModel.PendingFindingDesignProposalText.Should().Contain("INSTRUMENT:");

        viewModel.AcceptFindingDesignProposalCommand.Execute(null);
        viewModel.HasPendingFindingDesignProposal.Should().BeFalse();
        viewModel.DesignInstrumentText.Should().Be("MSFT");
        viewModel.DesignTimeframeText.Should().Be("1h");
        viewModel.DesignEvaluationTimingText.Should().Be("Completed bar");
        viewModel.DesignEntryRuleText.Should().Contain("volume");
        viewModel.DesignEntryRuleText.Should().Contain(viewModel.PendingResearchCondition!.ConditionId);
        StrategyAuthoringViewModel.IsDesignFieldUnresolved(viewModel.DesignExitRuleText).Should().BeTrue(
            "Apply must not treat Unresolved placeholders as real EXIT rules");
        viewModel.DesignUnresolvedChecklistText.Should().Contain("exit");
        viewModel.DesignUnresolvedChecklistText.Should().Contain("sizing");
        viewModel.AuthoredUnitSpecification.Should().BeNull(
            "Use in Design attaches evidence only — no generate/compile/register");
        viewModel.CompiledOk.Should().BeFalse();
        viewModel.IsRegistered.Should().BeFalse();
        viewModel.BuildStageState.Should().Be("PENDING");
        viewModel.Status.Should().Match(s =>
            s.Contains("Applied finding", StringComparison.Ordinal) ||
            s.Contains("condition", StringComparison.Ordinal) ||
            s.Contains("No compile or register", StringComparison.Ordinal) ||
            s.Contains("opening Design", StringComparison.Ordinal) ||
            s.Contains("Added finding", StringComparison.Ordinal));
    }

    [Fact]
    public void Finding_design_proposal_Discard_leaves_Design_fields_unchanged()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.DisplayName = "Momentum";
        viewModel.DesignEntryRuleText = "Keep manual entry";
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
            indicatorBindings: [new ResearchIndicatorBindingV1("ema-20", "ema", 20)]);
        viewModel.PendingConditionMultipleText = "2";
        viewModel.PendingConditionLookbackText = "20";
        viewModel.ApplyPendingResearchConditionCommand.Execute(null);
        viewModel.SaveResearchFinding1Command.Execute(null);
        viewModel.UseObservationInDesignCommand.Execute(null);
        viewModel.ConfirmAddFindingToStrategyCommand.Execute(null);
        viewModel.HasPendingFindingDesignProposal.Should().BeTrue();

        viewModel.DiscardFindingDesignProposalCommand.Execute(null);
        viewModel.HasPendingFindingDesignProposal.Should().BeFalse();
        viewModel.DesignEntryRuleText.Should().Be("Keep manual entry");
        viewModel.DesignInstrumentText.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Add_finding_review_cancel_and_Back_leave_Design_unchanged()
    {
        using var viewModel = new StrategyAuthoringViewModel(
            new StubCompiler(),
            new StubRegistry(),
            NullLogger<StrategyAuthoringViewModel>.Instance,
            sessionRepository: new MemoryAuthoringSessionRepository());

        viewModel.DisplayName = "Momentum";
        viewModel.DesignEntryRuleText = "Keep this entry";
        viewModel.OpenDesignScreenCommand.Execute(null);
        viewModel.OpenResearchScreenCommand.Execute(null);
        viewModel.IsResearchStudioShell = true;

        var start = new DateTimeOffset(2026, 3, 10, 14, 30, 0, TimeSpan.Zero);
        viewModel.SetResearchChartSelection(
            new ResearchChartSelectionV1(
                new InstrumentId(7),
                "MSFT",
                BarSize.FiveMinutes,
                start,
                start.AddHours(4),
                start.AddHours(4),
                start.AddHours(8),
                StrategyDataRequirement.Bars),
            indicatorBindings: [new ResearchIndicatorBindingV1("ema-20", "ema", 20)]);
        viewModel.PendingConditionMultipleText = "2";
        viewModel.PendingConditionLookbackText = "20";
        viewModel.ApplyPendingResearchConditionCommand.Execute(null);
        viewModel.SaveResearchFinding1Command.Execute(null);

        viewModel.UseObservationInDesignCommand.Execute(null);
        viewModel.HasPendingAddFindingReview.Should().BeTrue();
        viewModel.DiscardAddFindingReviewCommand.Execute(null);
        viewModel.HasPendingAddFindingReview.Should().BeFalse();
        viewModel.HasResearchDesignHandoff.Should().BeFalse();
        viewModel.DesignEntryRuleText.Should().Be("Keep this entry");

        viewModel.UseObservationInDesignCommand.Execute(null);
        viewModel.HasPendingAddFindingReview.Should().BeTrue();
        viewModel.ReturnToStrategyBuilderCommand.Execute(null);
        viewModel.HasPendingAddFindingReview.Should().BeFalse();
        viewModel.HasResearchDesignHandoff.Should().BeFalse();
        viewModel.DesignEntryRuleText.Should().Be("Keep this entry");
        viewModel.IsResearchStudioShell.Should().BeFalse();
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
