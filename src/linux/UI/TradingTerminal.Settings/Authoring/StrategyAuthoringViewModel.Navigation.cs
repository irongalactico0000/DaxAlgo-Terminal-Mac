using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

public enum StrategyAuthoringScreen
{
    Design = 0,
    Build = 1,
    Brief = 2,
    Research = 3,
    Validate = 4,
    Paper = 5,
}

public sealed partial class StrategyAuthoringViewModel
{
    [ObservableProperty]
    private StrategyAuthoringScreen _activeScreen = StrategyAuthoringScreen.Design;

    /// <summary>
    /// True when this VM is hosted in Research Studio (separate workspace from Strategy Builder).
    /// Chart-first layout; no Design→Build→Validate→Run stage rail.
    /// </summary>
    [ObservableProperty]
    private bool _isResearchStudioShell;

    /// <summary>Research Studio: Hyperion starts collapsed so the chart owns the width.</summary>
    [ObservableProperty]
    private bool _hyperionCollapsed = true;

    [ObservableProperty]
    private bool _hasDetachedImplementationSource;

    public bool IsBriefStage => ActiveScreen == StrategyAuthoringScreen.Brief;
    public bool IsResearchStage => ActiveScreen == StrategyAuthoringScreen.Research;
    public bool IsChartDesignStage => ActiveScreen == StrategyAuthoringScreen.Design;
    public bool IsBuildStage => ActiveScreen == StrategyAuthoringScreen.Build;
    public bool IsValidateStage => ActiveScreen == StrategyAuthoringScreen.Validate;
    public bool IsPaperStage => ActiveScreen == StrategyAuthoringScreen.Paper;

    // Compatibility layout groups: Brief/Research/Design reuse the current request canvas;
    // Build/Validate/Paper reuse the current artifact workbench until their dedicated panels land.
    public bool IsDesignScreen => ActiveScreen is StrategyAuthoringScreen.Brief or
        StrategyAuthoringScreen.Research or StrategyAuthoringScreen.Design;
    public bool IsBuildScreen => ActiveScreen is StrategyAuthoringScreen.Build or
        StrategyAuthoringScreen.Validate or StrategyAuthoringScreen.Paper;

    public int WorkbenchGridColumn => IsDesignScreen ? 3 : 1;
    public int WorkbenchGridColumnSpan => IsDesignScreen ? 1 : 3;

    /// <summary>
    /// Design always shows the rule workbench. Brief/Research still wait for inspectable content.
    /// Build/Validate/Paper always show the workbench.
    /// </summary>
    public bool ShowDesignInspector =>
        IsChartDesignStage ||
        (IsDesignScreen &&
         GenerateCandidateFirst &&
         (HasCandidate || HasStrategyIntentReview || HasChartReferences || ShowResearchWorkspace));

    public bool ShowWorkbenchPanel => !IsDesignScreen || ShowDesignInspector || !GenerateCandidateFirst;

    /// <summary>Editable Design rule draft when no Hyperion candidate is open yet.</summary>
    public bool ShowDesignRuleEditor =>
        IsChartDesignStage && !HasCandidate && !IsResearchStudioShell;

    /// <summary>
    /// Research Studio / Design: chart or rules get the star column; Hyperion is Auto + capped.
    /// Build keeps Hyperion as the star when used.
    /// </summary>
    public string MainWorkspaceColumnDefinitions =>
        (IsResearchStudioShell || (IsResearchStage && ShowDesignInspector) || IsChartDesignStage)
            ? "Auto,Auto,4,*"
            : "Auto,*,4,Auto";

    /// <summary>
    /// Research stretches the inspector so the embedded chart can dominate; Design rules fill the
    /// center column; Build uses NaN so the panel can fill.
    /// </summary>
    public double DesignInspectorWidth =>
        IsChartDesignStage
            ? double.NaN
            : IsDesignScreen
                ? (ShowDesignInspector ? (IsResearchStage ? double.NaN : 390) : 0)
                : double.NaN;

    public double DesignInspectorMinWidth =>
        IsChartDesignStage
            ? 420
            : IsResearchStage && ShowDesignInspector ? 480 : 0;

