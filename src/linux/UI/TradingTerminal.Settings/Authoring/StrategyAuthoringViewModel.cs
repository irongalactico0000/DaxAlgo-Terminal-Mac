using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.StrategyAgent;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// View-model for the AI Strategy Builder — a chat with a coding model about ONE strategy, plus the
/// files it writes and the compiler that judges them.
/// <list type="bullet">
///   <item><b>Chat</b> — a running <see cref="StrategyBuildSession"/>: the thread persists across turns,
///     so follow-ups ("tighten the stop"), the compiler's own errors, and the model's questions back to
///     the user all land in the same context. A reply with no code is a question, not a failure.</item>
///   <item><b>Code</b> — the files of the turn (a strategy is usually several), hand-editable; edits are
///     fed back into the next turn so the model patches what the user is actually looking at.</item>
///   <item><b>Compile</b> — the same <see cref="IStrategyCompiler"/> the manual path uses, so the policy
///     scan applies to model-written code: a strategy that P/Invokes never compiles, so it can never be
///     registered. Pressing Compile is the consent for running it.</item>
/// </list>
/// If the compiled class exposes a declarative <c>Schema</c>, its tunables render automatically in
/// <see cref="Parameters"/> via the shared auto-editor.
/// </summary>
public sealed partial class StrategyAuthoringViewModel : ViewModelBase, IDisposable
{
    /// <summary>Keeps the activity strip and the chat from growing without bound over a long session.</summary>
    private const int MaxActivityRows = 200;
    private const int MaxMessages = 400;

    private readonly IStrategyCompiler _compiler;
    private readonly IBacktestStrategyRegistry _registry;
    private readonly ILogger<StrategyAuthoringViewModel> _logger;
    private readonly IAiStrategyBuilder? _ai;
    private readonly IStrategyCandidateGeneratorV1? _candidateGenerator;
    private readonly IParallelStrategyCandidateGeneratorV1? _parallelCandidateGenerator;
    private readonly IStrategyIntentExtensionRegistryV1? _strategyIntentExtensionRegistry;
    private readonly AiCodegenOptions _options;
    private readonly AuthoredStrategyInstaller? _installer;
    private readonly ICliWorkspaceLauncher? _cliLauncher;
    private readonly IAuthoringSessionRepository _sessionRepository;
    private readonly IAuthoringChartReferenceRepository _chartReferenceRepository;
    private readonly IChartReferenceInspectorV1? _chartReferenceInspector;
    private readonly IChartPatternSearchV1? _chartPatternSearch;
    private readonly IResearchOutcomeGalleryScanV1? _researchOutcomeGalleryScan;
    private readonly IResearchMarketScreenerV1? _researchMarketScreener;
    private readonly IAuthoredUnitIntentClassifierV1? _authoredUnitIntentClassifier;
    private readonly IAuthoredUnitSpecificationGeneratorV1? _authoredUnitSpecificationGenerator;
    private readonly IAuthoredUnitSourceGeneratorV1? _authoredUnitSourceGenerator;
    private readonly IAuthoredUnitCompilerV1? _authoredUnitCompiler;
    private readonly IVisualizerRegistry? _visualizerRegistry;
    private readonly IStrategyKernelRegistry? _strategyKernelRegistry;
    private readonly IInstrumentRegistry? _instrumentRegistry;
    private readonly IResearchExperimentRunnerV1? _researchExperimentRunner;
    private readonly IResearchConditionSearchV1? _researchConditionSearch;

    private CancellationTokenSource? _generateCts;
    private StrategyBuildSession? _session;
    private StrategyGenerationSessionV1? _generationSession;
    private ParallelStrategyGenerationResultV1? _parallelCandidateBatch;
    private string? _fourLaneStrategyBrief;
    private string? _pendingFourLanePrompt;
    private string? _editorBaseGeneratedCandidateHash;
    private string? _generationProviderKey;
    private long _generationContextEpoch;
    private bool _filesEditedByUser;

    /// <summary>The model thread restored from disk, handed to the next session so a resumed conversation
    /// still remembers what it wrote. Cleared once used.</summary>
    private IReadOnlyList<CodegenMessage>? _restoredThread;
    private CodegenUsage? _restoredUsage;

    /// <summary>True while a saved session is being loaded — suppresses the auto-save and the
    /// "switched provider" notes that the restore itself would otherwise trigger.</summary>
    private bool _restoring;

    /// <summary>Set once the constructor's own property assignments are done, so seeding the pickers
    /// doesn't write the user-config file back with the defaults it just read.</summary>
    private bool _ready;

