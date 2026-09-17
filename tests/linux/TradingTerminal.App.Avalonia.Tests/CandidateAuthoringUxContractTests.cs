using System.Xml.Linq;
using FluentAssertions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class CandidateAuthoringUxContractTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Strategy_request_review_is_plain_language_editable_and_keeps_authority_locked()
    {
        var root = LoadAuthoringWindow();
        var gate = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Strategy request review");

        gate.Attribute("IsVisible")!.Value.Should().Be("{Binding HasStrategyIntentReview}");
        var confirm = gate.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Command") == "{Binding ConfirmStrategyIntentReviewCommand}");
        confirm.Attribute("Command")!.Value.Should().Be("{Binding ConfirmStrategyIntentReviewCommand}");
        confirm.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanConfirmStrategyIntentReview}");
        gate.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "STRATEGY REQUEST · REVIEW");
        gate.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyIntentProfiles}");
        gate.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyIntentShapes}");
        gate.Descendants(Avalonia + "TextBox").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StrategyResearchObjective, Mode=TwoWay}");
        gate.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyResearchEvidenceRows}");
        gate.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyResearchFalsifierRows}");
        gate.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyResearchUnresolvedRows}");
        gate.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyResearchResolvedRows}");
        gate.Descendants(Avalonia + "TextBox").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding Resolution, Mode=TwoWay}");
        gate.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding ResolveCommand}" &&
            (string?)element.Attribute("Content") == "Record resolution" &&
            (string?)element.Attribute("IsEnabled") == "{Binding CanResolve}");
        gate.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyIntentRequirements}");
        gate.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StrategyIntentQuestions}");
        gate.Descendants().Attributes().Should().NotContain(attribute =>
            attribute.Value.Contains("StrategyIntentDraftHash", StringComparison.Ordinal) ||
            attribute.Value.Contains("ConfirmedStrategyIntentHash", StringComparison.Ordinal));
        gate.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Observe → qualify evidence → decide intent → size/exposure → execution → manage lifecycle → finish/unwind");
        gate.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") != null &&
            ((string?)element.Attribute("Text"))!.Contains(
                "not proof that code compiles, scenarios pass, a backtest succeeds, paper trading is approved, or live trading is authorized",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_picker_is_a_two_by_two_grid_with_unambiguous_state_and_action_labels()
    {
        var root = LoadAuthoringWindow();
        var candidateList = root.Descendants(Avalonia + "ListBox").Single(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding GeneratedCandidateOptions}");

        var candidateGrid = candidateList.Descendants(Avalonia + "UniformGrid").Single();
        candidateGrid.Attribute("Columns")!.Value.Should().Be("2");
        candidateGrid.Attribute("Rows")!.Value.Should().Be("2");

        candidateList.Descendants(Avalonia + "Border").Should().Contain(border =>
            (string?)border.Attribute("IsVisible") == "{Binding IsPreviewed}" &&
            border.Descendants(Avalonia + "TextBlock").Any(label =>
                (string?)label.Attribute("Text") == "PREVIEW"));
        candidateList.Descendants(Avalonia + "Border").Should().Contain(border =>
            (string?)border.Attribute("IsVisible") == "{Binding IsChosen}" &&
            border.Descendants(Avalonia + "TextBlock").Any(label =>
                (string?)label.Attribute("Text") == "ACTIVE IN EDITOR"));
        candidateList.Descendants(Avalonia + "TextBlock").Should().Contain(label =>
            (string?)label.Attribute("Text") == "{Binding SyntheticTestCapabilityText}");
        candidateList.Descendants(Avalonia + "TextBlock").Should().Contain(label =>
            (string?)label.Attribute("Text") == "SELECT TO INSPECT EXACT RESULT");
        candidateList.Attribute("MaxHeight").Should().BeNull(
            "the enclosing candidate scroller owns compact-height navigation");

        var selectedPreview = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Selected candidate exact result preview");
        candidateList.ElementsAfterSelf().First().Should().BeSameAs(selectedPreview,
            "the exact selected result belongs immediately below the two-by-two grid");
        selectedPreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGeneratedCandidateOption.PreviewHeading}");
        selectedPreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGeneratedCandidateOption.PreviewStateText}");
        selectedPreview.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGeneratedCandidateOption.FirstIssueCode}");
        selectedPreview.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGeneratedCandidateOption.FirstIssuePath}");
        selectedPreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGeneratedCandidateOption.FirstIssueMessage}");
        var selectedPreviewText = selectedPreview.Descendants(Avalonia + "TextBox").Single();
        selectedPreviewText.Attribute("Text")!.Value.Should().Be(
            "{Binding SelectedGeneratedCandidateOption.InspectablePreview, Mode=OneWay}");
        selectedPreviewText.Attribute("IsReadOnly")!.Value.Should().Be("True");
        selectedPreviewText.Attribute("ScrollViewer.HorizontalScrollBarVisibility")!.Value
            .Should().Be("Auto");
        selectedPreviewText.Attribute("ScrollViewer.VerticalScrollBarVisibility")!.Value
            .Should().Be("Auto");

        var candidateAction = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Command") == "{Binding ChooseGeneratedCandidateCommand}");
        candidateAction.Attribute("Content")!.Value.Should().Be("{Binding CandidateActionText}");

        root.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value)
            .Should().NotContain("Choose & edit");
    }

    [Fact]
    public void Generation_mode_switch_is_secondary_and_candidate_generation_has_truthful_phase_feedback()
    {
        var root = LoadAuthoringWindow();

        var modeAction = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "{Binding GenerationModeActionText}");
        modeAction.Attribute("Command")!.Value.Should().Be("{Binding ToggleGenerationModeCommand}");
        modeAction.Attribute("Classes")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain("ghost").And.NotContain("aiAction");

        var progressRegion = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowBuildGenerationProgress}");
        progressRegion.Attribute("MaxHeight")!.Value.Should().Be("400");
        progressRegion.Descendants(Avalonia + "ScrollViewer").First()
            .Attribute("VerticalScrollBarVisibility")!.Value.Should().Be("Auto");
        progressRegion.Descendants(Avalonia + "ProgressBar").Should().BeEmpty(
            "generation has no provider percentage or ETA, so a progress bar would imply false precision");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Four initial AI requests; an invalid lane may make one visible repair request.");
        var progressList = progressRegion.Descendants(Avalonia + "ListBox").Single(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding GenerationLaneProgressRows}");
        progressList.Attribute("SelectedItem")!.Value.Should().Be(
            "{Binding SelectedGenerationLaneProgressRow, Mode=TwoWay}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StateLabel}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding PipelineText}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StateDetail}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding ElapsedCompact}");
        progressList.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("IsVisible") == "{Binding HasResult}" &&
            (string?)element.Attribute("Text") == "SELECT TO INSPECT EXACT RESULT");

        var livePreview = progressRegion.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Live lane exact result preview");
        livePreview.Attribute("IsVisible")!.Value.Should().Be(
            "{Binding SelectedGenerationLaneProgressRow.HasResult}");
        livePreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGenerationLaneProgressRow.PreviewHeading}");
        livePreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGenerationLaneProgressRow.ResultOption.StatusText}");
        livePreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "LIVE RESULT · READ ONLY · NOT COMMITTED");
        livePreview.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGenerationLaneProgressRow.ResultOption.FirstIssueCode}");
        livePreview.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGenerationLaneProgressRow.ResultOption.FirstIssuePath}");
        livePreview.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding SelectedGenerationLaneProgressRow.ResultOption.FirstIssueMessage}");
        var livePreviewText = livePreview.Descendants(Avalonia + "TextBox").Single();
        livePreviewText.Attribute("Text")!.Value.Should().Be(
            "{Binding SelectedGenerationLaneProgressRow.InspectablePreview, Mode=OneWay}");
        livePreviewText.Attribute("IsReadOnly")!.Value.Should().Be("True");
        livePreviewText.Attribute("ScrollViewer.HorizontalScrollBarVisibility")!.Value
            .Should().Be("Auto");
        livePreviewText.Attribute("ScrollViewer.VerticalScrollBarVisibility")!.Value
            .Should().Be("Auto");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Nothing is compiled, tested, run, or backtested here.");
    }

    [Fact]
    public void Builder_exposes_four_rail_stages_with_research_as_chrome_link()
    {
        var root = LoadAuthoringWindow();
        var navigation = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Authoring screen navigation");
        navigation.Descendants(Avalonia + "WrapPanel").Should().Contain(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowScreenNavigation}");
        var buttons = navigation.Descendants(Avalonia + "Button").ToArray();
        buttons.Should().NotContain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Brief screen");
        var research = buttons.Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Research screen");
        var design = buttons.Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Design and Confirm screen");
        var build = buttons.Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Build Test and Compare screen");
        var validate = buttons.Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Validate screen");
        var paper = buttons.Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Paper screen");

        research.Attribute("Content")!.Value.Should().Be("Research Studio");
        research.Attribute("Classes")!.Value.Should().Contain("ghost");
        design.Attribute("Content")!.Value.Should().Be("1  Design");
        build.Attribute("Content")!.Value.Should().Be("2  Build");
        validate.Attribute("Content")!.Value.Should().Be("3  Validate");
        paper.Attribute("Content")!.Value.Should().Be("4  Run");

        research.Attribute("Command")!.Value.Should().Be("{Binding OpenResearchScreenCommand}");
        research.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanOpenResearchScreen}");
        design.Attribute("Command")!.Value.Should().Be("{Binding OpenDesignScreenCommand}");
        design.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanOpenDesignScreen}");
        build.Attribute("Command")!.Value.Should().Be("{Binding OpenBuildScreenCommand}");
        build.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanOpenBuildScreen}");
        validate.Attribute("Command")!.Value.Should().Be("{Binding OpenValidateScreenCommand}");
        validate.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanOpenValidateScreen}");
        paper.Attribute("Command")!.Value.Should().Be("{Binding OpenPaperScreenCommand}");
        paper.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanOpenPaperScreen}");

        // Research is not a numbered pill with a PENDING/OPTIONAL eyebrow under it on the rail.
        navigation.Descendants(Avalonia + "TextBlock").Should().NotContain(element =>
            (string?)element.Attribute("Text") == "{Binding ResearchStageState}");

        var historicalValidation = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Run exact historical validation");
        historicalValidation.Attribute("Click")!.Value.Should().Be("OnHistoricalValidationRequested");
        historicalValidation.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanRunHistoricalValidation}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Evaluate validate Design ENTRY on chart" &&
            (string?)element.Attribute("Command") == "{Binding EvaluateValidateDesignEntryOnChartCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Show validation condition markers on chart" &&
            (string?)element.Attribute("Command") == "{Binding ShowValidationConditionMarkersOnChartCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Show validation fill markers on chart" &&
            (string?)element.Attribute("Command") == "{Binding ShowValidationFillMarkersOnChartCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Show validation chart layers on chart" &&
            (string?)element.Attribute("Command") == "{Binding ShowValidationChartLayersOnChartCommand}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Validation chart layers status" &&
            (string?)element.Attribute("Text") == "{Binding ValidationChartLayersStatusText}");
        root.Descendants(Avalonia + "Button").Should().ContainSingle(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Bind validated strategy to selected Paper book" &&
            (string?)element.Attribute("Click") == "OnPaperHandoffRequested");

        foreach (var state in new[]
                 {
                     "DesignStageState",
                     "BuildStageState", "ValidateStageState", "PaperStageState",
                 })
        {
            navigation.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
                (string?)element.Attribute("Text") == $"{{Binding {state}}}");
        }
        navigation.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding WorkspaceRevisionText}");


        var researchWorkspace = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research event discovery workspace");
        researchWorkspace.Attribute("IsVisible")!.Value.Should().Be("{Binding ShowResearchWorkspace}");
        // Builder hosts the markup but ShowResearchWorkspace is Studio-shell-only — never Research stage alone.
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Execution fidelity applied checklist");
        root.Descendants(Avalonia + "CheckBox").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Queue position (unavailable)" &&
            (string?)element.Attribute("IsEnabled") == "{Binding ExecutionUnsupportedOptionsAvailable}");
        root.Descendants(Avalonia + "CheckBox").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Partial fills (max 4 per touch)" &&
            (string?)element.Attribute("IsEnabled") == "{Binding ExecutionPartialsAndLatencyAvailable}");
        root.Descendants(Avalonia + "TextBox").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Execution latency ms" &&
            (string?)element.Attribute("IsEnabled") == "{Binding ExecutionPartialsAndLatencyAvailable}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Attach L1 execution lifecycle demo" &&
            (string?)element.Attribute("Command") == "{Binding AttachL1ExecutionLifecycleDemoCommand}");
        root.ToString().Should().Contain("Return to Design from Build");
        root.ToString().Should().Contain("Strategy version result list");
        root.ToString().Should().Contain("Run comparison for native strategy run");
        root.ToString().Should().Contain("Build design blockers");
        root.ToString().Should().Contain("CandidateEmptyTitle");
        root.ToString().Should().Contain("working draft");
        root.ToString().Should().NotContain("OPTIONAL RESEARCH TEMPLATES");
        root.ToString().Should().NotContain("top chrome");
        root.ToString().Should().NotContain("Focus rule editor");
        root.ToString().Should().Contain("STRATEGY TEMPLATES");
        root.ToString().Should().Contain("Use template");
        root.ToString().Should().Contain("Design rule editor");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open Research Studio" &&
            (string?)element.Attribute("Command") == "{Binding InvestigateInResearchStudioCommand}");
        root.ToString().Should().Contain("DesignInstrumentText");
        root.ToString().Should().Contain("DesignInstrumentSearchText");
        root.ToString().Should().Contain("SelectedDesignInstrument");
        root.ToString().Should().Contain("Design instrument search");
        root.ToString().Should().Contain("Design instrument picker");
        root.ToString().Should().Contain("Design instrument venue");
        root.ToString().Should().Contain("Design instrument picker status");
        root.ToString().Should().Contain("DesignOperandKindOptions");
        root.ToString().Should().Contain("Design entry left operand kind");
        root.ToString().Should().Contain("Design entry summary");
        root.ToString().Should().Contain("Design entry notes");
        root.ToString().Should().Contain("Design composer draft attachment");
        root.ToString().Should().Contain("Review &amp; continue");
        root.ToString().Should().Contain("Ask Hyperion about this draft");
        root.ToString().Should().Contain("Design review panel");
        root.ToString().Should().Contain("Continue design to Build");
        root.ToString().Should().Contain("Add design risk limit");
        root.ToString().Should().Contain("DesignRiskLimits");
        root.ToString().Should().NotContain("ENTRY (SUMMARY / FREEFORM)");
        root.ToString().Should().Contain("DesignTimeframeText");
        root.ToString().Should().Contain("DesignEvaluationTimingText");
        root.ToString().Should().Contain("DesignEntryRuleText");
        root.ToString().Should().Contain("USE AS ENTRY — REVIEW BEFORE APPLY");
        root.ToString().Should().Contain("Apply change");
        root.ToString().Should().Contain("PendingHyperionDesignChangeSummaryText");
        root.ToString().Should().Contain("Handoff condition role");
        root.ToString().Should().Contain("Hyperion design proposal review");
        root.ToString().Should().Contain("Finding design proposal review");
        root.ToString().Should().Contain("Design indicators editor");
        root.ToString().Should().Contain("Design indicator formula detail");
        root.ToString().Should().Contain("FormulaDetailText");
        root.ToString().Should().Contain("Design entry condition editor");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Add design indicator" &&
            (string?)element.Attribute("Command") == "{Binding AddDesignIndicatorCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Import research indicators to Design" &&
            (string?)element.Attribute("Command") == "{Binding ImportResearchIndicatorsToDesignCommand}");
        root.ToString().Should().Contain("Design entry left operand kind");
        root.ToString().Should().Contain("Design sizing form");
        root.ToString().Should().Contain("Design risk form");
        root.ToString().Should().Contain("Design orders form");
        root.ToString().Should().Contain("Design sizing range min");
        root.ToString().Should().Contain("Design exit condition editor");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Propose Design rules from finding" &&
            (string?)element.Attribute("Command") == "{Binding StageFindingAsDesignProposalCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Preview design condition on chart" &&
            (string?)element.Attribute("Command") == "{Binding PreviewDesignConditionOnChartCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Apply finding design proposal" &&
            (string?)element.Attribute("Command") == "{Binding AcceptFindingDesignProposalCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Review design rules for build" &&
            (string?)element.Attribute("Content") == "Review & continue" &&
            (string?)element.Attribute("Command") == "{Binding ReviewDesignRulesCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Ask Hyperion about this draft" &&
            (string?)element.Attribute("Command") == "{Binding PromoteDesignRulesToRequestCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Continue design to Build" &&
            (string?)element.Attribute("Command") == "{Binding ContinueDesignToBuildCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Add design risk limit" &&
            (string?)element.Attribute("Command") == "{Binding AddDesignRiskLimitCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Stage last Hyperion reply as design proposal" &&
            (string?)element.Attribute("Command") == "{Binding StageLastHyperionAsDesignProposalCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Accept Hyperion design proposal" &&
            (string?)element.Attribute("Command") == "{Binding AcceptHyperionDesignProposalCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Discard Hyperion design proposal" &&
            (string?)element.Attribute("Command") == "{Binding DiscardHyperionDesignProposalCommand}");
        root.ToString().Should().Contain("Apply change");
        root.ToString().Should().Contain("Linked research finding");
        // Single Research Studio CTA lives on the stage chrome — not duplicated in empty/rule panes.
        root.Descendants(Avalonia + "Button")
            .Count(element =>
                (string?)element.Attribute("AutomationProperties.Name") == "Open Research screen")
            .Should().Be(1);
        researchWorkspace.Descendants(Avalonia + "Border").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research embedded chart surface");
        researchWorkspace.Descendants(Avalonia + "Border").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research results column");
        researchWorkspace.Descendants(Avalonia + "ScrollViewer").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research details column" &&
            (string?)element.Attribute("IsVisible") == "{Binding ResearchDetailsOpen}");
        researchWorkspace.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research results event list");
        researchWorkspace.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research results coverage summary");
        researchWorkspace.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research results data provenance");
        researchWorkspace.Descendants(Avalonia + "Grid").Should().Contain(element =>
            (string?)element.Attribute("ColumnDefinitions") == "*,Auto" &&
            (string?)element.Attribute("AutomationProperties.Name") == "Research chart-first layout");
        researchWorkspace.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Toggle research details");
        researchWorkspace.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Open host chart for research selection" &&
            (string?)element.Attribute("Click") == "OnResearchChartRequested" &&
            (string?)element.Attribute("IsVisible") == "{Binding HasEmbeddedResearchChart}");
        researchWorkspace.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Retry open research chart");
        researchWorkspace.Descendants(Avalonia + "Button").Should().NotContain(element =>
            (string?)element.Attribute("Content") == "Save ref A");
        researchWorkspace.Descendants(Avalonia + "Button").Should().NotContain(element =>
            (string?)element.Attribute("Content") == "Save ref B");
        root.Descendants(Avalonia + "Button").Should().NotContain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Open research chart from details");
        root.Descendants(Avalonia + "Border").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research Hyperion hint");
        root.Descendants(Avalonia + "StackPanel").Should().Contain(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowConversationEmptyState}");
        root.Descendants(Avalonia + "Border").Should().NotContain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research quick actions");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding WorkingFlowNextActionText}");
        researchWorkspace.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research outcome labels optional hint");
        researchWorkspace.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding MarkPreBreakoutCommand}" &&
            (string?)element.Attribute("IsEnabled") == "{Binding HasResearchChartSelection}" &&
            ((string?)element.Attribute("Classes") ?? "").Contains("ghost"));
        researchWorkspace.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding MarkPreCrashCommand}");
        researchWorkspace.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding MarkNeutralCommand}");
        // B/C/N remain available but are never forced (ghost, not aiAction primary).
        researchWorkspace.Descendants(Avalonia + "Button").Should().NotContain(element =>
            (string?)element.Attribute("Command") == "{Binding MarkPreBreakoutCommand}" &&
            ((string?)element.Attribute("Classes") ?? "").Contains("aiAction"));
        researchWorkspace.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchEventSamples}");

        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Detach research chart");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Active artifact kind" &&
            (string?)element.Attribute("Text") == "{Binding ActiveArtifactKindText}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Research linked instrument context");

        var conversation = root.Descendants(Avalonia + "Grid").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Design and Confirm screen");
        conversation.Attribute("MinWidth")!.Value.Should().Be("{Binding ConversationColumnMinWidth}");
        conversation.Attribute("MaxWidth")!.Value.Should().Be("{Binding ConversationColumnMaxWidth}");
        conversation.Attribute("Width")!.Value.Should().Be("{Binding ConversationColumnWidth}");

        var workbench = root.Descendants(Avalonia + "Border").Single(element =>
            ((string?)element.Attribute("Classes"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("workbench") == true);
        workbench.Attribute("MinWidth")!.Value.Should().Be("{Binding DesignInspectorMinWidth}");

        root.Descendants(Avalonia + "Grid").Should().Contain(element =>
            (string?)element.Attribute(Xaml + "Name") == "MainWorkspaceGrid");
        root.Descendants(Avalonia + "Grid").Should().ContainSingle(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Design and Confirm screen" &&
            (string?)element.Attribute("IsVisible") == "{Binding IsDesignScreen}");
        root.Descendants(Avalonia + "StackPanel").Should().Contain(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowDesignRequestHeader}");
        root.Descendants(Avalonia + "StackPanel").Should().Contain(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowImplementationHeader}");
        workbench.Attribute("Grid.Column")!.Value.Should().Be("{Binding WorkbenchGridColumn}");
        workbench.Attribute("Grid.ColumnSpan")!.Value.Should().Be("{Binding WorkbenchGridColumnSpan}");

        root.Descendants(Avalonia + "TabItem").Where(element =>
                new[] { "Code", "Parameters", "Activity" }.Contains((string?)element.Attribute("Header")))
            .Should().OnlyContain(element =>
                (string?)element.Attribute("IsVisible") == "{Binding ShowImplementationTabs}");
        root.Descendants(Avalonia + "TabItem").Should().ContainSingle(element =>
            (string?)element.Attribute("Header") == "{Binding CandidateTabHeader}");

        var runnable = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Build canonical runnable Paper strategy");
        runnable.Attribute("Command")!.Value.Should().Be("{Binding GenerateCanonicalPaperStrategyCommand}");
        runnable.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanGenerateCanonicalPaperStrategy}");
        runnable.Attribute("IsVisible")!.Value.Should().Be("{Binding ShowStartImplementationAction}");

        var start = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Start implementation generation");
        start.Attribute("Command")!.Value.Should().Be("{Binding GenerateFourCandidatesCommand}");
        start.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanGenerateFourCandidates}");
        start.Attribute("IsVisible")!.Value.Should().Be("{Binding ShowStartImplementationAction}");

        var liveBoard = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Live four-lane generation board");
        var stop = liveBoard.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Stop implementation generation");
        stop.Attribute("Command")!.Value.Should().Be("{Binding StopCommand}");

        var activeTask = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Active build task");
        activeTask.Attribute("IsVisible")!.Value.Should().Be("{Binding ShowBuildBusyStop}");
        activeTask.Descendants(Avalonia + "Button").Single(element =>
                (string?)element.Attribute("AutomationProperties.Name") == "Stop active build task")
            .Attribute("Command")!.Value.Should().Be("{Binding StopCommand}");

        root.Descendants(Avalonia + "Border").Single(element =>
                (string?)element.Attribute("AutomationProperties.Name") ==
                "Detached implementation source warning")
            .Attribute("IsVisible")!.Value.Should().Be("{Binding HasDetachedImplementationSource}");

        root.Descendants(Avalonia + "ScrollViewer").Should().ContainSingle(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowBuildCandidateResults}");
        root.Descendants(Avalonia + "Grid").Should().ContainSingle(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowDesignCandidateReview}");
    }

    [Fact]
    public void Expert_mode_has_a_prominent_candidate_return_and_hides_compile_for_non_C_sharp_artifacts()
    {
        var root = LoadAuthoringWindow();
        var expertNotice = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "Expert C sharp mode notice");
        expertNotice.Attribute("IsVisible")!.Value.Should().Be("{Binding !GenerateCandidateFirst}");
        var returnAction = expertNotice.Descendants(Avalonia + "Button").Single();
        returnAction.Attribute("Content")!.Value.Should().Be("Return to candidates");
        returnAction.Attribute("Command")!.Value.Should().Be("{Binding ToggleGenerationModeCommand}");

        var compile = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "⚡  Compile & Register");
        compile.Attribute("IsVisible")!.Value.Should().Be("{Binding HasExpertCSharpFiles}");
        compile.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanCompileCurrentSource}");
        var boundary = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Non C sharp source review boundary");
        boundary.Attribute("IsVisible")!.Value.Should().Be("{Binding HasNonCSharpExpertArtifact}");
    }

    [Fact]
    public void New_strategy_uses_a_filterable_axis_catalog_instead_of_three_hard_coded_prompts()
    {
        var root = LoadAuthoringWindow();

        root.Descendants(Avalonia + "ListBox").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding VisibleStarterBriefs}");
        root.Descendants(Avalonia + "TextBox").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StarterSearchText, Mode=TwoWay}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StarterFamilyOptions}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StarterHorizonOptions}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StarterDataOptions}");

        root.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value)
            .Should().NotContain("{Binding SuggestionBriefs}");
    }

    [Fact]
    public void Package_valid_graph_exposes_an_explicit_synthetic_smoke_action_and_boundary()
    {
        var root = LoadAuthoringWindow();
        var testRegion = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Candidate synthetic test action"));
        var backtestAction = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "{Binding BacktestActionText}");

        backtestAction.Ancestors().Should().Contain(testRegion);
        backtestAction.Attribute("IsEnabled")!.Value
            .Should().Be("{Binding CanPrepareGeneratedCandidateForBacktest}");
        backtestAction.Attribute("Command")!.Value
            .Should().Be("{Binding RunTradeIrSimulatedBacktestCommand}");
        root.Descendants(Avalonia + "ItemsControl").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding BacktestReadinessStages}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding TradeIrBacktestBoundaryText}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding TradeIrBacktestSummary}");
    }

    [Fact]
    public void Regeneration_and_testing_are_distinct_actions_with_an_exact_hash_gate()
    {
        var root = LoadAuthoringWindow();
        var generation = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Candidate generation action"));
        var testing = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Candidate synthetic test action"));

        generation.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "GENERATE / REGENERATE");
        generation.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Creates four replacement drafts only. It does not compile, test, run, or backtest.");
        var regenerate = generation.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Regenerate 4 candidates");
        regenerate.Attribute("Command")!.Value.Should().Be("{Binding RegenerateFourCandidatesCommand}");

        testing.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "TEST · SYNTHETIC ONLY");
        testing.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "1 Preview Graph · Typed  →  2 Use selected in editor  →  3 Run exact-hash smoke");
        testing.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CandidateBacktestAvailabilityText}");
        testing.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Historical backtest unavailable here · Backtest Studio is a separate workflow.");

        var smoke = testing.Descendants(Avalonia + "Button").Single();
        smoke.Attribute("Command")!.Value.Should().Be("{Binding RunTradeIrSimulatedBacktestCommand}");
        smoke.Attribute("IsEnabled")!.Value
            .Should().Be("{Binding CanPrepareGeneratedCandidateForBacktest}");
    }

    [Fact]
    public void Candidate_outcome_panel_explains_selection_failure_truth_and_next_actions_above_the_grid()
    {
        var root = LoadAuthoringWindow();
        var outcome = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Candidate outcome and next steps"));

        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CandidateBatchHeadline}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding FirstBlockedGeneratedCandidateOption.FirstIssueCode}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding FirstBlockedGeneratedCandidateOption.FirstIssuePath}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding FirstBlockedGeneratedCandidateOption.FirstIssueMessage}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Generated = structurally shaped draft only — not proven correct, runnable, tested, or backtest-ready.");
        var pending = outcome.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Pending strategy refinement"));
        pending.Attribute("IsVisible")!.Value.Should().Be("{Binding HasPendingFourLanePrompt}");
        pending.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "PENDING REQUEST NOT APPLIED");
        var discardPending = pending.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Discard pending strategy request");
        discardPending.Attribute("Content")!.Value.Should().Be("Discard pending request");
        discardPending.Attribute("Command")!.Value
            .Should().Be("{Binding DiscardPendingFourLanePromptCommand}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CandidateBacktestAvailabilityText}");

        var smokeStarter = outcome.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Load QuoteL1 EMA smoke starter");
        smokeStarter.Attribute("Command")!.Value
            .Should().Be("{Binding UseQuoteL1EmaSmokeStarterCommand}");
        smokeStarter.Attribute("IsEnabled")!.Value
            .Should().Be("{Binding !IsGenerating}");

        var expert = outcome.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Expert C# (separate path)");
        expert.Attribute("Command")!.Value.Should().Be("{Binding ToggleGenerationModeCommand}");
    }

    [Fact]
    public void TradeIr_synthesis_is_an_explicit_fifth_artifact_with_separate_use_action()
    {
        var root = LoadAuthoringWindow();
        var synthesis = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "TradeIR synthesis bridge"));

        var synthesize = synthesis.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Synthesize valid drafts → TradeIR");
        synthesize.Attribute("Command")!.Value.Should().Be("{Binding SynthesizeTradeIrCommand}");
        synthesize.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanSynthesizeTradeIr}");

        var use = synthesis.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "{Binding CombinedTradeIrActionText}");
        use.Attribute("Command")!.Value.Should().Be("{Binding UseCombinedTradeIrCommand}");
        use.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanUseCombinedTradeIr}");

        synthesis.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CombinedTradeIrTargetHash}");
        synthesis.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CombinedTradeIrReceiptHash}");
    }

    [Fact]
    public void Generated_candidate_body_scrolls_as_one_surface_at_the_supported_compact_size()
    {
        var root = LoadAuthoringWindow();
        var scroller = root.Descendants(Avalonia + "ScrollViewer").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Scrollable candidate results"));

        scroller.Attribute("VerticalScrollBarVisibility")!.Value.Should().Be("Auto");
        scroller.Attribute("HorizontalScrollBarVisibility")!.Value.Should().Be("Disabled");
        scroller.Descendants(Avalonia + "ListBox").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding GeneratedCandidateOptions}");
        scroller.Descendants(Avalonia + "ItemsControl").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding BacktestReadinessStages}");
    }

    [Fact]
    public void Failed_lane_option_exposes_the_first_error_code_path_and_message_without_flattening_it()
    {
        var firstError = new StrategyCandidateGenerationIssueV1(
            StrategyCandidateGenerationIssueSeverityV1.Error,
            "LANE_JSON_INVALID",
            "$.definition.dataRequirements[0].instrumentSelector.references[0].assetClass",
            "The JSON value could not be converted to AssetClass.");
        var option = new StrategyGenerationCandidateOption(new StrategyGenerationLaneResultV1(
            StrategyGenerationLaneV1.TypedGraph,
            StrategyGenerationReadinessV1.Invalid,
            null,
            null,
            [firstError],
            new StrategyGenerationAgentRunV1(
                "graph-agent",
                "test-provider",
                null,
                true,
                null,
                null,
                CodegenUsage.None)));

        option.LaneName.Should().Be("Graph · Typed");
        option.FirstIssueCode.Should().Be(firstError.Code);
        option.FirstIssuePath.Should().Be(firstError.Path);
        option.FirstIssueMessage.Should().Be(firstError.Message);
    }

    [Fact]
    public void Composer_keeps_send_anchored_while_secondary_controls_wrap_at_narrow_widths()
    {
        var root = LoadAuthoringWindow();
        var send = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Command") == "{Binding SendCommand}" &&
            (string?)element.Attribute("Content") == "{Binding SendButtonText}");
        // Send sits on its own row under Attach/Model so it never competes for width.
        var actionGrid = send.Ancestors(Avalonia + "Grid").First(element =>
            (string?)element.Attribute("RowDefinitions") == "Auto,Auto");
        actionGrid.Descendants(Avalonia + "WrapPanel").Should().ContainSingle(panel =>
            (string?)panel.Attribute("Grid.Row") == "0");
        send.Ancestors(Avalonia + "StackPanel").First()
            .Attribute("Grid.Row")!.Value.Should().Be("1");
    }

    [Fact]
    public void Cli_workspace_footer_wraps_buttons_and_scrolls_vertically_in_narrow_workbenches()
    {
        var root = LoadAuthoringWindow();
        var footer = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "CliWorkspaceFooter"));
        var scroller = footer.Descendants(Avalonia + "ScrollViewer").Single();

        scroller.Attribute("HorizontalScrollBarVisibility")!.Value.Should().Be("Disabled");
        scroller.Attribute("VerticalScrollBarVisibility")!.Value.Should().Be("Auto");
        scroller.Attribute("MaxHeight")!.Value.Should().Be("84");

        var cliList = footer.Descendants(Avalonia + "ItemsControl").Single(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding AvailableClis}");
        cliList.Descendants(Avalonia + "ItemsPanelTemplate").Single()
            .Descendants(Avalonia + "WrapPanel").Should().ContainSingle();
        cliList.Descendants(Avalonia + "ItemsPanelTemplate").Single()
            .Descendants(Avalonia + "StackPanel").Should().BeEmpty();
    }

    [Fact]
    public void Research_Studio_exposes_market_screen_rank_table_and_linked_chart_controls()
    {
        var root = XDocument.Load(Fixture("ResearchStudioWindow.axaml")).Root
            ?? throw new InvalidOperationException("The Research Studio fixture has no root element.");

        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "MARKET SCREEN");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Rank" &&
            (string?)element.Attribute("Command") == "{Binding RunResearchMarketScreenCommand}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchScreenUniverseOptions}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchScreenMetricOptions}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchScreenTopNOptions}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Table" &&
            (string?)element.Attribute("Command") == "{Binding SetResearchScreenViewModeCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Compare" &&
            (string?)element.Attribute("Command") == "{Binding SetResearchScreenViewModeCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Open chart" &&
            (string?)element.Attribute("Command") == "{Binding OpenSelectedScreenChartsCommand}");
        root.Descendants(Avalonia + "Button").Should().NotContain(element =>
            (string?)element.Attribute("Command") == "{Binding OpenResearchOrderBookCommand}",
            "Order book / Footprint / Bookmap belong on the open chart, not Rank.");
        root.ToString().Should().Contain("ReturnToStrategyBuilderCommand");
        root.ToString().Should().Contain("Back to strategy from Research");
        root.ToString().Should().Contain("Add research finding to strategy");
        root.ToString().Should().Contain("AddFindingTransferPreviewText");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding ReturnToStrategyBuilderCommand}" &&
            (string?)element.Attribute("IsEnabled") == "{Binding CanReturnToStrategyBuilder}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding UseObservationInDesignCommand}" &&
            (string?)element.Attribute("IsEnabled") == "{Binding CanUseObservationInDesign}");
        root.ToString().Should().Contain("Add finding review before link");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding ConfirmAddFindingToStrategyCommand}" &&
            (string?)element.Attribute("AutomationProperties.Name") == "Confirm add finding to strategy");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Command") == "{Binding DiscardAddFindingReviewCommand}");
        root.ToString().Should().Contain("PendingConditionMultipleText");
        root.ToString().Should().Contain("ApplyPendingResearchConditionCommand");
        root.ToString().Should().Contain("SaveResearchFinding1Command");
        root.ToString().Should().Contain("Save finding 1");
        root.ToString().Should().Contain("SAVED FINDINGS");
        root.ToString().Should().NotContain("Reference A/B");
        root.ToString().Should().NotContain("Bookmark A");
        root.ToString().Should().Contain("OpenResearchConditionHitCommand");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchScreenBarSizeOptions}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding ResearchScreenResultHeaderText}");
        root.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchScreenRows}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding ActiveResearchContextText}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding ResearchScreenUniverseOptions}");
        root.ToString().Should().Contain("ResearchIndicatorInspectText");
        root.ToString().Should().Contain("ResearchIndicatorCompareText");
        root.ToString().Should().Contain("ResearchLinkedContextText");
        root.ToString().Should().Contain("OpenResearchScreenRowCommand");
        root.Descendants(Avalonia + "UniformGrid").Should().Contain(element =>
            (string?)element.Attribute(Xaml + "Name") == "CompareChartTilesHost",
            "Compare must host real multi-chart tiles, not strip-only.");
        root.Descendants(Avalonia + "ContentControl").Should().Contain(element =>
            (string?)element.Attribute(Xaml + "Name") == "ResearchChartHost");
        root.Descendants(Avalonia + "Grid").Should().Contain(element =>
            (string?)element.Attribute(Xaml + "Name") == "MainWorkspaceGrid" &&
            ((string?)element.Attribute("ColumnDefinitions"))!.Contains("52"));
        root.ToString().Should().Contain("HyperionCollapsed");
        root.ToString().Should().Contain("ClipToBounds");
    }

    [Fact]
    public void Chart_rail_exposes_editable_sma_and_ema_periods()
    {
        var root = XDocument.Load(Fixture("ChartsPanel.axaml")).Root
            ?? throw new InvalidOperationException("The Charts panel fixture has no root element.");

        root.Descendants(Avalonia + "NumericUpDown").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "SMA period" &&
            (string?)element.Attribute("Value") == "{Binding SmaPeriod}");
        root.Descendants(Avalonia + "NumericUpDown").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") == "EMA period" &&
            (string?)element.Attribute("Value") == "{Binding EmaPeriod}");
    }

    [Fact]
    public void Chart_workspace_exposes_market_views_for_the_open_instrument()
    {
        var root = XDocument.Load(Fixture("ChartsPanel.axaml")).Root
            ?? throw new InvalidOperationException("The Charts panel fixture has no root element.");

        root.ToString().Should().Contain("ChartMarketViews");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Order book" &&
            (string?)element.Attribute("Command") == "{Binding OpenChartOrderBookCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Footprint" &&
            (string?)element.Attribute("Command") == "{Binding OpenChartVolumeFootprintCommand}");
        root.Descendants(Avalonia + "Button").Should().Contain(element =>
            (string?)element.Attribute("Content") == "Bookmap" &&
            (string?)element.Attribute("Command") == "{Binding OpenChartBookmapCommand}");
    }

    private static XElement LoadAuthoringWindow() =>
        XDocument.Load(Fixture("StrategyAuthoringWindow.axaml")).Root
        ?? throw new InvalidOperationException("The strategy authoring fixture has no root element.");

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