    /// <summary>
    /// Hyperion width: Research Studio / Design keep a practical narrow pane so rules or chart dominate.
    /// </summary>
    public double ConversationColumnMaxWidth =>
        IsResearchStudioShell
            ? (HyperionCollapsed ? 52 : 320)
            : IsChartDesignStage
                ? 360
                : IsResearchStage ? 420 : double.PositiveInfinity;

    public double ConversationColumnMinWidth =>
        IsResearchStudioShell
            ? (HyperionCollapsed ? 52 : 240)
            : IsChartDesignStage
                ? 260
                : IsResearchStage ? 420 : 0;

    public double ConversationColumnWidth =>
        IsResearchStudioShell
            ? (HyperionCollapsed ? 52 : 280)
            : IsChartDesignStage
                ? 300
                : IsResearchStage && ShowDesignInspector ? 420 : double.NaN;

    public bool ShowConversationEmptyState =>
        !HasConversation && !IsResearchStage && !IsResearchStudioShell;

    public bool ShowResearchComposerHint =>
        (IsResearchStage || IsResearchStudioShell) && !HasConversation && !HyperionCollapsed;

    public string ResearchStudioTitle => "Research Studio";

    public string NewSessionActionText =>
        IsResearchStudioShell ? "＋  New research" : "＋  New strategy";

    /// <summary>True when Studio was opened from Strategy Builder — enables Back without a finding.</summary>
    [ObservableProperty] private bool _researchOpenedFromBuilder;

    [ObservableProperty] private StrategyAuthoringScreen _builderScreenBeforeResearch =
        StrategyAuthoringScreen.Design;

    public bool CanReturnToStrategyBuilder =>
        IsResearchStudioShell && ResearchOpenedFromBuilder && !IsGenerating;

    public string ReturnToStrategyBuilderText =>
        $"← Back to {StrategyReturnDisplayName}";

    public string UseInStrategyBuilderText =>
        $"Add finding to {StrategyReturnDisplayName}";

    public string StrategyReturnDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? "strategy" : DisplayName.Trim();