    public StrategyAuthoringViewModel(
        IStrategyCompiler compiler,
        IBacktestStrategyRegistry registry,
        ILogger<StrategyAuthoringViewModel> logger,
        IAiStrategyBuilder? ai = null,
        IStrategyCandidateGeneratorV1? candidateGenerator = null,
        IOptions<AiCodegenOptions>? options = null,
        AuthoredStrategyInstaller? installer = null,
        ICliWorkspaceLauncher? cliLauncher = null,
        IParallelStrategyCandidateGeneratorV1? parallelCandidateGenerator = null,
        IAuthoringSessionRepository? sessionRepository = null,
        ITradeIrCandidateSynthesizerV1? tradeIrCandidateSynthesizer = null,
        ITradeIrSimulatedBacktestRunnerV1? tradeIrSimulatedBacktestRunner = null,
        IStrategyIntentExtensionRegistryV1? strategyIntentExtensionRegistry = null,
        IStrategyAgentClient? strategyAgentClient = null,
        IAuthoringChartReferenceRepository? chartReferenceRepository = null,
        IChartReferenceInspectorV1? chartReferenceInspector = null,
        IChartPatternSearchV1? chartPatternSearch = null,
        IResearchOutcomeGalleryScanV1? researchOutcomeGalleryScan = null,
        IAuthoredUnitIntentClassifierV1? authoredUnitIntentClassifier = null,
        IAuthoredUnitSpecificationGeneratorV1? authoredUnitSpecificationGenerator = null,
        IInstrumentRegistry? instrumentRegistry = null,
        IAuthoredUnitSourceGeneratorV1? authoredUnitSourceGenerator = null,
        IAuthoredUnitCompilerV1? authoredUnitCompiler = null,
        IVisualizerRegistry? visualizerRegistry = null,
        IStrategyKernelRegistry? strategyKernelRegistry = null,
        IResearchExperimentRunnerV1? researchExperimentRunner = null,
        IResearchConditionSearchV1? researchConditionSearch = null,
        IResearchMarketScreenerV1? researchMarketScreener = null)
    {
        _compiler = compiler;
        _registry = registry;
        _logger = logger;
        _ai = ai;
        _candidateGenerator = candidateGenerator;
        _parallelCandidateGenerator = parallelCandidateGenerator;
        _strategyIntentExtensionRegistry = strategyIntentExtensionRegistry;
        _strategyAgentClient = strategyAgentClient;
        _tradeIrCandidateSynthesizer = tradeIrCandidateSynthesizer;
        _tradeIrSimulatedBacktestRunner = tradeIrSimulatedBacktestRunner;
        _options = options?.Value ?? new AiCodegenOptions();
        _installer = installer;
        _cliLauncher = cliLauncher;
        _sessionRepository = sessionRepository ?? FileAuthoringSessionRepository.Instance;
        _chartReferenceRepository = chartReferenceRepository ?? new FileAuthoringChartReferenceRepository();
        _chartReferenceInspector = chartReferenceInspector;
        _chartPatternSearch = chartPatternSearch;
        _researchOutcomeGalleryScan = researchOutcomeGalleryScan;
        _authoredUnitIntentClassifier = authoredUnitIntentClassifier;
        _authoredUnitSpecificationGenerator = authoredUnitSpecificationGenerator;
        _authoredUnitSourceGenerator = authoredUnitSourceGenerator;
        _authoredUnitCompiler = authoredUnitCompiler;
        _visualizerRegistry = visualizerRegistry;
        _strategyKernelRegistry = strategyKernelRegistry;
        _instrumentRegistry = instrumentRegistry;
        _researchExperimentRunner = researchExperimentRunner;
        _researchConditionSearch = researchConditionSearch;
        _researchMarketScreener = researchMarketScreener;

        Diagnostics = [];
        Messages = [];
        Activity = [];
        Files = [];
        Tasks = [];
        CandidateGroups = [];
        CandidateOpenQuestions = [];
        CandidateBuildSupport = [];
        CandidateIssues = [];
        GeneratedCandidateOptions = [];
        GenerationLaneProgressRows = [];
        ChartReferences = [];
        ChartReferenceInspections = [];
        ChartPatternSelections = [];
        AllStarterBriefs = StrategyStarterCatalog.All;
        foreach (var brief in AllStarterBriefs) AddStrategyIntentProfile(brief);
        VisibleStarterBriefs = [];
        StarterFamilyOptions = [AllStarterFamilies, .. StrategyStarterFamilies.All];
        StarterHorizonOptions =
        [
            AllStarterHorizons,
            .. AllStarterBriefs.Select(brief => brief.AxisLabels.Horizon)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static label => label, StringComparer.Ordinal),
        ];
        StarterDataOptions =
        [
            AllStarterData,
            .. AllStarterBriefs.SelectMany(brief => brief.AxisLabels.Data)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static label => label, StringComparer.Ordinal),
        ];
        RefreshStarterBriefs();
        ClearTurnFollowUps();

        // The hero empty state ↔ transcript switch watches the count; the VM owns the collection,
        // so the self-subscription cannot outlive it.
        Messages.CollectionChanged += OnMessagesCollectionChanged;

        // Backing field, not the property — the change handler resets sessions and persists, neither of
        // which applies to seeding the ctor's own default from config.
        _buildEffort = StrategyBuildEfforts.Parse(_options.BuildEffort);

        // The unified picker's rows — built BEFORE the provider selection below, so the initial
        // provider/model choice can sync into it.
        AllModels = new ObservableCollection<AiModelChoice>(_ai?.AllModels() ?? []);

        // Provider picker — every provider the app can build; unavailable ones show disabled so the user
        // sees "install Claude Code / add an API key". Null builder (AI not wired) ⇒ the chat pane hides.
        AiProviders = new ObservableCollection<AiProviderChoice>(
            (_ai?.Providers ?? []).Select(p => new AiProviderChoice(p)));
        SelectedAiProvider = AiProviders.FirstOrDefault(p =>
            _ai?.DefaultProvider is { } d && p.ProviderId == d.ProviderId)
            ?? AiProviders.FirstOrDefault(p => p.IsAvailable)
            ?? AiProviders.FirstOrDefault();

        SetFiles([new StrategyFile(StrategyFile.DefaultName, TemplateSource)]);
        _filesEditedByUser = false;
        _ready = true;

        // A strategy is several sittings' work. Offer saved chats in the rail, but start on Design
        // so Strategy Builder is not confused with Research Studio.
        RefreshSavedSessions();
        ActiveScreen = StrategyAuthoringScreen.Design;
        Status = "Design is open — edit entry, exit, sizing, risk, and order rules. Research Studio is optional.";
    }

    /// <summary>True when the AI builder is wired at all — drives the chat pane's visibility. When wired
    /// but nothing is usable, the pane shows setup guidance instead.</summary>
    public bool AiEnabled => _ai is not null;
    public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);

    /// <summary>False until the first message lands — the canvas shows the axis-filtered starter
    /// catalog instead of an empty transcript.</summary>
    public bool HasConversation => Messages.Count > 0;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(ShowConversationEmptyState));
        OnPropertyChanged(nameof(ShowResearchComposerHint));
    }

    private const string AllStarterFamilies = "All families";
    private const string AllStarterHorizons = "All horizons";
    private const string AllStarterData = "All data";

    /// <summary>
    /// Curated discovery starters classified by the canonical strategy axes. Families are overlapping
    /// navigation lenses rather than a claim that the catalog enumerates every possible strategy.
    /// </summary>
    public IReadOnlyList<StrategyStarterBrief> AllStarterBriefs { get; }
    public ObservableCollection<StrategyStarterBrief> VisibleStarterBriefs { get; }
    public IReadOnlyList<string> StarterFamilyOptions { get; }
    public IReadOnlyList<string> StarterHorizonOptions { get; }
    public IReadOnlyList<string> StarterDataOptions { get; }

    [ObservableProperty] private string _starterSearchText = string.Empty;
    [ObservableProperty] private string _selectedStarterFamily = AllStarterFamilies;
    [ObservableProperty] private string _selectedStarterHorizon = AllStarterHorizons;
    [ObservableProperty] private string _selectedStarterData = AllStarterData;

    public string StarterResultText =>
        $"{VisibleStarterBriefs.Count} of {AllStarterBriefs.Count} templates (optional)";

    public string StarterSearchWatermark =>
        $"Search {AllStarterBriefs.Count} optional templates — or start from a blank research question…";

    partial void OnStarterSearchTextChanged(string value) => RefreshStarterBriefs();
    partial void OnSelectedStarterFamilyChanged(string value) => RefreshStarterBriefs();
    partial void OnSelectedStarterHorizonChanged(string value) => RefreshStarterBriefs();
    partial void OnSelectedStarterDataChanged(string value) => RefreshStarterBriefs();

    private void RefreshStarterBriefs()
    {
        var search = StarterSearchText.Trim();
        var filtered = AllStarterBriefs.Where(brief =>
            (SelectedStarterFamily == AllStarterFamilies || brief.FamilyLabels.Contains(SelectedStarterFamily)) &&
            (SelectedStarterHorizon == AllStarterHorizons ||
                string.Equals(brief.AxisLabels.Horizon, SelectedStarterHorizon, StringComparison.Ordinal)) &&
            (SelectedStarterData == AllStarterData || brief.AxisLabels.Data.Contains(SelectedStarterData)) &&
            StrategyStarterCatalog.MatchesSearch(brief, search) &&
            // QuoteL1 EMA smoke is a Build/conformance fixture — not a Design/Research starter.
            !( !IsBuildStage &&
              string.Equals(brief.Id, "starter.quote-l1-ema-smoke", StringComparison.Ordinal)));

        VisibleStarterBriefs.Clear();
        foreach (var brief in filtered) VisibleStarterBriefs.Add(brief);
        OnPropertyChanged(nameof(StarterResultText));
    }

    [RelayCommand]
    private void ClearStarterFilters()
    {
        StarterSearchText = string.Empty;
        SelectedStarterFamily = AllStarterFamilies;
        SelectedStarterHorizon = AllStarterHorizons;
        SelectedStarterData = AllStarterData;
        RefreshStarterBriefs();
    }

    [RelayCommand]
    private void UseStarterPrompt(StrategyStarterBrief? brief)
    {
        if (brief is null) return;

        // Research-led creation: templates seed an investigation question. They do not confirm a
        // strategy profile or freeze executable rules — that happens only after Use in Design.
        EnterResearchWorkspace();
        Composer = BuildResearchQuestionFromStarter(brief);
        AiStatus =
            $"Research starter “{brief.Title}” loaded. Inspect the chart and save an observation before specifying trading rules.";
        Status =
            "Research question ready. No strategy specification, compile, or register yet.";
        NotifyWorkingFlowMapChanged();
    }

    /// <summary>
    /// Explicit smoke/build path: loads the known QuoteL1 EMA rule brief for generation tests.
    /// Not the Research-led empty-state default.
    /// </summary>
    [RelayCommand]
    private void UseQuoteL1EmaSmokeStarter()
    {
        var starter = AllStarterBriefs.First(brief =>
            string.Equals(brief.Id, "starter.quote-l1-ema-smoke", StringComparison.Ordinal));
        SelectStrategyIntentProfile(starter);
        Composer = StrategyStarterCatalog.QuoteL1EmaSmokePrompt;
        AiStatus = "Loaded the known QuoteL1 EMA smoke starter. Review the brief, then generate a fresh candidate batch.";
    }

    private static string BuildResearchQuestionFromStarter(StrategyStarterBrief brief)
    {
        var summary = brief.Summary.Trim();
        var prompt = brief.Prompt.Trim();
        return
            $"Investigate before writing trading rules: {summary}\n\n" +
            "Questions to answer on the chart:\n" +
            "- Which instrument and session show this behavior?\n" +
            "- What measurable condition appears before the move — and when does it fail?\n" +
            "- Which indicators are for analysis only vs candidates for Design?\n\n" +
            $"Template context (not yet a strategy specification):\n{prompt}";
    }

    /// <summary>Collapses the session rail to an icon strip — the workspace's only chrome toggle.</summary>
    [ObservableProperty] private bool _railCollapsed;

    /// <summary>
    /// Research DETAILS (condition / labels / references) — collapsed by default so the chart stays the main surface.
    /// </summary>
    [ObservableProperty] private bool _researchDetailsOpen;

    [RelayCommand]
    private void ToggleRail() => RailCollapsed = !RailCollapsed;

    [RelayCommand]
    private void ToggleResearchDetails() => ResearchDetailsOpen = !ResearchDetailsOpen;

    public string ResearchDetailsToggleText =>
        ResearchDetailsOpen ? "Hide details" : "Condition & labels";

    partial void OnResearchDetailsOpenChanged(bool value) =>
        OnPropertyChanged(nameof(ResearchDetailsToggleText));

    /// <summary>Selected workbench tab: 0 Code · 1 Parameters · 2 Activity. A file chip in the chat
    /// sets it back to Code so the click always lands on the file it names.</summary>
    [ObservableProperty] private int _workbenchTab = 3;

    // ── Strategy candidate (semantic lane, before source generation) ───────────────────────────────

    /// <summary>
    /// On by default: chat produces a reviewable Candidate rather than source. Turning it off keeps
    /// the existing Expert Code lane available for imported or hand-authored implementations.
    /// </summary>
    [ObservableProperty] private bool _generateCandidateFirst = true;

    [ObservableProperty] private StrategyCandidateV1? _currentCandidate;
    [ObservableProperty] private StrategyCandidateAssessmentV1? _candidateAssessment;
    [ObservableProperty] private string? _candidateContentHash;
    [ObservableProperty] private string? _candidateStatusText;
    [ObservableProperty] private string? _candidateRestoreWarning;
    [ObservableProperty] private bool _candidateBatchRestored;
    [ObservableProperty] private StrategyGenerationLaneProgressRow? _selectedGenerationLaneProgressRow;
    [ObservableProperty] private StrategyGenerationCandidateOption? _selectedGeneratedCandidateOption;
    [ObservableProperty] private string? _chosenGeneratedCandidateHash;

    public ObservableCollection<StrategyCandidateGroupRow> CandidateGroups { get; }
    public ObservableCollection<StrategyCandidateStatementV1> CandidateOpenQuestions { get; }
    public ObservableCollection<StrategyBuildSupportRow> CandidateBuildSupport { get; }
    public ObservableCollection<StrategyCandidateIssueV1> CandidateIssues { get; }
    public ObservableCollection<StrategyGenerationCandidateOption> GeneratedCandidateOptions { get; }
    public ObservableCollection<StrategyGenerationLaneProgressRow> GenerationLaneProgressRows { get; }

    /// <summary>
    /// Exact reference artifacts attached to this request. Their similarity modes are explicit so
    /// "like this chart" cannot silently mean visual styling in one stage and price-pattern search
    /// in another.
    /// </summary>
    public ObservableCollection<AuthoringChartReferenceSnapshot> ChartReferences { get; }
    public ObservableCollection<AuthoredChartReferenceInspectionV1> ChartReferenceInspections { get; }
    public ObservableCollection<ChartPatternSelectionV1> ChartPatternSelections { get; }
    public IReadOnlyList<BarSize> ChartPatternTimeframeOptions { get; } = Enum.GetValues<BarSize>();

    [ObservableProperty] private AuthoredUnitSpecificationV1? _authoredUnitSpecification;
    [ObservableProperty] private bool _isInspectingChartReferences;
    [ObservableProperty] private bool _isSearchingChartPatterns;
    [ObservableProperty] private BarSize _selectedChartPatternTimeframe = BarSize.OneHour;
    [ObservableProperty] private ChartPatternSearchResultV1? _chartPatternSearchResult;
    [ObservableProperty] private bool _isScanningResearchGallery;
    [ObservableProperty] private ResearchOutcomeGalleryResultV1? _researchOutcomeGalleryResult;
    [ObservableProperty] private ResearchGalleryCardViewModel? _selectedResearchGalleryCard;

    public ObservableCollection<ResearchQuickSuggestionV1> ResearchQuickSuggestions { get; } = [];
    public ObservableCollection<ResearchGalleryCardViewModel> ResearchGalleryCards { get; } = [];
    public bool HasResearchQuickSuggestions => ResearchQuickSuggestions.Count > 0;
    public bool HasResearchGalleryCards => ResearchGalleryCards.Count > 0;

    public bool HasChartReferences => ChartReferences.Count > 0;
    public bool HasChartReferenceInspections => ChartReferenceInspections.Count > 0;
    public bool HasSearchableChartReference => ChartReferences.Any(reference =>
        reference.Reference.Similarity.Any(static aspect => aspect is
            ChartReferenceSimilarityV1.MarketPattern or ChartReferenceSimilarityV1.RelatedInstruments) &&
        ChartReferenceInspections.Any(inspection =>
            string.Equals(inspection.ReferenceId, reference.Reference.ReferenceId, StringComparison.Ordinal) &&
            string.Equals(inspection.ContentHashSha256, reference.Reference.ContentHashSha256, StringComparison.Ordinal) &&
            inspection.PatternFingerprint is not null));
    public bool HasChartPatternSearchResult => ChartPatternSearchResult is not null;
    public bool HasChartPatternMatches => ChartPatternSearchResult?.Matches.Count > 0;
    public bool HasResearchOutcomeGalleryResult => ResearchOutcomeGalleryResult is not null;
    public bool HasResearchOutcomeGalleryMatches => ResearchOutcomeGalleryResult?.Matches.Count > 0;

    public string ResearchGalleryCoverageText => ResearchOutcomeGalleryResult is { } gallery
        ? (string.IsNullOrWhiteSpace(gallery.CoverageSummary)
            ? gallery.Explanation
            : gallery.CoverageSummary)
        : "No gallery scan yet.";

    public string ResearchGalleryDataProvenanceText => ResearchOutcomeGalleryResult is { } gallery
        ? (string.IsNullOrWhiteSpace(gallery.DataProvenanceSummary)
            ? "Data source for returned events is not summarized yet."
            : gallery.DataProvenanceSummary)
        : string.Empty;
    public bool HasSelectedChartPattern => ChartPatternSelections.Count > 0;
    public string SelectedChartPatternText => ChartPatternSelections.LastOrDefault() is { } selection
        ? $"Selected {selection.Match.CanonicalSymbol} · score {selection.Match.Score:0.0} · {selection.Match.Timeframe.ToDisplayString()}"
        : "No historical match selected";
    public bool HasUninspectedChartReferences => ChartReferences.Any(reference =>
        !ChartReferenceInspections.Any(inspection =>
            string.Equals(inspection.ReferenceId, reference.Reference.ReferenceId, StringComparison.Ordinal) &&
            string.Equals(inspection.ContentHashSha256, reference.Reference.ContentHashSha256, StringComparison.Ordinal)));
    public bool HasUnresolvedChartReferences
    {
        get
        {
            if (ChartReferences.Count == 0) return false;
            if (AuthoredUnitSpecification is null) return true;

            var resolvedIds = AuthoredUnitSpecification.ReferenceResolutions
                .Select(static resolution => resolution.ReferenceId)
                .ToHashSet(StringComparer.Ordinal);
            return ChartReferences.Any(item => !resolvedIds.Contains(item.Reference.ReferenceId));
        }
    }

    public string ChartReferenceSummary => ChartReferences.Count switch
    {
        0 => "No reference chart attached",
        1 => "1 exact reference chart attached",
        _ => $"{ChartReferences.Count} exact reference charts attached",
    };
    public string ChartReferenceReadinessText => HasUninspectedChartReferences
        ? "Reference analysis is required before implementation generation. Hyperion will not guess from the filename or prompt."
        : HasUnresolvedChartReferences && ChartReferences.Any(item => item.Reference.Similarity.Any(static aspect => aspect is
                ChartReferenceSimilarityV1.MarketPattern or ChartReferenceSimilarityV1.RelatedInstruments))
            ? "The image was inspected; historical similarity search and instrument selection are still required before generation."
            : HasUnresolvedChartReferences
                ? "The image was inspected; its reviewed layers must now be frozen into the authored-unit specification before generation."
        : HasChartReferences
            ? "Every attached reference is hash-bound to a verified analysis."
            : string.Empty;

    partial void OnAuthoredUnitSpecificationChanged(AuthoredUnitSpecificationV1? value)
    {
        OnPropertyChanged(nameof(HasUnresolvedChartReferences));
        OnPropertyChanged(nameof(ChartReferenceReadinessText));
        OnPropertyChanged(nameof(CanGenerateFourCandidates));
        OnPropertyChanged(nameof(CanGenerateCanonicalPaperStrategy));
        OnPropertyChanged(nameof(HasAuthoredUnitCSharpFiles));
        OnPropertyChanged(nameof(CanCompileCurrentSource));
        OnPropertyChanged(nameof(CanExportOpenPackage));
        GenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        GenerateCanonicalPaperStrategyCommand.NotifyCanExecuteChanged();
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        CompileCommand.NotifyCanExecuteChanged();
        RefreshInteractionBindingsInspector();
        PromptRequiredParameterPresence();
    }

    /// <summary>
    /// Hyperion chat: list Required parameters that still need a value; Defaultable stay as catalog defaults.
    /// </summary>
    private void PromptRequiredParameterPresence()
    {
        if (AuthoredUnitSpecification is not { } specification)
            return;

        if (AuthoredUnitParameterPresenceRulesV1.TryDescribeUnsetRequired(specification.Parameters, out var requiredMessage))
        {
            AiStatus = requiredMessage + " Open the Bindings tab and Apply each Required value.";
            Append(AuthoringMessage.Tool(
                "Ask",
                "Required parameters",
                requiredMessage + " Defaultable parameters keep their catalog defaults until you edit them."));
            WorkbenchTab = 4; // Bindings
            return;
        }

        var defaultable = specification.Parameters
            .Where(static item => item.Presence == AuthoredUnitParameterPresenceV1.Defaultable)
            .Select(static item => item.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (defaultable.Length == 0)
            return;

        if (AiStatus is null || !AiStatus.Contains("Required parameters", StringComparison.Ordinal))
        {
            var summary = defaultable.Length <= 6
                ? string.Join(", ", defaultable)
                : string.Join(", ", defaultable.Take(6)) + $" (+{defaultable.Length - 6} more)";
            AiStatus = $"Defaultable parameters using catalog defaults: {summary}.";
        }
    }

    partial void OnIsInspectingChartReferencesChanged(bool value)
    {
        InspectChartReferencesCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSearchingChartPatternsChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnChartPatternSearchResultChanged(ChartPatternSearchResultV1? value)
    {
        OnPropertyChanged(nameof(HasChartPatternSearchResult));
        OnPropertyChanged(nameof(HasChartPatternMatches));
    }

    partial void OnResearchOutcomeGalleryResultChanged(ResearchOutcomeGalleryResultV1? value)
    {
        OnPropertyChanged(nameof(HasResearchOutcomeGalleryResult));
        OnPropertyChanged(nameof(HasResearchOutcomeGalleryMatches));
        OnPropertyChanged(nameof(ResearchGalleryCoverageText));
        OnPropertyChanged(nameof(ResearchGalleryDataProvenanceText));
        RebuildResearchGalleryCards(value);
        NotifyWorkingFlowMapChanged();
    }

    private void RebuildResearchGalleryCards(ResearchOutcomeGalleryResultV1? result)
    {
        ResearchGalleryCards.Clear();
        SelectedResearchGalleryCard = null;
        if (result is null) return;
        foreach (var match in result.Matches)
            ResearchGalleryCards.Add(new ResearchGalleryCardViewModel(match));
        OnPropertyChanged(nameof(HasResearchGalleryCards));
    }

    private void SelectResearchGalleryCard(ResearchOutcomeGalleryMatchV1? match)
    {
        foreach (var card in ResearchGalleryCards)
            card.IsSelected = match is not null && ReferenceEquals(card.Match, match);
        SelectedResearchGalleryCard = ResearchGalleryCards.FirstOrDefault(card => card.IsSelected);
        OnPropertyChanged(nameof(ActiveArtifactKindText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
        PublishTurnFollowUps(lastUserText: null);
    }

    partial void OnSelectedResearchGalleryCardChanged(ResearchGalleryCardViewModel? value)
    {
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnIsScanningResearchGalleryChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    private bool CanInspectChartReferences() =>
        HasUninspectedChartReferences &&
        !IsInspectingChartReferences &&
        !IsGenerating &&
        _chartReferenceInspector is not null &&
        SelectedAiProvider is { IsAvailable: true };

    [RelayCommand(CanExecute = nameof(CanInspectChartReferences))]
    private async Task InspectChartReferencesAsync()
    {
        var choice = SelectedAiProvider;
        var provider = choice is null ? null : ResolveClient(choice) ?? choice.Client;
        if (provider is null || _chartReferenceInspector is null) return;

        IsInspectingChartReferences = true;
        try
        {
            foreach (var reference in ChartReferences.Where(item =>
                         !ChartReferenceInspections.Any(inspection =>
                             string.Equals(inspection.ReferenceId, item.Reference.ReferenceId, StringComparison.Ordinal) &&
                             string.Equals(inspection.ContentHashSha256, item.Reference.ContentHashSha256, StringComparison.Ordinal))))
            {
                var content = await File.ReadAllBytesAsync(reference.ArtifactPath);
                var result = await _chartReferenceInspector.InspectAsync(
                    provider,
                    new ChartReferenceInspectionRequestV1(reference.Reference, content));
                InputTokens += result.Usage.InputTokens;
                OutputTokens += result.Usage.OutputTokens;
                CachedTokens += result.Usage.CachedInputTokens;
                if (!result.Success)
                {
                    Status = $"Reference analysis stopped: {result.Error}";
                    return;
                }

                ChartReferenceInspections.Add(result.Inspection!);
                if (TryParseChartTimeframe(result.Inspection!.VisibleTimeframe, out var inspectedTimeframe))
                    SelectedChartPatternTimeframe = inspectedTimeframe;
            }

            OnPropertyChanged(nameof(HasChartReferenceInspections));
            OnPropertyChanged(nameof(HasUninspectedChartReferences));
            OnPropertyChanged(nameof(HasSearchableChartReference));
            Status = ChartReferences.Any(item => item.Reference.Similarity.Any(static aspect => aspect is
                    ChartReferenceSimilarityV1.MarketPattern or ChartReferenceSimilarityV1.RelatedInstruments))
                ? "Chart pixels were inspected. The extracted pattern query must now search real market history before matching instruments can be selected."
                : "Chart pixels were inspected and hash-bound. The observed layout and indicators are ready for authored-unit specification generation.";
            Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not read a saved reference artifact");
            Status = $"Reference analysis stopped: {exception.Message}";
        }
        finally
        {
            IsInspectingChartReferences = false;
        }
    }

    [RelayCommand]
    private async Task SearchSimilarChartsAsync(string? scopeName)
    {
        if (_chartPatternSearch is null)
        {
            Status = "Historical chart search is not available in this app composition.";
            return;
        }
        if (!Enum.TryParse<ChartPatternCandidateScopeV1>(scopeName, ignoreCase: true, out var scope) ||
            !Enum.IsDefined(scope))
        {
            Status = "Choose whether to search all known instruments or indexes only.";
            return;
        }

        var reference = ChartReferences.FirstOrDefault(item => item.Reference.Similarity.Any(static aspect =>
            aspect is ChartReferenceSimilarityV1.MarketPattern or ChartReferenceSimilarityV1.RelatedInstruments));
        if (reference is null)
        {
            Status = "Attach a reference as a historical pattern or related-instrument request first.";
            return;
        }
        var inspection = ChartReferenceInspections.FirstOrDefault(item =>
            string.Equals(item.ReferenceId, reference.Reference.ReferenceId, StringComparison.Ordinal) &&
            string.Equals(item.ContentHashSha256, reference.Reference.ContentHashSha256, StringComparison.Ordinal));
        if (inspection?.PatternFingerprint is null)
        {
            Status = "Analyze the reference image before searching market history.";
            return;
        }

        IsSearchingChartPatterns = true;
        try
        {
            ChartPatternSearchResult = await _chartPatternSearch.SearchAsync(
                new ChartPatternSearchRequestV1(
                    reference.Reference.ReferenceId,
                    reference.Reference.ContentHashSha256,
                    inspection.PatternFingerprint,
                    SelectedChartPatternTimeframe,
                    Scope: scope),
                CancellationToken.None);
            RemoveChartPatternSelections(reference.Reference.ReferenceId);
            Status = ChartPatternSearchResult.Matches.Count == 0
                ? $"No comparable {SelectedChartPatternTimeframe.ToDisplayString()} history was found. {ChartPatternSearchResult.Explanation}"
                : $"Found {ChartPatternSearchResult.Matches.Count} evidence-backed matches. Choose one before generating the chart unit. " +
                  ChartPatternSearchResult.Explanation;
            Save();
        }
        catch (OperationCanceledException)
        {
            Status = "Historical chart search stopped.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Historical chart similarity search failed");
            Status = $"Historical chart search stopped: {exception.Message}";
        }
        finally
        {
            IsSearchingChartPatterns = false;
        }
    }

    [RelayCommand]
    private void UseChartPatternMatch(ChartPatternMatchV1? match)
    {
        if (match is null || ChartPatternSearchResult is null ||
            !ChartPatternSearchResult.Matches.Contains(match)) return;

        RemoveChartPatternSelections(ChartPatternSearchResult.ReferenceId);
        ChartPatternSelections.Add(new ChartPatternSelectionV1(
            ChartPatternSearchResult.ReferenceId,
            ChartPatternSearchResult.ReferenceContentHashSha256,
            match));
        OnPropertyChanged(nameof(HasSelectedChartPattern));
        OnPropertyChanged(nameof(SelectedChartPatternText));
        Status = $"Selected {match.CanonicalSymbol} from real {match.Timeframe.ToDisplayString()} history. " +
                 "The selection describes similarity, not expected future return.";
        AuthoredUnitSpecification = null;
        Save();
    }

    /// <summary>
    /// A→B slice: use the similar-history match and open host Charts on that symbol/window (draft only).
    /// </summary>
    [RelayCommand]
    private void OpenChartPatternMatch(ChartPatternMatchV1? match)
    {
        if (match is null || ChartPatternSearchResult is null ||
            !ChartPatternSearchResult.Matches.Contains(match))
            return;

        UseChartPatternMatch(match);
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                Array.Empty<string>(),
                startResearchCapture: false,
                preferredSymbol: match.CanonicalSymbol,
                galleryMatch: null,
                historyFromUtc: match.FromUtc,
                historyToUtc: match.ToUtc,
                historyBarSize: match.Timeframe));
        Status =
            $"Opening Charts for {match.CanonicalSymbol} · {match.Timeframe.ToDisplayString()} " +
            $"({match.FromUtc:u} → {match.ToUtc:u}). Similarity only — not predicted return. No orders.";
    }

    private void RemoveChartPatternSelections(string referenceId)
    {
        for (var index = ChartPatternSelections.Count - 1; index >= 0; index--)
            if (string.Equals(ChartPatternSelections[index].ReferenceId, referenceId, StringComparison.Ordinal))
                ChartPatternSelections.RemoveAt(index);
        OnPropertyChanged(nameof(HasSelectedChartPattern));
        OnPropertyChanged(nameof(SelectedChartPatternText));
    }

    private static bool TryParseChartTimeframe(string? value, out BarSize timeframe)
    {
        var normalized = value?.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        timeframe = normalized switch
        {
            "1m" or "1min" or "1minute" => BarSize.OneMinute,
            "3m" or "3min" or "3minutes" => BarSize.ThreeMinutes,
            "5m" or "5min" or "5minutes" => BarSize.FiveMinutes,
            "15m" or "15min" or "15minutes" => BarSize.FifteenMinutes,
            "1h" or "1hr" or "1hour" => BarSize.OneHour,
            "1d" or "1day" or "daily" => BarSize.OneDay,
            _ => default,
        };
        return normalized is "1m" or "1min" or "1minute" or
            "3m" or "3min" or "3minutes" or
            "5m" or "5min" or "5minutes" or
            "15m" or "15min" or "15minutes" or
            "1h" or "1hr" or "1hour" or
            "1d" or "1day" or "daily";
    }

    [RelayCommand]
    private async Task AttachChartReferenceAsync(string? similarityName)
    {
        if (!Enum.TryParse<ChartReferenceSimilarityV1>(similarityName, ignoreCase: true, out var similarity) ||
            !Enum.IsDefined(similarity))
        {
            Status = "Choose whether the reference means appearance, indicators, market pattern, or related instruments.";
            return;
        }

        var path = await UiFile.OpenAsync(
            "Reference chart",
            ["png", "jpg", "jpeg", "webp", "pdf", "json"]);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var imported = await _chartReferenceRepository.ImportAsync(path, similarity);
            var duplicate = ChartReferences.FirstOrDefault(item =>
                string.Equals(
                    item.Reference.ContentHashSha256,
                    imported.Reference.ContentHashSha256,
                    StringComparison.Ordinal));
            if (duplicate is not null)
            {
                var merged = duplicate.Reference.Similarity
                    .Append(similarity)
                    .Distinct()
                    .OrderBy(static value => value)
                    .ToArray();
                var index = ChartReferences.IndexOf(duplicate);
                ChartReferences[index] = duplicate with
                {
                    Reference = duplicate.Reference with { Similarity = merged },
                };
            }
            else
            {
                ChartReferences.Add(imported);
            }

            AuthoredUnitSpecification = null;
            RemoveChartReferenceInspections(imported.Reference.ReferenceId);
            ChartPatternSearchResult = null;
            RemoveChartPatternSelections(imported.Reference.ReferenceId);
            OnPropertyChanged(nameof(HasChartReferences));
            NotifyDesignInspectorLayoutChanged();
            OnPropertyChanged(nameof(HasChartReferenceInspections));
            OnPropertyChanged(nameof(HasUninspectedChartReferences));
            OnPropertyChanged(nameof(HasSearchableChartReference));
            OnPropertyChanged(nameof(HasUnresolvedChartReferences));
            OnPropertyChanged(nameof(ChartReferenceSummary));
            OnPropertyChanged(nameof(ChartReferenceReadinessText));
            Status = similarity switch
            {
                ChartReferenceSimilarityV1.MarketPattern =>
                    "Reference saved for historical price-pattern search. It must resolve candidate instruments before a chart can launch.",
                ChartReferenceSimilarityV1.RelatedInstruments =>
                    "Reference saved for related-instrument search. It must resolve candidate instruments before a chart can launch.",
                ChartReferenceSimilarityV1.IndicatorComposition =>
                    "Reference saved for indicator extraction. The analyzed layers must be reviewed before generation.",
                _ => "Reference saved for chart appearance and layout analysis.",
            };
            Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not import reference chart");
            Status = $"Reference chart was not attached: {exception.Message}";
        }
    }

    [RelayCommand]
    private void RemoveChartReference(AuthoringChartReferenceSnapshot? reference)
    {
        if (reference is null || !ChartReferences.Remove(reference)) return;
        RemoveChartReferenceInspections(reference.Reference.ReferenceId);
        if (string.Equals(ChartPatternSearchResult?.ReferenceId, reference.Reference.ReferenceId, StringComparison.Ordinal))
            ChartPatternSearchResult = null;
        RemoveChartPatternSelections(reference.Reference.ReferenceId);
        AuthoredUnitSpecification = null;
        OnPropertyChanged(nameof(HasChartReferences));
        NotifyDesignInspectorLayoutChanged();
        OnPropertyChanged(nameof(HasChartReferenceInspections));
        OnPropertyChanged(nameof(HasUninspectedChartReferences));
        OnPropertyChanged(nameof(HasSearchableChartReference));
        OnPropertyChanged(nameof(HasUnresolvedChartReferences));
        OnPropertyChanged(nameof(ChartReferenceSummary));
        OnPropertyChanged(nameof(ChartReferenceReadinessText));
        Status = "Reference removed from this request. The host-owned artifact remains available to other saved sessions.";
        Save();
    }

    private void RemoveChartReferenceInspections(string referenceId)
    {
        for (var index = ChartReferenceInspections.Count - 1; index >= 0; index--)
            if (string.Equals(
                    ChartReferenceInspections[index].ReferenceId,
                    referenceId,
                    StringComparison.Ordinal))
                ChartReferenceInspections.RemoveAt(index);
    }

    public bool HasCandidate => CurrentCandidate is not null;
    public bool HasGeneratedCandidates => GeneratedCandidateOptions.Count > 0;
    public bool HasCandidateContent => HasCandidate || HasGeneratedCandidates;
    public bool HasCandidateRestoreWarning => !string.IsNullOrWhiteSpace(CandidateRestoreWarning);
    public int SelectableGeneratedCandidateCount =>
        GeneratedCandidateOptions.Count(static option => option.Result.Selectable);
    public int BlockedGeneratedCandidateCount =>
        GeneratedCandidateOptions.Count(static option => !option.Result.Selectable);
    public bool HasBlockedGeneratedCandidates => BlockedGeneratedCandidateCount > 0;
    public StrategyGenerationCandidateOption? FirstBlockedGeneratedCandidateOption =>
        GeneratedCandidateOptions.FirstOrDefault(static option => !option.Result.Selectable);
    public string CandidateBatchHeadline
    {
        get
        {
            var selectable = SelectableGeneratedCandidateCount;
            var blocked = BlockedGeneratedCandidateCount;
            var selectableLabel = selectable == 1 ? "draft selectable" : "drafts selectable";
            if (blocked == 0) return $"{selectable} {selectableLabel}";
            var blockedLabel = blocked == 1 ? "lane blocked" : "lanes blocked";
            return $"{selectable} {selectableLabel} · {blocked} {blockedLabel}";
        }
    }
    public bool HasSelectedGeneratedCandidate => SelectedGeneratedCandidateOption?.Candidate is not null;
    public bool HasChosenGeneratedCandidate => ChosenGeneratedCandidateOption is not null;
    public bool HasPendingFourLanePrompt => !string.IsNullOrWhiteSpace(_pendingFourLanePrompt);
    public bool IsGeneratingCandidates => IsGenerating && GenerateCandidateFirst && !IsSynthesizingTradeIr;
    public bool HasRetainedCandidateBatchDuringGeneration => IsGeneratingCandidates && HasGeneratedCandidates;
    public bool CanChooseGeneratedCandidate =>
        CanEnterFourLaneConformance &&
        !HasPendingFourLanePrompt &&
        _parallelCandidateBatch is not null &&
        SelectedGeneratedCandidateOption is { Result.Selectable: true } selected &&
        GeneratedCandidateOptions.Contains(selected) &&
        _parallelCandidateBatch.Lanes.Any(lane => ReferenceEquals(lane, selected.Result)) &&
        !string.Equals(
            selected.CandidateHashSha256,
            ChosenGeneratedCandidateHash,
            StringComparison.Ordinal) &&
        !IsGenerating;
    public bool CanRevalidateGeneratedCandidate =>
        CanEnterFourLaneConformance &&
        !HasPendingFourLanePrompt &&
        _parallelCandidateBatch is not null &&
        _editorBaseGeneratedCandidateHash is not null &&
        _parallelCandidateBatch.Lanes.Count(lane => lane.Candidate is not null &&
            string.Equals(lane.CandidateHashSha256, _editorBaseGeneratedCandidateHash, StringComparison.Ordinal)) == 1 &&
        Files.Count == 1 &&
        !IsGenerating;
    public bool CanConfirmCandidate => CurrentCandidate is not null && CandidateContentHash is not null &&
        StrategyCandidateConfirmationV1.Confirm(CurrentCandidate, CandidateContentHash).Success;
    public string GenerationModeLabel => GenerateCandidateFirst ? "STRATEGY RESEARCH" : "EXPERT C#";
    public string GenerationModeActionText => GenerateCandidateFirst
        ? "Use Expert C#"
        : "Return to Strategy Builder";
    public string GenerationLaneText => GenerateCandidateFirst ? "Research, confirm, then implement" : "Expert code";
    public string SendButtonText => GenerateCandidateFirst ? "Send  ⌘↵" : "Generate code  ⌘↵";
    public string AuthoringBoundaryText => GenerateCandidateFirst
        ? "confirm meaning before implementation"
        : HasNonCSharpExpertArtifact
            ? "source review only · no importer/runtime"
            : "reviewed C# runs in-process";
    public bool HasExpertCSharpFiles =>
        !GenerateCandidateFirst &&
        Files.Count > 0 &&
        Files.All(static file => file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
    public bool HasNonCSharpExpertArtifact =>
        !GenerateCandidateFirst &&
        Files.Any(static file => !file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
    public string CandidateActionText => SelectedGeneratedCandidateOption is { CandidateHashSha256: { } selectedHash } &&
        string.Equals(selectedHash, ChosenGeneratedCandidateHash, StringComparison.Ordinal)
            ? "Using this candidate"
            : HasChosenGeneratedCandidate
                ? "Replace active candidate"
                : "Use selected in editor";
    public string ChosenGeneratedCandidateSummary => ChosenGeneratedCandidateOption is { } chosen
        ? $"Active in editor: {chosen.LaneName}"
        : "No active candidate in editor";
    public string GenerationProgressSummary
    {
        get
        {
            var finished = GenerationLaneProgressRows.Count(row =>
                row.State is StrategyGenerationLaneProgressStateV1.Completed or
                    StrategyGenerationLaneProgressStateV1.Failed or
                    StrategyGenerationLaneProgressStateV1.Canceled);
            return $"{finished}/4 lanes finished";
        }
    }
    private StrategyGenerationCandidateOption? ChosenGeneratedCandidateOption =>
        ChosenGeneratedCandidateHash is { } chosenHash
            ? GeneratedCandidateOptions.FirstOrDefault(option => string.Equals(
                option.CandidateHashSha256,
                chosenHash,
                StringComparison.Ordinal))
            : null;

    partial void OnGenerateCandidateFirstChanged(bool value)
    {
        InvalidateGenerationIfActive();
        if (ReviewOpen)
            CloseReview();
        if (value &&
            !string.IsNullOrWhiteSpace(_pendingFourLanePrompt) &&
            string.IsNullOrWhiteSpace(Composer))
        {
            Composer = _pendingFourLanePrompt;
        }
        if (value)
        {
            if (IsBuildScreen && !CanEnterFourLaneConformance)
                ActiveScreen = StrategyAuthoringScreen.Design;
        }
        else
        {
            // Expert C# is a separate legacy workspace. It does not require semantic confirmation,
            // and it keeps chat beside the code editor instead of entering the confirmed Build screen.
            ActiveScreen = StrategyAuthoringScreen.Design;
            HasDetachedImplementationSource = false;
            EditorOriginatedFromCombinedTradeIr = false;
        }
        OnPropertyChanged(nameof(IsGeneratingCandidates));
        OnPropertyChanged(nameof(GenerationModeLabel));
        OnPropertyChanged(nameof(GenerationModeActionText));
        OnPropertyChanged(nameof(GenerationLaneText));
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(AuthoringBoundaryText));
        OnPropertyChanged(nameof(HasExpertCSharpFiles));
        OnPropertyChanged(nameof(HasNonCSharpExpertArtifact));
        NotifyAuthoringScreenStateChanged();
        CompileCommand.NotifyCanExecuteChanged();
        RegenerateRecoveredCandidatesCommand.NotifyCanExecuteChanged();
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        AiStatus = value
            ? "Four AI agents generate editable strategy alternatives in parallel; available package validators are reported separately."
            : "Expert Code lane: chat writes and compiles C# directly.";
    }

    [RelayCommand]
    private void ToggleGenerationMode()
    {
        GenerateCandidateFirst = !GenerateCandidateFirst;
        WorkbenchTab = GenerateCandidateFirst ? 3 : 0;
        Status = GenerateCandidateFirst
            ? "Four AI candidate comparison is visible. Select a lane or generate a fresh batch."
            : HasNonCSharpExpertArtifact
                ? "Expert C# mode is open, but the current file is a source-review artifact and cannot be compiled here."
                : "Expert C# mode is open. Review C# before compiling and registering it.";
        Save();
    }

    partial void OnCurrentCandidateChanged(StrategyCandidateV1? value)
    {
        InvalidateStrategyIntentIfCandidateChanged(value);
        OnPropertyChanged(nameof(HasCandidate));
        OnPropertyChanged(nameof(HasCandidateContent));
        OnPropertyChanged(nameof(ShowDesignInspector));
        OnPropertyChanged(nameof(ShowWorkbenchPanel));
        OnPropertyChanged(nameof(DesignInspectorWidth));
        OnPropertyChanged(nameof(ShowDesignRequestHeader));
        NotifyWorkingFlowMapChanged();
        OnPropertyChanged(nameof(CanConfirmCandidate));
        ConfirmCandidateCommand.NotifyCanExecuteChanged();
        NotifyStrategyIntentStateChanged();
    }

    partial void OnCandidateContentHashChanged(string? value)
    {
        InvalidateResearchDatasetForBriefChange();
        OnPropertyChanged(nameof(CanConfirmCandidate));
        ConfirmCandidateCommand.NotifyCanExecuteChanged();
    }

    partial void OnCandidateRestoreWarningChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCandidateRestoreWarning));
        RegenerateRecoveredCandidatesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedGeneratedCandidateOptionChanged(StrategyGenerationCandidateOption? value)
    {
        RefreshGeneratedCandidateOptionFlags();
        OnPropertyChanged(nameof(HasSelectedGeneratedCandidate));
        OnPropertyChanged(nameof(CanChooseGeneratedCandidate));
        OnPropertyChanged(nameof(CanRevalidateGeneratedCandidate));
        OnPropertyChanged(nameof(CandidateActionText));
        OnPropertyChanged(nameof(CandidateBacktestAvailabilityText));
        ChooseGeneratedCandidateCommand.NotifyCanExecuteChanged();
        RevalidateGeneratedCandidateCommand.NotifyCanExecuteChanged();
    }

    partial void OnChosenGeneratedCandidateHashChanged(string? value)
    {
        RefreshGeneratedCandidateOptionFlags();
        OnPropertyChanged(nameof(HasChosenGeneratedCandidate));
        OnPropertyChanged(nameof(ChosenGeneratedCandidateSummary));
        OnPropertyChanged(nameof(CandidateActionText));
        OnPropertyChanged(nameof(CanChooseGeneratedCandidate));
        OnPropertyChanged(nameof(CanPrepareGeneratedCandidateForBacktest));
        OnPropertyChanged(nameof(BacktestActionText));
        OnPropertyChanged(nameof(BacktestReadinessTitle));
        OnPropertyChanged(nameof(BacktestReadinessText));
        OnPropertyChanged(nameof(BacktestReadinessStages));
        ChooseGeneratedCandidateCommand.NotifyCanExecuteChanged();
        NotifyTradeIrBacktestStateChanged(clearStaleResult: true);
    }

    partial void OnStrategyIdChanged(string value)
    {
        if (!_ready || _restoring) return;

        InvalidateGenerationContext();
        ResetSession(null);
        ClearCandidate();
        InvalidateDerivedArtifactState(markUnregistered: true);
        ResetStrategyWorkspace();
        AiStatus = "Strategy identity changed. Generate a fresh set of candidates for this id.";
        Status = "Strategy id changed; prior candidates and derived compile state were cleared.";
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (!_ready || _restoring || !ReviewOpen) return;

        CloseReview();
        Status = "Registration review expired because the strategy name changed. Compile and review the current Expert C# source again.";
    }

    [RelayCommand]
    private void FocusFile(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            SelectedFile = file;
            WorkbenchTab = 0;
        }
    }

    [ObservableProperty] private string _strategyId = "myStrategy";
    [ObservableProperty] private string _displayName = "My custom strategy";

    private const string DefaultStrategyId = "myStrategy";
    private const string DefaultDisplayName = "My custom strategy";

    /// <summary>True once this strategy has been registered (this session, or per the saved snapshot) —
    /// drives the DRAFT/REGISTERED chip and the rail's status line.</summary>
    [ObservableProperty] private bool _isRegistered;

    [ObservableProperty] private string? _status = "Describe the idea to check four strategy-generation lanes, or switch to Expert Code for direct C# authoring.";
    [ObservableProperty] private bool _compiledOk;

    /// <summary>Auto-generated editor for the compiled strategy's tunables, or null when it declares none
    /// / hasn't compiled yet.</summary>
    [ObservableProperty] private StrategyParametersViewModel? _parameters;

    /// <summary>Errors + warnings from the most recent compile, mapped to a UI-friendly shape.</summary>
    public ObservableCollection<StrategyDiagnostic> Diagnostics { get; }

    /// <summary>Selecting a diagnostic jumps the Code tab to the file it points at.</summary>
    [ObservableProperty] private StrategyDiagnostic? _selectedDiagnostic;

    partial void OnSelectedDiagnosticChanged(StrategyDiagnostic? value)
    {
        if (value is null || string.IsNullOrEmpty(value.File)) return;
        var file = Files.FirstOrDefault(f => f.Name.Equals(value.File, StringComparison.OrdinalIgnoreCase));
        if (file is not null) SelectedFile = file;
    }

    // ── Files ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strategy's source files — what the model wrote, or what the user typed.</summary>
    public ObservableCollection<AuthoredFile> Files { get; }

    [ObservableProperty] private AuthoredFile? _selectedFile;

    [RelayCommand]
    private void AddFile()
    {
        SetEditorBaseGeneratedCandidateHash(null);
        var name = UniqueFileName("Helpers.cs");
        var file = Track(new AuthoredFile(name, string.Empty));
        Files.Add(file);
        SelectedFile = file;
        _filesEditedByUser = true;
        NotifyEditorFileModeChanged();
        InvalidateEditorProofs("The file set changed; prior compile, registration, and candidate-hash proofs were cleared.");
    }

    [RelayCommand]
    private void RemoveFile(AuthoredFile? file)
    {
        if (file is null || Files.Count <= 1) return;
        SetEditorBaseGeneratedCandidateHash(null);
        file.PropertyChanged -= OnFileEdited;
        Files.Remove(file);
        SelectedFile = Files.FirstOrDefault();
        _filesEditedByUser = true;
        NotifyEditorFileModeChanged();
        InvalidateEditorProofs("The file set changed; prior compile, registration, and candidate-hash proofs were cleared.");
    }

    // ── Providers & models ──────────────────────────────────────────────────────────────────────────

    /// <summary>The codegen providers offered in the picker (available and not).</summary>
    public ObservableCollection<AiProviderChoice> AiProviders { get; }

    [ObservableProperty] private AiProviderChoice? _selectedAiProvider;

    /// <summary>Models offered for the selected provider — the curated shortlist plus whatever the
    /// provider itself reports. The picker is editable, so an unlisted model id can just be typed.</summary>
    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private bool _isRefreshingModels;

    /// <summary>How hard the model thinks before answering. "Provider default" sends no effort parameter
    /// at all, which is the only setting a model that predates the parameter will accept.</summary>
    public IReadOnlyList<CodegenEffort> Efforts { get; } =
        [CodegenEffort.Default, CodegenEffort.Low, CodegenEffort.Medium, CodegenEffort.High, CodegenEffort.XHigh, CodegenEffort.Max];

    [ObservableProperty] private CodegenEffort _selectedEffort = CodegenEffort.Default;

    /// <summary>False for a provider with no effort knob (Ollama, DeepSeek, the Codex CLI) — the picker
    /// disables rather than sending a parameter the provider would reject.</summary>
    public bool EffortSupported => SelectedAiProvider is { } choice && AiModelCatalog.SupportsEffort(choice.ProviderId);

    partial void OnSelectedAiProviderChanged(AiProviderChoice? value)
    {
        InvalidateGenerationIfActive();
        // A different provider is a different conversation — its context window holds none of this thread.
        ResetSession("Switched provider.");
        Models.Clear();
        OnPropertyChanged(nameof(EffortSupported));
        InspectChartReferencesCommand.NotifyCanExecuteChanged();
        RegenerateRecoveredCandidatesCommand.NotifyCanExecuteChanged();
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        GenerateCanonicalPaperStrategyCommand.NotifyCanExecuteChanged();
        if (value is null)
        {
            SyncModelChoice();
            NotifyTradeIrSynthesisStateChanged();
            return;
        }

        foreach (var model in _ai?.ModelsFor(value.ProviderId) ?? []) Models.Add(model);
        SelectedModel = Models.FirstOrDefault();
        SelectedEffort = value.Client.Effort;
        SyncModelChoice();
        NotifyTradeIrSynthesisStateChanged();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        InvalidateGenerationIfActive();
        ResetSession("Switched model.");
        Persist();
        SyncModelChoice();
        OnPropertyChanged(nameof(ModelPillText));
    }

    /// <summary>What the composer's model pill reads: the unified row's label, a hand-typed id, or the
    /// setup nudge when nothing is selectable yet.</summary>
    public string ModelPillText =>
        SelectedModelChoice?.Display
        ?? (string.IsNullOrEmpty(SelectedModel)
            ? (SelectedAiProvider?.DisplayName ?? "choose a model")
            : SelectedModel!);

    partial void OnSelectedEffortChanged(CodegenEffort value)
    {
        InvalidateGenerationIfActive();
        // Effort changes how the model reasons, so the thread it produced is no longer representative.
        ResetSession("Switched effort.");
        Persist();
    }

    private void Persist()
    {
        if (_ready && SelectedAiProvider is { } choice)
            PersistSelection(choice.ProviderId, SelectedModel, SelectedEffort);
    }

    // ── Unified model picker ────────────────────────────────────────────────────────────────────────

    /// <summary>Every provider × its known models, flattened into one list ("claude-opus-4-8 · Claude
    /// Code (installed CLI)") — a single dropdown over the provider/model machinery underneath.
    /// Unavailable providers' rows are included, tagged via <see cref="AiModelChoice.IsAvailable"/>.</summary>
    public ObservableCollection<AiModelChoice> AllModels { get; }

    /// <summary>The unified picker's selection. Setting it drives <see cref="SelectedAiProvider"/> +
    /// <see cref="SelectedModel"/>; changing those (the classic pickers, a restore) points it back at
    /// the matching row, or null for a hand-typed model id with no row.</summary>
    [ObservableProperty] private AiModelChoice? _selectedModelChoice;

    /// <summary>Guards the two-way sync between the unified picker and the provider/model pair, so
    /// neither setter can re-trigger the other.</summary>
    private bool _syncingModelChoice;

    partial void OnSelectedModelChoiceChanged(AiModelChoice? value)
    {
        if (_syncingModelChoice || value is null) return;

        _syncingModelChoice = true;
        try
        {
            if (SelectedAiProvider?.ProviderId != value.ProviderId &&
                AiProviders.FirstOrDefault(p => p.ProviderId == value.ProviderId) is { } provider)
            {
                SelectedAiProvider = provider;   // repopulates Models and re-seeds SelectedModel/effort
            }

            if (value.ModelId.Length == 0)
            {
                // The "vendor default" row (a CLI with no pinned model): whatever the provider offers.
                SelectedModel = Models.FirstOrDefault();
            }
            else
            {
                if (!Models.Contains(value.ModelId, StringComparer.OrdinalIgnoreCase))
                    Models.Insert(0, value.ModelId);
                SelectedModel = value.ModelId;
            }
        }
        finally
        {
            _syncingModelChoice = false;
        }
    }

    /// <summary>The reverse sync: after the provider/model pair moves (classic pickers, restore, model
    /// refresh), point the unified picker at the row that matches — or null when none does.</summary>
    private void SyncModelChoice()
    {
        if (_syncingModelChoice) return;

        _syncingModelChoice = true;
        try
        {
            SelectedModelChoice = AllModels.FirstOrDefault(c =>
                c.ProviderId == SelectedAiProvider?.ProviderId &&
                (string.IsNullOrEmpty(SelectedModel)
                    ? c.ModelId.Length == 0
                    : c.ModelId.Equals(SelectedModel, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _syncingModelChoice = false;
        }

        OnPropertyChanged(nameof(ModelPillText));
    }

    // ── Build effort (the pipeline dial — separate from the model's reasoning effort) ───────────────

    /// <summary>The four pipeline efforts, for the picker.</summary>
    public IReadOnlyList<StrategyBuildEffort> BuildEfforts { get; } =
        [StrategyBuildEffort.Quick, StrategyBuildEffort.Standard, StrategyBuildEffort.Deep, StrategyBuildEffort.Max];

    /// <summary>How hard the BUILD works — skill budget, auto-fix retries, and whether the self-review /
    /// backtest-smoke passes run (<see cref="StrategyBuildProfile.For"/>). Orthogonal to
    /// <see cref="SelectedEffort"/>, which is how hard the model thinks inside one generation.</summary>
    [ObservableProperty] private StrategyBuildEffort _buildEffort = StrategyBuildEffort.Standard;

    partial void OnBuildEffortChanged(StrategyBuildEffort value)
    {
        InvalidateGenerationIfActive();
        // The profile is fixed at session creation (its skill budget shapes the cached system prompt),
        // so a new effort needs a new session — the same rule as switching the model's own effort.
        ResetSession("Switched build effort.");
        Persist();
    }

    // ── Agent CLI hand-off ──────────────────────────────────────────────────────────────────────────

    /// <summary>The installed agent CLIs the workspace launcher can open. Empty when none are on PATH,
    /// or when the launcher isn't wired — either way the UI hides the hand-off.</summary>
    public IReadOnlyList<AgentCliAdapter> AvailableClis => _cliLauncher?.AvailableClis() ?? [];

    /// <summary>Scaffolds this strategy's Vibe Quant workspace (context pack, skills, starter project)
    /// and opens the CLI there in a real terminal — interactive, never headless.</summary>
    [RelayCommand]
    private void LaunchCli(AgentCliAdapter? adapter)
    {
        if (_cliLauncher is null || adapter is null) return;
        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            Status = "Give the strategy an id first — it names the workspace folder.";
            return;
        }

        try
        {
            var result = _cliLauncher.Launch(adapter, StrategyId.Trim(), DisplayName.Trim(), BuildEffort);
            Status = result.Message;
            _logger.LogInformation(
                "CLI workspace launch for {Id} via {Cli}: success={Success} at {Path}",
                StrategyId, adapter.DisplayName, result.Success, result.WorkspacePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI workspace launch threw for {Id}", StrategyId);
            Status = $"Couldn't launch {adapter.DisplayName}: {ex.Message}";
        }
    }

    /// <summary>Ask the provider what models this key/endpoint can actually call (OpenAI, Anthropic and
    /// Ollama all expose a models endpoint). Falls back silently to the curated list.</summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (_ai is null || SelectedAiProvider is not { } choice || IsRefreshingModels) return;

        IsRefreshingModels = true;
        try
        {
            var client = ResolveClient(choice) ?? choice.Client;
            var live = await client.ListModelsAsync(CancellationToken.None);
            if (live.Count == 0)
            {
                AiStatus = $"{choice.DisplayName} didn't return a model list — type the model id instead.";
                return;
            }

            var previous = SelectedModel;
            Models.Clear();
            foreach (var model in live) Models.Add(model);
            SelectedModel = live.Contains(previous ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                ? previous
                : live[0];
            AiStatus = $"{live.Count} model(s) available from {choice.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing models failed for {Provider}", choice.ProviderId);
            AiStatus = $"Couldn't list models: {ex.Message}";
        }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    // ── Chat ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The conversation the user reads — their turns, and the model's replies verbatim.</summary>
    public ObservableCollection<AuthoringMessage> Messages { get; }

    /// <summary>
    /// Raised when chat resolves famous-indicator overlays from the host catalog so the shell can
    /// open the research chart with those same native toggles enabled.
    /// </summary>
    public event EventHandler<HostChartOverlayPreviewRequestedEventArgs>? HostChartOverlayPreviewRequested;

    /// <summary>
    /// Raised when Research Studio wants the shell to open an existing market-structure window
    /// (order book, footprint, bookmap) for the selected research instrument.
    /// </summary>
    public event EventHandler<HostResearchMarketStructureRequestedEventArgs>? HostResearchMarketStructureRequested;

    /// <summary>What the builder is doing right now ("Asking Claude…", "Compiling 3 file(s)…") — the
    /// live feedback that a long generation is actually progressing.</summary>
    public ObservableCollection<string> Activity { get; }

    /// <summary>
    /// The turn's pipeline as a structured checklist (Understand brief → Load skills → Generate →
    /// Compile → Auto-fix → Self-review → Backtest smoke, the last two only at Deep/Max build effort) —
    /// the right panel's "Tasks" row. Re-seeded at the start of every Send turn and advanced from the
    /// same activity stream that feeds <see cref="Activity"/>; bounded by construction (one row per
    /// step, at most seven).
    /// </summary>
    public ObservableCollection<BuildTask> Tasks { get; }

    private BuildTask? _taskBrief, _taskSkills, _taskGenerate, _taskCompile, _taskAutoFix, _taskReview, _taskSmoke;

    /// <summary>One-shot guards so a repeated activity string can't append the same tool card twice
    /// within a turn. Reset by <see cref="SeedTasks"/>.</summary>
    private bool _reviewCardEmitted, _smokeCardEmitted;

    /// <summary>Fresh checklist for a new turn — the optional passes appear only when the profile buys them.</summary>
    private void SeedTasks(StrategyBuildProfile profile)
    {
        Tasks.Clear();
        Tasks.Add(_taskBrief = new BuildTask("Understand brief"));
        Tasks.Add(_taskSkills = new BuildTask("Load skills"));
        Tasks.Add(_taskGenerate = new BuildTask("Generate"));
        Tasks.Add(_taskCompile = new BuildTask("Compile"));
        Tasks.Add(_taskAutoFix = new BuildTask("Auto-fix"));
        _taskReview = profile.SelfReview ? new BuildTask("Self-review") : null;
        if (_taskReview is not null) Tasks.Add(_taskReview);
        _taskSmoke = profile.BacktestSmoke ? new BuildTask("Backtest smoke") : null;
        if (_taskSmoke is not null) Tasks.Add(_taskSmoke);

        _taskBrief!.State = BuildTaskState.Running;
        _reviewCardEmitted = _smokeCardEmitted = false;
        RefreshWorkStatus();
    }

    /// <summary>Maps the session's activity strings onto the checklist. Prefix matching against the
    /// strings <see cref="StrategyBuildSession"/> reports — cosmetic by design: an unrecognized step
    /// just doesn't advance the strip, it never breaks a turn.</summary>
    private void AdvanceTasks(string step)
    {
        if (step.StartsWith("Loaded reference", StringComparison.Ordinal))
        {
            Done(_taskBrief);
            Done(_taskSkills);
        }
        else if (step.StartsWith("Asking", StringComparison.Ordinal))
        {
            Done(_taskBrief);
            Done(_taskSkills);
            if (step.Contains("to fix", StringComparison.Ordinal)) Run(_taskAutoFix);
            Run(_taskGenerate);
        }
        else if (step.StartsWith("Compiling", StringComparison.Ordinal))
        {
            Done(_taskGenerate);
            Run(_taskCompile);
        }
        else if (step.StartsWith("Compiled", StringComparison.Ordinal))
        {
            Done(_taskCompile);
            Done(_taskAutoFix);   // ran and won, or was never needed — either way it isn't outstanding
        }
        else if (step.StartsWith("Self-review", StringComparison.Ordinal) ||
                 step.StartsWith("The self-review", StringComparison.Ordinal))
        {
            if (step.StartsWith("Self-review pass", StringComparison.Ordinal))
            {
                Run(_taskReview);
            }
            else
            {
                Done(_taskReview);
                if (!_reviewCardEmitted)
                {
                    _reviewCardEmitted = true;
                    Append(AuthoringMessage.Tool("Ok", "Self-review", step));
                }
            }
        }
        else if (step.StartsWith("Backtest smoke", StringComparison.Ordinal))
        {
            if (step.Contains("passed", StringComparison.Ordinal))
            {
                Done(_taskSmoke);
                EmitSmokeCard("Ok", step);
            }
            else if (step.Contains("failed", StringComparison.Ordinal))
            {
                Fail(_taskSmoke);
                EmitSmokeCard("Fail", step);
            }
            else
            {
                Run(_taskSmoke);
            }
        }
        else if (step.StartsWith("Still", StringComparison.Ordinal))
        {
            Fail(_taskCompile);
            Fail(_taskAutoFix);
        }
        else if (step.Contains("has a question", StringComparison.Ordinal))
        {
            Done(_taskGenerate);
        }
        else if (step.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            Fail(_taskGenerate);
        }

        RefreshWorkStatus();
    }

    private void EmitSmokeCard(string state, string step)
    {
        if (_smokeCardEmitted) return;
        _smokeCardEmitted = true;
        Append(AuthoringMessage.Tool(state, "Backtest smoke", step));
    }

    /// <summary>Settles the checklist when the turn ends: a compiled turn closes everything that didn't
    /// fail; a question leaves the not-yet-applicable steps pending; anything running on a failure is
    /// marked failed.</summary>
    private void FinishTasks(BuildTurnKind kind)
    {
        var success = kind is BuildTurnKind.Compiled or BuildTurnKind.Question;
        foreach (var task in Tasks)
        {
            if (task.State == BuildTaskState.Running)
                task.State = success ? BuildTaskState.Done : BuildTaskState.Failed;
            else if (kind == BuildTurnKind.Compiled && task.State == BuildTaskState.Pending)
                task.State = BuildTaskState.Done;
        }

        RefreshWorkStatus();
    }

    /// <summary>A stopped/crashed turn: whatever was in flight didn't finish.</summary>
    private void FailRunningTasks()
    {
        foreach (var task in Tasks)
            if (task.State == BuildTaskState.Running) task.State = BuildTaskState.Failed;

        RefreshWorkStatus();
    }

    private static void Run(BuildTask? task)
    {
        if (task is not null && task.State != BuildTaskState.Failed) task.State = BuildTaskState.Running;
    }

    private static void Done(BuildTask? task)
    {
        if (task is not null && task.State != BuildTaskState.Failed) task.State = BuildTaskState.Done;
    }

    private static void Fail(BuildTask? task)
    {
        if (task is not null) task.State = BuildTaskState.Failed;
    }

    /// <summary>The chat composer. Multi-line: Enter adds a newline, Ctrl+Enter sends.</summary>
    [ObservableProperty] private string _composer = string.Empty;

    [ObservableProperty] private string? _aiStatus;
    [ObservableProperty] private bool _isGenerating;

    /// <summary>"1m 20s elapsed…" while a turn runs. A detailed brief at a high effort is a multi-minute
    /// request; without a clock ticking, a working generation is indistinguishable from a hang.</summary>
    [ObservableProperty] private string? _elapsedText;

    /// <summary>"2:41" — the session header's compact clock while a turn runs.</summary>
    [ObservableProperty] private string? _elapsedCompact;

    /// <summary>The shimmering status verb ("Writing the strategy…") — the current pipeline step,
    /// phrased as what the agent is doing rather than as a checklist label.</summary>
    [ObservableProperty] private string? _workingVerb;

    /// <summary>"step 3 of 6" next to the verb.</summary>
    [ObservableProperty] private string? _stepText;

    /// <summary>Re-derives the verb + step counter from the checklist. Called whenever a task state
    /// moves; null when nothing is running (which stops the shimmer).</summary>
    private void RefreshWorkStatus()
    {
        var running = Tasks.FirstOrDefault(t => t.State == BuildTaskState.Running);
        if (running is null)
        {
            WorkingVerb = null;
            StepText = null;
            return;
        }

        StepText = $"step {Tasks.IndexOf(running) + 1} of {Tasks.Count}";
        WorkingVerb = running.Title switch
        {
            "Understand brief" => "Reading the brief…",
            "Load skills" => "Loading skills…",
            "Generate" => "Writing the strategy…",
            "Compile" => "Compiling…",
            "Auto-fix" => "Fixing compile errors…",
            "Self-review" => "Self-reviewing the code…",
            "Backtest smoke" => "Running the backtest smoke…",
            _ => running.Title + "…",
        };
    }

    /// <summary>The model asked a question instead of writing code, and is waiting for the answer. It is
    /// a normal turn — the strategy is under-specified and it wants to know, rather than guess.</summary>
    [ObservableProperty] private bool _awaitingAnswer;

    /// <summary>The assistant bubble currently being streamed into, or null between turns.</summary>
    private AuthoringMessage? _streamingReply;

    [ObservableProperty] private int _inputTokens;
    [ObservableProperty] private int _outputTokens;
    [ObservableProperty] private int _cachedTokens;

    /// <summary>Tokens billed this session. The cached share is called out because it is the difference
    /// between a long conversation costing a little and costing a lot — and because a session where it
    /// stays at zero is one paying full price to re-read the same context every turn.</summary>
    public string UsageText => InputTokens + OutputTokens == 0
        ? "tokens: not reported"
        : CachedTokens > 0
            ? $"tokens: {InputTokens:N0} in ({CachedTokens:N0} cached) · {OutputTokens:N0} out"
            : $"tokens: {InputTokens:N0} in · {OutputTokens:N0} out";

    partial void OnInputTokensChanged(int value) => OnPropertyChanged(nameof(UsageText));
    partial void OnOutputTokensChanged(int value) => OnPropertyChanged(nameof(UsageText));
    partial void OnCachedTokensChanged(int value) => OnPropertyChanged(nameof(UsageText));

    partial void OnIsGeneratingChanged(bool value)
    {
        if (!value && IsSynthesizingTradeIr) IsSynthesizingTradeIr = false;
        InspectChartReferencesCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        RegenerateRecoveredCandidatesCommand.NotifyCanExecuteChanged();
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        DiscardPendingFourLanePromptCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ConfirmCandidateCommand.NotifyCanExecuteChanged();
        NotifyStrategyIntentStateChanged();
        OnPropertyChanged(nameof(IsGeneratingCandidates));
        OnPropertyChanged(nameof(HasRetainedCandidateBatchDuringGeneration));
        OnPropertyChanged(nameof(CanCompileCurrentSource));
        OnPropertyChanged(nameof(CanExportOpenPackage));
        NotifyWorkingFlowMapChanged();
        CompileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChooseGeneratedCandidate));
        OnPropertyChanged(nameof(CanRevalidateGeneratedCandidate));
        ChooseGeneratedCandidateCommand.NotifyCanExecuteChanged();
        RevalidateGeneratedCandidateCommand.NotifyCanExecuteChanged();
        NotifyTradeIrSynthesisStateChanged();
        NotifyTradeIrBacktestStateChanged();
    }

    partial void OnComposerChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        RegenerateRecoveredCandidatesCommand.NotifyCanExecuteChanged();
        // Follow-ups stay turn-scoped — do not invent chips while typing.
    }

    private bool CanSend => !IsGenerating && !IsInspectingChartReferences && !IsSearchingChartPatterns &&
        !string.IsNullOrWhiteSpace(Composer);

    private bool CanRegenerateRecoveredCandidatesAction() =>
        HasCandidateRestoreWarning &&
        GenerateCandidateFirst &&
        !IsGenerating &&
        !string.IsNullOrWhiteSpace(Composer) &&
        _candidateGenerator is not null &&
        SelectedAiProvider is { IsAvailable: true };

    private bool CanRegenerateFourCandidatesAction() =>
        CanEnterFourLaneConformance &&
        GenerateCandidateFirst &&
        !HasUnresolvedChartReferences &&
        !HasPendingFourLanePrompt &&
        !IsGenerating &&
        !string.IsNullOrWhiteSpace(StrategyId) &&
        !string.IsNullOrWhiteSpace(_fourLaneStrategyBrief) &&
        _ai is not null &&
        _parallelCandidateGenerator is not null &&
        SelectedAiProvider is { IsAvailable: true };

    public bool CanGenerateFourCandidates =>
        CanEnterFourLaneConformance &&
        ResearchEvidenceReadyForGeneration &&
        GenerateCandidateFirst &&
        !HasUnresolvedChartReferences &&
        (!HasPendingFourLanePrompt || !HasGeneratedCandidates) &&
        !IsGenerating &&
        !string.IsNullOrWhiteSpace(StrategyId) &&
        _ai is not null &&
        _parallelCandidateGenerator is not null &&
        SelectedAiProvider is { IsAvailable: true };

    /// <summary>
    /// Starts implementation only from the locally confirmed strategy request. Chat never reaches
    /// this path implicitly, and the host rechecks the confirmation after the provider returns.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateFourCandidatesAction))]
    private Task GenerateFourCandidatesAsync()
    {
        var choice = SelectedAiProvider;
        return !CanGenerateFourCandidates || choice is null
            ? Task.CompletedTask
            : SendParallelCandidateTurnAsync(
                choice,
                BuildConfirmedStrategyImplementationBrief(),
                "Generate implementations from the confirmed strategy request.");
    }

    private bool CanGenerateFourCandidatesAction() => CanGenerateFourCandidates;

    /// <summary>
    /// Replays the recovered brief only after an explicit user click. Restore itself never starts a
    /// provider request; this command re-enters semantic review and cannot start implementation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateRecoveredCandidatesAction))]
    private Task RegenerateRecoveredCandidatesAsync() => SendAsync();

    /// <summary>
    /// Explicitly reruns the four generation agents against the unchanged durable strategy brief.
    /// This is separate from the composer refinement path and never starts a test or backtest.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateFourCandidatesAction))]
    private Task RegenerateFourCandidatesAsync()
    {
        var choice = SelectedAiProvider;
        return !CanRegenerateFourCandidatesAction() ||
               choice is null ||
               string.IsNullOrWhiteSpace(StrategyId) ||
               string.IsNullOrWhiteSpace(_fourLaneStrategyBrief)
            ? Task.CompletedTask
            : SendParallelCandidateTurnAsync(
                choice,
                _fourLaneStrategyBrief,
                "Regenerate four candidates from the preserved strategy brief.");
    }

    /// <summary>
    /// One turn: send what the user typed (plus their hand-edits, if any), let the session generate →
    /// compile → auto-fix, and land the result in the chat, the file list and the diagnostics. It does
    /// NOT register — the user reviews the code and presses Compile &amp; Register, which is the consent for
    /// running model-authored code (it's already scan-gated, so a strategy that P/Invokes never compiles).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            AiStatus = "Give the strategy an id first.";
            return;
        }

        var prompt = Composer.Trim();
        if (prompt.Length == 0) return;

        ClearTurnFollowUps();

        // Backtesting is a separate, explicit action. A short navigation request must never become
        // the next four-lane strategy prompt and silently replace the user's actual strategy brief.
        if (GenerateCandidateFirst && IsBacktestNavigationIntent(prompt))
        {
            RouteBacktestNavigationIntent(prompt);
            return;
        }

        // Host catalog (famous indicators / research scans) must work offline — no model call.
        if (GenerateCandidateFirst &&
            CurrentCandidate is null &&
            TryRouteHostCatalogWithoutProvider(prompt))
        {
            return;
        }

        if (_ai is null || SelectedAiProvider is not { } choice) return;
        if (!choice.IsAvailable)
        {
            AiStatus = $"{choice.DisplayName} isn't set up — install it, or add an API key in Settings → AI providers.";
            return;
        }

        // First brief on an untouched identity: name the strategy after what it does, not "myStrategy".
        if (Messages.Count == 0) DeriveIdentityFrom(prompt);

        if (GenerateCandidateFirst)
        {
            if (_candidateGenerator is null)
            {
                AiStatus = "The strategy-meaning agent is not registered. Restart after updating the app or use Expert Code.";
                return;
            }

            // The first turn chooses the product lane. A display-only request freezes a Visualizer
            // specification; an exposure-changing request continues into the established strategy
            // meaning/research confirmation flow. Revisions stay in their already chosen lane.
            if (CurrentCandidate is null &&
                _authoredUnitIntentClassifier is not null &&
                _authoredUnitSpecificationGenerator is not null)
            {
                await ClassifyAndRouteInitialRequestAsync(choice, prompt);
            }
            else
            {
                await SendCandidateTurnAsync(choice, prompt);
            }
            return;
        }

        var turnStrategyId = StrategyId.Trim();
        var turnEpoch = Interlocked.Increment(ref _generationContextEpoch);
        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        InvalidateDerivedArtifactState(markUnregistered: true);
        ChosenGeneratedCandidateHash = null;
        IsGenerating = true;

        // The pipeline's dial for this turn — and the checklist the right panel watches.
        var profile = StrategyBuildProfile.For(BuildEffort);
        SeedTasks(profile);

        // The turn's plan, pinned into the transcript. It snapshots THIS turn's task instances, so an
        // older card keeps its final states when the next turn re-seeds the checklist.
        Append(AuthoringMessage.Plan([.. Tasks]));

        _generateCts?.Cancel();
        _generateCts?.Dispose();
        var turnCts = new CancellationTokenSource();
        _generateCts = turnCts;

        var ticking = TickElapsedAsync(turnCts.Token);
        var session = EnsureSession(choice, profile);
        var tokensBefore = session.TotalUsage;
        _streamingReply = null;

        // The editor is the truth: hand-edits and all. The session ships exactly one copy of it with the
        // turn, so the model always works from the code that is actually there.
        session.SyncEditedFiles([.. Files.Select(f => new StrategyFile(f.Name, f.Content))]);
        _filesEditedByUser = false;

        try
        {
            var turn = await session.SendAsync(
                prompt,
                new Progress<string>(step =>
                {
                    if (IsGenerationContextCurrent(turnEpoch, turnStrategyId)) PushActivity(step);
                }),
                turnCts.Token,
                new Progress<CodegenEvent>(evt =>
                {
                    if (IsGenerationContextCurrent(turnEpoch, turnStrategyId)) OnStreamed(evt, tokensBefore);
                }));
            if (!IsGenerationContextCurrent(turnEpoch, turnStrategyId)) return;

            // The session's running total is authoritative: a turn can be several generations (the
            // auto-fix retries), and the streamed updates are per-generation.
            InputTokens = session.TotalUsage.InputTokens;
            OutputTokens = session.TotalUsage.OutputTokens;
            CachedTokens = session.TotalUsage.CachedInputTokens;

            FinishTasks(turn.Kind);

            if (turn.Kind == BuildTurnKind.ProviderError)
            {
                AiStatus = $"{choice.DisplayName} failed: {turn.Error}";
                Append(AuthoringMessage.Tool("Fail", $"{choice.DisplayName} failed", turn.Error ?? "The provider returned an error."));
                return;
            }

            // The reply was streamed into a bubble as it arrived; settle it on the final text (the
            // provider's own assembled version). Nothing streamed ⇒ the provider doesn't stream, so the
            // bubble appears now, whole.
            if (_streamingReply is null) Append(new AuthoringMessage(CodegenRole.Assistant, turn.AssistantText));
            else _streamingReply.Text = turn.AssistantText;

            AwaitingAnswer = turn.Kind == BuildTurnKind.Question;

            if (turn.Files.Count > 0)
            {
                var prior = Files.ToDictionary(f => f.Name, f => f.Content, StringComparer.OrdinalIgnoreCase);
                HasDetachedImplementationSource = false;
                SetFiles(turn.Files);
                _filesEditedByUser = false;
                AppendFileChanges(prior, turn.Files);
            }

            foreach (var diagnostic in turn.Compile?.Diagnostics ?? [])
                Diagnostics.Add(diagnostic);

            // The turn's compile verdict as a card — the numbers the user actually wants at a glance.
            if (turn.Kind == BuildTurnKind.Compiled)
            {
                var warnings = turn.Compile?.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning) ?? 0;
                Append(AuthoringMessage.Tool(
                    "Ok", "Compiled",
                    $"{turn.Files.Count} file(s) · {turn.Generations} generation(s)" +
                    (warnings > 0 ? $" · {warnings} warning(s)" : string.Empty)));
            }
            else if (turn.Kind != BuildTurnKind.Question)
            {
                Append(AuthoringMessage.Tool(
                    "Fail", "Compile failed",
                    $"{turn.Compile?.Errors.Count() ?? 0} error(s) after {turn.Generations} generation(s) — see Diagnostics"));
            }

            AiStatus = turn.Kind switch
            {
                BuildTurnKind.Question =>
                    "The model asked you something — answer in the chat.",
                BuildTurnKind.Compiled =>
                    $"Wrote {turn.Files.Count} file(s) and compiled cleanly in {turn.Generations} generation(s). " +
                    "Review the Code tab, then press Compile & Register.",
                _ =>
                    $"Still {turn.Compile?.Errors.Count() ?? 0} error(s) after {turn.Generations} generation(s) — " +
                    "they're in the Diagnostics list. Ask for a fix, or edit the code yourself.",
            };

            _logger.LogInformation(
                "AI builder turn for {Id} via {Provider}/{Model}: {Kind}, {Files} file(s), {Generations} generation(s)",
                turnStrategyId, choice.ProviderId, SelectedModel ?? "(default)", turn.Kind, turn.Files.Count, turn.Generations);
        }
        catch (OperationCanceledException)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                AiStatus = "Stopped.";
                PushActivity("Stopped by the user.");
                FailRunningTasks();
            }
        }
        catch (Exception ex)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                _logger.LogError(ex, "AI builder turn threw for {Id}", turnStrategyId);
                AiStatus = $"Generation error: {ex.Message}";
                Append(AuthoringMessage.System(AiStatus));
                FailRunningTasks();
            }
        }
        finally
        {
            turnCts.Cancel();
            await ticking;
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                IsGenerating = false;
                _streamingReply = null;
                ElapsedText = null;
                ElapsedCompact = null;
                Save();   // a turn is expensive — never lose one to a crash or a restart
            }
            if (ReferenceEquals(_generateCts, turnCts)) _generateCts = null;
            turnCts.Dispose();
        }
    }

    /// <summary>
    /// Four-way strategy-generation lane. Every agent generates its native editable representation;
    /// deterministic format checks and any available package validator are reported as separate facts.
    /// Results stop at the package handoff boundary and are never tested or executed here.
    /// </summary>
    private async Task SendParallelCandidateTurnAsync(
        AiProviderChoice choice,
        string prompt,
        string? displayedPrompt = null)
    {
        if (!CanEnterFourLaneConformance ||
            ConfirmedStrategyIntent is not { } boundIntent ||
            ConfirmedStrategyIntentHash is not { Length: 64 } boundIntentHash ||
            CurrentCandidate is not { } boundCandidate ||
            _strategyIntentResearchCase is not { } boundResearchCase ||
            _strategyIntentClassification is not { } boundClassification ||
            _parallelCandidateGenerator is null)
        {
            AiStatus = "Confirm the complete strategy request before generating implementations.";
            return;
        }

        var boundIntentCanonicalJson = StrategyIntentCanonicalJsonV1.Serialize(boundIntent);
        var boundIntentContext = new StrategyGenerationConfirmedIntentContextV1(
            StrategyCandidateCanonicalJsonV1.Serialize(boundCandidate),
            ResearchCaseCanonicalJsonV1.Serialize(boundResearchCase),
            StrategySpecCanonicalJsonV1.Serialize(boundClassification),
            ResearchExperimentEvidence is null
                ? null
                : ResearchExperimentCanonicalJsonV1.Serialize(ResearchExperimentEvidence));
        if (!string.Equals(
                StrategyIntentCanonicalJsonV1.Hash(boundIntent),
                boundIntentHash,
                StringComparison.Ordinal))
        {
            AiStatus = "The confirmed strategy request failed its local integrity check. Review and confirm it again.";
            return;
        }

        var turnStrategyId = StrategyId.Trim();
        var turnEpoch = Interlocked.Increment(ref _generationContextEpoch);
        var strategyBrief = BuildFourLaneStrategyBrief(prompt);
        SetPendingFourLanePrompt(string.Equals(
            prompt,
            _fourLaneStrategyBrief,
            StringComparison.Ordinal)
                ? null
                : prompt);
        // Keep the last completed, hash-validated batch until the replacement fully validates and
        // commits. A cancelled provider call or app shutdown must not erase expensive prior output.
        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, displayedPrompt ?? prompt));
        Activity.Clear();
        Diagnostics.Clear();
        CompiledOk = false;
        AwaitingAnswer = false;
        WorkbenchTab = 3;
        Tasks.Clear();
        ResetGenerationLaneProgress();
        WorkingVerb = "Generating four alternatives…";
        StepText = GenerationProgressSummary;
        IsGenerating = true;
        AiStatus = HasGeneratedCandidates
            ? "Generating a replacement batch. The last completed candidates remain preserved until all replacement results validate."
            : "Asking four AI agents to generate editable strategy alternatives…";
        Save();

        _generateCts?.Cancel();
        _generateCts?.Dispose();
        var turnCts = new CancellationTokenSource();
        _generateCts = turnCts;
        var ticking = TickElapsedAsync(turnCts.Token);
        var replacementCommitted = false;

        try
        {
            var provider = ResolveClient(choice) ?? choice.Client;
            var progress = new Progress<StrategyGenerationLaneProgressV1>(laneProgress =>
            {
                if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
                    ApplyGenerationLaneProgress(laneProgress);
            });
            var result = await _parallelCandidateGenerator!.GenerateAsync(
                provider,
                new ParallelStrategyGenerationRequestV1(
                    turnStrategyId,
                    strategyBrief,
                    boundIntentCanonicalJson,
                    boundIntentHash,
                    boundIntentContext),
                turnCts.Token,
                progress);
            if (!IsGenerationContextCurrent(turnEpoch, turnStrategyId)) return;
            if (!CanEnterFourLaneConformance ||
                !string.Equals(boundIntentHash, ConfirmedStrategyIntentHash, StringComparison.Ordinal))
            {
                AiStatus = "The confirmed strategy request changed while implementations were being generated. The returned batch was discarded.";
                return;
            }
            if (!string.Equals(
                    result.ConfirmedIntentCanonicalJson,
                    boundIntentCanonicalJson,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    result.ConfirmedIntentHashSha256,
                    boundIntentHash,
                    StringComparison.Ordinal))
            {
                AiStatus = "The implementation batch did not bind the exact confirmed strategy request and was discarded.";
                return;
            }
            InputTokens += result.Usage.InputTokens;
            OutputTokens += result.Usage.OutputTokens;
            CachedTokens += result.Usage.CachedInputTokens;

            ApplyParallelCandidateBatch(result);
            _fourLaneStrategyBrief = strategyBrief;
            SetPendingFourLanePrompt(null);
            RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
            replacementCommitted = true;
            SetFinalGenerationLaneProgress(result);
            Append(new AuthoringMessage(CodegenRole.Assistant, FormatParallelCandidates(result)));

            var selectable = result.Lanes.Count(static lane => lane.Selectable);
            var packageValid = result.Lanes.Count(static lane => lane.PackageValid);
            var generatedOnly = result.Lanes.Count(static lane =>
                lane.Readiness == StrategyGenerationReadinessV1.Generated && lane.Selectable);
            var blocked = result.Lanes.Count - selectable;
            Append(AuthoringMessage.Tool(
                selectable > 0 ? "Ok" : "Fail",
                $"Four AI generation lanes · {selectable} selectable",
                $"{packageValid} package-valid; {generatedOnly} generated without a package validator; {blocked} blocked. No tests or execution ran."));

            AiStatus = selectable > 0
                ? $"{selectable} generated strategy artifact(s) are ready to choose: {packageValid} package-valid and {generatedOnly} not package-validated. None were tested or run."
                : "All four generated alternatives were invalid or failed. Inspect each lane's blocking reason.";
            WorkbenchTab = 3;
            _logger.LogInformation(
                "Parallel strategy generation for {Id}: selectable={Selectable}, packageValid={PackageValid}, blocked={Blocked}",
                StrategyId,
                selectable,
                packageValid,
                blocked);
        }
        catch (OperationCanceledException)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId)) AiStatus = "Stopped.";
        }
        catch (Exception ex)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                _logger.LogError(ex, "Parallel strategy generation threw for {Id}", turnStrategyId);
                AiStatus = $"Candidate generation error: {ex.Message}";
                Append(AuthoringMessage.System(AiStatus));
            }
        }
        finally
        {
            turnCts.Cancel();
            await ticking;
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                if (!replacementCommitted && string.IsNullOrWhiteSpace(Composer))
                    Composer = _pendingFourLanePrompt ?? prompt;
                IsGenerating = false;
                Tasks.Clear();
                WorkingVerb = null;
                StepText = null;
                ElapsedText = null;
                ElapsedCompact = null;
                Save();
            }
            if (ReferenceEquals(_generateCts, turnCts)) _generateCts = null;
            turnCts.Dispose();
        }
    }

    private string BuildFourLaneStrategyBrief(string prompt)
    {
        if (string.IsNullOrWhiteSpace(_fourLaneStrategyBrief))
            return prompt;

        if (string.Equals(_fourLaneStrategyBrief.Trim(), prompt, StringComparison.Ordinal))
            return _fourLaneStrategyBrief;

        return CombineFourLaneStrategyBrief(_fourLaneStrategyBrief, prompt);
    }

    private string BuildConfirmedStrategyImplementationBrief()
    {
        if (ConfirmedStrategyIntent is null || _strategyIntentResearchCase is null)
            return string.Empty;

        var builder = new StringBuilder()
            .AppendLine("Confirmed strategy request. Implement these reviewed decisions without inventing missing behavior.")
            .Append("Objective: ").AppendLine(_strategyIntentResearchCase.Objective)
            .Append("Hypothesis: ").AppendLine(_strategyIntentResearchCase.Hypothesis)
            .Append("Intent shape: ").AppendLine(ConfirmedStrategyIntent.IntentModel.Kind.ToString())
            .AppendLine("Reviewed decisions:");
        foreach (var requirement in ConfirmedStrategyIntent.Requirements.OrderBy(static item => item.Stage))
        {
            builder.Append("- ").Append(requirement.Stage).Append(": ").Append(requirement.Description).Append(" => ")
                .AppendLine(requirement.Disposition == StrategySemanticDispositionV1.Applicable
                    ? requirement.Value?.CanonicalValue ?? "missing"
                    : requirement.DispositionRationale ?? requirement.Disposition.ToString());
        }
        if (ResearchExperimentEvidence is { } evidence)
        {
            builder.AppendLine("Bound exploratory research evidence (not historical validation):")
                .Append("- Evidence SHA-256: ").AppendLine(ResearchExperimentCanonicalJsonV1.Hash(evidence))
                .Append("- Dataset SHA-256: ").AppendLine(evidence.DatasetHashSha256)
                .Append("- Formula: ").AppendLine(evidence.Formula)
                .Append("- Chronological split: ")
                .Append(evidence.Split.TrainingCount).Append('/')
                .Append(evidence.Split.ValidationCount).Append('/')
                .AppendLine(evidence.Split.TestCount.ToString())
                .AppendLine("- Treat this only as a feature hypothesis; exact historical validation remains mandatory.");
        }
        return builder.ToString().TrimEnd();
    }

    private void SetPendingFourLanePrompt(string? prompt)
    {
        var normalized = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        if (string.Equals(_pendingFourLanePrompt, normalized, StringComparison.Ordinal)) return;

        _pendingFourLanePrompt = normalized;
        OnPropertyChanged(nameof(HasPendingFourLanePrompt));
        OnPropertyChanged(nameof(CanGenerateFourCandidates));
        OnPropertyChanged(nameof(CanChooseGeneratedCandidate));
        OnPropertyChanged(nameof(CanRevalidateGeneratedCandidate));
        OnPropertyChanged(nameof(CandidateBacktestAvailabilityText));
        ChooseGeneratedCandidateCommand.NotifyCanExecuteChanged();
        RevalidateGeneratedCandidateCommand.NotifyCanExecuteChanged();
        GenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        DiscardPendingFourLanePromptCommand.NotifyCanExecuteChanged();
        NotifyTradeIrSynthesisStateChanged();
        NotifyTradeIrBacktestStateChanged();
    }

    private bool CanDiscardPendingFourLanePromptAction() =>
        HasPendingFourLanePrompt && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanDiscardPendingFourLanePromptAction))]
    private void DiscardPendingFourLanePrompt()
    {
        if (!CanDiscardPendingFourLanePromptAction()) return;

        var pending = _pendingFourLanePrompt;
        SetPendingFourLanePrompt(null);
        if (!string.IsNullOrWhiteSpace(pending) &&
            string.Equals(Composer.Trim(), pending, StringComparison.Ordinal))
        {
            Composer = string.Empty;
        }

        AiStatus = "Discarded the uncommitted refinement. The last completed candidate batch and its exact hashes remain active.";
        Status = "Pending request discarded. Review or test only the retained completed batch.";
        Append(AuthoringMessage.Tool(
            "Ok",
            "Pending request discarded",
            "No generation, selection, synthesis, test, or backtest ran. The committed brief and candidate hashes were preserved."));
        Save();
    }

    private const string OrderedFourLaneBriefPreamble =
        "Ordered strategy request. Later refinements supersede only directly conflicting earlier clauses.\n" +
        "Preserve every non-conflicting requirement, and do not implement a superseded clause alongside its replacement.";

    private static string CombineFourLaneStrategyBrief(string strategyBrief, string refinement)
    {
        var orderedBrief = strategyBrief.TrimStart().StartsWith(
            OrderedFourLaneBriefPreamble,
            StringComparison.Ordinal)
                ? strategyBrief.Trim()
                : $"{OrderedFourLaneBriefPreamble}\n\nOriginal strategy brief:\n{strategyBrief.Trim()}";
        return $"{orderedBrief}\n\nFollow-up refinement:\n{refinement.Trim()}";
    }

    private static bool IsBacktestNavigationIntent(string prompt)
    {
        var words = new string(prompt.Trim().ToLowerInvariant()
                .Select(static character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!words.Any(static word => word is "backtest" or "backtesting") || words.Length > 14)
            return false;

        // Keep this deliberately narrow: mixed instructions such as "run backtest with 20-period ATR"
        // carry strategy information and must flow through the refinement-preserving generation path.
        return words.All(static word => word is
            "backtest" or "go" or "gow" or "goto" or "open" or "run" or "start" or "switch" or
            "move" or "proceed" or "to" or "the" or "please" or "can" or "could" or "we" or "you" or "i" or
            "want" or "let" or "lets" or "s" or "now" or "next" or "tab" or "screen" or "page" or "view" or
            "it" or "this" or "backtesting" or "said" or "do" or "after" or "make" or "made" or "and" or
            "ok" or "okay" or "be" or
            "those" or "them" or "once" or "then" or "how" or "test" or "testing" or "candidate" or
            "candidates" or "strategy" or "strategies" or "generated");
    }

    private void RouteBacktestNavigationIntent(string prompt)
    {
        // A navigation turn must not hide an interrupted strategy refinement that is still waiting
        // to be applied to the retained candidate batch.
        Composer = _pendingFourLanePrompt ?? string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        WorkbenchTab = 3;

        var guidance = CanPrepareGeneratedCandidateForBacktest
            ? "The active package-valid Graph is ready. Choose Run synthetic smoke test below; this is separate from generation and is not a historical backtest."
            : CandidateBacktestAvailabilityText;
        AiStatus = "No replacement candidates were generated. The strategy brief and current candidate batch were preserved. " + guidance;
        Status = guidance;
        Append(AuthoringMessage.Tool(
            "Ok",
            "Backtest kept separate from generation",
            guidance));
        Save();
    }

    private void ResetGenerationLaneProgress()
    {
        SelectedGenerationLaneProgressRow = null;
        GenerationLaneProgressRows.Clear();
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
            GenerationLaneProgressRows.Add(new StrategyGenerationLaneProgressRow(lane));
        NotifyGenerationProgressChanged();
    }

    private void ApplyGenerationLaneProgress(StrategyGenerationLaneProgressV1 progress)
    {
        var row = GenerationLaneProgressRows.FirstOrDefault(candidate => candidate.Lane == progress.Lane);
        if (row is null) return;
        row.Apply(progress);
        if (progress.Result is not null &&
            (SelectedGenerationLaneProgressRow is null || !SelectedGenerationLaneProgressRow.HasResult))
        {
            SelectedGenerationLaneProgressRow = row;
        }
        NotifyGenerationProgressChanged();
    }

    private void SetFinalGenerationLaneProgress(ParallelStrategyGenerationResultV1 result)
    {
        foreach (var lane in result.Lanes)
        {
            var state = lane.Readiness is StrategyGenerationReadinessV1.Generated or
                StrategyGenerationReadinessV1.PackageValid or StrategyGenerationReadinessV1.TestPassed
                    ? StrategyGenerationLaneProgressStateV1.Completed
                    : StrategyGenerationLaneProgressStateV1.Failed;
            ApplyGenerationLaneProgress(new StrategyGenerationLaneProgressV1(
                lane.Lane,
                state,
                Result: lane));
        }
    }

    private void NotifyGenerationProgressChanged()
    {
        OnPropertyChanged(nameof(GenerationProgressSummary));
        if (IsGeneratingCandidates) StepText = GenerationProgressSummary;
    }

    private void ApplyParallelCandidateBatch(
        ParallelStrategyGenerationResultV1 result,
        bool restored = false)
    {
        if (!string.Equals(result.StrategyId, StrategyId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Candidate batch '{result.StrategyId}' does not belong to strategy '{StrategyId.Trim()}'.");
        var issues = StrategyGenerationBatchValidationV1.Validate(result);
        if (issues.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, issues.Select(issue =>
                $"{issue.Path}: {issue.Message}")));
        if (!CanEnterFourLaneConformance ||
            !string.Equals(
                result.ConfirmedIntentHashSha256,
                ConfirmedStrategyIntentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The candidate batch is not bound to the currently confirmed strategy request.");
        }

        CandidateRestoreWarning = null;
        ClearTradeIrSynthesis();
        SetEditorBaseGeneratedCandidateHash(null);
        SelectedGeneratedCandidateOption = null;
        ChosenGeneratedCandidateHash = null;
        ClearGeneratedCandidateOptionFlags();
        GeneratedCandidateOptions.Clear();
        _parallelCandidateBatch = result;
        CandidateBatchRestored = restored;
        foreach (var lane in result.Lanes) GeneratedCandidateOptions.Add(new StrategyGenerationCandidateOption(lane));
        SelectedGeneratedCandidateOption = GeneratedCandidateOptions.FirstOrDefault(static option => option.Result.Selectable)
            ?? GeneratedCandidateOptions.FirstOrDefault();
        NotifyParallelCandidateStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanChooseGeneratedCandidateAction))]
    private void ChooseGeneratedCandidate()
    {
        if (!CanEnterFourLaneConformance ||
            _parallelCandidateBatch is null ||
            SelectedGeneratedCandidateOption?.CandidateHashSha256 is not { } hash)
            return;

        var selection = StrategyGenerationBatchValidationV1.Select(_parallelCandidateBatch, hash);
        if (!selection.Success)
        {
            var detail = string.Join(Environment.NewLine, selection.Issues.Select(issue =>
                $"{issue.Path}: {issue.Message}"));
            AiStatus = "That strategy option is no longer a valid selection.";
            Append(AuthoringMessage.Tool("Fail", "Candidate not selected", detail));
            return;
        }

        var candidate = selection.Candidate!;
        var laneResult = _parallelCandidateBatch.Lanes.Single(lane =>
            string.Equals(lane.CandidateHashSha256, selection.CandidateHashSha256, StringComparison.Ordinal));
        InvalidateDerivedArtifactState(markUnregistered: true);
        HasDetachedImplementationSource = false;
        SetFiles([new StrategyFile(candidate.Artifact.FileName, EditableArtifactContent(candidate.Artifact))]);
        _filesEditedByUser = false;
        SetEditorBaseGeneratedCandidateHash(selection.CandidateHashSha256);
        ChosenGeneratedCandidateHash = selection.CandidateHashSha256;
        // Keep a package-valid graph on Candidate so the exact-hash admission action is visible.
        // Source-review lanes still open Code because they have no runnable target yet.
        WorkbenchTab = candidate.Lane == StrategyGenerationLaneV1.TypedGraph ? 3 : 0;
        if (laneResult.PackageValid)
        {
            AiStatus = $"{StrategyGenerationLaneCatalogV1.DisplayName(candidate.Lane)} loaded at package-valid hash {selection.CandidateHashSha256![..12]}…. The smoke admission action is now visible below; compatibility is not proven and nothing has run yet.";
            Status = $"Loaded {candidate.Artifact.FileName}. Choose Run synthetic smoke test to perform exact-hash data, target, and runtime admission. This is not a historical backtest.";
        }
        else
        {
            AiStatus = $"{StrategyGenerationLaneCatalogV1.DisplayName(candidate.Lane)} generated artifact selected at hash {selection.CandidateHashSha256![..12]}…. It is not package-validated, tested, or run.";
            Status = $"Loaded {candidate.Artifact.FileName}. Its generation format is valid, but this lane has no registered package validator or importer; no tests or execution ran.";
        }
        Append(AuthoringMessage.Tool(
            "Ok",
            $"Selected {StrategyGenerationLaneCatalogV1.DisplayName(candidate.Lane)}",
            $"Loaded {candidate.Artifact.FileName} · {(laneResult.PackageValid ? "package valid · smoke admission available" : "not package-validated")} · not tested · hash {selection.CandidateHashSha256![..12]}…"));
        Save();
    }

    private bool CanChooseGeneratedCandidateAction() => CanChooseGeneratedCandidate;

    [RelayCommand(CanExecute = nameof(CanRevalidateGeneratedCandidateAction))]
    private void RevalidateGeneratedCandidate()
    {
        if (!CanEnterFourLaneConformance ||
            _parallelCandidateBatch is null ||
            _editorBaseGeneratedCandidateHash is not { } priorHash ||
            Files.Count != 1)
            return;

        var matchingLanes = _parallelCandidateBatch.Lanes.Where(lane => lane.Candidate is not null &&
            string.Equals(lane.CandidateHashSha256, priorHash, StringComparison.Ordinal)).ToArray();
        if (matchingLanes.Length != 1) return;
        var candidate = matchingLanes[0].Candidate!;

        var file = Files[0];
        StrategyGenerationArtifactV1 editedArtifact;
        try
        {
            if (candidate.Artifact.Source is not null)
            {
                editedArtifact = candidate.Artifact with
                {
                    FileName = file.Name,
                    Source = file.Content,
                    Document = null,
                };
            }
            else if (candidate.Artifact.Document is not null)
            {
                using var document = JsonDocument.Parse(file.Content);
                editedArtifact = candidate.Artifact with
                {
                    FileName = file.Name,
                    Source = null,
                    Document = document.RootElement.Clone(),
                };
            }
            else
            {
                throw new InvalidOperationException("The selected candidate has no editable source or JSON document.");
            }
        }
        catch (JsonException exception)
        {
            var detail = $"ARTIFACT_JSON_INVALID · artifact.document: {exception.Message}";
            AiStatus = "The edited artifact is not valid JSON; the prior candidate remains unchanged.";
            Status = AiStatus;
            Append(AuthoringMessage.Tool("Fail", "Local revalidation failed", detail));
            Save();
            return;
        }
        catch (InvalidOperationException exception)
        {
            var detail = $"ARTIFACT_NOT_EDITABLE · artifact: {exception.Message}";
            AiStatus = "The selected artifact cannot be locally revalidated.";
            Status = AiStatus;
            Append(AuthoringMessage.Tool("Fail", "Local revalidation failed", detail));
            Save();
            return;
        }

        var revalidation = StrategyGenerationBatchValidationV1.RevalidateArtifact(
            _parallelCandidateBatch,
            priorHash,
            editedArtifact);
        if (!revalidation.Applied)
        {
            var detail = FormatGenerationIssues(revalidation.Issues);
            AiStatus = "The edited artifact could not be rebound to this candidate; the prior candidate remains unchanged.";
            Status = AiStatus;
            Append(AuthoringMessage.Tool("Fail", "Local revalidation failed", detail));
            Save();
            return;
        }

        var laneResult = revalidation.LaneResult!;
        var newHash = laneResult.CandidateHashSha256!;
        ApplyParallelCandidateBatch(revalidation.Batch!);
        SetEditorBaseGeneratedCandidateHash(newHash);
        SelectedGeneratedCandidateOption = GeneratedCandidateOptions.First(option =>
            string.Equals(option.CandidateHashSha256, newHash, StringComparison.Ordinal));

        if (laneResult.Selectable)
        {
            ChosenGeneratedCandidateHash = newHash;
            if (laneResult.PackageValid)
            {
                AiStatus = $"The local edit passed package validation at hash {newHash[..12]}…. It has not been tested or run.";
                Status = laneResult.Candidate!.PackageBinding.ImporterId is null
                    ? $"Revalidated {file.Name}. Package validation passed, but no terminal importer is registered; no tests or execution ran."
                    : $"Revalidated {file.Name} for importer '{laneResult.Candidate.PackageBinding.ImporterId}'. No tests or execution ran.";
                Append(AuthoringMessage.Tool(
                    "Ok",
                    $"Revalidated {StrategyGenerationLaneCatalogV1.DisplayName(laneResult.Lane)}",
                    $"Package valid · not tested · hash {newHash[..12]}…"));
            }
            else
            {
                var validationBoundary = laneResult.PackageValidationAvailable
                    ? "No package-valid proof was produced"
                    : "No package validator or importer is registered for this lane";
                AiStatus = $"The local edit passed generation-format validation at hash {newHash[..12]}…. It is not package-validated, tested, or run.";
                Status = $"Revalidated {file.Name}. {validationBoundary}; no tests or execution ran.";
                Append(AuthoringMessage.Tool(
                    "Ok",
                    $"Revalidated {StrategyGenerationLaneCatalogV1.DisplayName(laneResult.Lane)}",
                    $"Generated · not package-validated · not tested · hash {newHash[..12]}…"));
            }
        }
        else
        {
            ChosenGeneratedCandidateHash = null;
            var detail = FormatGenerationIssues(laneResult.Issues);
            AiStatus = "The local edit received a new hash but is invalid and cannot be selected. Fix the editor content and revalidate again.";
            Status = AiStatus;
            Append(AuthoringMessage.Tool(
                "Fail",
                $"{StrategyGenerationLaneCatalogV1.DisplayName(laneResult.Lane)} edit is invalid",
                detail));
        }

        Save();
    }

    private bool CanRevalidateGeneratedCandidateAction() => CanRevalidateGeneratedCandidate;

    private static string FormatGenerationIssues(IReadOnlyList<StrategyCandidateGenerationIssueV1> issues) =>
        string.Join(Environment.NewLine, issues.Select(issue =>
            $"{issue.Code} · {issue.Path}: {issue.Message}"));

    private static string EditableArtifactContent(StrategyGenerationArtifactV1 artifact)
    {
        if (artifact.Source is { } source) return source;
        return artifact.Document is { } document
            ? JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true })
            : string.Empty;
    }

    private static string FormatParallelCandidates(ParallelStrategyGenerationResultV1 result)
    {
        var builder = new StringBuilder("I asked four strategy-generation agents to express the same brief.\n");
        for (var index = 0; index < result.Lanes.Count; index++)
        {
            var lane = result.Lanes[index];
            builder.AppendLine().Append(index + 1).Append(". ")
                .Append(StrategyGenerationLaneCatalogV1.DisplayName(lane.Lane)).Append(" — ");
            if (lane.PackageValid)
                builder.Append("package-valid · ").Append(lane.Candidate!.Title).Append(" · ")
                    .Append(lane.Candidate.Artifact.FileName);
            else if (lane.Selectable)
                builder.Append("generated, not package-validated · ").Append(lane.Candidate!.Title)
                    .Append(" · ").Append(lane.Candidate.Artifact.FileName);
            else if (lane.Readiness == StrategyGenerationReadinessV1.Invalid)
                builder.Append("generated, invalid · ")
                    .Append(lane.Issues.FirstOrDefault()?.Message ?? "deterministic validation failed");
            else
                builder.Append(lane.Readiness.ToString().ToLowerInvariant()).Append(" · ")
                    .Append(lane.Issues.FirstOrDefault()?.Message ?? "unknown generation error");
        }
        return builder.AppendLine().AppendLine()
            .Append("Every structurally valid option can be chosen. Package-valid only means an installed package validator passed; none were tested or run.")
            .ToString().TrimEnd();
    }

    /// <summary>
    /// Legacy semantic lane retained for saved sessions and deployments that have not registered the
    /// four-way coordinator yet.
    /// </summary>
    /// <summary>
    /// Offline host-catalog routing for famous indicators / research scans. Returns true when the
    /// turn was fully handled without a codegen provider.
    /// </summary>
    private bool TryRouteHostCatalogWithoutProvider(string prompt)
    {
        if (string.IsNullOrWhiteSpace(StrategyId))
            return false;

        var effectiveRequest = AuthoredUnitSpecification is { Kind: AuthoredUnitKindV1.Visualizer } existing
            ? $"{existing.RawRequest}\nRevision requested by user: {prompt}"
            : BuildInitialAuthoredUnitRequest(prompt);
        var chartChoice = AuthoredChartChoiceCatalogV1.Resolve(
            effectiveRequest,
            prompt,
            LoadUserChartIndicators());

        if (chartChoice.NeedsClarification &&
            chartChoice.ClarificationQuestion is { Length: > 0 } catalogQuestion)
        {
            if (Messages.Count == 0) DeriveIdentityFrom(prompt);
            Composer = string.Empty;
            Append(new AuthoringMessage(CodegenRole.User, prompt));
            Append(new AuthoringMessage(CodegenRole.Assistant, catalogQuestion));
            AwaitingAnswer = true;
            AiStatus = "Pick a numbered host chart choice, or use a follow-up under this reply.";
            PublishTurnFollowUps(prompt);
            Save();
            return true;
        }

        if ((chartChoice.HasOverlays || chartChoice.HasResearchScans) &&
            (IsResearchStage || !AuthoredChartChoiceCatalogV1.LooksLikeTradingRequest(effectiveRequest)) &&
            !AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(prompt) &&
            !AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(effectiveRequest))
        {
            if (Messages.Count == 0) DeriveIdentityFrom(prompt);
            if (chartChoice.HasOverlays)
                _ = CompleteHostOverlayVisualizerAsync(prompt, effectiveRequest, chartChoice);
            else
                _ = CompleteHostResearchScanAsync(prompt, chartChoice);
            return true;
        }

        // Analytical questions about indicators: apply overlays for context, then continue to AI.
        if (chartChoice.HasOverlays &&
            AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(prompt))
        {
            ApplyResearchOverlayAnalysis(chartChoice);
            HostChartOverlayPreviewRequested?.Invoke(
                this,
                new HostChartOverlayPreviewRequestedEventArgs(
                    chartChoice.Overlays.Select(static item => item.Id).ToArray(),
                    startResearchCapture: false));
        }

        return false;
    }

    /// <summary>
    /// Diagnostics / owned E2E: attach an already-compiled strategy as if Build + Register succeeded,
    /// so Validate → Paper can be exercised without a live model turn.
    /// </summary>
    public bool TryAttachCompiledStrategyForDiagnostics(
        AuthoredUnitSpecificationV1 specification,
        CompiledAuthoredUnitV1 unit,
        StrategyScript script,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(script);

        if (unit.Kind != AuthoredUnitKindV1.Strategy)
        {
            reason = "Diagnostics attach requires a Strategy compiled unit.";
            return false;
        }

        if (!string.Equals(
                AuthoredUnitSpecificationCanonicalJsonV1.Hash(specification),
                unit.SpecificationHashSha256,
                StringComparison.Ordinal))
        {
            reason = "Compiled unit hash does not match the provided specification.";
            return false;
        }

        StrategyId = specification.UnitId;
        AuthoredUnitSpecification = specification;
        SetFiles(script.Files);
        _filesEditedByUser = false;
        HasDetachedImplementationSource = false;
        ConfirmAuthoredUnitRegistration(script, unit);
        SynchronizeStrategyWorkspace();
        if (!CanRunHistoricalValidation)
        {
            reason = "Strategy registered but historical validation is still locked.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private async Task ClassifyAndRouteInitialRequestAsync(AiProviderChoice choice, string prompt)
    {
        var provider = ResolveClient(choice) ?? choice.Client;
        var effectiveRequest = AuthoredUnitSpecification is { Kind: AuthoredUnitKindV1.Visualizer } existing
            ? $"{existing.RawRequest}\nRevision requested by user: {prompt}"
            : BuildInitialAuthoredUnitRequest(prompt);
        if (_authoredUnitIntentClassifier is null || _authoredUnitSpecificationGenerator is null)
        {
            await SendCandidateTurnAsync(choice, prompt);
            return;
        }

        var chartChoice = AuthoredChartChoiceCatalogV1.Resolve(
            effectiveRequest,
            prompt,
            LoadUserChartIndicators());
        if (chartChoice.NeedsClarification &&
            chartChoice.ClarificationQuestion is { Length: > 0 } catalogQuestion)
        {
            Composer = string.Empty;
            Append(new AuthoringMessage(CodegenRole.User, prompt));
            Append(new AuthoringMessage(CodegenRole.Assistant, catalogQuestion));
            AwaitingAnswer = true;
            AiStatus = "Pick a numbered host chart choice, or use a follow-up under this reply.";
            PublishTurnFollowUps(prompt);
            Save();
            return;
        }

        // Famous-indicator / research-scan picks go host-first while Research is active —
        // unless the user asked an analytical question (why/how/compare/fail…), which must reach AI.
        if ((chartChoice.HasOverlays || chartChoice.HasResearchScans) &&
            (IsResearchStage || !AuthoredChartChoiceCatalogV1.LooksLikeTradingRequest(effectiveRequest)) &&
            !AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(prompt) &&
            !AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(effectiveRequest))
        {
            if (chartChoice.HasOverlays)
                await CompleteHostOverlayVisualizerAsync(prompt, effectiveRequest, chartChoice);
            else
                await CompleteHostResearchScanAsync(prompt, chartChoice);
            return;
        }

        // Apply mentioned overlays as context, then continue to classifier/AI for the explanation.
        if (chartChoice.HasOverlays &&
            AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(prompt))
        {
            ApplyResearchOverlayAnalysis(chartChoice);
            HostChartOverlayPreviewRequested?.Invoke(
                this,
                new HostChartOverlayPreviewRequestedEventArgs(
                    chartChoice.Overlays.Select(static item => item.Id).ToArray(),
                    startResearchCapture: false));
        }

        IsGenerating = true;
        AiStatus = "Deciding whether this request is a display-only visualizer or a position-changing strategy…";
        AuthoredUnitIntentClassificationResultV1 classification;
        try
        {
            classification = await _authoredUnitIntentClassifier.ClassifyAsync(
                provider,
                effectiveRequest,
                ChartReferences.Select(static item => item.Reference).ToArray(),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Classification stopped; no artifact was generated.";
            IsGenerating = false;
            return;
        }
        finally
        {
            // The strategy lane owns its own generation state. Clear this first so its command state
            // and progress strip start from a clean boundary.
            IsGenerating = false;
        }

        InputTokens += classification.Usage.InputTokens;
        OutputTokens += classification.Usage.OutputTokens;
        CachedTokens += classification.Usage.CachedInputTokens;

        if (classification.Classification?.ClarificationQuestion is { Length: > 0 } question)
        {
            Composer = string.Empty;
            Append(new AuthoringMessage(CodegenRole.User, prompt));
            Append(new AuthoringMessage(CodegenRole.Assistant, question));
            AwaitingAnswer = true;
            AiStatus = "Hyperion needs one execution-intent choice before selecting the runtime contract.";
            Save();
            return;
        }

        if (!classification.Success)
        {
            var detail = string.Join(Environment.NewLine, classification.Issues.Select(issue =>
                $"{issue.Path}: {issue.Message}"));
            var looksLikeProvider =
                detail.Contains("exit code", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("OAuth", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("CLI", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("authentication", StringComparison.OrdinalIgnoreCase);

            if (looksLikeProvider)
            {
                AiStatus =
                    "AI provider failed while classifying this request. Your message is preserved — charts and Research stay usable. Retry, or open Settings → AI providers.";
                Append(AuthoringMessage.Tool(
                    "Fail",
                    "Provider failed (not an unsupported strategy)",
                    detail + Environment.NewLine + Environment.NewLine +
                    "Retry Send, or configure the provider under Settings → AI providers. " +
                    "You can keep investigating on the chart without AI."));
            }
            else
            {
                AiStatus = "This request could not be classified as a supported unit type. No source was generated.";
                Append(AuthoringMessage.Tool("Fail", "Request not accepted", detail));
            }

            return;
        }

        if (classification.Classification!.Kind == AuthoredUnitKindV1.Strategy)
        {
            AuthoredUnitSpecification = null;
            await SendCandidateTurnAsync(choice, effectiveRequest);
            return;
        }

        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        if (chartChoice.HasOverlays || chartChoice.HasResearchScans)
        {
            Append(AuthoringMessage.Tool(
                "Ok",
                "Host chart choices resolved",
                AuthoredChartChoiceCatalogV1.DescribeSelection(chartChoice)));
            foreach (var scan in chartChoice.ResearchScans)
                Append(new AuthoringMessage(CodegenRole.Assistant, scan.ClarificationHint));
            if (chartChoice.HasOverlays)
            {
                HostChartOverlayPreviewRequested?.Invoke(
                    this,
                    new HostChartOverlayPreviewRequestedEventArgs(
                        chartChoice.Overlays.Select(static item => item.Id).ToArray()));
            }
        }

        InvalidateDerivedArtifactState(markUnregistered: true);
        IsGenerating = true;
        AiStatus = "Freezing the visualizer's asset, timeframe, data streams, parameters, and drawing contract…";
        try
        {
            var instruments = BuildAuthoredUnitInstrumentCandidates(effectiveRequest);
            var result = await _authoredUnitSpecificationGenerator.GenerateAsync(
                provider,
                new AuthoredUnitSpecificationGenerationRequestV1(
                    StrategyId.Trim(),
                    effectiveRequest,
                    instruments,
                    ChartReferences: ChartReferences.Select(static item => item.Reference).ToArray(),
                    ChartReferenceInspections: ChartReferenceInspections.ToArray(),
                    ChartPatternSelections: ChartPatternSelections.ToArray(),
                    SelectedChartOverlayIds: chartChoice.Overlays.Select(static item => item.Id).ToArray()),
                CancellationToken.None);
            InputTokens += result.Usage.InputTokens;
            OutputTokens += result.Usage.OutputTokens;
            CachedTokens += result.Usage.CachedInputTokens;

            if (!result.Success)
            {
                var detail = string.Join(Environment.NewLine, result.Issues.Select(issue =>
                    $"{issue.Path}: {issue.Message}"));
                AiStatus = "The visualizer specification failed launch validation. Nothing was compiled or registered.";
                Append(AuthoringMessage.Tool("Fail", "Visualizer specification rejected", detail));
                return;
            }

            AuthoredUnitSpecification = result.Specification;
            var specification = result.Specification!;
            var hash = AuthoredUnitSpecificationCanonicalJsonV1.Hash(specification);
            Append(new AuthoringMessage(
                CodegenRole.Assistant,
                $"I classified this as a display-only visualizer. It will use {string.Join(", ", specification.Instruments.Select(static item => item.UserText))}, " +
                $"{specification.Timeframe.UserText}, {specification.DataRequirement}, and {specification.Drawing.Layers.Count} drawing layer(s)."));
            Append(AuthoringMessage.Tool(
                "Ok",
                "Runnable visualizer contract frozen",
                $"No order target · launch-valid · specification hash {hash[..12]}… · generating the bound IVisualizer source."));

            if (!await GenerateAuthoredUnitSourceAsync(provider, specification))
                return;

            AwaitingAnswer = false;
            if (IsResearchStage)
                WorkbenchTab = 3;
            else
                WorkbenchTab = 0;
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Visualizer specification generation stopped; nothing was compiled.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Authored visualizer specification generation failed for {Id}", StrategyId);
            AiStatus = $"Visualizer specification error: {exception.Message}";
            Append(AuthoringMessage.System(AiStatus));
        }
        finally
        {
            IsGenerating = false;
            Save();
        }
    }

    private Task CompleteHostOverlayVisualizerAsync(
        string prompt,
        string effectiveRequest,
        AuthoredChartChoiceResolutionV1 chartChoice)
    {
        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        Append(AuthoringMessage.Tool(
            "Ok",
            "Research chart indicators applied",
            AuthoredChartChoiceCatalogV1.DescribeSelection(chartChoice)));
        foreach (var scan in chartChoice.ResearchScans)
            Append(new AuthoringMessage(CodegenRole.Assistant, scan.ClarificationHint));

        // Research-led path: overlays change analysis only. They must not freeze a visualizer /
        // strategy specification — that would mark Design READY and Build REVIEW before research
        // has informed any trading rules.
        EnterResearchWorkspace();
        ApplyResearchOverlayAnalysis(chartChoice);

        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                chartChoice.Overlays.Select(static item => item.Id).ToArray(),
                startResearchCapture: chartChoice.HasResearchScans));

        AiStatus = chartChoice.HasResearchScans
            ? "Research indicators are on the linked chart. Scanning for comparable outcome events…"
            : "Research indicators are on the linked chart. They are analysis tools until you choose Use in Design.";
        Append(new AuthoringMessage(
            CodegenRole.Assistant,
            $"Applied {AuthoredChartChoiceCatalogV1.DescribeSelection(chartChoice)} for investigation. " +
            "No trading rule or executable version was created. Save an observation, then Use in Design when a condition is worth testing."));
        if (chartChoice.HasResearchScans)
            _ = RunResearchOutcomeGalleryAsync(chartChoice.ResearchScans.Select(static scan => scan.Id).ToArray());

        AwaitingAnswer = false;
        WorkbenchTab = 3;
        Save();
        PublishTurnFollowUps(prompt);
        NotifyWorkingFlowMapChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Record host overlay ids as pending Research analysis bindings without creating an
    /// AuthoredUnitSpecification.
    /// </summary>
    private void ApplyResearchOverlayAnalysis(AuthoredChartChoiceResolutionV1 chartChoice)
    {
        if (!chartChoice.HasOverlays) return;
        var overlayIds = chartChoice.Overlays.Select(static item => item.Id).ToArray();
        PendingResearchOverlayIds = overlayIds;
        // Keep existing exact indicator bindings when present; otherwise store overlay ids only.
        if (PendingResearchIndicatorBindings.Count == 0)
        {
            Status =
                $"Research analysis overlays [{string.Join(", ", overlayIds)}]. Not strategy rules until Use in Design.";
        }
    }

    /// <summary>
    /// Research-scan picks open the live host chart in observation→outcome brush mode and kick off
    /// the S&amp;P 100 outcome gallery when a scan service is registered. Not a Paper unlock.
    /// </summary>
    private Task CompleteHostResearchScanAsync(string prompt, AuthoredChartChoiceResolutionV1 chartChoice)
    {
        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        Append(AuthoringMessage.Tool(
            "Ok",
            "Host research scans resolved",
            AuthoredChartChoiceCatalogV1.DescribeSelection(chartChoice)));
        foreach (var scan in chartChoice.ResearchScans)
            Append(new AuthoringMessage(CodegenRole.Assistant, scan.ClarificationHint));

        EnterResearchWorkspace();
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                Array.Empty<string>(),
                startResearchCapture: true));

        Append(new AuthoringMessage(
            CodegenRole.Assistant,
            $"Opened the live research chart for {AuthoredChartChoiceCatalogV1.DescribeSelection(chartChoice)}. " +
            "Brush an observation window and a later outcome window, or pick a gallery hit below, then label B/C/N. " +
            "Gallery hits are research evidence only — not Paper."));
        AiStatus = "Research chart is open in the Strategy Builder workspace. Scanning local history for matching outcome events…";
        AwaitingAnswer = false;
        WorkbenchTab = 3;
        Save();
        PublishTurnFollowUps(prompt);
        _ = RunResearchOutcomeGalleryAsync(chartChoice.ResearchScans.Select(static scan => scan.Id).ToArray());
        return Task.CompletedTask;
    }

    private async Task RunResearchOutcomeGalleryAsync(IReadOnlyList<string> scanIds)
    {
        if (_researchOutcomeGalleryScan is null)
        {
            AiStatus = "Research chart is open for manual capture. Outcome gallery scan is not registered in this composition.";
            return;
        }

        var primary = scanIds.FirstOrDefault(static id =>
            id is "next-day-plus-5" or "pre-crash" or "pre-breakout");
        if (primary is null)
            return;

        IsScanningResearchGallery = true;
        try
        {
            ResearchOutcomeGalleryResult = await _researchOutcomeGalleryScan.ScanAsync(
                new ResearchOutcomeGalleryScanRequestV1(primary),
                CancellationToken.None);
            var count = ResearchOutcomeGalleryResult.Matches.Count;
            AiStatus = count == 0
                ? $"Gallery scan finished with no hits. {ResearchOutcomeGalleryResult.Explanation} Change the Charts symbol and brush manually — capture only, no backtest."
                : $"Gallery found {count} comparable event(s). Select one in RESULTS to load it on the Research chart — nothing was auto-applied.";
            Append(AuthoringMessage.Tool(
                count == 0 ? "Warn" : "Ok",
                ResearchOutcomeGalleryResult.DisplayName,
                ResearchOutcomeGalleryResult.Explanation));
            Status = AiStatus;
            // Do not auto-focus Matches[0]: on limited stores that often jumps to an unrelated
            // symbol (e.g. MSFT) while the research question / chart instrument stays unexplained.
            PublishTurnFollowUps(lastUserText: null);
            Save();
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Research gallery scan stopped.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Research outcome gallery scan failed");
            AiStatus = $"Research gallery scan stopped: {exception.Message}";
        }
        finally
        {
            IsScanningResearchGallery = false;
        }
    }

    [RelayCommand]
    private void UseResearchOutcomeGalleryMatch(ResearchOutcomeGalleryMatchV1? match) =>
        FocusResearchGalleryMatch(match, openChart: true, announce: true);

    /// <summary>
    /// Bind selection + optional single Charts focus. Auto-collect uses openChart=false so the
    /// Research tab boxes stay the primary UI instead of spawning many chart windows.
    /// </summary>
    private void FocusResearchGalleryMatch(
        ResearchOutcomeGalleryMatchV1? match,
        bool openChart,
        bool announce)
    {
        if (match is null || ResearchOutcomeGalleryResult is null ||
            !ResearchOutcomeGalleryResult.Matches.Contains(match))
            return;

        var selection = new ResearchChartSelectionV1(
            match.InstrumentId,
            match.CanonicalSymbol,
            match.Timeframe,
            new DateTimeOffset(DateTime.SpecifyKind(match.ObservationFromUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(match.ObservationToUtcExclusive, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(match.OutcomeFromUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(match.OutcomeToUtcExclusive, DateTimeKind.Utc)),
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars);
        SetResearchChartSelection(
            selection,
            PendingResearchOverlayIds.Count > 0 ? PendingResearchOverlayIds : null,
            PendingResearchIndicatorBindings.Count > 0 ? PendingResearchIndicatorBindings : null);
        SelectResearchGalleryCard(match);

        if (openChart)
        {
            // Carry the current Research-space indicators (catalog overlay ids, not BindingIds).
            var overlays = OverlaysForResearchPreview().ToArray();
            HostChartOverlayPreviewRequested?.Invoke(
                this,
                new HostChartOverlayPreviewRequestedEventArgs(
                    overlays,
                    startResearchCapture: false,
                    preferredSymbol: match.CanonicalSymbol,
                    galleryMatch: match));
        }

        Status =
            $"Focused gallery event {match.CanonicalSymbol} · {match.OutcomeReturn:P1} ({match.LabelHint})" +
            (string.IsNullOrWhiteSpace(match.IndicatorScoreSummary)
                ? "."
                : $" · {match.IndicatorScoreSummary}.");
        if (announce)
        {
            Append(AuthoringMessage.Tool(
                "Ok",
                "Gallery event focused",
                $"{match.CanonicalSymbol} · {match.LabelHint} · return {match.OutcomeReturn:P2} · " +
                $"{match.IndicatorScoreSummary ?? "no scores"} · " +
                $"{match.ObservationFromUtc:u} → outcome {match.OutcomeFromUtc:u}"));
        }

        Save();
    }

    private static IReadOnlyList<UserChartIndicatorDefinitionV1> LoadUserChartIndicators()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DaxAlgo Terminal",
                "user-indicators.json");
            if (!File.Exists(path))
                return Array.Empty<UserChartIndicatorDefinitionV1>();
            return UserChartIndicatorCatalogV1.Parse(File.ReadAllText(path));
        }
        catch
        {
            return Array.Empty<UserChartIndicatorDefinitionV1>();
        }
    }

    private string BuildInitialAuthoredUnitRequest(string prompt)
    {
        if (!AwaitingAnswer && !IsRetryOnlyRequest(prompt)) return prompt;

        var priorRequest = Messages
            .Reverse()
            .Where(static message => message.IsUser)
            .Select(static message => message.Text.Trim())
            .FirstOrDefault(text =>
                text.Length > 0 &&
                !string.Equals(text, prompt, StringComparison.Ordinal) &&
                !IsRetryOnlyRequest(text));

        return string.IsNullOrWhiteSpace(priorRequest)
            ? prompt
            : $"{priorRequest}\nUser clarification or retry instruction: {prompt}";
    }

    private static bool IsRetryOnlyRequest(string prompt)
    {
        var normalized = prompt.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
        return normalized is "retry" or "retry it" or "try again" or "again";
    }

    private async Task<bool> GenerateAuthoredUnitSourceAsync(
        IStrategyCodegenClient provider,
        AuthoredUnitSpecificationV1 specification,
        CancellationToken cancellationToken = default)
    {
        if (_authoredUnitSourceGenerator is null)
        {
            AiStatus = "The authored-unit source generator is not registered. The validated specification was kept, but no code was generated.";
            Append(AuthoringMessage.Tool("Fail", $"{specification.Kind} source unavailable", AiStatus));
            return false;
        }

        var interfaceName = specification.Kind == AuthoredUnitKindV1.Visualizer
            ? "IVisualizer"
            : "IStrategyKernel";
        AiStatus = $"Generating {interfaceName} source bound to the reviewed intent, assets, feeds, timeframe, and chart layers…";
        var result = await _authoredUnitSourceGenerator.GenerateAsync(
            provider,
            new AuthoredUnitSourceGenerationRequestV1(specification),
            cancellationToken);
        InputTokens += result.Usage.InputTokens;
        OutputTokens += result.Usage.OutputTokens;
        CachedTokens += result.Usage.CachedInputTokens;

        if (!result.Success || result.Script is null)
        {
            var detail = string.Join(Environment.NewLine, result.Issues.Select(issue =>
                $"{issue.Path}: {issue.Message}"));
            AiStatus = $"Generated {specification.Kind.ToString().ToLowerInvariant()} source did not preserve the frozen specification. Nothing was compiled.";
            Append(AuthoringMessage.Tool("Fail", $"{specification.Kind} source rejected", detail));
            return false;
        }

        SetFiles(result.Script.Files);
        _filesEditedByUser = false;
        HasDetachedImplementationSource = false;
        CompiledOk = false;
        IsRegistered = false;
        Append(AuthoringMessage.Tool(
            "Ok",
            "Specification-bound source generated",
            $"{result.Script.Files.Count} C# file(s) · {result.SpecificationHashSha256![..12]}… · review and compile before registration."));
        AiStatus = $"The requested {specification.Kind.ToString().ToLowerInvariant()} source is ready. Review it, then Compile & Register.";
        NotifyEditorFileModeChanged();
        return true;
    }

    private IReadOnlyList<AuthoredUnitInstrumentCandidateV1> BuildAuthoredUnitInstrumentCandidates(string prompt)
    {
        if (_instrumentRegistry is null) return [];

        var selectedIds = ChartPatternSelections
            .Select(static item => item.Match.InstrumentId)
            .ToHashSet();
        var normalizedPrompt = NormalizeInstrumentText(prompt);
        var brokers = Enum.GetValues<BrokerKind>();

        return _instrumentRegistry.All()
            .Select(instrument => new
            {
                Instrument = instrument,
                Brokers = brokers.Where(broker =>
                    !string.IsNullOrWhiteSpace(_instrumentRegistry.ToBrokerSymbol(instrument.Id, broker))).ToArray(),
            })
            .Where(static item => item.Brokers.Length > 0)
            .OrderBy(item => selectedIds.Contains(item.Instrument.Id) ? 0 :
                normalizedPrompt.Contains(NormalizeInstrumentText(item.Instrument.CanonicalSymbol), StringComparison.Ordinal)
                    ? 1
                    : 2)
            .ThenBy(static item => item.Instrument.CanonicalSymbol, StringComparer.OrdinalIgnoreCase)
            .Take(AuthoredUnitSpecificationGeneratorV1.MaxAvailableInstruments)
            .Select(static item => new AuthoredUnitInstrumentCandidateV1(
                item.Instrument.Id,
                item.Instrument.CanonicalSymbol,
                item.Instrument.AssetClass,
                item.Instrument.Exchange,
                item.Instrument.Currency,
                item.Brokers))
            .ToArray();
    }

    private static string NormalizeInstrumentText(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private async Task SendCandidateTurnAsync(AiProviderChoice choice, string prompt)
    {
        var turnStrategyId = StrategyId.Trim();
        var turnEpoch = Interlocked.Increment(ref _generationContextEpoch);
        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        Activity.Clear();
        Diagnostics.Clear();
        CompiledOk = false;
        AwaitingAnswer = false;
        IsGenerating = true;
        AiStatus = CurrentCandidate is null
            ? "Understanding the strategy and identifying the choices that matter…"
            : "Applying your clarification to a new candidate revision…";

        _generateCts?.Cancel();
        _generateCts?.Dispose();
        var turnCts = new CancellationTokenSource();
        _generateCts = turnCts;
        var ticking = TickElapsedAsync(turnCts.Token);

        try
        {
            var session = EnsureGenerationSession(choice);
            var result = await session.SendAsync(
                prompt,
                turnCts.Token,
                ChartReferences.Select(static item => item.Reference).ToArray(),
                ChartReferenceInspections.ToArray(),
                AuthoredUnitSpecification?.ReferenceResolutions,
                ChartPatternSelections.ToArray());
            if (!IsGenerationContextCurrent(turnEpoch, turnStrategyId)) return;
            InputTokens += result.Usage.InputTokens;
            OutputTokens += result.Usage.OutputTokens;
            CachedTokens += result.Usage.CachedInputTokens;

            if (!result.Success)
            {
                var detail = string.Join(Environment.NewLine, result.Issues
                    .Where(issue => issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error)
                    .Select(issue => $"{issue.Path}: {issue.Message}"));
                AiStatus = "The strategy proposal did not pass the generation contract. Nothing was compiled.";
                Append(AuthoringMessage.Tool("Fail", "Candidate not accepted", detail));
                return;
            }

            var candidate = result.Candidate!;
            var assessment = result.Assessment!;
            _fourLaneStrategyBrief = null;
            SetPendingFourLanePrompt(null);
            ClearParallelCandidates();
            ApplyCandidate(candidate, assessment);
            Append(new AuthoringMessage(CodegenRole.Assistant, FormatCandidate(candidate, assessment)));
            Append(AuthoringMessage.Tool(
                "Ok",
                $"Candidate revision {candidate.Revision}",
                $"{result.AgentRuns.Count} agent run(s) · hash {StrategyCandidateCanonicalJsonV1.Hash(candidate)[..12]}…"));

            AwaitingAnswer = HasUserChoice(assessment);
            AiStatus = CandidateStatusText;
            WorkbenchTab = 3;
            _logger.LogInformation(
                "Strategy candidate turn for {Id}: revision {Revision}, agents={Agents}, confirmable={Confirmable}, lowerable={Lowerable}",
                StrategyId,
                candidate.Revision,
                result.AgentRuns.Count,
                CanConfirmCandidate,
                assessment.CanLower);
        }
        catch (OperationCanceledException)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId)) AiStatus = "Stopped.";
        }
        catch (Exception ex)
        {
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                _logger.LogError(ex, "Strategy candidate generation threw for {Id}", turnStrategyId);
                AiStatus = $"Candidate generation error: {ex.Message}";
                Append(AuthoringMessage.System(AiStatus));
            }
        }
        finally
        {
            turnCts.Cancel();
            await ticking;
            if (IsGenerationContextCurrent(turnEpoch, turnStrategyId))
            {
                IsGenerating = false;
                ElapsedText = null;
                ElapsedCompact = null;
                Save();
            }
            if (ReferenceEquals(_generateCts, turnCts)) _generateCts = null;
            turnCts.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmCandidateAction))]
    private void ConfirmCandidate()
    {
        if (CurrentCandidate is null || CandidateContentHash is null) return;

        var result = _generationSession is null
            ? StrategyCandidateConfirmationV1.Confirm(CurrentCandidate, CandidateContentHash)
            : _generationSession.Confirm(CandidateContentHash);
        if (!result.Success)
        {
            var detail = string.Join(Environment.NewLine, result.Issues.Select(issue =>
                $"{issue.Path}: {issue.Message}"));
            AiStatus = "This candidate still needs a choice or has changed since you reviewed it.";
            Append(AuthoringMessage.Tool("Fail", "Cannot confirm candidate", detail));
            return;
        }

        ApplyCandidate(result.Candidate!, result.Assessment!);
        BeginStrategyIntentReview();
        AwaitingAnswer = false;
        Append(AuthoringMessage.Tool(
            "Ok",
            $"Meaning confirmed · revision {result.Candidate!.Revision}",
            result.Assessment!.CanLower
                ? "All required build support is present; the confirmed candidate may enter executable lowering."
                : "The strategy meaning is accepted. Missing data or implementation remains visible below; no code was generated."));
        AiStatus = CandidateStatusText;
        Save();
    }

    private bool CanConfirmCandidateAction() => CanConfirmCandidate && !IsGenerating;

    private void ApplyCandidate(StrategyCandidateV1 candidate, StrategyCandidateAssessmentV1 assessment)
    {
        CandidateRestoreWarning = null;
        CurrentCandidate = candidate;
        CandidateAssessment = assessment;
        CandidateContentHash = StrategyCandidateCanonicalJsonV1.Hash(candidate);

        CandidateGroups.Clear();
        foreach (var row in FlattenCandidateGroups(candidate.Groups)) CandidateGroups.Add(row);

        CandidateOpenQuestions.Clear();
        foreach (var question in candidate.Groups
                     .SelectMany(FlattenCandidateGroupsRaw)
                     .SelectMany(static group => group.Statements)
                     .Where(static statement => statement.Kind == StrategyCandidateStatementKindV1.Question &&
                                                statement.State == StrategyCandidateStatementStateV1.Open))
            CandidateOpenQuestions.Add(question);

        CandidateBuildSupport.Clear();
        foreach (var support in candidate.BuildSupport)
            CandidateBuildSupport.Add(new StrategyBuildSupportRow(
                support.Description,
                SupportLabel(support.Status),
                support.Detail,
                support.RequiredForLowering));

        CandidateIssues.Clear();
        foreach (var issue in assessment.Issues) CandidateIssues.Add(issue);

        var canConfirm = StrategyCandidateConfirmationV1.Confirm(candidate, CandidateContentHash).Success;
        CandidateStatusText = assessment.CanLower
            ? "Confirmed and ready for executable lowering."
            : candidate.Status == StrategyCandidateStatusV1.Confirmed
                ? "Strategy meaning confirmed. Data or implementation is still missing; no executable strategy was produced."
                : HasUserChoice(assessment)
                    ? "Answer the open choices, then review the next candidate revision."
                    : canConfirm
                        ? "Review the exact rules and press Confirm meaning."
                        : "Review the candidate details and resolve the remaining items.";

        OnPropertyChanged(nameof(CanConfirmCandidate));
        ConfirmCandidateCommand.NotifyCanExecuteChanged();
    }

    private StrategyGenerationSessionV1 EnsureGenerationSession(AiProviderChoice choice)
    {
        var provider = ResolveClient(choice) ?? choice.Client;
        var providerKey = $"{provider.ProviderId}\u001f{provider.Model}\u001f{provider.Effort}";
        if (_generationSession is not null && string.Equals(_generationProviderKey, providerKey, StringComparison.Ordinal))
            return _generationSession;

        _generationSession = new StrategyGenerationSessionV1(
            _candidateGenerator!,
            provider,
            $"strategy-generation/{StrategyId.Trim()}",
            DisplayName.Trim(),
            CurrentCandidate?.CandidateId ?? StrategyId.Trim(),
            CurrentCandidate is null ? [] : [CurrentCandidate]);
        _generationProviderKey = providerKey;
        return _generationSession;
    }

    private static bool HasUserChoice(StrategyCandidateAssessmentV1 assessment) =>
        assessment.Issues.Any(static issue => issue.Scope == StrategyCandidateIssueScopeV1.Confirmation &&
            issue.Code is "CANDIDATE_QUESTION_OPEN" or "CANDIDATE_BUILD_SUPPORT_INCOMPLETE");

    private static string FormatCandidate(
        StrategyCandidateV1 candidate,
        StrategyCandidateAssessmentV1 assessment)
    {
        var rules = candidate.Groups.SelectMany(FlattenCandidateGroupsRaw)
            .SelectMany(static group => group.Statements)
            .Where(static statement => statement.Kind is StrategyCandidateStatementKindV1.Rule or
                StrategyCandidateStatementKindV1.Constraint)
            .Select(static statement => $"• {statement.Text}")
            .ToArray();
        var questions = candidate.Groups.SelectMany(FlattenCandidateGroupsRaw)
            .SelectMany(static group => group.Statements)
            .Where(static statement => statement.Kind == StrategyCandidateStatementKindV1.Question &&
                                       statement.State == StrategyCandidateStatementStateV1.Open)
            .Select(static statement => $"• {statement.Text}")
            .ToArray();
        var missing = candidate.BuildSupport
            .Where(static item => item.Status != StrategyBuildSupportStatusV1.Supported)
            .Select(static item => $"• {item.Description}: {SupportLabel(item.Status)} — {item.Detail}")
            .ToArray();

        var builder = new StringBuilder()
            .AppendLine(candidate.Title)
            .AppendLine()
            .AppendLine("Interpretation")
            .AppendLine(candidate.Interpretation.Summary);
        if (rules.Length > 0)
            builder.AppendLine().AppendLine("Rules").AppendLine(string.Join(Environment.NewLine, rules));
        if (questions.Length > 0)
            builder.AppendLine().AppendLine("Your choices").AppendLine(string.Join(Environment.NewLine, questions));
        if (missing.Length > 0)
            builder.AppendLine().AppendLine("What is missing").AppendLine(string.Join(Environment.NewLine, missing));
        if (!HasUserChoice(assessment))
            builder.AppendLine().AppendLine("Next").AppendLine("Review the Candidate tab and confirm the exact meaning.");
        return builder.ToString().TrimEnd();
    }

    private static IEnumerable<StrategyCandidateGroupRow> FlattenCandidateGroups(
        IReadOnlyList<StrategyCandidateGroupV1> groups,
        int depth = 0)
    {
        foreach (var group in groups)
        {
            yield return new StrategyCandidateGroupRow(depth, group.Kind.ToString(), group.Title, group.Summary, group.Statements);
            foreach (var child in FlattenCandidateGroups(group.Children, depth + 1)) yield return child;
        }
    }

    private static IEnumerable<StrategyCandidateGroupV1> FlattenCandidateGroupsRaw(
        StrategyCandidateGroupV1 group)
    {
        yield return group;
        foreach (var child in group.Children.SelectMany(FlattenCandidateGroupsRaw)) yield return child;
    }

    private static string SupportLabel(StrategyBuildSupportStatusV1 status) => status switch
    {
        StrategyBuildSupportStatusV1.NeedsUserChoice => "needs your choice",
        StrategyBuildSupportStatusV1.NeedsImplementation => "needs implementation",
        StrategyBuildSupportStatusV1.DataUnavailable => "data unavailable",
        StrategyBuildSupportStatusV1.Unknown => "not checked yet",
        _ => "supported",
    };

    /// <summary>
    /// One streamed event, on the UI context (<see cref="Progress{T}"/> marshals it). Text grows the
    /// assistant's bubble as it is written — this is the whole point of streaming, and the difference
    /// between watching a strategy get written and staring at a spinner for four minutes.
    /// </summary>
    private void OnStreamed(CodegenEvent evt, CodegenUsage tokensBefore)
    {
        switch (evt)
        {
            case CodegenEvent.TextDelta delta:
                if (_streamingReply is null)
                {
                    _streamingReply = new AuthoringMessage(CodegenRole.Assistant, delta.Text);
                    Append(_streamingReply);
                }
                else
                {
                    _streamingReply.Text += delta.Text;
                }
                break;

            case CodegenEvent.UsageUpdate update:
                // The update is absolute for the CURRENT generation, so add it to what the session had
                // banked before this turn. The exact total is set from the session when the turn ends.
                InputTokens = tokensBefore.InputTokens + update.Usage.InputTokens;
                OutputTokens = tokensBefore.OutputTokens + update.Usage.OutputTokens;
                CachedTokens = tokensBefore.CachedInputTokens + update.Usage.CachedInputTokens;
                break;
        }
    }

    /// <summary>Ticks the elapsed clock on the UI context until the turn ends or the user stops it.</summary>
    private async Task TickElapsedAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var elapsed = DateTime.UtcNow - started;
                ElapsedCompact = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
                ElapsedText = elapsed.TotalSeconds < 60
                    ? $"{elapsed.TotalSeconds:0}s elapsed…"
                    : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s elapsed — a detailed brief at a high effort takes minutes.";
            }
        }
        catch (OperationCanceledException)
        {
            // The turn finished (or was stopped) — nothing to report.
        }
    }

    private bool CanStop => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (!IsGenerating) return;
        var pendingFourLanePrompt = GenerateCandidateFirst ? _pendingFourLanePrompt : null;
        InvalidateGenerationContext();
        if (!string.IsNullOrWhiteSpace(pendingFourLanePrompt) && string.IsNullOrWhiteSpace(Composer))
            Composer = pendingFourLanePrompt;
        AiStatus = "Stopped. Late provider output will be ignored.";
        Save();
    }

    /// <summary>Start over with a fresh identity, model thread, starter catalog, and editor template.
    /// The previous chat is not deleted; it remains in the session rail under its saved strategy id.</summary>
    [RelayCommand]
    private void NewChat()
    {
        InvalidateGenerationContext();
        Save();   // bank the outgoing conversation before abandoning it

        _restoring = true;
        try
        {
            ResetSession(null);
            _restoredThread = null;
            _restoredUsage = null;
            ClearCandidate();
            StrategyId = DefaultStrategyId;
            DisplayName = DefaultDisplayName;
            GenerateCandidateFirst = true;
            ChartReferences.Clear();
            ChartReferenceInspections.Clear();
            ChartPatternSearchResult = null;
            ChartPatternSelections.Clear();
            ResearchDatasetDefinition = null;
            PendingResearchChartSelection = null;
            PendingResearchOverlayIds = Array.Empty<string>();
            PendingResearchIndicatorBindings = Array.Empty<ResearchIndicatorBindingV1>();
            PendingResearchCondition = null;
            ResearchConditionSearchResult = null;
            ResearchExperimentEvidence = null;
            ResearchOutcomeGalleryResult = null;
            _researchAnalysisReferences.Clear();
            NotifyResearchFindingsChanged();
            PendingStrategyDraft = null;
            AuthoredUnitSpecification = null;
            ConfirmedStrategyIntent = null;
            ConfirmedStrategyIntentHash = null;
            HasResearchDesignHandoff = false;
            OnPropertyChanged(nameof(HasChartReferences));
            NotifyDesignInspectorLayoutChanged();
            OnPropertyChanged(nameof(HasChartReferenceInspections));
            OnPropertyChanged(nameof(HasUninspectedChartReferences));
            OnPropertyChanged(nameof(HasSelectedChartPattern));
            OnPropertyChanged(nameof(SelectedChartPatternText));
            OnPropertyChanged(nameof(HasSearchableChartReference));
            OnPropertyChanged(nameof(ChartReferenceSummary));
            Composer = string.Empty;
            StarterSearchText = string.Empty;
            SelectedStarterFamily = AllStarterFamilies;
            SelectedStarterHorizon = AllStarterHorizons;
            SelectedStarterData = AllStarterData;
            RefreshStarterBriefs();
            Messages.Clear();
            Activity.Clear();
            Tasks.Clear();
            _taskBrief = _taskSkills = _taskGenerate = _taskCompile = _taskAutoFix = _taskReview = _taskSmoke = null;
            _reviewCardEmitted = _smokeCardEmitted = false;
            Diagnostics.Clear();
            SelectedDiagnostic = null;
            InputTokens = OutputTokens = CachedTokens = 0;
            CompiledOk = false;
            AwaitingAnswer = false;
            IsRegistered = false;
            WorkbenchTab = 3;
            ActiveScreen = IsResearchStudioShell
                ? StrategyAuthoringScreen.Research
                : StrategyAuthoringScreen.Design;
            ResetStrategyWorkspace();
            SelectedSavedSession = null;
            CloseReview();
            _registeredBaseline.Clear();
            RefreshWorkStatus();
            Parameters = null;
            SetFiles([new StrategyFile(StrategyFile.DefaultName, TemplateSource)]);
            HasDetachedImplementationSource = false;
            _filesEditedByUser = false;
            AiStatus = null;
            Status = IsResearchStudioShell
                ? "New research. Open the chart, inspect events, and save findings — no strategy required."
                : "Design is open — edit entry, exit, sizing, risk, and order rules. Research Studio is optional.";
        }
        finally
        {
            _restoring = false;
        }
    }

    // ── Saved sessions ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Every strategy the user has an authoring chat for, newest first.</summary>
    public ObservableCollection<AuthoringSessionSnapshot> SavedSessions { get; } = [];

    [ObservableProperty] private AuthoringSessionSnapshot? _selectedSavedSession;

    partial void OnSelectedSavedSessionChanged(AuthoringSessionSnapshot? value)
    {
        if (_restoring || value is null) return;
        // Always restore on an explicit picker selection. Blank Research starts share the default
        // strategy id with unsaved research sessions, so StrategyId equality alone is not enough.
        Restore(value);
    }

    /// <summary>Forget a strategy's chat. The strategy itself (if it was registered) is untouched — this
    /// deletes the conversation, not the plugin.</summary>
    [RelayCommand]
    private void DeleteSavedSession(AuthoringSessionSnapshot? session)
    {
        if (session is null) return;

        _sessionRepository.Delete(session.StrategyId);
        RefreshSavedSessions();
        Status = $"Deleted the chat for '{session.DisplayName}'. The strategy itself is untouched.";
    }

    private void RefreshSavedSessions()
    {
        var saved = _sessionRepository.List();
        var previousId = SelectedSavedSession?.StrategyId;

        _restoring = true;   // repopulating the list re-fires the selection binding
        try
        {
            SavedSessions.Clear();
            foreach (var session in saved) SavedSessions.Add(session);
            // Keep the picker highlight only for an already-selected session. Never auto-select a
            // saved chat that shares the default strategy id with a blank Research launch.
            SelectedSavedSession = previousId is null
                ? null
                : SavedSessions.FirstOrDefault(session => session.StrategyId == previousId);
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Loads a saved session back into the pane — the chat, the files, the provider setup, the
    /// token total, AND the model's own thread, so a follow-up like "now tighten the stop" still works.</summary>
    private void Restore(AuthoringSessionSnapshot session)
    {
        InvalidateGenerationContext();
        _restoring = true;
        string? restoreWarning = null;
        var candidateBatchRejected = false;
        try
        {
            _session = null;
            _generationSession = null;
            _generationProviderKey = null;
            _restoredThread = session.Thread;
            _restoredUsage = new CodegenUsage(session.InputTokens, session.OutputTokens);

            StrategyId = session.StrategyId;
            DisplayName = session.DisplayName;

            // Provider-independent: the pipeline effort comes back even when the provider doesn't.
            // Absent on a pre-build-effort snapshot ⇒ Standard.
            BuildEffort = StrategyBuildEfforts.Parse(session.BuildEffort);

            if (session.ProviderId is { Length: > 0 } providerId &&
                AiProviders.FirstOrDefault(p => p.ProviderId == providerId) is { } provider)
            {
                SelectedAiProvider = provider;
                if (session.Model is { Length: > 0 } model)
                {
                    if (!Models.Contains(model)) Models.Insert(0, model);
                    SelectedModel = model;
                }
                SelectedEffort = CodegenEfforts.Parse(session.Effort);
            }

            Messages.Clear();
            foreach (var entry in session.Chat)
                Append(FromChatEntry(entry));

            if (session.Files.Count > 0) SetFiles(session.Files);

            InputTokens = session.InputTokens;
            OutputTokens = session.OutputTokens;
            Diagnostics.Clear();
            Activity.Clear();
            Tasks.Clear();
            Parameters = null;
            CompiledOk = false;
            AwaitingAnswer = false;
            // The old snapshot stores only a Boolean, not a live registry receipt bound to the exact
            // files. Restoring it could claim runnable state after an in-memory-only install vanished.
            IsRegistered = false;
            if (session.Registered)
                restoreWarning = "The chat was restored, but historical registration was cleared because no live, file-hash-bound registration proof exists.";
            GenerateCandidateFirst = session.FourLaneGenerationEnabled;
            ChartReferences.Clear();
            foreach (var reference in session.ChartReferences ?? [])
            {
                if (_chartReferenceRepository.Exists(reference))
                {
                    ChartReferences.Add(reference);
                }
                else
                {
                    restoreWarning =
                        $"The session was restored, but reference chart '{reference.OriginalFileName}' " +
                        "is missing or no longer matches its saved SHA-256 hash. It was detached.";
                    _logger.LogWarning(
                        "Detached missing or modified reference chart {ReferenceId} from {Id}",
                        reference.Reference.ReferenceId,
                        session.StrategyId);
                }
            }
            OnPropertyChanged(nameof(HasChartReferences));
            NotifyDesignInspectorLayoutChanged();
            OnPropertyChanged(nameof(ChartReferenceSummary));
            ChartReferenceInspections.Clear();
            foreach (var inspection in session.ChartReferenceInspections ?? [])
            {
                if (ChartReferences.Any(reference =>
                        string.Equals(reference.Reference.ReferenceId, inspection.ReferenceId, StringComparison.Ordinal) &&
                        string.Equals(reference.Reference.ContentHashSha256, inspection.ContentHashSha256, StringComparison.Ordinal)))
                    ChartReferenceInspections.Add(inspection);
            }
            OnPropertyChanged(nameof(HasChartReferenceInspections));
            OnPropertyChanged(nameof(HasUninspectedChartReferences));
            ChartPatternSearchResult = session.ChartPatternSearchResult is { } savedSearch &&
                ChartReferences.Any(reference =>
                    string.Equals(reference.Reference.ReferenceId, savedSearch.ReferenceId, StringComparison.Ordinal) &&
                    string.Equals(reference.Reference.ContentHashSha256, savedSearch.ReferenceContentHashSha256, StringComparison.Ordinal))
                    ? savedSearch
                    : null;
            ChartPatternSelections.Clear();
            foreach (var selection in session.ChartPatternSelections ?? [])
            {
                if (ChartPatternSearchResult is { } restoredSearch &&
                    string.Equals(selection.ReferenceId, restoredSearch.ReferenceId, StringComparison.Ordinal) &&
                    string.Equals(selection.ReferenceContentHashSha256, restoredSearch.ReferenceContentHashSha256, StringComparison.Ordinal) &&
                    restoredSearch.Matches.Contains(selection.Match))
                    ChartPatternSelections.Add(selection);
            }
            OnPropertyChanged(nameof(HasSelectedChartPattern));
            OnPropertyChanged(nameof(SelectedChartPatternText));
            AuthoredUnitSpecification = RestoreAuthoredUnitSpecification(session, ChartReferences, ref restoreWarning);
            RestoreResearchDataset(session, ref restoreWarning);
            RestoreResearchExperiment(session, ref restoreWarning);
            RestoreStrategyDraft(session, ref restoreWarning);
            ClearCandidate();
            _fourLaneStrategyBrief = !string.IsNullOrWhiteSpace(session.FourLaneStrategyBrief) ||
                                     !string.IsNullOrWhiteSpace(session.ParallelCandidateBatchJson)
                ? RecoverFourLaneStrategyBrief(session)
                : null;
            SetPendingFourLanePrompt(session.PendingFourLanePrompt);
            RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
            if (!string.IsNullOrWhiteSpace(session.CandidateJson))
            {
                try
                {
                    var restoredCandidate = StrategyCandidateCanonicalJsonV1.Deserialize(session.CandidateJson);
                    if (!string.Equals(restoredCandidate.CandidateId, session.StrategyId, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Candidate '{restoredCandidate.CandidateId}' does not belong to session '{session.StrategyId}'.");
                    ApplyCandidate(restoredCandidate, StrategyCandidateValidatorV1.Assess(restoredCandidate));
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not restore strategy candidate for {Id}", session.StrategyId);
                }
            }
            RestoreStrategyIntentReview(session);
            if (!string.IsNullOrWhiteSpace(session.ParallelCandidateBatchJson))
            {
                var recoveryPrompt = RecoverParallelCandidatePrompt(session);
                try
                {
                    var restoredBatch = StrategyGenerationCandidateCanonicalJsonV1.DeserializeBatch(
                        session.ParallelCandidateBatchJson);
                    if (IsBacktestNavigationIntent(restoredBatch.UserPrompt))
                        throw new InvalidOperationException(
                            "The saved candidate batch was generated from a backtest navigation request instead of a strategy brief.");
                    if (!string.IsNullOrWhiteSpace(_fourLaneStrategyBrief) &&
                        !string.Equals(
                            restoredBatch.UserPrompt,
                            _fourLaneStrategyBrief,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The saved candidate batch is bound to an older strategy brief than the durable session brief.");
                    }
                    if (string.IsNullOrWhiteSpace(recoveryPrompt) &&
                        !string.IsNullOrWhiteSpace(restoredBatch.UserPrompt))
                        recoveryPrompt = restoredBatch.UserPrompt;
                    ApplyParallelCandidateBatch(restoredBatch, restored: true);

                    // The editor base survives hand-edits even after the selected-candidate proof
                    // is cleared. Persist it separately so an invalid edit can be repaired and revalidated
                    // after a restart without rebinding it to whichever row happens to be selected.
                    var restoredEditorBaseHash = session.EditorBaseParallelCandidateHash
                        ?? session.SelectedParallelCandidateHash;
                    if (CanEnterFourLaneConformance &&
                        !string.IsNullOrWhiteSpace(restoredEditorBaseHash))
                    {
                        var editorMatches = restoredBatch.Lanes.Where(lane => lane.Candidate is not null &&
                            string.Equals(
                                lane.CandidateHashSha256,
                                restoredEditorBaseHash,
                                StringComparison.Ordinal)).ToArray();
                        if (editorMatches.Length == 1)
                        {
                            // This hash records where the editor content came from; it is not a proof
                            // that hand-edited content still equals that candidate. Keep the origin so an
                            // edited draft can be revalidated or detached after a strategy change, while
                            // restoring ChosenGeneratedCandidateHash below only on an exact content match.
                            SetEditorBaseGeneratedCandidateHash(restoredEditorBaseHash);
                            SelectedGeneratedCandidateOption = GeneratedCandidateOptions.FirstOrDefault(option =>
                                string.Equals(
                                    option.CandidateHashSha256,
                                    restoredEditorBaseHash,
                                    StringComparison.Ordinal));
                            if (!EditorMatchesCandidate(editorMatches[0].Candidate!))
                            {
                                restoreWarning =
                                    "The edited candidate source was restored with its original lane provenance, but its selected-candidate proof remains cleared until revalidation.";
                            }
                        }
                        else
                        {
                            restoreWarning = "The chat was restored, but the saved candidate editor origin did not identify exactly one restored artifact and was cleared.";
                            _logger.LogWarning(
                                "Cleared restored editor-base candidate hash for {Id}: origin does not identify exactly one artifact",
                                session.StrategyId);
                        }
                    }

                    if (CanEnterFourLaneConformance &&
                        !string.IsNullOrWhiteSpace(session.SelectedParallelCandidateHash) &&
                        StrategyGenerationBatchValidationV1.Select(
                            restoredBatch,
                            session.SelectedParallelCandidateHash) is { Success: true } selection)
                    {
                        if (string.Equals(
                                _editorBaseGeneratedCandidateHash,
                                selection.CandidateHashSha256,
                                StringComparison.Ordinal) &&
                            EditorMatchesCandidate(selection.Candidate!))
                        {
                            ChosenGeneratedCandidateHash = selection.CandidateHashSha256;
                            SelectedGeneratedCandidateOption = GeneratedCandidateOptions.FirstOrDefault(option =>
                                string.Equals(
                                    option.CandidateHashSha256,
                                    selection.CandidateHashSha256,
                                    StringComparison.Ordinal));
                        }
                        else
                        {
                            restoreWarning = "The chat was restored, but the saved selected-candidate proof did not match the editor and was cleared.";
                            _logger.LogWarning(
                                "Cleared restored parallel candidate hash for {Id}: editor files do not match the selected artifact",
                                session.StrategyId);
                        }
                    }
                }
                catch (Exception exception)
                {
                    // ApplyParallelCandidateBatch may have populated cards before a later editor-proof
                    // restore fails. Never leave those cards visible while claiming they were discarded.
                    ClearParallelCandidates();
                    candidateBatchRejected = true;
                    if (!string.IsNullOrWhiteSpace(recoveryPrompt)) Composer = recoveryPrompt;
                    CandidateRestoreWarning =
                        "Saved candidate results no longer match the current validation contract. " +
                        "Your chat and code were kept, but the old candidate choices were discarded. " +
                        (string.IsNullOrWhiteSpace(recoveryPrompt)
                            ? "Paste or refine the strategy brief in the composer, then review and reconfirm the recovered strategy request."
                            : "The original brief is loaded in the composer; review it, then reconfirm the recovered strategy request before implementation.");
                    restoreWarning = CandidateRestoreWarning;
                    WorkbenchTab = 3;
                    _logger.LogWarning(exception, "Could not restore parallel strategy candidates for {Id}", session.StrategyId);
                }
            }
            if (GenerateCandidateFirst && !string.IsNullOrWhiteSpace(_pendingFourLanePrompt))
                Composer = _pendingFourLanePrompt;
            HasDetachedImplementationSource = session.HasDetachedImplementationSource ||
                candidateBatchRejected &&
                GenerateCandidateFirst &&
                Files.Count > 0 &&
                Files.All(static file => file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            EditorOriginatedFromCombinedTradeIr = session.EditorOriginatedFromCombinedTradeIr;
            ActiveScreen = GenerateCandidateFirst &&
                           session.ActiveScreen == StrategyAuthoringScreen.Build && CanEnterFourLaneConformance
                ? StrategyAuthoringScreen.Build
                : session.ActiveScreen is StrategyAuthoringScreen.Brief or StrategyAuthoringScreen.Design
                    ? session.ActiveScreen
                    : session.ActiveScreen == StrategyAuthoringScreen.Research && IsResearchStudioShell
                        ? StrategyAuthoringScreen.Research
                        : StrategyAuthoringScreen.Design;
            CoerceLegacyExecutionFidelitySettings();
            RestoreStrategyWorkspace(session, ref restoreWarning);
            WorkbenchTab = GenerateCandidateFirst ? 3 : 0;
            CloseReview();
            _registeredBaseline.Clear();   // the diff baseline is per-process; a restored review starts from "all new"
            _filesEditedByUser = false;

            SelectedSavedSession = SavedSessions.FirstOrDefault(s => s.StrategyId == session.StrategyId);
            Status = restoreWarning ?? (Messages.Count > 0
                ? $"Restored the chat for '{session.DisplayName}' ({session.Age}). Carry on where you left off."
                : "Describe the idea to check four strategy-generation lanes, or switch to Expert Code for direct C# authoring.");
        }
        finally
        {
            _restoring = false;
        }
    }

    private static string? RecoverParallelCandidatePrompt(AuthoringSessionSnapshot session) =>
        RecoverFourLaneStrategyBrief(session);

    private AuthoredUnitSpecificationV1? RestoreAuthoredUnitSpecification(
        AuthoringSessionSnapshot session,
        IReadOnlyCollection<AuthoringChartReferenceSnapshot> restoredReferences,
        ref string? restoreWarning)
    {
        if (string.IsNullOrWhiteSpace(session.AuthoredUnitSpecificationJson)) return null;

        try
        {
            var specification = AuthoredUnitSpecificationCanonicalJsonV1.Deserialize(
                session.AuthoredUnitSpecificationJson);
            var issues = AuthoredUnitSpecificationValidatorV1.Validate(specification);
            if (issues.Count > 0)
                throw new InvalidOperationException(string.Join("; ", issues.Select(static issue => issue.Message)));

            var attached = restoredReferences
                .Select(static item => item.Reference.ContentHashSha256)
                .OrderBy(static hash => hash, StringComparer.Ordinal)
                .ToArray();
            var specified = specification.References
                .Select(static item => item.ContentHashSha256)
                .OrderBy(static hash => hash, StringComparer.Ordinal)
                .ToArray();
            if (!attached.SequenceEqual(specified, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    "The authored-unit specification is not bound to the session's exact reference artifacts.");

            return specification;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not restore authored-unit specification for {Id}", session.StrategyId);
            restoreWarning =
                "The chat and valid reference artifacts were restored, but the authored-unit specification " +
                "failed validation and must be generated again.";
            return null;
        }
    }

    private static string? RecoverFourLaneStrategyBrief(AuthoringSessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.FourLaneStrategyBrief))
            return session.FourLaneStrategyBrief.Trim();

        // Read the prompt independently of the typed contract first. This keeps recovery useful when
        // the batch fails specifically because its schema version can no longer be deserialized.
        string? batchPrompt = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(session.ParallelCandidateBatchJson))
            {
                using var document = JsonDocument.Parse(session.ParallelCandidateBatchJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("userPrompt", out var promptElement) &&
                    promptElement.ValueKind == JsonValueKind.String &&
                    promptElement.GetString() is { } persistedPrompt &&
                    !string.IsNullOrWhiteSpace(persistedPrompt))
                {
                    batchPrompt = persistedPrompt.Trim();
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the visible transcript. The invalid batch is intentionally discarded.
        }

        var confirmedParallelPrompts = RecoverConfirmedParallelChatPrompts(session.Chat);
        var fallbackUserPrompts = session.Chat
            .Where(entry => string.Equals(entry.Role, AuthoringChatEntry.User, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(entry.Text) &&
                            !IsBacktestNavigationIntent(entry.Text))
            .Select(static entry => entry.Text.Trim())
            .ToArray();

        if (!string.IsNullOrWhiteSpace(batchPrompt) && !IsBacktestNavigationIntent(batchPrompt))
        {
            // Before the explicit brief field existed, the batch prompt was overwritten by each
            // refinement. When it is visibly the last user turn, rebuild the durable request from the
            // whole strategy-only transcript. Otherwise the batch may itself contain a combined brief.
            if (confirmedParallelPrompts.Length > 1 && string.Equals(
                    batchPrompt,
                    confirmedParallelPrompts[^1],
                    StringComparison.Ordinal))
            {
                return confirmedParallelPrompts.Skip(1).Aggregate(
                    confirmedParallelPrompts[0],
                    CombineFourLaneStrategyBrief);
            }

            return batchPrompt;
        }

        if (!string.IsNullOrWhiteSpace(batchPrompt))
        {
            if (confirmedParallelPrompts.Length > 0)
                return confirmedParallelPrompts.Skip(1).Aggregate(
                    confirmedParallelPrompts[0],
                    CombineFourLaneStrategyBrief);

            // A legacy navigation-corrupted batch has no trustworthy lane provenance. Recover only
            // the nearest prior strategy-like user turn rather than contaminating it with old Expert chat.
            return fallbackUserPrompts.LastOrDefault();
        }

        return null;
    }

    private static string[] RecoverConfirmedParallelChatPrompts(IReadOnlyList<AuthoringChatEntry> chat)
    {
        var confirmed = new List<string>();
        var pendingUsers = new List<string>();
        foreach (var entry in chat)
        {
            if (string.Equals(entry.Role, AuthoringChatEntry.User, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(entry.Text)) pendingUsers.Add(entry.Text.Trim());
                continue;
            }

            if (!string.Equals(entry.Role, AuthoringChatEntry.Assistant, StringComparison.Ordinal))
                continue;

            if (entry.Text.StartsWith("I asked four strategy-generation agents", StringComparison.Ordinal))
            {
                confirmed.AddRange(pendingUsers.Where(static prompt =>
                    !IsBacktestNavigationIntent(prompt)));
            }
            pendingUsers.Clear();
        }
        return [.. confirmed];
    }

    /// <summary>Writes the current session out. Called after anything worth not losing: a turn, a compile,
    /// an edit. Cheap — a chat is a few KB of JSON.</summary>
    private void Save()
    {
        if (_restoring || !_ready || string.IsNullOrWhiteSpace(StrategyId)) return;
        if (Messages.Count == 0 && !_filesEditedByUser && StrategyIntentDraft is null &&
            ChartReferences.Count == 0 && AuthoredUnitSpecification is null && ResearchDatasetDefinition is null &&
            PendingStrategyDraft is null && PendingResearchCondition is null &&
            PendingResearchChartSelection is null && _researchAnalysisReferences.Count == 0)
            return;   // nothing worth a file yet

        SynchronizeStrategyWorkspace();

        var snapshot = new AuthoringSessionSnapshot(
            StrategyId: StrategyId.Trim(),
            DisplayName: DisplayName.Trim(),
            Chat: [.. Messages.Select(ToChatEntry)],
            // The MODEL's thread, not the chat: it also carries the compiler's auto-fix prompts, which are
            // what let a resumed conversation pick up mid-repair.
            Thread: _session?.Transcript ?? _restoredThread ?? [],
            Files: [.. Files.Select(f => new StrategyFile(f.Name, f.Content))],
            ProviderId: SelectedAiProvider?.ProviderId,
            Model: SelectedModel,
            Effort: SelectedEffort.Wire(),
            BuildEffort: BuildEffort.Wire(),
            InputTokens: InputTokens,
            OutputTokens: OutputTokens,
            Registered: IsRegistered,
            CandidateJson: CurrentCandidate is null ? null : StrategyCandidateCanonicalJsonV1.Serialize(CurrentCandidate),
            GenerateCandidateFirst: GenerateCandidateFirst,
            FourLaneStrategyBrief: _fourLaneStrategyBrief,
            PendingFourLanePrompt: _pendingFourLanePrompt,
            ParallelCandidateBatchJson: _parallelCandidateBatch is null
                ? null
                : StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(
                    WithoutRawParallelResponses(_parallelCandidateBatch)),
            SelectedParallelCandidateHash: ChosenGeneratedCandidateHash,
            EditorBaseParallelCandidateHash: _editorBaseGeneratedCandidateHash,
            ResearchCaseJson: _strategyIntentResearchCase is null
                ? null
                : ResearchCaseCanonicalJsonV1.Serialize(_strategyIntentResearchCase),
            StrategyClassificationJson: _strategyIntentClassification is null
                ? null
                : StrategySpecCanonicalJsonV1.Serialize(_strategyIntentClassification),
            StrategyIntentDraftJson: StrategyIntentDraft is null
                ? null
                : StrategyIntentCanonicalJsonV1.Serialize(StrategyIntentDraft),
            ConfirmedStrategyIntentJson: ConfirmedStrategyIntent is null
                ? null
                : StrategyIntentCanonicalJsonV1.Serialize(ConfirmedStrategyIntent),
            ActiveScreen: ActiveScreen,
            HasDetachedImplementationSource: HasDetachedImplementationSource,
            EditorOriginatedFromCombinedTradeIr: EditorOriginatedFromCombinedTradeIr,
            AuthoringUxVersion: AuthoringSessionSnapshot.CurrentAuthoringUxVersion,
            ChartReferences: [.. ChartReferences],
            AuthoredUnitSpecificationJson: AuthoredUnitSpecification is null
                ? null
                : AuthoredUnitSpecificationCanonicalJsonV1.Serialize(AuthoredUnitSpecification),
            ChartReferenceInspections: [.. ChartReferenceInspections],
            ChartPatternSearchResult: ChartPatternSearchResult,
            ChartPatternSelections: [.. ChartPatternSelections],
            StrategyWorkspaceJson: StrategyWorkspaceCanonicalJsonV1.Serialize(StrategyWorkspace),
            ResearchDatasetJson: ResearchDatasetDefinition is null
                ? null
                : ResearchDatasetCanonicalJsonV1.Serialize(ResearchDatasetDefinition),
            ResearchExperimentJson: ResearchExperimentEvidence is null
                ? null
                : ResearchExperimentCanonicalJsonV1.Serialize(ResearchExperimentEvidence),
            StrategyDraftJson: PendingStrategyDraft is null
                ? null
                : StrategyDraftCanonicalJsonV1.Serialize(PendingStrategyDraft),
            ResearchConditionJson: PendingResearchCondition is null
                ? null
                : ResearchConditionCanonicalJsonV1.Serialize(PendingResearchCondition),
            ResearchConditionSearchResultJson: ResearchConditionSearchResult is null
                ? null
                : ExecutableStrategyDefinitionCanonicalJson.Serialize(ResearchConditionSearchResult),
            ResearchAnalysisReferencesJson: _researchAnalysisReferences.Count == 0
                ? null
                : ResearchAnalysisReferenceCanonicalJsonV1.SerializeMany(
                    _researchAnalysisReferences.Values.OrderBy(static r => r.ReferenceId).ToArray()),
            ResearchChartSelectionJson: PendingResearchChartSelection is null
                ? null
                : ExecutableStrategyDefinitionCanonicalJson.Serialize(PendingResearchChartSelection),
            ResearchIndicatorBindingsJson: PendingResearchIndicatorBindings.Count == 0
                ? null
                : ExecutableStrategyDefinitionCanonicalJson.Serialize(PendingResearchIndicatorBindings));

        if (!_sessionRepository.Save(snapshot))
        {
            _logger.LogWarning("Could not save the authoring chat for {Id}", StrategyId);
            return;
        }

        RefreshSavedSessions();
    }

    // ── Compile & register (the review gate) ────────────────────────────────────────────────────────

    /// <summary>What the review overlay shows per file. The diff baseline is the last content this
    /// process registered for that file (empty ⇒ everything reads as added — honest for new code).</summary>
    public ObservableCollection<ReviewFileEntry> ReviewFiles { get; } = [];

    [ObservableProperty] private ReviewFileEntry? _selectedReviewFile;
    [ObservableProperty] private bool _reviewOpen;
    [ObservableProperty] private string? _reviewSummary;

    /// <summary>Held between a clean compile and the Register click, so registering never re-compiles
    /// different code than the user just reviewed.</summary>
    private StrategyCompileResult? _pendingCompile;
    private AuthoredUnitCompilationResultV1? _pendingAuthoredUnitCompile;
    private StrategyScript? _pendingScript;

    /// <summary>File contents as of the last successful register (per process). Keys are file names.</summary>
    private readonly Dictionary<string, string> _registeredBaseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Step 1 of consent: compile everything and, if clean, open the review overlay — per-file diffs
    /// against what was last registered, plus the diagnostics. Registration itself only happens from
    /// <see cref="ConfirmRegisterCommand"/> inside the overlay; there is no path around the review.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCompileCurrentSourceAction))]
    private void Compile()
    {
        Diagnostics.Clear();
        CompiledOk = false;
        Parameters = null;
        CloseReview();

        var authoredSpecification = AuthoredUnitSpecification;
        if (GenerateCandidateFirst && authoredSpecification is null)
        {
            Status = "Compile and Register is available only after explicitly switching to Expert C#. Strategy Builder artifacts keep their confirmed-request binding and use their own admission paths.";
            return;
        }

        if (Files.Any(static file => !file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Compile is the C# expert-code path. Use the selected artifact's package importer; none is registered for this file type yet.";
            return;
        }

        if (HasDetachedImplementationSource)
        {
            Status = "This source belongs to a previous strategy request. Generate a new bound implementation, or explicitly switch to Expert C# to keep it as an unbound draft.";
            return;
        }

        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            Status = "Give the strategy an id before compiling.";
            return;
        }

        var script = CurrentScript();

        if (authoredSpecification is not null)
        {
            CompileAuthoredUnitForReview(authoredSpecification, script);
            return;
        }

        StrategyCompileResult result;
        try
        {
            result = _compiler.Compile(script);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strategy compile threw for {Id}", StrategyId);
            Status = $"Compiler error: {ex.Message}";
            return;
        }

        foreach (var diagnostic in result.Diagnostics)
            Diagnostics.Add(diagnostic);

        if (!result.Success || result.Option is null)
        {
            // A policy-scan Block comes back as an error diagnostic, so a strategy that reaches for
            // P/Invoke / Process / the registry fails here with a clear reason, just like a plugin.
            Status = $"Compile failed — {result.Errors.Count()} error(s).";
            return;
        }

        CompiledOk = true;
        if (result.Option.HasParameters)
            Parameters = StrategyParametersViewModel.FromSchema(result.Option.Schema);

        ReviewFiles.Clear();
        foreach (var file in script.Files)
        {
            var baseline = _registeredBaseline.GetValueOrDefault(file.Name, string.Empty);
            ReviewFiles.Add(new ReviewFileEntry(file.Name, LineDiff.Build(baseline, file.Content)));
        }

        SelectedReviewFile = ReviewFiles.FirstOrDefault();

        var warnings = result.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning);
        ReviewSummary =
            $"{script.Files.Count} file(s), compiled clean" +
            (warnings > 0 ? $" with {warnings} warning(s)" : string.Empty) +
            ". It runs in-process once registered — read it first.";

        _pendingCompile = result;
        _pendingScript = script;
        ReviewOpen = true;
        ConfirmRegisterCommand.NotifyCanExecuteChanged();
        Status = "Compiled clean — review the code, then press Register.";
    }

    private void CompileAuthoredUnitForReview(
        AuthoredUnitSpecificationV1 specification,
        StrategyScript script)
    {
        if (_authoredUnitCompiler is null)
        {
            Status = "The canonical authored-unit compiler is not registered. The generated source was not loaded.";
            return;
        }

        AuthoredUnitCompilationResultV1 result;
        try
        {
            result = _authoredUnitCompiler.Compile(specification, script);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Authored-unit compile threw for {Id}", specification.UnitId);
            Status = $"Compiler error: {exception.Message}";
            return;
        }

        foreach (var diagnostic in result.Diagnostics)
            Diagnostics.Add(diagnostic);
        if (!result.Success || result.Unit is null)
        {
            Status = $"Compile failed — {result.Errors.Count()} error(s). The authored unit was not loaded.";
            return;
        }

        CompiledOk = true;
        if (!result.Unit.Schema.IsEmpty)
            Parameters = StrategyParametersViewModel.FromSchema(result.Unit.Schema);

        ReviewFiles.Clear();
        foreach (var file in script.Files)
        {
            var baseline = _registeredBaseline.GetValueOrDefault(file.Name, string.Empty);
            ReviewFiles.Add(new ReviewFileEntry(file.Name, LineDiff.Build(baseline, file.Content)));
        }
        SelectedReviewFile = ReviewFiles.FirstOrDefault();

        var warnings = result.Diagnostics.Count(diagnostic =>
            diagnostic.Severity == StrategyDiagnosticSeverity.Warning);
        ReviewSummary =
            $"{script.Files.Count} file(s), exact {specification.Kind} contract verified" +
            (warnings > 0 ? $" with {warnings} warning(s)" : string.Empty) +
            $". Bound to specification {result.Unit.SpecificationHashSha256[..12]}…; registration runs it in-process.";

        _pendingAuthoredUnitCompile = result;
        _pendingScript = script;
        ReviewOpen = true;
        ConfirmRegisterCommand.NotifyCanExecuteChanged();
        Status = "Compiled and specification-verified — review the code, then press Register.";
    }

    private bool CanCompileCurrentSourceAction() => CanCompileCurrentSource;

    /// <summary>Step 2 of consent: the actual registration, only reachable from the review overlay.
    /// The installer makes this a real strategy (backtest registry, catalog card, plugin on disk);
    /// without one (Basic, tests) it falls back to the backtest registry alone.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmRegisterAction))]
    private void ConfirmRegister()
    {
        if (!CanConfirmRegisterAction() || _pendingScript is not { } script)
        {
            CloseReview();
            Status = "Registration review expired because the authored specification or reviewed source changed. Compile and review the current source again.";
            return;
        }

        if (_pendingAuthoredUnitCompile is { Success: true, Unit: not null } authored)
        {
            ConfirmAuthoredUnitRegistration(script, authored.Unit);
            return;
        }

        if (_pendingCompile is not { Option: not null } result)
        {
            CloseReview();
            Status = "Registration review expired because the reviewed source changed. Compile it again.";
            return;
        }

        var warnings = result.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning);
        var caveat = warnings > 0 ? $" {warnings} capability warning(s) in Diagnostics." : string.Empty;

        if (_installer is null)
        {
            _registry.Register(result.Option!);
            Status = $"Registered '{result.Option!.DisplayName}' from {script.Files.Count} file(s) — DEV (unsigned).{caveat}";
        }
        else
        {
            var install = _installer.Install(script, result);
            Status = install.Message + caveat;
            _logger.LogInformation(
                "Authored strategy {Id} installed from {Files} file(s): catalog={InCatalog}",
                result.Option!.Id, script.Files.Count, install.InCatalog);
        }

        _registeredBaseline.Clear();
        foreach (var file in script.Files) _registeredBaseline[file.Name] = file.Content;

        IsRegistered = true;
        Append(AuthoringMessage.Tool("Ok", "Registered", Status ?? "The strategy is registered."));
        CloseReview();
        Save();
    }

    private void ConfirmAuthoredUnitRegistration(
        StrategyScript script,
        CompiledAuthoredUnitV1 unit)
    {
        var specification = AuthoredUnitSpecification;
        if (specification is null ||
            !string.Equals(
                AuthoredUnitSpecificationCanonicalJsonV1.Hash(specification),
                unit.SpecificationHashSha256,
                StringComparison.Ordinal))
        {
            CloseReview();
            Status = "The reviewed specification changed after compilation. Nothing was registered.";
            return;
        }

        if (AuthoredUnitParameterPresenceRulesV1.TryDescribeUnsetRequired(specification.Parameters, out var requiredMessage))
        {
            CloseReview();
            Status = requiredMessage;
            AiStatus = requiredMessage;
            return;
        }

        if (unit.Kind == AuthoredUnitKindV1.Visualizer)
        {
            if (_visualizerRegistry is null)
            {
                CloseReview();
                Status = "The visualizer registry is unavailable. Nothing was registered.";
                return;
            }

            var discovered = VisualizerDescriptors.FromType(unit.RuntimeType, specification.UnitId);
            _visualizerRegistry.Register(discovered with
            {
                Descriptor = discovered.Descriptor with
                {
                    DisplayName = specification.Name,
                    Description = specification.RawRequest,
                },
                AuthoredSpecification = specification,
            });

            CompleteAuthoredUnitRegistration(
                script,
                unit,
                specification,
                "Visualizer registered",
                "Open Visualizers and choose Add to chart; it cannot submit orders.");
            return;
        }

        if (_strategyKernelRegistry is null)
        {
            CloseReview();
            Status = "The canonical strategy registry is unavailable. Nothing was registered.";
            return;
        }

        _strategyKernelRegistry.Register(new StrategyKernelRegistration(
            specification.UnitId,
            specification.Name,
            specification.RawRequest,
            () => (DaxAlgo.Sdk.IStrategyKernel)Activator.CreateInstance(unit.RuntimeType)!,
            specification,
            unit.Schema));

        CompleteAuthoredUnitRegistration(
            script,
            unit,
            specification,
            "Paper strategy registered",
            "Open it from the Catalog or Paper Strategy Runner. Real-money routing remains unavailable.");
    }

    private void CompleteAuthoredUnitRegistration(
        StrategyScript script,
        CompiledAuthoredUnitV1 unit,
        AuthoredUnitSpecificationV1 specification,
        string activityTitle,
        string nextAction)
    {

        var persistence = _installer?.PersistAuthoredUnit(script, unit);
        Status = persistence?.Message ??
            $"'{specification.Name}' is registered for this session.";

        _registeredBaseline.Clear();
        foreach (var file in script.Files)
            _registeredBaseline[file.Name] = file.Content;

        IsRegistered = true;
        Append(AuthoringMessage.Tool(
            "Ok",
            activityTitle,
            $"{Status} {nextAction}"));
        CloseReview();
        Save();
    }

    /// <summary>Backs out of the review — nothing was registered, the compile result is discarded.</summary>
    [RelayCommand]
    private void CancelReview()
    {
        CloseReview();
        Status = "Review dismissed — the strategy was NOT registered.";
    }

    private void CloseReview()
    {
        ReviewOpen = false;
        ReviewFiles.Clear();
        SelectedReviewFile = null;
        ReviewSummary = null;
        _pendingCompile = null;
        _pendingAuthoredUnitCompile = null;
        _pendingScript = null;
        ConfirmRegisterCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmRegisterAction() =>
        ReviewOpen &&
        (_pendingCompile is { Success: true, Option: not null } ||
         _pendingAuthoredUnitCompile is { Success: true, Unit: not null }) &&
        _pendingScript is { } pendingScript &&
        ScriptMatchesCurrentSource(pendingScript);

    private bool ScriptMatchesCurrentSource(StrategyScript pendingScript)
    {
        var currentScript = CurrentScript();
        if (!string.Equals(pendingScript.Id, currentScript.Id, StringComparison.Ordinal) ||
            !string.Equals(pendingScript.DisplayName, currentScript.DisplayName, StringComparison.Ordinal) ||
            pendingScript.Files.Count != currentScript.Files.Count)
            return false;

        for (var index = 0; index < pendingScript.Files.Count; index++)
        {
            var pendingFile = pendingScript.Files[index];
            var currentFile = currentScript.Files[index];
            if (!string.Equals(pendingFile.Name, currentFile.Name, StringComparison.Ordinal) ||
                !string.Equals(pendingFile.Content, currentFile.Content, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────

    private StrategyScript CurrentScript() => new(
        StrategyId.Trim(),
        DisplayName.Trim(),
        [.. Files.Select(f => new StrategyFile(f.Name, f.Content))]);

    private StrategyBuildSession EnsureSession(AiProviderChoice choice, StrategyBuildProfile profile)
    {
        if (_session is not null) return _session;

        var client = ResolveClient(choice) ?? choice.Client;

        // Resume the restored thread exactly once: the model gets back everything it said, so a follow-up
        // ("now tighten the stop") lands on the code it actually wrote rather than on an empty context.
        _session = _ai!.StartSession(
            client, StrategyId.Trim(), DisplayName.Trim(), _restoredThread, _restoredUsage, profile);
        _restoredThread = null;
        _restoredUsage = null;
        return _session;
    }

    /// <summary>The selected provider bound to the selected model + effort (the factory rebuilds the
    /// client — a client is immutable in both).</summary>
    private IStrategyCodegenClient? ResolveClient(AiProviderChoice choice) =>
        _ai?.WithSettings(choice.ProviderId, SelectedModel, SelectedEffort);

    private void ResetSession(string? note)
    {
        _generationSession = null;
        _generationProviderKey = null;
        if (_session is null) return;
        _session = null;
        if (note is not null && Messages.Count > 0)
            Append(AuthoringMessage.System($"{note} The model won't remember what was said above."));
    }

    private bool IsGenerationContextCurrent(long epoch, string strategyId) =>
        epoch == Volatile.Read(ref _generationContextEpoch) &&
        string.Equals(strategyId, StrategyId.Trim(), StringComparison.Ordinal);

    private void InvalidateGenerationContext()
    {
        var wasGenerating = IsGenerating;
        if (wasGenerating) CancelActiveGenerationLaneProgress();
        Interlocked.Increment(ref _generationContextEpoch);
        var active = _generateCts;
        _generateCts = null;
        active?.Cancel();
        IsGenerating = false;
        _streamingReply = null;
        if (wasGenerating) FailRunningTasks();
        ElapsedText = null;
        ElapsedCompact = null;
    }

    /// <summary>Settle transient four-lane rows before advancing the generation epoch. Provider
    /// cancellation callbacks arrive after the token is canceled and are deliberately rejected by the
    /// epoch guard, so the view-model must record the terminal state itself. Completed and failed lanes
    /// keep their truthful terminal result; only work that had not finished becomes canceled.</summary>
    private void CancelActiveGenerationLaneProgress()
    {
        var changed = false;
        foreach (var row in GenerationLaneProgressRows)
        {
            if (row.State is StrategyGenerationLaneProgressStateV1.Completed or
                StrategyGenerationLaneProgressStateV1.Failed or
                StrategyGenerationLaneProgressStateV1.Canceled)
                continue;

            row.Apply(new StrategyGenerationLaneProgressV1(
                row.Lane,
                StrategyGenerationLaneProgressStateV1.Canceled));
            changed = true;
        }

        if (changed) NotifyGenerationProgressChanged();
    }

    private void InvalidateGenerationIfActive()
    {
        if (IsGenerating) InvalidateGenerationContext();
    }

    private void InvalidateDerivedArtifactState(bool markUnregistered)
    {
        Parameters = null;
        Diagnostics.Clear();
        Activity.Clear();
        Tasks.Clear();
        CompiledOk = false;
        AwaitingAnswer = false;
        CloseReview();
        if (markUnregistered) IsRegistered = false;
    }

    private static ParallelStrategyGenerationResultV1 WithoutRawParallelResponses(
        ParallelStrategyGenerationResultV1 batch) =>
        batch with
        {
            Lanes = batch.Lanes.Select(lane => lane with
            {
                AgentRun = lane.AgentRun with { RawResponse = null },
            }).ToArray(),
        };

    private void ClearCandidate()
    {
        _fourLaneStrategyBrief = null;
        SetPendingFourLanePrompt(null);
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        CandidateRestoreWarning = null;
        ClearSemanticCandidate();
        ClearParallelCandidates();
    }

    private void ClearSemanticCandidate()
    {
        ClearStrategyIntentReview();
        _generationSession = null;
        _generationProviderKey = null;
        CurrentCandidate = null;
        CandidateAssessment = null;
        CandidateContentHash = null;
        CandidateStatusText = null;
        CandidateGroups.Clear();
        CandidateOpenQuestions.Clear();
        CandidateBuildSupport.Clear();
        CandidateIssues.Clear();
    }

    private void ClearParallelCandidates()
    {
        ClearTradeIrSynthesis();
        SetEditorBaseGeneratedCandidateHash(null);
        _parallelCandidateBatch = null;
        CandidateBatchRestored = false;
        SelectedGeneratedCandidateOption = null;
        ChosenGeneratedCandidateHash = null;
        ClearGeneratedCandidateOptionFlags();
        GeneratedCandidateOptions.Clear();
        SelectedGenerationLaneProgressRow = null;
        GenerationLaneProgressRows.Clear();
        NotifyGenerationProgressChanged();
        NotifyParallelCandidateStateChanged();
    }

    private void RefreshGeneratedCandidateOptionFlags()
    {
        foreach (var option in GeneratedCandidateOptions)
        {
            option.IsPreviewed = ReferenceEquals(option, SelectedGeneratedCandidateOption);
            option.IsChosen = ChosenGeneratedCandidateHash is { } chosenHash &&
                string.Equals(option.CandidateHashSha256, chosenHash, StringComparison.Ordinal);
        }
    }

    private void ClearGeneratedCandidateOptionFlags()
    {
        foreach (var option in GeneratedCandidateOptions)
        {
            option.IsPreviewed = false;
            option.IsChosen = false;
        }
    }

    private void NotifyParallelCandidateStateChanged()
    {
        RefreshGeneratedCandidateOptionFlags();
        OnPropertyChanged(nameof(HasGeneratedCandidates));
        OnPropertyChanged(nameof(HasRetainedCandidateBatchDuringGeneration));
        OnPropertyChanged(nameof(HasCandidateContent));
        OnPropertyChanged(nameof(SelectableGeneratedCandidateCount));
        OnPropertyChanged(nameof(BlockedGeneratedCandidateCount));
        OnPropertyChanged(nameof(HasBlockedGeneratedCandidates));
        OnPropertyChanged(nameof(FirstBlockedGeneratedCandidateOption));
        OnPropertyChanged(nameof(CandidateBatchHeadline));
        OnPropertyChanged(nameof(CandidateBacktestAvailabilityText));
        OnPropertyChanged(nameof(HasSelectedGeneratedCandidate));
        OnPropertyChanged(nameof(HasChosenGeneratedCandidate));
        OnPropertyChanged(nameof(ChosenGeneratedCandidateSummary));
        OnPropertyChanged(nameof(CandidateActionText));
        OnPropertyChanged(nameof(CanChooseGeneratedCandidate));
        OnPropertyChanged(nameof(CanRevalidateGeneratedCandidate));
        ChooseGeneratedCandidateCommand.NotifyCanExecuteChanged();
        RevalidateGeneratedCandidateCommand.NotifyCanExecuteChanged();
        NotifyTradeIrSynthesisStateChanged();
        NotifyAuthoringScreenStateChanged();
    }

    private void SetFiles(IReadOnlyList<StrategyFile> files)
    {
        SetLoadedCombinedTradeIrCandidateHash(null);
        EditorOriginatedFromCombinedTradeIr = false;
        SetEditorBaseGeneratedCandidateHash(null);
        ChosenGeneratedCandidateHash = null;
        CompiledOk = false;
        Parameters = null;
        IsRegistered = false;
        CloseReview();
        foreach (var existing in Files) existing.PropertyChanged -= OnFileEdited;
        Files.Clear();

        foreach (var file in files)
            Files.Add(Track(new AuthoredFile(file.Name, file.Content)));

        SelectedFile = Files.FirstOrDefault();
        NotifyEditorFileModeChanged();
        _session?.SyncEditedFiles(files);
    }

    private AuthoredFile Track(AuthoredFile file)
    {
        file.PropertyChanged += OnFileEdited;
        return file;
    }

    private void OnFileEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AuthoredFile.Content) or nameof(AuthoredFile.Name))
        {
            _filesEditedByUser = true;
            if (e.PropertyName == nameof(AuthoredFile.Name)) NotifyEditorFileModeChanged();
            InvalidateLoadedTradeIrSynthesisProof();
            InvalidateEditorProofs("The editor changed; prior compile, registration, and candidate-hash proofs were cleared.");
        }
    }

    private void NotifyEditorFileModeChanged()
    {
        OnPropertyChanged(nameof(HasExpertCSharpFiles));
        OnPropertyChanged(nameof(HasAuthoredUnitCSharpFiles));
        OnPropertyChanged(nameof(HasNonCSharpExpertArtifact));
        OnPropertyChanged(nameof(AuthoringBoundaryText));
        OnPropertyChanged(nameof(CanCompileCurrentSource));
        CompileCommand.NotifyCanExecuteChanged();
    }

    private void SetEditorBaseGeneratedCandidateHash(string? hash)
    {
        if (string.Equals(_editorBaseGeneratedCandidateHash, hash, StringComparison.Ordinal)) return;
        _editorBaseGeneratedCandidateHash = hash;
        OnPropertyChanged(nameof(CanRevalidateGeneratedCandidate));
        RevalidateGeneratedCandidateCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateEditorProofs(string status)
    {
        var hadProof = ChosenGeneratedCandidateHash is not null || CompiledOk || IsRegistered || ReviewOpen;
        ChosenGeneratedCandidateHash = null;
        InvalidateDerivedArtifactState(markUnregistered: true);
        if (hadProof) Status = status;
    }

    private bool EditorMatchesCandidate(StrategyGenerationCandidateV1 candidate)
    {
        if (Files.Count != 1 ||
            !string.Equals(Files[0].Name, candidate.Artifact.FileName, StringComparison.Ordinal))
            return false;

        if (candidate.Artifact.Source is { } source)
            return string.Equals(Files[0].Content, source, StringComparison.Ordinal);

        if (candidate.Artifact.Document is not { } document) return false;
        try
        {
            // JSON whitespace and object-property order are not semantic candidate changes. Use the
            // same RFC 8785 canonicalizer that hashes the artifact so a locally revalidated JSON edit
            // can safely recover its editor-base provenance after restart.
            return string.Equals(
                ExecutableStrategyDefinitionCanonicalJson.Canonicalize(Files[0].Content),
                ExecutableStrategyDefinitionCanonicalJson.Canonicalize(document.GetRawText()),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private string UniqueFileName(string preferred)
    {
        if (Files.All(f => !f.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))) return preferred;

        var stem = preferred.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? preferred[..^3] : preferred;
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}{i}.cs";
            if (Files.All(f => !f.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private void Append(AuthoringMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
    }

    private void PushActivity(string step)
    {
        Activity.Add(step);
        while (Activity.Count > MaxActivityRows) Activity.RemoveAt(0);
        AdvanceTasks(step);
    }

    /// <summary>Per-turn "what changed" chips: line counts for every file the model wrote, against what
    /// was in the editor before the turn. Skipped when nothing actually changed (a pure-question turn
    /// re-sending identical files).</summary>
    private void AppendFileChanges(IReadOnlyDictionary<string, string> prior, IReadOnlyList<StrategyFile> files)
    {
        var changes = new List<FileChangeSummary>(files.Count);
        foreach (var file in files)
        {
            var (added, removed) = LineDiff.Count(prior.GetValueOrDefault(file.Name, string.Empty), file.Content);
            if (added > 0 || removed > 0)
                changes.Add(new FileChangeSummary(file.Name, added, removed));
        }

        if (changes.Count > 0) Append(AuthoringMessage.FilesChanged(changes));
    }

    /// <summary>Names an untouched strategy after its first brief: "Fade liquidity sweeps on ES…" ⇒
    /// id <c>fadeLiquiditySweeps</c>, display name = the brief's first clause. Never fires once the
    /// user has typed their own id or name.</summary>
    private void DeriveIdentityFrom(string brief)
    {
        if (StrategyId != DefaultStrategyId || DisplayName != DefaultDisplayName) return;

        var firstLine = brief.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        if (firstLine.Length == 0) return;

        // Display name: the first sentence/clause, cut at a word boundary around 60 chars.
        var clause = firstLine.Split(':', '.', ';')[0].Trim().TrimEnd(',');
        if (clause.Length == 0) clause = firstLine;
        if (clause.Length > 60)
        {
            var cut = clause.LastIndexOf(' ', 60);
            clause = clause[..(cut > 20 ? cut : 60)].TrimEnd() + "…";
        }

        // Id: the first three meaningful words, lowerCamelCase, alphanumeric only.
        string[] stop = ["a", "an", "the", "on", "in", "at", "of", "to", "for", "with", "and", "or", "that", "when", "using"];
        var words = clause
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 1 && !stop.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        if (words.Length == 0) return;

        var id = string.Concat(words.Select((w, i) => i == 0
            ? w.ToLowerInvariant()
            : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

        StrategyId = id;
        DisplayName = char.ToUpperInvariant(clause[0]) + clause[1..];
    }

    /// <summary>Chat → snapshot. Rich kinds flatten into the entry's optional fields; the expandable
    /// tool output is intentionally dropped (summaries restore, transcripts don't bloat).</summary>
    private static AuthoringChatEntry ToChatEntry(AuthoringMessage m) => m.Kind switch
    {
        AuthoringMessage.KindTool => new AuthoringChatEntry(
            AuthoringChatEntry.System, m.ToolTitle ?? string.Empty, m.TimestampLocal,
            Kind: m.Kind, State: m.ToolState, Detail: m.ToolDetail),
        AuthoringMessage.KindPlan or AuthoringMessage.KindPlanText => new AuthoringChatEntry(
            AuthoringChatEntry.System, m.PlanSnapshotText(), m.TimestampLocal, Kind: AuthoringMessage.KindPlanText),
        AuthoringMessage.KindFiles => new AuthoringChatEntry(
            AuthoringChatEntry.System, m.Text, m.TimestampLocal,
            Kind: m.Kind, Detail: FileChangeSummary.Pack(m.FileChanges ?? [])),
        _ => new AuthoringChatEntry(
            m.IsSystem ? AuthoringChatEntry.System
                : m.IsUser ? AuthoringChatEntry.User : AuthoringChatEntry.Assistant,
            m.Text, m.TimestampLocal),
    };

    /// <summary>Snapshot → chat. Entries from pre-redesign files carry no Kind and restore exactly as
    /// they always did.</summary>
    private static AuthoringMessage FromChatEntry(AuthoringChatEntry entry) => entry.Kind switch
    {
        AuthoringMessage.KindTool => AuthoringMessage.Tool(entry.State ?? "Info", entry.Text, entry.Detail ?? string.Empty),
        AuthoringMessage.KindPlanText => AuthoringMessage.PlanText(entry.Text),
        AuthoringMessage.KindFiles when FileChangeSummary.Unpack(entry.Detail) is { Count: > 0 } changes =>
            AuthoringMessage.FilesChanged(changes),
        AuthoringMessage.KindFiles => AuthoringMessage.System(entry.Text),
        _ => entry.Role == AuthoringChatEntry.System
            ? AuthoringMessage.System(entry.Text)
            : new AuthoringMessage(
                entry.Role == AuthoringChatEntry.User ? CodegenRole.User : CodegenRole.Assistant,
                entry.Text),
    };

    private void PersistSelection(string providerId, string? model, CodegenEffort effort)
    {
        try
        {
            AiCodegenUserFile.SaveSelection(providerId, model, effort, _options, BuildEffort.Wire());
        }
        catch (Exception ex)
        {
            // A read-only profile shouldn't break the builder — the choice just won't survive a restart.
            _logger.LogWarning(ex, "Could not persist the AI provider/model choice");
        }
    }

    public void Dispose()
    {
        InvalidateGenerationContext();
        DisposeTradeIrBacktest();
        DisposeNativeStrategyRun();
        // Hand-edits in the Code tab aren't saved per keystroke; catch them on the way out.
        Save();

        foreach (var file in Files) file.PropertyChanged -= OnFileEdited;
    }

    /// <summary>Starter strategy shown in the editor — a complete, compiling skeleton with a
    /// declarative parameter schema so the auto-editor lights up on first compile.</summary>
    private const string TemplateSource = """
        // Authored strategy. The following namespaces are imported for you:
        //   System, System.Collections.Generic, System.Linq, System.Threading(.Tasks),
        //   TradingTerminal.Core.Domain / Trading / Time / Backtest / MarketData,
        //   TradingTerminal.Core.Strategies.Parameters
        //
        // Rules: define exactly ONE public class implementing IBacktestStrategy with a
        // public (Contract) constructor. Optionally add a static Schema and a static
        // Create(Contract, StrategyParameters) to expose tunable parameters in the UI.
        // Helpers may live in additional files (the + button on the file list).

        public sealed class MyStrategy : IBacktestStrategy
        {
            public static StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
                StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1));

            public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
                new MyStrategy(contract, p.GetInt("lookback"), p.GetDouble("threshold"));

            private readonly Contract _contract;
            private readonly int _lookback;
            private readonly double _threshold;

            public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }

            public MyStrategy(Contract contract, int lookback, double threshold)
            {
                _contract = contract;
                _lookback = lookback;
                _threshold = threshold;
            }

            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
                => Task.CompletedTask;

            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
            {
                // Your signal logic here. Submit orders via
                // router.PlaceOrderAsync(new OrderRequest(...)). _contract names the instrument.
                if (_lookback <= 0 || _threshold <= 0 || _contract is null) return Task.CompletedTask;
                return Task.CompletedTask;
            }

            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
                => Task.CompletedTask;
        }
        """;
}

/// <summary>One source file in the builder's Code tab — editable, and observed so a hand-edit is fed
/// back to the model on the next turn.</summary>
public sealed partial class AuthoredFile(string name, string content) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _content = content;
}

/// <summary>
/// One element of the agent-workspace transcript. <see cref="Kind"/> is a string (not an enum) on
/// purpose: the shared XAML templates live in TradingTerminal.UI, which cannot reference this
/// assembly, so every template trigger is duck-typed against these values:
/// <c>User</c> / <c>Assistant</c> / <c>Note</c> (a builder aside) / <c>Tool</c> (a one-line action
/// card) / <c>Plan</c> (the live turn checklist) / <c>PlanText</c> (a restored plan snapshot) /
/// <c>Files</c> (per-file change chips).
/// </summary>
public sealed partial class AuthoringMessage : ObservableObject
{
    public const string KindUser = "User";
    public const string KindAssistant = "Assistant";
    public const string KindNote = "Note";
    public const string KindTool = "Tool";
    public const string KindPlan = "Plan";
    public const string KindPlanText = "PlanText";
    public const string KindFiles = "Files";

    public AuthoringMessage(CodegenRole role, string text)
    {
        Role = role;
        Kind = role == CodegenRole.User ? KindUser : KindAssistant;
        _text = text;
    }

    private AuthoringMessage(string kind, string text)
    {
        Role = CodegenRole.Assistant;
        IsSystem = kind is not (KindUser or KindAssistant);
        Kind = kind;
        _text = text;
    }

    /// <summary>A builder-generated note, styled apart from the model's own words.</summary>
    public static AuthoringMessage System(string? text) => new(KindNote, text ?? string.Empty);

    /// <summary>An action card: <paramref name="state"/> is "Ok" / "Fail" / "Run" / "Info" (duck-typed
    /// by the templates), <paramref name="detail"/> the numbers worth reading at a glance,
    /// <paramref name="more"/> the expandable full output.</summary>
    public static AuthoringMessage Tool(string state, string title, string detail, string? more = null) =>
        new(KindTool, title)
        {
            ToolState = state,
            ToolTitle = title,
            ToolDetail = detail,
            ToolMore = string.IsNullOrWhiteSpace(more) ? null : more,
        };

    /// <summary>The turn's live checklist — holds THIS turn's task instances, whose states keep
    /// animating in place while the pipeline runs and then freeze as history.</summary>
    public static AuthoringMessage Plan(IReadOnlyList<BuildTask> tasks) =>
        new(KindPlan, string.Empty) { PlanTasks = tasks };

    /// <summary>A plan restored from disk — glyph lines, no live states.</summary>
    public static AuthoringMessage PlanText(string text) => new(KindPlanText, text);

    public static AuthoringMessage FilesChanged(IReadOnlyList<FileChangeSummary> changes) =>
        new(KindFiles, string.Join(" · ", changes.Select(c => $"{c.Name} {c.Counts}")))
        {
            FileChanges = changes,
        };

    public CodegenRole Role { get; }
    public bool IsSystem { get; }
    public string Kind { get; }
    public bool IsUser => !IsSystem && Role == CodegenRole.User;
    public bool IsAssistant => !IsSystem && Role == CodegenRole.Assistant;

    public string? ToolState { get; private init; }
    public string? ToolTitle { get; private init; }
    public string? ToolDetail { get; private init; }
    public string? ToolMore { get; private init; }
    public bool HasMore => !string.IsNullOrEmpty(ToolMore);

    public IReadOnlyList<BuildTask>? PlanTasks { get; private init; }
    public IReadOnlyList<FileChangeSummary>? FileChanges { get; private init; }

    /// <summary>ChatGPT-style follow-ups for this assistant/tool turn only (0–3).</summary>
    public IReadOnlyList<ResearchQuickSuggestionV1>? FollowUps { get; private set; }

    public bool HasFollowUps => FollowUps is { Count: > 0 };

    public void SetFollowUps(IReadOnlyList<ResearchQuickSuggestionV1>? followUps)
    {
        FollowUps = followUps is { Count: > 0 } ? followUps : null;
        OnPropertyChanged(nameof(FollowUps));
        OnPropertyChanged(nameof(HasFollowUps));
    }

    /// <summary>The live plan flattened to glyph lines for persistence (and for a restored render).</summary>
    public string PlanSnapshotText() => PlanTasks is null
        ? Text
        : string.Join("\n", PlanTasks.Select(t => t.State switch
        {
            BuildTaskState.Done => $"✓ {t.Title}",
            BuildTaskState.Failed => $"✕ {t.Title}",
            BuildTaskState.Running => $"◐ {t.Title}",
            _ => $"○ {t.Title}",
        }));

    /// <summary>Observable so streaming can grow the bubble token by token.</summary>
    [ObservableProperty] private string _text;

    public DateTime TimestampLocal { get; } = DateTime.Now;
}

/// <summary>A flattened display row for one recursively nested candidate group.</summary>
public sealed record StrategyCandidateGroupRow(
    int Depth,
    string Kind,
    string Title,
    string Summary,
    IReadOnlyList<StrategyCandidateStatementV1> Statements)
{
    public string Location => Depth == 0 ? Kind : $"{new string('·', Depth)} {Kind}";
}

/// <summary>Plain-language build-support row for the Candidate tab.</summary>
public sealed record StrategyBuildSupportRow(
    string Description,
    string Status,
    string Detail,
    bool RequiredForLowering);

/// <summary>One of the four parallel authoring alternatives shown before the user chooses an artifact.</summary>
public sealed partial class StrategyGenerationCandidateOption : ObservableObject
{
    public StrategyGenerationCandidateOption(StrategyGenerationLaneResultV1 result) => Result = result;

    public StrategyGenerationLaneResultV1 Result { get; }

    [ObservableProperty] private bool _isPreviewed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewStateText))]
    private bool _isChosen;

    public StrategyGenerationCandidateV1? Candidate => Result.Candidate;
    public string? CandidateHashSha256 => Result.CandidateHashSha256;
    public bool IsGenerated => Result.Generated;
    public bool IsFailed => Result.Readiness is StrategyGenerationReadinessV1.Invalid
        or StrategyGenerationReadinessV1.Failed
        or StrategyGenerationReadinessV1.Unsupported;
    public bool PackageValidationAvailable => Result.PackageValidationAvailable;
    public string LaneName => StrategyGenerationLaneCatalogV1.DisplayName(Result.Lane);
    public string Representation => Result.Lane switch
    {
        StrategyGenerationLaneV1.VibePython => "Vibe Quant Python source profile",
        StrategyGenerationLaneV1.DeclarativeSpec => "closed Vibe Quant Rules JSON",
        StrategyGenerationLaneV1.TypedGraph => "canonical DaxAlgo TradeIR module",
        StrategyGenerationLaneV1.CspPython => "Vibe Quant inert CSP profile",
        _ => Result.Lane.ToString(),
    };
    public string ContractVersion => Candidate?.PackageBinding.ArtifactContractVersion ?? "no contract";
    public string ContractAuthority => Candidate?.PackageBinding.Authority.AuthorityId ?? "no authority";
    public string ContractRole => Candidate?.PackageBinding.Authority.SemanticRole switch
    {
        StrategyGenerationSemanticRoleV1.SourceReview => "SOURCE / REVIEW",
        StrategyGenerationSemanticRoleV1.CanonicalExecutableIr => "CANONICAL IR",
        _ => "UNKNOWN ROLE",
    };
    public string LoweringBoundary => Candidate?.PackageBinding.Authority.LoweringMode switch
    {
        StrategyGenerationLoweringModeV1.ReviewedAiSynthesis => "reviewed AI synthesis → TradeIR",
        StrategyGenerationLoweringModeV1.Identity => "already canonical TradeIR",
        StrategyGenerationLoweringModeV1.DeterministicLowererRequired =>
            "deterministic lowerer required for canonical backtest",
        _ => "no lowering route declared",
    };
    public string CompatibilityBoundary => Candidate?.PackageBinding.Authority.ExternalCompatibility switch
    {
        StrategyGenerationExternalCompatibilityV1.Unverified => "external compatibility unverified",
        StrategyGenerationExternalCompatibilityV1.Verified => "external compatibility verified",
        _ => "DaxAlgo-owned contract",
    };
    public string SpecificationReference =>
        Candidate?.PackageBinding.Authority.SpecificationReference ?? "no specification reference";
    public string StatusText => Result.Readiness switch
    {
        StrategyGenerationReadinessV1.PackageValid => "PACKAGE VALID · NOT TESTED",
        StrategyGenerationReadinessV1.TestPassed => "TEST PASSED",
        StrategyGenerationReadinessV1.Generated => "GENERATED · NOT PACKAGE-VALIDATED",
        StrategyGenerationReadinessV1.Invalid => "GENERATED · INVALID",
        StrategyGenerationReadinessV1.Unsupported => "UNSUPPORTED",
        _ => "FAILED",
    };
    public string FailureHeading => Result.Readiness switch
    {
        StrategyGenerationReadinessV1.Unsupported => "LANE UNSUPPORTED",
        StrategyGenerationReadinessV1.Invalid => "GENERATED ARTIFACT INVALID",
        StrategyGenerationReadinessV1.Failed => "GENERATION FAILED",
        _ => "LANE NOT SELECTABLE",
    };
    public string ArtifactName => Candidate?.Artifact.FileName ?? "no artifact";
    public string Summary => Candidate?.Interpretation ?? ErrorText;
    public StrategyCandidateGenerationIssueV1? FirstIssue =>
        Result.Issues.FirstOrDefault(static issue =>
            issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error)
        ?? Result.Issues.FirstOrDefault();
    public string FirstIssueCode => FirstIssue?.Code ?? "No issue code reported";
    public string FirstIssuePath => FirstIssue?.Path ?? "No issue path reported";
    public string FirstIssueMessage => FirstIssue?.Message
        ?? Result.AgentRun.Error
        ?? "The lane did not return diagnostic detail.";
    public string ErrorText => string.Join(Environment.NewLine, Result.Issues.Select(issue =>
        $"{issue.Code} · {issue.Path}: {issue.Message}"));
    public string RecoveryText => FirstIssueCode switch
    {
        "LANE_JSON_INVALID" =>
            "The model answered, but its candidate wrapper was not valid JSON for this lane. A fresh generation can repair the response; this saved result cannot be repaired in place.",
        "LANE_ARTIFACT_VALIDATION_FAILED" or "GRAPH_INVALID" =>
            "The candidate JSON parsed, but the lane artifact violated its deterministic contract. A fresh generation receives the exact failing field and can repair it once.",
        "LANE_PROVIDER_FAILED" or "LANE_PROVIDER_EXCEPTION" =>
            "The AI provider failed before a candidate could be validated. Check provider availability, then regenerate.",
        _ when Result.Readiness == StrategyGenerationReadinessV1.Invalid =>
            "The AI response failed a deterministic lane check. Regenerate to run one validation-aware repair pass; the current saved result remains read-only evidence.",
        _ => "Regenerate this batch after resolving the provider error.",
    };
    public string ArtifactPreview => Candidate?.Artifact.Source
        ?? (Candidate?.Artifact.Document is { } document
            ? JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true })
            : string.Empty);
    public string InspectablePreview => !string.IsNullOrWhiteSpace(ArtifactPreview)
        ? ArtifactPreview
        : !string.IsNullOrWhiteSpace(Result.AgentRun.RawResponse)
            ? Result.AgentRun.RawResponse!
            : ErrorText;
    public string PreviewHeading => !string.IsNullOrWhiteSpace(ArtifactPreview) && Candidate?.Artifact is { } artifact
        ? $"{artifact.FileName} · exact generated artifact"
        : !string.IsNullOrWhiteSpace(Result.AgentRun.RawResponse)
            ? "Raw model response · candidate envelope invalid"
            : $"{LaneName} · generation diagnostic";
    public string PreviewStateText => IsChosen
        ? "ACTIVE IN EDITOR · EXACT HASH"
        : "PREVIEW ONLY · NOT ACTIVE";
    public string FlexibilityText => Candidate is null
        ? string.Empty
        : $"{Candidate.Parameters.Count} proposed parameters · {Candidate.VariationAxes.Count} proposed forks";
}

/// <summary>Live, transient state for one of the four concurrent generation agents.</summary>
public sealed partial class StrategyGenerationLaneProgressRow : ObservableObject
{
    public StrategyGenerationLaneProgressRow(StrategyGenerationLaneV1 lane) => Lane = lane;

    public StrategyGenerationLaneV1 Lane { get; }
    public string LaneName => StrategyGenerationLaneCatalogV1.DisplayName(Lane);
    public string AgentName => Lane switch
    {
        StrategyGenerationLaneV1.VibePython => "VibeAgent",
        StrategyGenerationLaneV1.DeclarativeSpec => "SpecAgent",
        StrategyGenerationLaneV1.TypedGraph => "GraphAgent",
        StrategyGenerationLaneV1.CspPython => "CspAgent",
        _ => Lane.ToString(),
    };
    public string ArtifactName => Lane switch
    {
        StrategyGenerationLaneV1.VibePython => "strategy.py",
        StrategyGenerationLaneV1.DeclarativeSpec => "strategy.spec.json",
        StrategyGenerationLaneV1.TypedGraph => "strategy.tradeir.json",
        StrategyGenerationLaneV1.CspPython => "strategy.csp.py",
        _ => "strategy artifact",
    };
    public string PurposeText => Lane switch
    {
        StrategyGenerationLaneV1.VibePython => "readable Python draft",
        StrategyGenerationLaneV1.DeclarativeSpec => "explicit rules JSON",
        StrategyGenerationLaneV1.TypedGraph => "canonical typed graph",
        StrategyGenerationLaneV1.CspPython => "event-graph draft",
        _ => "editable strategy draft",
    };
    public string ValidationPlanText => Lane switch
    {
        StrategyGenerationLaneV1.TypedGraph => "TradeIR structure + installed package check",
        StrategyGenerationLaneV1.DeclarativeSpec => "closed Rules v1 contract check",
        StrategyGenerationLaneV1.CspPython => "inert CSP authoring-profile check",
        _ => "Python authoring-profile check",
    };

    private StrategyGenerationLaneProgressStateV1 _lastActiveState =
        StrategyGenerationLaneProgressStateV1.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(StateDetail))]
    [NotifyPropertyChangedFor(nameof(PipelineText))]
    private StrategyGenerationLaneProgressStateV1 _state = StrategyGenerationLaneProgressStateV1.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDetail))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(InspectablePreview))]
    [NotifyPropertyChangedFor(nameof(PreviewHeading))]
    private StrategyGenerationCandidateOption? _resultOption;

    public bool HasResult => ResultOption is not null;
    public string InspectablePreview => ResultOption?.InspectablePreview ?? string.Empty;
    public string PreviewHeading => ResultOption?.PreviewHeading ?? $"{ArtifactName} · waiting for result";

    public void Apply(StrategyGenerationLaneProgressV1 progress)
    {
        if (progress.Lane != Lane)
            throw new ArgumentException("Progress belongs to a different generation lane.", nameof(progress));

        if (progress.Result is not null)
        {
            if (progress.Result.Lane != Lane)
                throw new ArgumentException("The terminal result belongs to a different generation lane.", nameof(progress));
            if (!IsTerminal(progress.State))
                throw new ArgumentException("A lane result can only accompany terminal progress.", nameof(progress));
        }

        if (IsTerminal(State))
        {
            if (ResultOption is null && progress.Result is not null && progress.State == State)
                ResultOption = new StrategyGenerationCandidateOption(progress.Result);
            return;
        }
        var parsingRepairedResponse =
            State == StrategyGenerationLaneProgressStateV1.RepairingResponse &&
            progress.State == StrategyGenerationLaneProgressStateV1.ParsingResponse;
        if (!IsTerminal(progress.State) && progress.State < State && !parsingRepairedResponse) return;
        if (!IsTerminal(progress.State)) _lastActiveState = progress.State;

        if (progress.Result is not null)
            ResultOption = new StrategyGenerationCandidateOption(progress.Result);
        Detail = progress.Detail;
        State = progress.State;
    }

    public string StateLabel => State switch
    {
        StrategyGenerationLaneProgressStateV1.Queued => "QUEUED",
        StrategyGenerationLaneProgressStateV1.PreparingRequest => "PREPARING",
        StrategyGenerationLaneProgressStateV1.WaitingForModel => "WAITING FOR MODEL",
        StrategyGenerationLaneProgressStateV1.ParsingResponse => "PARSING RESPONSE",
        StrategyGenerationLaneProgressStateV1.ValidatingArtifact => "VALIDATING",
        StrategyGenerationLaneProgressStateV1.RepairingResponse => "REPAIRING RESPONSE",
        StrategyGenerationLaneProgressStateV1.Completed => "READY",
        StrategyGenerationLaneProgressStateV1.Failed => "BLOCKED",
        StrategyGenerationLaneProgressStateV1.Canceled => "CANCELED",
        _ => State.ToString().ToUpperInvariant(),
    };
    public string StateDetail => State switch
    {
        StrategyGenerationLaneProgressStateV1.Queued => "Waiting to start its independent request",
        StrategyGenerationLaneProgressStateV1.PreparingRequest => "Binding the brief to this lane's contract",
        StrategyGenerationLaneProgressStateV1.WaitingForModel => "Request sent; waiting for the model response",
        StrategyGenerationLaneProgressStateV1.ParsingResponse => "Response received; parsing the candidate envelope",
        StrategyGenerationLaneProgressStateV1.ValidatingArtifact => $"Checking {ValidationPlanText}",
        StrategyGenerationLaneProgressStateV1.RepairingResponse =>
            Detail ?? "First response failed validation; waiting for one bounded repair",
        StrategyGenerationLaneProgressStateV1.Completed => Detail ?? "Artifact returned and checks finished",
        StrategyGenerationLaneProgressStateV1.Failed => Detail is { Length: > 0 }
            ? $"Stopped at {FailureStageLabel}: {Detail}"
            : $"Stopped at {FailureStageLabel}",
        StrategyGenerationLaneProgressStateV1.Canceled => "Stopped by the user",
        _ => string.Empty,
    };
    public string PipelineText => State switch
    {
        StrategyGenerationLaneProgressStateV1.Queued => "○ PREPARE   ○ MODEL   ○ PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.PreparingRequest => "● PREPARE   ○ MODEL   ○ PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.WaitingForModel => "✓ PREPARE   ● MODEL   ○ PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.ParsingResponse => "✓ PREPARE   ✓ MODEL   ● PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.ValidatingArtifact => "✓ PREPARE   ✓ MODEL   ✓ PARSE   ● CHECK",
        StrategyGenerationLaneProgressStateV1.RepairingResponse => "✓ PREPARE   ! CHECK   ● REPAIR",
        StrategyGenerationLaneProgressStateV1.Completed => "✓ PREPARE   ✓ MODEL   ✓ PARSE   ✓ CHECK",
        StrategyGenerationLaneProgressStateV1.Failed => FailurePipelineText,
        StrategyGenerationLaneProgressStateV1.Canceled => "■ STOPPED",
        _ => string.Empty,
    };

    private string FailureStageLabel => _lastActiveState switch
    {
        StrategyGenerationLaneProgressStateV1.PreparingRequest => "request preparation",
        StrategyGenerationLaneProgressStateV1.WaitingForModel => "model response",
        StrategyGenerationLaneProgressStateV1.ParsingResponse => "response parsing",
        StrategyGenerationLaneProgressStateV1.ValidatingArtifact => "contract validation",
        StrategyGenerationLaneProgressStateV1.RepairingResponse => "repair response",
        _ => "generation",
    };

    private string FailurePipelineText => _lastActiveState switch
    {
        StrategyGenerationLaneProgressStateV1.PreparingRequest => "! PREPARE   ○ MODEL   ○ PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.WaitingForModel => "✓ PREPARE   ! MODEL   ○ PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.ParsingResponse => "✓ PREPARE   ✓ MODEL   ! PARSE   ○ CHECK",
        StrategyGenerationLaneProgressStateV1.ValidatingArtifact => "✓ PREPARE   ✓ MODEL   ✓ PARSE   ! CHECK",
        StrategyGenerationLaneProgressStateV1.RepairingResponse => "✓ PREPARE   ✓ MODEL   ! REPAIR",
        _ => "! GENERATION BLOCKED",
    };

    private static bool IsTerminal(StrategyGenerationLaneProgressStateV1 state) =>
        state is StrategyGenerationLaneProgressStateV1.Completed or
            StrategyGenerationLaneProgressStateV1.Failed or
            StrategyGenerationLaneProgressStateV1.Canceled;
}

/// <summary>One explicit gate between a generated authoring artifact and a runnable backtest.</summary>
public sealed record CandidateReadinessStageRow(
    string Step,
    string Title,
    string Status,
    string Detail);

/// <summary>One file's change counts for the per-turn chips ("SweepDetector.cs +64 −8").</summary>
public sealed record FileChangeSummary(string Name, int Added, int Removed)
{
    public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";

    /// <summary>Machine form for the session snapshot ("name|added|removed;…").</summary>
    public static string Pack(IReadOnlyList<FileChangeSummary> changes) =>
        string.Join(";", changes.Select(c => $"{c.Name}|{c.Added}|{c.Removed}"));

    public static IReadOnlyList<FileChangeSummary>? Unpack(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed)) return null;

        var changes = new List<FileChangeSummary>();
        foreach (var part in packed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split('|');
            if (fields.Length == 3 && int.TryParse(fields[1], out var added) && int.TryParse(fields[2], out var removed))
                changes.Add(new FileChangeSummary(fields[0], added, removed));
        }

        return changes.Count > 0 ? changes : null;
    }
}

/// <summary>One file in the review overlay: its full diff against the last registered content, plus
/// the +/− counts for the file strip.</summary>
public sealed class ReviewFileEntry(string name, IReadOnlyList<DiffLine> lines)
{
    public string Name { get; } = name;
    public IReadOnlyList<DiffLine> Lines { get; } = lines;
    public int Added { get; } = lines.Count(l => l.Kind == "add");
    public int Removed { get; } = lines.Count(l => l.Kind == "del");
    public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
}

/// <summary>One row in the AI provider picker — wraps a codegen client with display + availability for
/// binding, so an unavailable provider shows disabled with a hint rather than vanishing.</summary>
public sealed class AiProviderChoice(IStrategyCodegenClient client)
{
    public IStrategyCodegenClient Client { get; } = client;
    public string ProviderId => Client.ProviderId;
    public string DisplayName => Client.DisplayName;
    public bool IsAvailable => Client.IsAvailable;
    public string Label => IsAvailable ? DisplayName : $"{DisplayName} — not set up";
}

/// <summary>Where one step of the build pipeline stands.</summary>
public enum BuildTaskState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>One row of the builder's Tasks strip — a pipeline step ("Generate", "Compile", "Backtest
/// smoke") whose <see cref="State"/> advances live as the turn's activity stream arrives.</summary>
public sealed partial class BuildTask(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty] private BuildTaskState _state = BuildTaskState.Pending;
}