    /// <summary>
    /// Preview of what Add finding will transfer — independent of whether Back is available.
    /// </summary>
    public string AddFindingTransferPreviewText
    {
        get
        {
            if (!CanUseObservationInDesign)
                return "Nothing to transfer yet. Save a finding, apply a condition, or select a chart window first.";

            var parts = new List<string>();
            if (HasResearchFinding1)
                parts.Add($"Finding 1: {ResearchFinding1Text}");
            else if (HasResearchFinding2)
                parts.Add($"Finding 2: {ResearchFinding2Text}");
            if (HasPendingResearchCondition)
                parts.Add($"Condition: {PendingResearchConditionText}");
            if (HasResearchChartSelection)
                parts.Add($"Selection: {ResearchChartSelectionText}");
            if (HasPendingResearchIndicatorBindings)
                parts.Add("Indicators: " + string.Join(", ",
                    PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel)));
            return parts.Count == 0
                ? "Ready to attach the current Research evidence to Design."
                : "Will transfer — " + string.Join(" · ", parts);
        }
    }

    public string HyperionToggleText =>
        HyperionCollapsed ? "Show Hyperion" : "Hide Hyperion";

    public string ActiveResearchContextText
    {
        get
        {
            var ranked = SelectedResearchScreenRow?.CanonicalSymbol;
            var chart = ResearchChartInstrumentText;
            // Ranked-row click: Hyperion follows the ranked instrument until the chart links.
            var symbol = !string.IsNullOrWhiteSpace(ranked)
                ? ranked!
                : chart
                    ?? SelectedResearchGalleryCard?.Symbol
                    ?? PendingResearchChartSelection?.CanonicalSymbol
                    ?? "No instrument";
            var rankPrefix = SelectedResearchScreenRow is { } row &&
                             string.Equals(row.CanonicalSymbol, symbol, StringComparison.OrdinalIgnoreCase)
                ? $"#{row.Rank} "
                : "";
            var indicators = HasPendingResearchIndicatorBindings
                ? string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))
                : "indicators from chart";
            return $"{rankPrefix}{symbol} · {indicators}";
        }
    }

    [RelayCommand]
    private void ToggleHyperion() => HyperionCollapsed = !HyperionCollapsed;

    partial void OnHyperionCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConversationColumnMaxWidth));
        OnPropertyChanged(nameof(ConversationColumnMinWidth));
        OnPropertyChanged(nameof(ConversationColumnWidth));
        OnPropertyChanged(nameof(HyperionToggleText));
        OnPropertyChanged(nameof(ShowResearchComposerHint));
        NotifyDesignInspectorLayoutChanged();
    }

    partial void OnIsResearchStudioShellChanged(bool value)
    {
        if (value)
        {
            ActiveScreen = StrategyAuthoringScreen.Research;
            RailCollapsed = true;
            HyperionCollapsed = true;
            ResearchDetailsOpen = false;
        }
        else
        {
            // Leaving Studio shell does not clear ResearchOpenedFromBuilder — Back remains valid
            // if the operator reopens Studio from the same Builder trip.
        }

        OnPropertyChanged(nameof(ShowScreenNavigation));
        OnPropertyChanged(nameof(ShowResearchWorkspace));
        OnPropertyChanged(nameof(NewSessionActionText));
        OnPropertyChanged(nameof(ShowConversationEmptyState));
        OnPropertyChanged(nameof(ShowResearchComposerHint));
        NotifyAuthoringScreenStateChanged();
        NotifyStrategyBuilderReturnStateChanged();
    }

    partial void OnResearchOpenedFromBuilderChanged(bool value) =>
        NotifyStrategyBuilderReturnStateChanged();

    /// <summary>Code/Parameters/Activity belong in Build — never steal Research for Strategy.cs.</summary>
    public bool ShowImplementationTabs =>
        !IsResearchStage &&
        (IsBuildScreen || !GenerateCandidateFirst || AuthoredUnitSpecification is not null);

    public bool ShowScreenNavigation => GenerateCandidateFirst && !IsResearchStudioShell;
    public bool ShowDesignRequestHeader =>
        IsDesignScreen && GenerateCandidateFirst && ShowDesignInspector && !IsResearchStage && !IsResearchStudioShell;
    public bool ShowImplementationHeader =>
        (IsBuildScreen && !ShowNativeStrategyRunPanel) || !GenerateCandidateFirst;
    public bool ShowNativeImplementationHeader => ShowNativeStrategyRunPanel;
    public bool ShowResearchWorkspace =>
        GenerateCandidateFirst && IsResearchStudioShell;

    /// <summary>True when the Research chart is hosted inside Strategy Builder (not detached).</summary>
    [ObservableProperty] private bool _hasEmbeddedResearchChart;

    public void NotifyEmbeddedResearchChartChanged(bool embedded)
    {
        HasEmbeddedResearchChart = embedded;
        NotifyDesignInspectorLayoutChanged();
    }

    public string ChartLoadActionText =>
        HasEmbeddedResearchChart ? "Change instrument / data" : "Load chart";

    /// <summary>Labeling / condition tools only after a chart (or brush) exists — not before.</summary>
    public bool ShowResearchObservationTools =>
        HasEmbeddedResearchChart || HasResearchChartSelection;

    partial void OnHasEmbeddedResearchChartChanged(bool value)
    {
        OnPropertyChanged(nameof(ChartLoadActionText));
        OnPropertyChanged(nameof(ShowResearchObservationTools));
    }

    /// <summary>
    /// User-facing label for whatever currently occupies the workbench — visualization vs research
    /// observation vs draft vs executable — so Strategy.cs is not inferred from diagnostics alone.
    /// </summary>
    public string ActiveArtifactKindText
    {
        get
        {
            if (IsResearchStage)
            {
                if (SelectedResearchGalleryCard is not null || HasResearchChartSelection)
                    return "Research observation";
                if (HasResearchOutcomeGalleryMatches)
                    return "Research results";
                return "Research workspace";
            }

            if (HasStrategyDraft && !IsBuildStage)
                return "Strategy draft (not executable)";

            if (AuthoredUnitSpecification is { } spec)
            {
                return spec.Kind == AuthoredUnitKindV1.Visualizer
                    ? "Display-only visualizer"
                    : "Executable strategy version";
            }

            if (HasCandidate)
                return "Interpreted strategy request";

            return IsBuildStage ? "Build workbench" : "Strategy Builder";
        }
    }

    public string ResearchLinkedContextText
    {
        get
        {
            if (SelectedResearchScreenRow is { } screenRow)
            {
                var chart = ResearchChartInstrumentText ?? "unset";
                var linked = string.Equals(screenRow.CanonicalSymbol, chart, StringComparison.OrdinalIgnoreCase);
                return linked
                    ? $"Screen #{screenRow.Rank} {screenRow.CanonicalSymbol} · chart linked · {screenRow.MetricLabel}={screenRow.MetricValue:N0}"
                    : $"Screen #{screenRow.Rank} {screenRow.CanonicalSymbol} selected · chart catching up from {chart}";
            }

            var eventSymbol = SelectedResearchGalleryCard?.Symbol
                ?? PendingResearchChartSelection?.CanonicalSymbol;
            var chartSymbol = ResearchChartInstrumentText;
            if (string.IsNullOrWhiteSpace(eventSymbol) && string.IsNullOrWhiteSpace(chartSymbol))
                return "No event or chart instrument selected yet.";
            if (string.IsNullOrWhiteSpace(eventSymbol))
                return $"Chart instrument: {chartSymbol}. Select an event to bind Research.";
            if (string.IsNullOrWhiteSpace(chartSymbol))
                return $"Selected event: {eventSymbol}. Chart will follow this event.";
            var match = string.Equals(eventSymbol, chartSymbol, StringComparison.OrdinalIgnoreCase);
            return match
                ? $"Event + chart: {eventSymbol} (linked)"
                : $"Research event {eventSymbol} · chart instrument {chartSymbol} (not linked — select the event row or change the chart symbol so research informs Design)";
        }
    }
    public bool HasAuthoredUnitCSharpFiles =>
        AuthoredUnitSpecification is not null &&
        Files.Count > 0 &&
        Files.All(static file => file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    public bool CanCompileCurrentSource =>
        (HasExpertCSharpFiles || HasAuthoredUnitCSharpFiles) &&
        !HasDetachedImplementationSource &&
        !IsGenerating;

    public bool CanOpenBriefScreen => GenerateCandidateFirst && !IsGenerating;
    public bool CanOpenResearchScreen => GenerateCandidateFirst && !IsGenerating;
    public bool CanOpenDesignScreen =>
        GenerateCandidateFirst &&
        !IsGenerating &&
        (IsChartDesignStage ||
         HasResearchDesignHandoff ||
         HasCandidate ||
         HasConfirmedStrategyIntent ||
         HasChartReferences ||
         AuthoredUnitSpecification is { Kind: AuthoredUnitKindV1.Strategy });
    public bool CanOpenBuildScreen =>
        !IsGenerating &&
        (IsBuildStage || IsNativeStrategyAgentWired || CanEnterFourLaneConformance);
    public bool CanOpenValidateScreen =>
        GenerateCandidateFirst &&
        !IsGenerating &&
        (IsValidateStage || IsRegistered || StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null);
    public bool CanOpenPaperScreen =>
        GenerateCandidateFirst &&
        !IsGenerating &&
        (IsPaperStage || StrategyWorkspace.Bindings.ValidationEvidenceHashSha256 is not null);

    public bool ShowDesignCandidateReview =>
        (IsBriefStage || IsChartDesignStage) && HasCandidate;
    public bool ShowBuildGenerationProgress =>
        IsBuildScreen && !ShowNativeStrategyRunPanel && IsGeneratingCandidates;
    public bool ShowBuildBusyStop =>
        IsBuildScreen && !ShowNativeStrategyRunPanel && IsGenerating && !IsGeneratingCandidates;
    public bool ShowBuildCandidateResults =>
        IsBuildScreen && !ShowNativeStrategyRunPanel && HasGeneratedCandidates;
    public bool ShowCandidateEmptyState => IsDesignScreen
        ? !HasCandidate
        : !ShowNativeStrategyRunPanel && !HasGeneratedCandidates &&
          !HasAuthoredUnitCSharpFiles && !IsGeneratingCandidates;
    public bool ShowStartImplementationAction =>
        IsBuildScreen &&
        !ShowNativeStrategyRunPanel &&
        !HasGeneratedCandidates &&
        !HasAuthoredUnitCSharpFiles &&
        !IsGeneratingCandidates;
    public bool ShowCliWorkspaceFooter =>
        IsBuildScreen && !ShowNativeStrategyRunPanel && AvailableClis.Count > 0;

    public string ActiveScreenTitle => IsResearchStudioShell
        ? "Research Studio"
        : !GenerateCandidateFirst
            ? "Expert Code"
            : ActiveScreen switch
            {
                StrategyAuthoringScreen.Brief => "Brief",
                StrategyAuthoringScreen.Research => "Research",
                StrategyAuthoringScreen.Design => "Design",
                StrategyAuthoringScreen.Build => "Build",
                StrategyAuthoringScreen.Validate => "Validate",
                StrategyAuthoringScreen.Paper => "Run",
                _ => "Design",
            };

    public string ActiveScreenDescription => IsResearchStudioShell
        ? "Inspect markets, study indicators, compare cases, and save findings — without creating a strategy."
        : !GenerateCandidateFirst
            ? "Direct C# authoring is a separate path; it does not inherit Strategy Builder confirmation."
            : ActiveScreen switch
            {
                StrategyAuthoringScreen.Brief =>
                    "Editable project description. Use Research Studio for charts and investigation.",
                StrategyAuthoringScreen.Research =>
                    "Chart-first investigation. Findings hand off into Design — Research is not a Builder stage.",
                StrategyAuthoringScreen.Design =>
                    "Edit entry, exit, sizing, risk, and order rules in the center. Open Research Studio when you need chart evidence.",
                StrategyAuthoringScreen.Build when ShowNativeStrategyRunPanel =>
                    "Inspect a retained native research → compare run.",
                StrategyAuthoringScreen.Build =>
                    "Generate or edit code, compile, and register this strategy version.",
                StrategyAuthoringScreen.Validate =>
                    "Backtest and replay this exact revision; inspect trades and failures.",
                StrategyAuthoringScreen.Paper =>
                    "Choose Paper or Live book, execution limits, then start and monitor.",
                _ => "Confirm rules, then Build.",
            };

    public string CandidateTabHeader => IsDesignScreen ? "Rules" : "Compare";

    public string CandidateEmptyTitle => IsChartDesignStage
        ? "Trading rules"
        : IsDesignScreen
            ? "Waiting for your first message"
            : "No implementation run yet";

    public string CandidateEmptyText => IsChartDesignStage
        ? "These fields are the working draft shared with Build after you review. Open Research Studio when you need chart evidence. Strategy templates are under Use template."
        : IsDesignScreen
            ? "Send a message in Hyperion. Proposed changes appear here for review before they become the build input."
            : "Start implementation when you want workers to generate code for the confirmed request.";

    [RelayCommand(CanExecute = nameof(CanOpenBriefScreenAction))]
    private void OpenBriefScreen()
    {
        if (!CanOpenBriefScreen) return;
        OpenStage(StrategyAuthoringScreen.Brief,
            "Project brief is open. Prefer Research for chart investigation.");
    }

    private bool CanOpenBriefScreenAction() => CanOpenBriefScreen;

    [RelayCommand(CanExecute = nameof(CanOpenResearchScreenAction))]
    private void OpenResearchScreen()
    {
        if (!CanOpenResearchScreen) return;
        if (IsResearchStudioShell)
        {
            OpenStage(StrategyAuthoringScreen.Research, "Research Studio is already open.");
            return;
        }

        MarkResearchOpenedFromBuilder();
        ResearchStudioRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Record Builder origin so Studio can offer Back without requiring a finding.</summary>
    public void MarkResearchOpenedFromBuilder()
    {
        ResearchOpenedFromBuilder = true;
        BuilderScreenBeforeResearch = ActiveScreen is StrategyAuthoringScreen.Research
            ? StrategyAuthoringScreen.Design
            : ActiveScreen;
        NotifyStrategyBuilderReturnStateChanged();
    }

    /// <summary>
    /// Navigation only: return to the Builder draft/stage. Does not transfer Research evidence.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReturnToStrategyBuilder))]
    private void ReturnToStrategyBuilder()
    {
        if (!CanReturnToStrategyBuilder) return;

        var target = BuilderScreenBeforeResearch;
        if (target is StrategyAuthoringScreen.Research)
            target = StrategyAuthoringScreen.Design;

        Status =
            $"Returned to {StrategyReturnDisplayName}. Research chart, indicators, and conversation are kept — reopen Research Studio to continue.";
        // Leave Research screen before clearing shell mode so OnActiveScreenChanged does not re-open Studio.
        ActiveScreen = target;
        WorkbenchTab = 3;
        IsResearchStudioShell = false;
        StrategyBuilderHandoffRequested?.Invoke(this, EventArgs.Empty);
        NotifyWorkingFlowMapChanged();
        NotifyAuthoringScreenStateChanged();
        NotifyStrategyBuilderReturnStateChanged();
    }

    private void NotifyStrategyBuilderReturnStateChanged()
    {
        OnPropertyChanged(nameof(CanReturnToStrategyBuilder));
        OnPropertyChanged(nameof(ReturnToStrategyBuilderText));
        OnPropertyChanged(nameof(UseInStrategyBuilderText));
        OnPropertyChanged(nameof(StrategyReturnDisplayName));
        OnPropertyChanged(nameof(AddFindingTransferPreviewText));
        ReturnToStrategyBuilderCommand.NotifyCanExecuteChanged();
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Builder asks the shell to open Research Studio as a separate workspace.</summary>
    public event EventHandler? ResearchStudioRequested;

    private bool CanOpenResearchScreenAction() => CanOpenResearchScreen;

    [RelayCommand(CanExecute = nameof(CanOpenBuildScreenAction))]
    private void OpenBuildScreen()
    {
        if (!CanOpenBuildScreen)
        {
            Status = IsNativeStrategyAgentWired
                ? "Stop the active task before opening Build, Test & Compare."
                : "Confirm the complete strategy request before opening Build, Test & Compare.";
            return;
        }

        OpenStage(StrategyAuthoringScreen.Build, string.Empty);
        Status = ShowNativeStrategyRunPanel
            ? "Build, Test & Compare is ready to load a retained native run ID. Chart-to-run creation is not connected here yet."
            : HasGeneratedCandidates
                ? "Build, Test & Compare is open on the retained implementation results."
                : "Build, Test & Compare is ready. Start implementation generation when you are ready.";
    }

    private bool CanOpenBuildScreenAction() => CanOpenBuildScreen;

    [RelayCommand(CanExecute = nameof(CanOpenDesignScreenAction))]
    private void OpenDesignScreen()
    {
        if (!CanOpenDesignScreen) return;

        OpenStage(StrategyAuthoringScreen.Design,
            "Design is open. Make entry, exit, sizing, and risk rules explicit before Build.");
    }

    private bool CanOpenDesignScreenAction() => CanOpenDesignScreen;

    [RelayCommand(CanExecute = nameof(CanOpenValidateScreenAction))]
    private void OpenValidateScreen()
    {
        if (!CanOpenValidateScreen) return;
        OpenStage(StrategyAuthoringScreen.Validate,
            "Validate is open. Run historical validation for this exact revision, then Run.");
    }

    private bool CanOpenValidateScreenAction() => CanOpenValidateScreen;

    [RelayCommand(CanExecute = nameof(CanOpenPaperScreenAction))]
    private void OpenPaperScreen()
    {
        if (!CanOpenPaperScreen) return;
        OpenStage(StrategyAuthoringScreen.Paper,
            "Run is open. Bind a Paper book (or Live when authorized), set limits, then start.");
    }

    private bool CanOpenPaperScreenAction() => CanOpenPaperScreen;

    private void OpenStage(StrategyAuthoringScreen stage, string status)
    {
        ActiveScreen = stage;
        WorkbenchTab = 3;
        // Research needs Hyperion + chart side by side — collapse the session rail so Send stays visible.
        if (stage == StrategyAuthoringScreen.Research)
            RailCollapsed = true;
        if (!string.IsNullOrWhiteSpace(status)) Status = status;
    }

    partial void OnActiveScreenChanged(StrategyAuthoringScreen value)
    {
        // Strategy Builder must never host Research chrome — route to Research Studio instead.
        if (value == StrategyAuthoringScreen.Research && !IsResearchStudioShell)
        {
            ActiveScreen = StrategyAuthoringScreen.Design;
            MarkResearchOpenedFromBuilder();
            ResearchStudioRequested?.Invoke(this, EventArgs.Empty);
            Status = "Opening Research Studio — Strategy Builder stays on Design.";
            return;
        }

        // Request/Compare is the only tab shared by both screens. Selecting it here avoids a blank
        // workbench when Design hides the implementation-only Code, Parameters, and Activity tabs.
        WorkbenchTab = 3;
        RefreshStarterBriefs();
        NotifyAuthoringScreenStateChanged();
        if (_ready && !_restoring) Save();
    }

    /// <summary>
    /// Enter Research only inside Research Studio. From Builder, open the Studio window instead.
    /// </summary>
    internal void EnterResearchWorkspace(string? status = null)
    {
        if (IsResearchStudioShell)
        {
            OpenStage(StrategyAuthoringScreen.Research, status ?? "Research Studio.");
            return;
        }

        if (ActiveScreen == StrategyAuthoringScreen.Research)
            ActiveScreen = StrategyAuthoringScreen.Design;
        ResearchStudioRequested?.Invoke(this, EventArgs.Empty);
        if (!string.IsNullOrWhiteSpace(status))
            Status = status;
    }

    private void RefreshAuthoringScreenGate()
    {
        if (GenerateCandidateFirst &&
            IsBuildScreen &&
            !IsNativeStrategyAgentWired &&
            !CanEnterFourLaneConformance)
        {
            ActiveScreen = StrategyAuthoringScreen.Design;
            Status = "The strategy request changed or lost confirmation. Review it again before implementation.";
            return;
        }

        NotifyAuthoringScreenStateChanged();
    }

    private void NotifyAuthoringScreenStateChanged()
    {
        // Research needs Hyperion + chart side-by-side; keep the session rail collapsed.
        if (IsResearchStage && !RailCollapsed)
            RailCollapsed = true;

        OnPropertyChanged(nameof(IsDesignScreen));
        OnPropertyChanged(nameof(IsBuildScreen));
        OnPropertyChanged(nameof(IsBriefStage));
        OnPropertyChanged(nameof(IsResearchStage));
        OnPropertyChanged(nameof(IsChartDesignStage));
        OnPropertyChanged(nameof(IsBuildStage));
        OnPropertyChanged(nameof(IsValidateStage));
        OnPropertyChanged(nameof(IsPaperStage));
        OnPropertyChanged(nameof(WorkbenchGridColumn));
        OnPropertyChanged(nameof(WorkbenchGridColumnSpan));
        OnPropertyChanged(nameof(ShowDesignInspector));
        OnPropertyChanged(nameof(ShowWorkbenchPanel));
        OnPropertyChanged(nameof(DesignInspectorWidth));
        OnPropertyChanged(nameof(DesignInspectorMinWidth));
        OnPropertyChanged(nameof(MainWorkspaceColumnDefinitions));
        OnPropertyChanged(nameof(ConversationColumnMaxWidth));
        OnPropertyChanged(nameof(ConversationColumnMinWidth));
        OnPropertyChanged(nameof(ConversationColumnWidth));
        OnPropertyChanged(nameof(ShowConversationEmptyState));
        OnPropertyChanged(nameof(ShowResearchComposerHint));
        OnPropertyChanged(nameof(ShowImplementationTabs));
        OnPropertyChanged(nameof(ShowScreenNavigation));
        OnPropertyChanged(nameof(ShowDesignRequestHeader));
        OnPropertyChanged(nameof(ShowImplementationHeader));
        OnPropertyChanged(nameof(ShowNativeImplementationHeader));
        OnPropertyChanged(nameof(ShowResearchWorkspace));
        OnPropertyChanged(nameof(ActiveArtifactKindText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(CanCompileCurrentSource));
        OnPropertyChanged(nameof(CanOpenDesignScreen));
        OnPropertyChanged(nameof(CanOpenBuildScreen));
        OnPropertyChanged(nameof(CanOpenBriefScreen));
        OnPropertyChanged(nameof(CanOpenResearchScreen));
        OnPropertyChanged(nameof(CanOpenValidateScreen));
        OnPropertyChanged(nameof(CanOpenPaperScreen));
        OnPropertyChanged(nameof(ShowDesignCandidateReview));
        OnPropertyChanged(nameof(ShowBuildGenerationProgress));
        OnPropertyChanged(nameof(ShowBuildBusyStop));
        OnPropertyChanged(nameof(ShowBuildCandidateResults));
        OnPropertyChanged(nameof(ShowCandidateEmptyState));
        OnPropertyChanged(nameof(ShowDesignRuleEditor));
        OnPropertyChanged(nameof(ShowStartImplementationAction));
        OnPropertyChanged(nameof(ShowCliWorkspaceFooter));
        OnPropertyChanged(nameof(ShowNativeStrategyRunPanel));
        OnPropertyChanged(nameof(ShowLegacyCandidateBoundary));
        OnPropertyChanged(nameof(ActiveScreenTitle));
        OnPropertyChanged(nameof(ActiveScreenDescription));
        OnPropertyChanged(nameof(CandidateTabHeader));
        OnPropertyChanged(nameof(CandidateEmptyTitle));
        NotifyWorkingFlowMapChanged();
        OnPropertyChanged(nameof(CandidateEmptyText));
        OpenDesignScreenCommand.NotifyCanExecuteChanged();
        OpenBuildScreenCommand.NotifyCanExecuteChanged();
        OpenBriefScreenCommand.NotifyCanExecuteChanged();
        OpenResearchScreenCommand.NotifyCanExecuteChanged();
        OpenValidateScreenCommand.NotifyCanExecuteChanged();
        OpenPaperScreenCommand.NotifyCanExecuteChanged();
    }

    private void NotifyDesignInspectorLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowDesignInspector));
        OnPropertyChanged(nameof(ShowWorkbenchPanel));
        OnPropertyChanged(nameof(DesignInspectorWidth));
        OnPropertyChanged(nameof(DesignInspectorMinWidth));
        OnPropertyChanged(nameof(MainWorkspaceColumnDefinitions));
        OnPropertyChanged(nameof(ConversationColumnMaxWidth));
        OnPropertyChanged(nameof(ConversationColumnMinWidth));
        OnPropertyChanged(nameof(ConversationColumnWidth));
        OnPropertyChanged(nameof(ShowConversationEmptyState));
        OnPropertyChanged(nameof(ShowResearchComposerHint));
        OnPropertyChanged(nameof(ShowDesignRequestHeader));
        OnPropertyChanged(nameof(ActiveArtifactKindText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
    }

    private static StrategyWorkspaceStageV1 ToWorkspaceStage(StrategyAuthoringScreen screen) => screen switch
    {
        StrategyAuthoringScreen.Brief => StrategyWorkspaceStageV1.Brief,
        StrategyAuthoringScreen.Research => StrategyWorkspaceStageV1.Research,
        StrategyAuthoringScreen.Design => StrategyWorkspaceStageV1.Design,
        StrategyAuthoringScreen.Build => StrategyWorkspaceStageV1.Build,
        StrategyAuthoringScreen.Validate => StrategyWorkspaceStageV1.Validate,
        StrategyAuthoringScreen.Paper => StrategyWorkspaceStageV1.Paper,
        _ => StrategyWorkspaceStageV1.Brief,
    };

    partial void OnHasDetachedImplementationSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCompileCurrentSource));
        CompileCommand.NotifyCanExecuteChanged();
    }
}
