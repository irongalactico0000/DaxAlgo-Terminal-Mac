using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

public sealed partial class StrategyAuthoringViewModel
{
    [ObservableProperty]
    private ResearchDatasetDefinitionV1? _researchDatasetDefinition;

    [ObservableProperty]
    private ResearchChartSelectionV1? _pendingResearchChartSelection;

    /// <summary>Host overlay ids captured with the pending chart selection (lossy catalog ids).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _pendingResearchOverlayIds = Array.Empty<string>();

    /// <summary>Exact indicator bindings (kind + period) for R05/R12 continuity.</summary>
    [ObservableProperty]
    private IReadOnlyList<ResearchIndicatorBindingV1> _pendingResearchIndicatorBindings =
        Array.Empty<ResearchIndicatorBindingV1>();

    /// <summary>Editable condition attached to the next labeled sample (R07 / V05).</summary>
    [ObservableProperty]
    private ResearchConditionDefinitionV1? _pendingResearchCondition;

    [ObservableProperty]
    private string _pendingConditionMultipleText = "2";

    [ObservableProperty]
    private string _pendingConditionLookbackText = "20";

    [ObservableProperty]
    private ResearchConditionSearchResultV1? _researchConditionSearchResult;

    [ObservableProperty]
    private bool _isResearchConditionSearching;

    [ObservableProperty]
    private ResearchExperimentEvidenceV1? _researchExperimentEvidence;

    [ObservableProperty]
    private bool _isResearchExperimentRunning;

    private readonly Dictionary<string, ResearchAnalysisReferenceV1> _researchAnalysisReferences =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasResearchDataset => ResearchDatasetDefinition is not null;
    public bool HasResearchChartSelection => PendingResearchChartSelection is not null;

    /// <summary>
    /// Instrument currently bound on the Research chart surface (embedded or detached).
    /// Shell updates this when host chart focus changes so event vs chart mismatch is visible.
    /// </summary>
    private string? _boundResearchChartInstrument;

    public string? ResearchChartInstrumentText =>
        _boundResearchChartInstrument ?? PendingResearchChartSelection?.CanonicalSymbol;

    public void SetBoundResearchChartInstrument(string? symbol)
    {
        var normalized = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim();
        if (string.Equals(_boundResearchChartInstrument, normalized, StringComparison.OrdinalIgnoreCase))
            return;
        _boundResearchChartInstrument = normalized;
        OnPropertyChanged(nameof(ResearchChartInstrumentText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(ActiveArtifactKindText));
        OnPropertyChanged(nameof(ActiveResearchContextText));
        OnPropertyChanged(nameof(CanOpenResearchMarketStructure));
        OpenResearchOrderBookCommand.NotifyCanExecuteChanged();
        OpenResearchVolumeFootprintCommand.NotifyCanExecuteChanged();
        OpenResearchBookmapCommand.NotifyCanExecuteChanged();
    }
    public bool HasPendingResearchIndicatorBindings => PendingResearchIndicatorBindings.Count > 0;
    public bool HasPendingResearchCondition => PendingResearchCondition is not null;
    public string PendingResearchConditionText => PendingResearchCondition is null
        ? "No condition set — optional for labeling; set volume multiple to attach R08 condition."
        : $"{PendingResearchCondition.SummaryText} · ver {PendingResearchCondition.VersionShort}";
    public string PendingResearchIndicatorsText => PendingResearchIndicatorBindings.Count == 0
        ? "No indicators enabled on the Research chart — toggle SMA/EMA/… on the chart rail."
        : string.Join(" · ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel));
    public bool HasResearchConditionSearchResult => ResearchConditionSearchResult is not null;
    public bool CanSearchResearchCondition =>
        PendingResearchCondition is not null &&
        !IsResearchConditionSearching &&
        _researchConditionSearch is not null;
    public bool CanSaveResearchReference => PendingResearchCondition is not null;
    public bool HasResearchReferenceA => _researchAnalysisReferences.ContainsKey("A");
    public bool HasResearchReferenceB => _researchAnalysisReferences.ContainsKey("B");
    public string ResearchReferenceAText =>
        _researchAnalysisReferences.TryGetValue("A", out var a) ? a.SummaryText : "Reference A: empty";
    public string ResearchReferenceBText =>
        _researchAnalysisReferences.TryGetValue("B", out var b) ? b.SummaryText : "Reference B: empty";
    public string ResearchConditionSearchSummaryText => ResearchConditionSearchResult is null
        ? "No condition search yet. Apply a condition, then Search local or Search TSD."
        : $"{ResearchConditionSearchResult.DataSource} · {ResearchConditionSearchResult.Symbol} · " +
          $"universe {ResearchConditionSearchResult.UniverseBars} · hits {ResearchConditionSearchResult.HitCount} · " +
          $"fwd+ {ResearchConditionSearchResult.PositiveForwardCount} / fwd- {ResearchConditionSearchResult.NegativeForwardCount} · " +
          $"live {(ResearchConditionSearchResult.LiveMeetsCondition is null ? "n/a" : ResearchConditionSearchResult.LiveMeetsCondition.Value ? "MEETS" : "no")} · " +
          $"ver {ResearchConditionSearchResult.ConditionVersionHashSha256[..Math.Min(12, ResearchConditionSearchResult.ConditionVersionHashSha256.Length)]}";

    /// <summary>Still-valid badge: same version hash as pending condition + live meet/fail.</summary>
    public string ResearchConditionValidityBadgeText
    {
        get
        {
            if (PendingResearchCondition is null)
                return "Condition: none";
            if (ResearchConditionSearchResult is null)
                return $"Condition ver {PendingResearchCondition.VersionShort} · not scanned";
            var sameVersion = string.Equals(
                ResearchConditionSearchResult.ConditionVersionHashSha256,
                PendingResearchCondition.VersionHashSha256,
                StringComparison.Ordinal);
            var live = ResearchConditionSearchResult.LiveMeetsCondition switch
            {
                true => "SATISFIED now",
                false => "not satisfied now",
                null => "live n/a",
            };
            return sameVersion
                ? $"Same version {PendingResearchCondition.VersionShort} · {live}"
                : $"Stale scan (ver mismatch) · re-search required";
        }
    }

    public bool CanBindResearchConditionToDraft =>
        PendingResearchCondition is not null &&
        PendingStrategyDraft is not null &&
        PendingStrategyDraft is { IsLocked: false };

    /// <summary>
    /// Research → Design handoff: promote the current observation/condition into Design without
    /// requiring an executable strategy to already exist.
    /// </summary>
    public bool CanUseObservationInDesign =>
        GenerateCandidateFirst &&
        !IsGenerating &&
        (HasResearchChartSelection ||
         HasResearchReferenceA ||
         HasResearchReferenceB ||
         SelectedResearchGalleryCard is not null ||
         ResearchEventSampleCount > 0 ||
         HasPendingResearchCondition);

    /// <summary>
    /// True after the user explicitly promotes Research evidence into Design. Existing drafts with
    /// confirmed intent or a strategy specification also count as Design-ready without this flag.
    /// </summary>
    [ObservableProperty] private bool _hasResearchDesignHandoff;

    partial void OnHasResearchDesignHandoffChanged(bool value)
    {
        NotifyWorkingFlowMapChanged();
        NotifyAuthoringScreenStateChanged();
    }

    public int ResearchEventSampleCount => ResearchDatasetDefinition?.Samples.Count ?? 0;
    public bool HasResearchExperimentEvidence => ResearchExperimentEvidence is not null;
    public bool CanRunResearchExperiment =>
        _researchExperimentRunner is not null && ResearchEventSampleCount >= 4 && !IsResearchExperimentRunning;
    public bool ResearchEvidenceReadyForGeneration => ResearchDatasetDefinition is null ||
        ResearchExperimentEvidence is not null && string.Equals(
            ResearchExperimentEvidence.DatasetHashSha256,
            ResearchDatasetCanonicalJsonV1.Hash(ResearchDatasetDefinition),
            StringComparison.Ordinal);
    public IReadOnlyList<ResearchEventSampleV1> ResearchEventSamples =>
        ResearchDatasetDefinition?.Samples ?? [];
    public string ResearchDatasetStatusText => ResearchDatasetDefinition is null
        ? "No event samples yet. Brush an observation window on the host chart, then extend the shaded future outcome window."
        : $"{ResearchEventSampleCount} labeled event sample(s) · {ResearchDatasetDefinition.RequiredData} · future outcome excluded from features";
    public string ResearchChartSelectionText => PendingResearchChartSelection is null
        ? "No chart window selected"
        : $"{PendingResearchChartSelection.CanonicalSymbol} · {PendingResearchChartSelection.Timeframe.ToDisplayString()} · " +
          $"observe {PendingResearchChartSelection.ObservationFromUtc:u} → {PendingResearchChartSelection.ObservationToUtc:u} · " +
          $"outcome {PendingResearchChartSelection.OutcomeFromUtc:u} → {PendingResearchChartSelection.OutcomeToUtc:u}" +
          (PendingResearchIndicatorBindings.Count == 0
              ? PendingResearchOverlayIds.Count == 0
                  ? ""
                  : $" · overlays [{string.Join(", ", PendingResearchOverlayIds)}]"
              : $" · indicators [{string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))}]");
    public string ResearchExperimentStatusText => ResearchExperimentEvidence is null
        ? ResearchEventSampleCount < 4
            ? $"Add {4 - ResearchEventSampleCount} more labeled sample(s) for chronological train/validation/test evidence."
            : _researchExperimentRunner is null
                ? "The local research runner is unavailable."
                : "Ready to extract observation-only bars, quotes, trades, and depth."
        : $"{ResearchExperimentEvidence.Features.Count} features · " +
          $"split {ResearchExperimentEvidence.Split.TrainingCount}/{ResearchExperimentEvidence.Split.ValidationCount}/{ResearchExperimentEvidence.Split.TestCount} · " +
          $"holdout accuracy {ResearchExperimentEvidence.TestAccuracy:P0} · exploratory only";
    public string ResearchFormulaText => ResearchExperimentEvidence?.Formula ?? "No research formula yet.";

    /// <summary>
    /// Called only by the trusted host chart overlay. Authored visualizer/strategy code never receives
    /// this mutation seam or an Avalonia pointer event.
    /// </summary>
    public void SetResearchChartSelection(
        ResearchChartSelectionV1 selection,
        IReadOnlyList<string>? activeOverlayIds = null,
        IReadOnlyList<ResearchIndicatorBindingV1>? indicatorBindings = null)
    {
        ResearchDatasetValidatorV1.RequireValidSelection(selection);
        PendingResearchChartSelection = selection;
        PendingResearchIndicatorBindings = NormalizeBindings(indicatorBindings);
        PendingResearchOverlayIds = PendingResearchIndicatorBindings.Count > 0
            ? OverlayIdsFromBindings(PendingResearchIndicatorBindings)
            : NormalizeOverlayIds(activeOverlayIds);
        EnterResearchWorkspace();
        Status = PendingResearchIndicatorBindings.Count == 0
            ? "Observation and future outcome windows selected. Label the event B, C, or N."
            : $"Selection kept with [{string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))}]. Label B, C, or N.";
        Save();
    }

    /// <summary>
    /// Mirror the live Research chart's indicator toggles into pending Research state so DETAILS,
    /// Find similar, and Design handoff stay in the same space without requiring Keep first.
    /// Does not clear or invent a chart selection window.
    /// </summary>
    public void SyncResearchChartIndicators(
        IReadOnlyList<ResearchIndicatorBindingV1>? indicatorBindings,
        IReadOnlyList<string>? activeOverlayIds = null)
    {
        PendingResearchIndicatorBindings = NormalizeBindings(indicatorBindings);
        PendingResearchOverlayIds = PendingResearchIndicatorBindings.Count > 0
            ? OverlayIdsFromBindings(PendingResearchIndicatorBindings)
            : NormalizeOverlayIds(activeOverlayIds);
        OnPropertyChanged(nameof(ResearchChartSelectionText));
        OnPropertyChanged(nameof(ActiveResearchContextText));
    }

    private static IReadOnlyList<ResearchIndicatorBindingV1> NormalizeBindings(
        IReadOnlyList<ResearchIndicatorBindingV1>? bindings)
    {
        if (bindings is null || bindings.Count == 0)
            return Array.Empty<ResearchIndicatorBindingV1>();
        return bindings
            .Where(static b => b is not null && !string.IsNullOrWhiteSpace(b.Kind))
            .Select(static b => b with
            {
                BindingId = (b.BindingId ?? string.Empty).Trim(),
                Kind = b.Kind.Trim().ToLowerInvariant(),
                Period = b.Period < 0 ? 0 : b.Period,
            })
            .ToArray();
    }

    private static IReadOnlyList<string> OverlayIdsFromBindings(
        IReadOnlyList<ResearchIndicatorBindingV1> bindings)
    {
        var ids = new List<string>();
        foreach (var b in bindings)
        {
            if (b.BindingId.StartsWith("user.", StringComparison.Ordinal))
            {
                ids.Add(b.BindingId["user.".Length..]);
                continue;
            }

            ids.Add(b.Kind switch
            {
                "sma" => "sma-20",
                "ema" => b.Period <= 20 ? "ema-20" : b.Period == 50 ? "ema-50" : $"ema-{b.Period}",
                "rsi" => "rsi-14",
                "macd" => "macd-12-26-9",
                "bollinger" => "bollinger-20",
                "stochastic" => "stochastic-14-3-3",
                "atr" => "atr-14",
                "vwap" => "vwap",
                "adx" => "adx-14",
                _ => b.BindingId,
            });
        }

        return ids.Count == 0 ? Array.Empty<string>() : ids;
    }

    private static IReadOnlyList<string> NormalizeOverlayIds(IReadOnlyList<string>? overlayIds)
    {
        if (overlayIds is null || overlayIds.Count == 0)
            return Array.Empty<string>();
        return overlayIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string> OverlaysForResearchPreview() =>
        PendingResearchOverlayIds.Count > 0
            ? PendingResearchOverlayIds
            : Array.Empty<string>();

    [RelayCommand]
    private void ApplyPendingResearchCondition()
    {
        if (!double.TryParse(
                PendingConditionMultipleText.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var multiple) ||
            multiple <= 0)
        {
            Status = "Condition multiple must be a number > 0 (e.g. 2 or 3).";
            return;
        }

        if (!int.TryParse(
                PendingConditionLookbackText.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var lookback) ||
            lookback < 1)
        {
            Status = "Condition lookback must be an integer >= 1.";
            return;
        }

        PendingResearchCondition = ResearchConditionDefinitionV1.VolumeMultiple(multiple, lookback);
        ResearchConditionSearchResult = null; // edit → must re-search (IG-1)
        Status = $"Condition set: {PendingResearchCondition.SummaryText} · ver {PendingResearchCondition.VersionShort}";
        SearchResearchConditionLocalCommand.NotifyCanExecuteChanged();
        SearchResearchConditionTsdCommand.NotifyCanExecuteChanged();
        Save();
    }

    [RelayCommand]
    private void SetConditionMultipleTwo()
    {
        PendingConditionMultipleText = "2";
        ApplyPendingResearchCondition();
    }

    [RelayCommand]
    private void SetConditionMultipleThree()
    {
        PendingConditionMultipleText = "3";
        ApplyPendingResearchCondition();
    }

    [RelayCommand]
    private void ClearPendingResearchCondition()
    {
        PendingResearchCondition = null;
        ResearchConditionSearchResult = null;
        Status = "Pending research condition cleared.";
        SearchResearchConditionLocalCommand.NotifyCanExecuteChanged();
        SearchResearchConditionTsdCommand.NotifyCanExecuteChanged();
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanSearchResearchConditionAction))]
    private async Task SearchResearchConditionLocalAsync()
    {
        if (PendingResearchCondition is not { } condition || _researchConditionSearch is null)
            return;

        ResearchChartSelectionV1? selection = PendingResearchChartSelection
            ?? ResearchDatasetDefinition?.Samples.LastOrDefault()?.Selection;
        if (selection is null)
        {
            Status = "Need a chart selection or labeled sample to pick instrument/timeframe for local search.";
            return;
        }

        IsResearchConditionSearching = true;
        try
        {
            ResearchConditionSearchResult = await _researchConditionSearch.SearchLocalAsync(
                    condition,
                    selection.InstrumentId,
                    selection.CanonicalSymbol,
                    selection.Timeframe)
                .ConfigureAwait(true);
            Status =
                $"Local search: {ResearchConditionSearchResult.HitCount} hits · " +
                ResearchConditionSearchResult.Note;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local condition search failed");
            Status = $"Local condition search failed: {ex.Message}";
        }
        finally
        {
            IsResearchConditionSearching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchResearchConditionAction))]
    private async Task SearchResearchConditionTsdAsync()
    {
        if (PendingResearchCondition is not { } condition || _researchConditionSearch is null)
            return;

        var symbol = PendingResearchChartSelection?.CanonicalSymbol
            ?? ResearchChartInstrumentText
            ?? ResearchDatasetDefinition?.Samples.LastOrDefault()?.Selection.CanonicalSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            Status = "TSD search needs a chart instrument (open a chart or save an observation first).";
            return;
        }

        var timeframe = PendingResearchChartSelection?.Timeframe
            ?? BarSizeExtensions.ParseOrDefault(ResearchScreenBarSizeChoice, BarSize.OneHour);
        var interval = timeframe.ToDisplayString();

        IsResearchConditionSearching = true;
        try
        {
            ResearchConditionSearchResult = await _researchConditionSearch.SearchTsdAsync(
                    condition,
                    symbol,
                    interval)
                .ConfigureAwait(true);
            Status =
                $"TSD search ({ResearchConditionSearchResult.DataSource}, {interval}): {ResearchConditionSearchResult.HitCount} hits · " +
                ResearchConditionSearchResult.Note;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TSD condition search failed");
            Status = $"TSD condition search failed: {ex.Message}";
        }
        finally
        {
            IsResearchConditionSearching = false;
        }
    }

    private bool CanSearchResearchConditionAction() => CanSearchResearchCondition;

    /// <summary>
    /// Open a condition-search hit on the shared Research chart with the current indicator space.
    /// </summary>
    [RelayCommand]
    private void OpenResearchConditionHit(ResearchConditionHitV1? hit)
    {
        if (hit is null || ResearchConditionSearchResult is null)
            return;
        if (PendingResearchChartSelection is not { } current || current.InstrumentId.IsNone)
        {
            Status = "Keep a setup on the Research chart first, then open condition hits in that space.";
            return;
        }

        var symbol = ResearchConditionSearchResult.Symbol;
        var timeframe = current.Timeframe;
        var instrumentId = current.InstrumentId;
        var lookback = PendingResearchCondition?.LookbackBars ?? 20;
        var forward = ResearchConditionEvaluatorV1.DefaultForwardBars;

        // Approximate observation length from lookback bars using the selection timeframe.
        var bar = hit.BarTimeUtc;
        var obsSpan = TimeSpanForBars(timeframe, lookback);
        var outSpan = TimeSpanForBars(timeframe, forward);
        var observationFrom = bar - obsSpan;
        var observationTo = bar;
        var outcomeFrom = bar;
        var outcomeTo = bar + outSpan;

        var selection = new ResearchChartSelectionV1(
            instrumentId,
            symbol,
            timeframe,
            observationFrom,
            observationTo,
            outcomeFrom,
            outcomeTo,
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars);
        try
        {
            ResearchDatasetValidatorV1.RequireValidSelection(selection);
        }
        catch (ArgumentException exception)
        {
            Status = $"Cannot open hit: {exception.Message}";
            return;
        }

        SetResearchChartSelection(
            selection,
            OverlaysForResearchPreview(),
            PendingResearchIndicatorBindings.Count > 0 ? PendingResearchIndicatorBindings : null);
        SetBoundResearchChartInstrument(symbol);
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                OverlaysForResearchPreview().ToArray(),
                preferredSymbol: symbol,
                researchSelection: selection));
        Status =
            $"Opened condition hit {symbol} @ {bar:u} · metric {hit.ConditionMetric:0.00} · fwd {hit.ForwardReturn:P1}.";
    }

    private static TimeSpan TimeSpanForBars(BarSize timeframe, int bars) =>
        TimeSpan.FromTicks(Math.Max(1, bars) * timeframe.ToTimeSpan().Ticks);

    [RelayCommand(CanExecute = nameof(CanUseObservationInDesign))]
    private void UseObservationInDesign()
    {
        if (!CanUseObservationInDesign) return;

        // Handoff unit is a versioned reference (U07/R11), not chat prose alone.
        if (PendingResearchCondition is not null)
        {
            if (!HasResearchReferenceA)
                SaveResearchReference("A");
        }
        else if (!HasResearchReferenceA && !HasResearchReferenceB)
        {
            Status =
                "Bookmark Reference A (or B) from an applied condition before Use in Strategy Builder. " +
                "Research and Builder stay linked through references, not duplicated toolbars.";
            return;
        }

        var eventSymbol = SelectedResearchGalleryCard?.Symbol
            ?? PendingResearchChartSelection?.CanonicalSymbol
            ?? (_researchAnalysisReferences.TryGetValue("A", out var refA)
                ? refA.Selection?.CanonicalSymbol
                : null)
            ?? (_researchAnalysisReferences.TryGetValue("B", out var refB)
                ? refB.Selection?.CanonicalSymbol
                : null)
            ?? "the selected instrument";
        var activeReference = HasResearchReferenceA
            ? ResearchReferenceAText
            : HasResearchReferenceB
                ? ResearchReferenceBText
                : "(none)";
        var indicators = HasPendingResearchIndicatorBindings
            ? string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))
            : PendingResearchOverlayIds.Count > 0
                ? string.Join(", ", PendingResearchOverlayIds)
                : "none locked yet";
        var condition = PendingResearchConditionText;
        var selection = ResearchChartSelectionText;
        var evidence =
            $"Use this Research reference in Design for {eventSymbol}.\n\n" +
            $"Active reference: {activeReference}\n" +
            $"Selection: {selection}\n" +
            $"Analysis indicators (candidates — include only what Design confirms): {indicators}\n" +
            $"Candidate condition: {condition}\n" +
            $"Chart instrument binding: {ResearchChartInstrumentText ?? "unset"}\n" +
            $"Samples labeled: {ResearchEventSampleCount}\n\n" +
            "Write explicit entry, confirmation, exit, sizing, and risk rules from this evidence. " +
            "Do not treat analysis-only indicators as strategy rules until confirmed here.";

        if (string.IsNullOrWhiteSpace(Composer) || Composer.StartsWith("Investigate before writing", StringComparison.Ordinal))
            Composer = evidence;
        else
            Composer = evidence + "\n\n---\n\n" + Composer.Trim();

        HasResearchDesignHandoff = true;
        AiStatus = "Design opened from Research reference. Confirm which conditions become strategy rules.";
        Status = IsResearchStudioShell
            ? "Opening Strategy Builder with the saved research reference."
            : "Research reference attached to Design. No compile or register yet.";
        Append(AuthoringMessage.Tool(
            "Ok",
            IsResearchStudioShell ? "Use in Strategy Builder" : "Use observation in Design",
            $"Reference · {eventSymbol} · indicators [{indicators}] · samples {ResearchEventSampleCount}."));
        Save();

        if (IsResearchStudioShell)
            StrategyBuilderHandoffRequested?.Invoke(this, EventArgs.Empty);

        ActiveScreen = StrategyAuthoringScreen.Design;
        WorkbenchTab = 3;
        NotifyWorkingFlowMapChanged();
        NotifyAuthoringScreenStateChanged();
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when Research Studio asks MainWindow to open Strategy Builder with this handoff.</summary>
    public event EventHandler? StrategyBuilderHandoffRequested;

    [RelayCommand(CanExecute = nameof(CanBindResearchConditionToDraft))]
    private void BindResearchConditionToDraft()
    {
        if (PendingResearchCondition is not { } condition || PendingStrategyDraft is null)
            return;

        var sampleIds = ResearchDatasetDefinition?.Samples
            .Where(s => s.Condition is not null &&
                        string.Equals(
                            s.Condition.VersionHashSha256,
                            condition.VersionHashSha256,
                            StringComparison.Ordinal))
            .Select(static s => s.EventSampleId)
            .ToArray()
            ?? Array.Empty<string>();

        PendingStrategyDraft = StrategyDraftGestureApplierV1.BindResearchCondition(
            PendingStrategyDraft,
            condition,
            sampleIds);
        Status =
            $"Draft bound to condition {condition.ConditionId} · ver {condition.VersionShort} " +
            $"(not a formula re-narration).";
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanSaveResearchReference))]
    private void SaveResearchReferenceA() => SaveResearchReference("A");

    [RelayCommand(CanExecute = nameof(CanSaveResearchReference))]
    private void SaveResearchReferenceB() => SaveResearchReference("B");

    [RelayCommand(CanExecute = nameof(HasResearchReferenceA))]
    private void RestoreResearchReferenceA() => RestoreResearchReference("A");

    [RelayCommand(CanExecute = nameof(HasResearchReferenceB))]
    private void RestoreResearchReferenceB() => RestoreResearchReference("B");

    private void SaveResearchReference(string label)
    {
        if (PendingResearchCondition is not { } condition)
            return;

        var reference = new ResearchAnalysisReferenceV1(
            ResearchAnalysisReferenceV1.CurrentSchemaVersion,
            ReferenceId: label.Trim().ToUpperInvariant(),
            Label: label.Trim().ToUpperInvariant(),
            SavedAtUtc: DateTimeOffset.UtcNow,
            Condition: condition,
            SearchResult: ResearchConditionSearchResult,
            Selection: PendingResearchChartSelection,
            IndicatorBindings: PendingResearchIndicatorBindings.Count == 0
                ? Array.Empty<ResearchIndicatorBindingV1>()
                : PendingResearchIndicatorBindings.ToArray());

        _researchAnalysisReferences[reference.ReferenceId] = reference;
        NotifyResearchReferencesChanged();
        Status = $"Saved research reference {reference.ReferenceId} in-app (no file upload) · {reference.SummaryText}";
        Save();
    }

    private void RestoreResearchReference(string label)
    {
        if (!_researchAnalysisReferences.TryGetValue(label, out var reference))
        {
            Status = $"Research reference {label} is empty.";
            return;
        }

        PendingResearchCondition = reference.Condition;
        PendingConditionMultipleText = reference.Condition.Threshold.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        PendingConditionLookbackText = reference.Condition.LookbackBars.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        ResearchConditionSearchResult = reference.SearchResult;
        if (reference.Selection is not null)
            PendingResearchChartSelection = reference.Selection;
        PendingResearchIndicatorBindings = reference.IndicatorBindings.Count == 0
            ? Array.Empty<ResearchIndicatorBindingV1>()
            : reference.IndicatorBindings.ToArray();
        PendingResearchOverlayIds = PendingResearchIndicatorBindings.Count > 0
            ? OverlayIdsFromBindings(PendingResearchIndicatorBindings)
            : Array.Empty<string>();
        EnterResearchWorkspace();
        Status = $"Restored reference {reference.ReferenceId} · {reference.SummaryText}";
        SearchResearchConditionLocalCommand.NotifyCanExecuteChanged();
        SearchResearchConditionTsdCommand.NotifyCanExecuteChanged();
        BindResearchConditionToDraftCommand.NotifyCanExecuteChanged();
        SaveResearchReferenceACommand.NotifyCanExecuteChanged();
        SaveResearchReferenceBCommand.NotifyCanExecuteChanged();
        Save();

        // Drive the shared Research chart so Restore A/B is not authoring-only.
        if (reference.Selection is { } selection)
        {
            HostChartOverlayPreviewRequested?.Invoke(
                this,
                new HostChartOverlayPreviewRequestedEventArgs(
                    OverlaysForResearchPreview().ToArray(),
                    preferredSymbol: selection.CanonicalSymbol,
                    researchSelection: selection));
        }
        else if (PendingResearchOverlayIds.Count > 0)
        {
            HostChartOverlayPreviewRequested?.Invoke(
                this,
                new HostChartOverlayPreviewRequestedEventArgs(OverlaysForResearchPreview().ToArray()));
        }
    }

    private void NotifyResearchReferencesChanged()
    {
        OnPropertyChanged(nameof(HasResearchReferenceA));
        OnPropertyChanged(nameof(HasResearchReferenceB));
        OnPropertyChanged(nameof(ResearchReferenceAText));
        OnPropertyChanged(nameof(ResearchReferenceBText));
        OnPropertyChanged(nameof(CanSaveResearchReference));
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        RestoreResearchReferenceACommand.NotifyCanExecuteChanged();
        RestoreResearchReferenceBCommand.NotifyCanExecuteChanged();
        SaveResearchReferenceACommand.NotifyCanExecuteChanged();
        SaveResearchReferenceBCommand.NotifyCanExecuteChanged();
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLabelResearchSelection))]
    private void MarkPreBreakout() => CommitResearchSelection(ResearchEventLabelKindV1.PreBreakout);

    [RelayCommand(CanExecute = nameof(CanLabelResearchSelection))]
    private void MarkPreCrash() => CommitResearchSelection(ResearchEventLabelKindV1.PreCrash);

    [RelayCommand(CanExecute = nameof(CanLabelResearchSelection))]
    private void MarkNeutral() => CommitResearchSelection(ResearchEventLabelKindV1.Neutral);

    private bool CanLabelResearchSelection() => PendingResearchChartSelection is not null && !IsGenerating;

    [RelayCommand]
    private void ClearResearchChartSelection()
    {
        PendingResearchChartSelection = null;
        PendingResearchOverlayIds = Array.Empty<string>();
        PendingResearchIndicatorBindings = Array.Empty<ResearchIndicatorBindingV1>();
        Status = "Chart research selection cleared. No dataset sample was changed.";
        Save();
    }

    [RelayCommand]
    private void RemoveResearchEventSample(ResearchEventSampleV1? sample)
    {
        if (sample is null || ResearchDatasetDefinition is null ||
            !ResearchDatasetDefinition.Samples.Contains(sample)) return;

        var remaining = ResearchDatasetDefinition.Samples.Where(item => item != sample).ToArray();
        if (remaining.Length == 0)
        {
            ResearchDatasetDefinition = null;
            ApplyResearchDatasetWorkspaceChange(null, "Removed final research event sample");
            Status = "The final event sample was removed; the research dataset is empty.";
            Save();
            return;
        }

        var requiredData = remaining.Aggregate(
            StrategyDataRequirement.None,
            static (value, item) => value | item.Selection.RequiredData);
        var updated = ResearchDatasetDefinition with
        {
            WorkspaceRevisionHashSha256 = StrategyWorkspaceCanonicalJsonV1.Hash(StrategyWorkspace),
            RequiredData = requiredData,
            Samples = remaining,
        };
        ResearchDatasetValidatorV1.RequireStructurallyValid(updated);
        ResearchDatasetDefinition = updated;
        ApplyResearchDatasetWorkspaceChange(updated, "Removed research event sample");
        Status = $"Removed one event sample; {remaining.Length} remain.";
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanRunResearchExperimentAction))]
    private async Task RunResearchExperimentAsync()
    {
        if (!CanRunResearchExperiment || ResearchDatasetDefinition is not { } dataset ||
            _researchExperimentRunner is null) return;

        var datasetHash = ResearchDatasetCanonicalJsonV1.Hash(dataset);
        IsResearchExperimentRunning = true;
        Status = "Extracting observation-only market features and fitting training-only transforms…";
        try
        {
            var evidence = await _researchExperimentRunner.RunAsync(dataset);
            if (ResearchDatasetDefinition is null || !string.Equals(
                    datasetHash,
                    ResearchDatasetCanonicalJsonV1.Hash(ResearchDatasetDefinition),
                    StringComparison.Ordinal))
            {
                Status = "The research dataset changed while the experiment was running; the stale result was discarded.";
                return;
            }

            ResearchExperimentValidatorV1.RequireValid(evidence);
            if (!string.Equals(evidence.DatasetHashSha256, datasetHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The research result did not bind the exact labeled dataset.");
            ResearchExperimentEvidence = evidence;
            ApplyResearchDatasetWorkspaceChange(dataset, "Produced chronological research feature evidence");
            Status = $"Research evidence ready: {evidence.Formula}. Historical validation is still required before Paper.";
            PublishTurnFollowUps(lastUserText: "research experiment complete");
            Save();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Research experiment failed for {Id}", StrategyId);
            Status = $"Research experiment failed: {exception.Message}";
        }
        finally
        {
            IsResearchExperimentRunning = false;
        }
    }

    private bool CanRunResearchExperimentAction() => CanRunResearchExperiment;

    private void CommitResearchSelection(
        ResearchEventLabelKindV1 label,
        ResearchEventLabelSourceV1 source = ResearchEventLabelSourceV1.Manual)
    {
        if (PendingResearchChartSelection is not { } selection || IsGenerating) return;

        var identityPayload = new ResearchSampleIdentity(selection, label);
        var sampleId = "event-" + StrategyWorkspaceCanonicalJsonV1.HashArtifact(identityPayload)[..20];
        var samples = ResearchDatasetDefinition?.Samples.ToList() ?? [];
        if (samples.Any(sample => string.Equals(sample.EventSampleId, sampleId, StringComparison.Ordinal)))
        {
            Status = "That exact chart window and label already exist in the dataset.";
            return;
        }

        samples.Add(new ResearchEventSampleV1(
            ResearchEventSampleV1.CurrentSchemaVersion,
            sampleId,
            selection,
            label,
            null,
            source,
            Note: null,
            IndicatorBindings: PendingResearchIndicatorBindings.Count == 0
                ? null
                : PendingResearchIndicatorBindings.ToArray(),
            Condition: PendingResearchCondition));
        var requiredData = samples.Aggregate(
            StrategyDataRequirement.None,
            static (value, item) => value | item.Selection.RequiredData);
        var updated = new ResearchDatasetDefinitionV1(
            ResearchDatasetDefinitionV1.CurrentSchemaVersion,
            $"{StrategyId.Trim()}.research",
            StrategyWorkspaceCanonicalJsonV1.Hash(StrategyWorkspace),
            requiredData,
            ResearchDatasetDefinition?.LeakagePolicy ?? ResearchLeakagePolicyV1.SafeDefault,
            samples);
        ResearchDatasetValidatorV1.RequireStructurallyValid(updated);

        ResearchDatasetDefinition = updated;
        PendingResearchChartSelection = null;
        // Keep indicator bindings — they define the current Research study space for the next event.
        ApplyResearchDatasetWorkspaceChange(updated, $"Added {label} research event sample");
        var remaining = Math.Max(0, 4 - updated.Samples.Count);
        Status = remaining > 0
            ? $"Added {label} sample ({updated.Samples.Count}/4 for research experiment). Future outcome excluded from features."
            : $"Added {label} sample. Dataset has {updated.Samples.Count} events — run the research experiment when ready.";
        Save();
        if (!_suppressGalleryAdvance)
            TryAdvanceToNextUnusedGalleryMatch();
        PublishTurnFollowUps(lastUserText: null);
    }

    private bool _suppressGalleryAdvance;

    /// <summary>
    /// After B/C/N, load the next unused gallery hit onto Charts so the original
    /// research→label loop does not stall on a single event.
    /// </summary>
    private void TryAdvanceToNextUnusedGalleryMatch()
    {
        if (ResearchOutcomeGalleryResult is not { Matches.Count: > 0 } gallery)
            return;

        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (ResearchDatasetDefinition is { Samples: { } samples })
        {
            foreach (var sample in samples)
            {
                usedKeys.Add(GalleryMatchKey(
                    sample.Selection.CanonicalSymbol,
                    sample.Selection.ObservationFromUtc.UtcDateTime,
                    sample.Selection.OutcomeFromUtc.UtcDateTime));
            }
        }

        var next = gallery.Matches.FirstOrDefault(match =>
            !usedKeys.Contains(GalleryMatchKey(
                match.CanonicalSymbol,
                match.ObservationFromUtc,
                match.OutcomeFromUtc)));
        if (next is null)
            return;

        UseResearchOutcomeGalleryMatch(next);
        Status =
            $"{Status} Selected next gallery box {next.CanonicalSymbol} · {next.OutcomeReturn:P1} for labeling.";
    }

    private static string GalleryMatchKey(string symbol, DateTime observationFromUtc, DateTime outcomeFromUtc) =>
        $"{symbol}|{observationFromUtc:O}|{outcomeFromUtc:O}";

    /// <summary>
    /// Scan local Simulated history for the chosen research outcome, open Charts on hits, and
    /// auto-label up to four samples so the user can click Run research experiment next.
    /// </summary>
    [RelayCommand]
    private async Task AutoCollectLocalResearchSamplesAsync(string? scanId)
    {
        var primary = string.IsNullOrWhiteSpace(scanId) ? "next-day-plus-5" : scanId.Trim();
        if (!ResearchOutcomeEventFinderV1.TryDescribeScan(primary, out var displayName, out _))
        {
            AiStatus = $"Unknown research scan '{primary}'.";
            return;
        }

        if (_researchOutcomeGalleryScan is null)
        {
            AiStatus = "Outcome gallery scan is not registered in this composition.";
            return;
        }

        EnterResearchWorkspace();
        Append(new AuthoringMessage(
            CodegenRole.User,
            $"Auto-collect research samples from my local Simulated history ({displayName})."));
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                PendingResearchOverlayIds.Count > 0
                    ? PendingResearchOverlayIds.ToArray()
                    : Array.Empty<string>(),
                startResearchCapture: true));

        IsScanningResearchGallery = true;
        AiStatus = $"Scanning local Simulated history for {displayName}…";
        try
        {
            ResearchOutcomeGalleryResult = await _researchOutcomeGalleryScan.ScanAsync(
                new ResearchOutcomeGalleryScanRequestV1(primary),
                CancellationToken.None);
            var matches = ResearchOutcomeGalleryResult.Matches;
            Append(AuthoringMessage.Tool(
                matches.Count == 0 ? "Warn" : "Ok",
                ResearchOutcomeGalleryResult.DisplayName,
                ResearchOutcomeGalleryResult.Explanation));

            if (matches.Count == 0)
            {
                AiStatus =
                    $"No {displayName} hits in local history. Chart is open for manual brush — or try another suggestion chip.";
                Status = AiStatus;
                Save();
                return;
            }

            var label = primary switch
            {
                "pre-crash" => ResearchEventLabelKindV1.PreCrash,
                "pre-breakout" => ResearchEventLabelKindV1.PreBreakout,
                _ => ResearchEventLabelKindV1.PreBreakout,
            };

            var labeled = 0;
            _suppressGalleryAdvance = true;
            try
            {
                foreach (var match in matches.Take(4))
                {
                    FocusResearchGalleryMatch(match, openChart: false, announce: false);
                    if (PendingResearchChartSelection is null)
                        continue;
                    CommitResearchSelection(label, ResearchEventLabelSourceV1.RuleSuggestedHumanReviewed);
                    labeled++;
                }
            }
            finally
            {
                _suppressGalleryAdvance = false;
            }

            if (labeled == 0)
            {
                AiStatus = "Gallery found events but none could be committed as research samples.";
                Status = AiStatus;
                Save();
                return;
            }

            // One Charts window for review — not one window per sample.
            FocusResearchGalleryMatch(matches[Math.Min(labeled, matches.Count) - 1], openChart: true, announce: false);
            AiStatus = labeled >= 4
                ? $"Captured {labeled} before-jump samples (observation = state before the move; scores on each box). Click ▶ Run chronological experiment."
                : $"Captured {labeled} before-jump sample(s) (need {4 - labeled} more). Click chip 1 again or label another box.";
            Status = AiStatus;
            Append(new AuthoringMessage(CodegenRole.Assistant, AiStatus));
            PublishTurnFollowUps(lastUserText: displayName);
            Save();
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Research auto-collect stopped.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Research auto-collect failed");
            AiStatus = $"Research auto-collect stopped: {exception.Message}";
        }
        finally
        {
            IsScanningResearchGallery = false;
        }
    }

    [RelayCommand]
    private async Task ApplyResearchQuickSuggestionAsync(ResearchQuickSuggestionV1? suggestion)
    {
        if (suggestion is null) return;

        if (suggestion.Kind == ResearchQuickSuggestionKindV1.AutoCollectLocalGallery)
        {
            await AutoCollectLocalResearchSamplesAsync(suggestion.ScanId);
            return;
        }

        if (suggestion.Kind == ResearchQuickSuggestionKindV1.RunResearchExperiment)
        {
            if (!CanRunResearchExperiment)
            {
                AiStatus = ResearchEventSampleCount < 4
                    ? $"Need {4 - ResearchEventSampleCount} more before-move sample(s). Ask in chat (e.g. before jump) or wait for capture follow-ups."
                    : "Research experiment is not ready yet.";
                return;
            }

            await RunResearchExperimentCommand.ExecuteAsync(null);
            PublishTurnFollowUps(lastUserText: "run chronological experiment");
            return;
        }

        if (suggestion.Kind == ResearchQuickSuggestionKindV1.RunHistoricalValidation)
        {
            if (OpenValidateScreenCommand.CanExecute(null))
                OpenValidateScreenCommand.Execute(null);
            AiStatus = "Validate is open — click Run historical validation for the exact-hash replay.";
            Status = AiStatus;
            return;
        }

        if (suggestion.Kind == ResearchQuickSuggestionKindV1.OpenPaperHandoff)
        {
            if (OpenPaperScreenCommand.CanExecute(null))
                OpenPaperScreenCommand.Execute(null);
            AiStatus = "Paper stage is open — click Bind selected Paper book (Simulated only).";
            Status = AiStatus;
            return;
        }

        if (suggestion.Kind == ResearchQuickSuggestionKindV1.FocusFirstGalleryMatch)
        {
            var first = ResearchOutcomeGalleryResult?.Matches.FirstOrDefault();
            if (first is null)
            {
                AiStatus = "No gallery hits to open yet.";
                return;
            }

            FocusResearchGalleryMatch(first, openChart: true, announce: true);
            PublishTurnFollowUps(lastUserText: "open gallery hit");
            return;
        }

        var prompt = suggestion.Prompt?.Trim() ?? suggestion.Title;
        if (prompt.Length == 0) return;

        var chartChoice = AuthoredChartChoiceCatalogV1.Resolve(
            prompt,
            prompt,
            LoadUserChartIndicators());
        if (chartChoice.NeedsClarification &&
            chartChoice.ClarificationQuestion is { Length: > 0 } catalogQuestion)
        {
            Composer = string.Empty;
            Append(new AuthoringMessage(CodegenRole.User, prompt));
            Append(new AuthoringMessage(CodegenRole.Assistant, catalogQuestion));
            AwaitingAnswer = true;
            AiStatus = "Pick a numbered host chart choice, or click another suggestion chip.";
            Save();
            return;
        }

        if (chartChoice.HasResearchScans &&
            !AuthoredChartChoiceCatalogV1.LooksLikeTradingRequest(prompt) &&
            !AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(prompt))
        {
            await CompleteHostResearchScanAsync(prompt, chartChoice);
            return;
        }

        if (chartChoice.HasOverlays &&
            !AuthoredChartChoiceCatalogV1.LooksLikeTradingRequest(prompt) &&
            !AuthoredChartChoiceCatalogV1.LooksLikeAnalyticalResearchQuestion(prompt))
        {
            Composer = string.Empty;
            Append(new AuthoringMessage(CodegenRole.User, prompt));
            ApplyResearchOverlayAnalysis(chartChoice);
            HostChartOverlayPreviewRequested?.Invoke(
                this,
                new HostChartOverlayPreviewRequestedEventArgs(
                    chartChoice.Overlays.Select(static item => item.Id).ToArray(),
                    startResearchCapture: false));
            Append(new AuthoringMessage(
                CodegenRole.Assistant,
                $"Opened the live chart with {AuthoredChartChoiceCatalogV1.DescribeSelection(chartChoice)}. " +
                "Analysis only — Use in Design when a condition is worth testing."));
            AiStatus = "Chart overlays applied for investigation. Not strategy rules yet.";
            PublishTurnFollowUps(prompt);
            Save();
            return;
        }

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

        Composer = prompt;
        AiStatus = "Suggestion loaded in the composer — press Send when ready.";
        PublishTurnFollowUps(prompt);
    }

    private string? _lastFollowUpUserText;

    /// <summary>
    /// ChatGPT-style follow-ups for the latest turn only. Idle / unrelated chat → empty.
    /// </summary>
    public void PublishTurnFollowUps(string? lastUserText)
    {
        if (!string.IsNullOrWhiteSpace(lastUserText))
            _lastFollowUpUserText = lastUserText.Trim();

        var planned = ResearchSuggestionPlannerV1.PlanForTurn(new ResearchSuggestionPlannerV1.TurnContext(
            LastUserText: lastUserText ?? _lastFollowUpUserText,
            SampleCount: ResearchEventSampleCount,
            CanRunExperiment: CanRunResearchExperiment,
            HasExperimentEvidence: HasResearchExperimentEvidence,
            HasGalleryMatches: HasResearchOutcomeGalleryMatches,
            HasFocusedGalleryMatch: SelectedResearchGalleryCard is not null || PendingResearchChartSelection is not null,
            CanRunHistoricalValidation: CanRunHistoricalValidation,
            CanOpenPaperScreen: CanOpenPaperScreen,
            IsRegistered: IsRegistered));

        ResearchQuickSuggestions.Clear();
        foreach (var suggestion in planned)
            ResearchQuickSuggestions.Add(suggestion);
        OnPropertyChanged(nameof(HasResearchQuickSuggestions));

        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var message = Messages[i];
            if (message.IsUser) break;
            if (message.IsAssistant || message.IsSystem)
            {
                message.SetFollowUps(planned);
                break;
            }
        }
    }

    public void ClearTurnFollowUps()
    {
        _lastFollowUpUserText = null;
        ResearchQuickSuggestions.Clear();
        OnPropertyChanged(nameof(HasResearchQuickSuggestions));
        foreach (var message in Messages)
            message.SetFollowUps(null);
    }

    /// <summary>Idle refresh: no forced chips unless a prior turn left stage next-steps.</summary>
    public void RefreshResearchQuickSuggestions() =>
        PublishTurnFollowUps(lastUserText: null);

    private void ApplyResearchDatasetWorkspaceChange(
        ResearchDatasetDefinitionV1? dataset,
        string reason)
    {
        var requested = BuildCurrentWorkspaceBindings(StrategyWorkspace.Bindings) with
        {
            DatasetDefinitionHashSha256 = dataset is null
                ? null
                : ResearchDatasetCanonicalJsonV1.Hash(dataset),
        };
        StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Revise(
            StrategyWorkspace,
            StrategyWorkspaceChangeKindV1.DatasetOrFeatureDefinition,
            requested,
            StrategyWorkspaceStageV1.Research,
            revisionReason: reason);
    }

    private void RestoreResearchDataset(AuthoringSessionSnapshot session, ref string? restoreWarning)
    {
        ResearchDatasetDefinition = null;
        PendingResearchChartSelection = null;
        PendingResearchOverlayIds = Array.Empty<string>();
        PendingResearchIndicatorBindings = Array.Empty<ResearchIndicatorBindingV1>();
        PendingResearchCondition = null;
        ResearchConditionSearchResult = null;
        _researchAnalysisReferences.Clear();
        NotifyResearchReferencesChanged();
        if (string.IsNullOrWhiteSpace(session.ResearchDatasetJson))
        {
            RestoreResearchConditionState(session, ref restoreWarning);
            return;
        }

        try
        {
            ResearchDatasetDefinition = ResearchDatasetCanonicalJsonV1.Deserialize(session.ResearchDatasetJson);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Could not restore research dataset for {Id}", session.StrategyId);
            restoreWarning = "The session was restored, but its research dataset failed structural or leakage validation and was detached.";
        }

        RestoreResearchConditionState(session, ref restoreWarning);
    }

    private void RestoreResearchConditionState(AuthoringSessionSnapshot session, ref string? restoreWarning)
    {
        if (!string.IsNullOrWhiteSpace(session.ResearchConditionJson))
        {
            try
            {
                PendingResearchCondition = ResearchConditionCanonicalJsonV1.Deserialize(session.ResearchConditionJson);
                PendingConditionMultipleText = PendingResearchCondition.Threshold.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                PendingConditionLookbackText = PendingResearchCondition.LookbackBars.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not restore research condition for {Id}", session.StrategyId);
                restoreWarning ??= "Research condition failed restore and was detached.";
                PendingResearchCondition = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(session.ResearchConditionSearchResultJson))
        {
            try
            {
                ResearchConditionSearchResult = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchConditionSearchResultV1>(
                    session.ResearchConditionSearchResultJson);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not restore condition search for {Id}", session.StrategyId);
                ResearchConditionSearchResult = null;
            }
        }

        SearchResearchConditionLocalCommand.NotifyCanExecuteChanged();
        SearchResearchConditionTsdCommand.NotifyCanExecuteChanged();
        BindResearchConditionToDraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ResearchConditionValidityBadgeText));
        OnPropertyChanged(nameof(CanSaveResearchReference));
        SaveResearchReferenceACommand.NotifyCanExecuteChanged();
        SaveResearchReferenceBCommand.NotifyCanExecuteChanged();

        if (!string.IsNullOrWhiteSpace(session.ResearchAnalysisReferencesJson))
        {
            try
            {
                _researchAnalysisReferences.Clear();
                foreach (var reference in ResearchAnalysisReferenceCanonicalJsonV1.DeserializeMany(
                             session.ResearchAnalysisReferencesJson))
                {
                    _researchAnalysisReferences[reference.ReferenceId] = reference;
                }

                NotifyResearchReferencesChanged();
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not restore research references for {Id}", session.StrategyId);
                _researchAnalysisReferences.Clear();
                NotifyResearchReferencesChanged();
            }
        }

        if (!string.IsNullOrWhiteSpace(session.ResearchChartSelectionJson))
        {
            try
            {
                var selection = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchChartSelectionV1>(
                    session.ResearchChartSelectionJson);
                if (selection is not null)
                {
                    ResearchDatasetValidatorV1.RequireValidSelection(selection);
                    PendingResearchChartSelection = selection;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not restore research chart selection for {Id}", session.StrategyId);
                restoreWarning ??= "Research chart selection failed restore and was detached.";
                PendingResearchChartSelection = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(session.ResearchIndicatorBindingsJson))
        {
            try
            {
                var bindings = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchIndicatorBindingV1[]>(
                    session.ResearchIndicatorBindingsJson);
                PendingResearchIndicatorBindings = bindings is null || bindings.Length == 0
                    ? Array.Empty<ResearchIndicatorBindingV1>()
                    : NormalizeBindings(bindings);
                PendingResearchOverlayIds = PendingResearchIndicatorBindings.Count > 0
                    ? OverlayIdsFromBindings(PendingResearchIndicatorBindings)
                    : Array.Empty<string>();
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not restore research indicator bindings for {Id}", session.StrategyId);
                restoreWarning ??= "Research indicator bindings failed restore and were cleared.";
                PendingResearchIndicatorBindings = Array.Empty<ResearchIndicatorBindingV1>();
                PendingResearchOverlayIds = Array.Empty<string>();
            }
        }
    }

    private void RestoreResearchExperiment(AuthoringSessionSnapshot session, ref string? restoreWarning)
    {
        ResearchExperimentEvidence = null;
        if (ResearchDatasetDefinition is null || string.IsNullOrWhiteSpace(session.ResearchExperimentJson)) return;

        try
        {
            var evidence = ResearchExperimentCanonicalJsonV1.Deserialize(session.ResearchExperimentJson);
            var datasetHash = ResearchDatasetCanonicalJsonV1.Hash(ResearchDatasetDefinition);
            if (!string.Equals(evidence.DatasetHashSha256, datasetHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The saved research evidence belongs to another dataset revision.");
            ResearchExperimentEvidence = evidence;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Could not restore research evidence for {Id}", session.StrategyId);
            restoreWarning = "The session was restored, but stale or invalid research experiment evidence was detached.";
        }
    }

    private void InvalidateResearchDatasetForBriefChange()
    {
        if (_restoring || ResearchDatasetDefinition is null) return;
        ResearchDatasetDefinition = null;
        PendingResearchChartSelection = null;
        PendingResearchOverlayIds = Array.Empty<string>();
        PendingResearchIndicatorBindings = Array.Empty<ResearchIndicatorBindingV1>();
        ResearchExperimentEvidence = null;
    }

    partial void OnResearchDatasetDefinitionChanged(ResearchDatasetDefinitionV1? value)
    {
        if (!_restoring && ResearchExperimentEvidence is not null &&
            (value is null || !string.Equals(
                ResearchExperimentEvidence.DatasetHashSha256,
                ResearchDatasetCanonicalJsonV1.Hash(value),
                StringComparison.Ordinal)))
            ResearchExperimentEvidence = null;
        OnPropertyChanged(nameof(HasResearchDataset));
        OnPropertyChanged(nameof(ResearchEventSampleCount));
        OnPropertyChanged(nameof(ResearchEventSamples));
        OnPropertyChanged(nameof(ResearchDatasetStatusText));
        OnPropertyChanged(nameof(CanRunResearchExperiment));
        OnPropertyChanged(nameof(ResearchExperimentStatusText));
        OnPropertyChanged(nameof(ResearchEvidenceReadyForGeneration));
        OnPropertyChanged(nameof(CanGenerateFourCandidates));
        OnPropertyChanged(nameof(CanGenerateCanonicalPaperStrategy));
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        RunResearchExperimentCommand.NotifyCanExecuteChanged();
        GenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        GenerateCanonicalPaperStrategyCommand.NotifyCanExecuteChanged();
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingResearchChartSelectionChanged(ResearchChartSelectionV1? value)
    {
        OnPropertyChanged(nameof(HasResearchChartSelection));
        OnPropertyChanged(nameof(ResearchChartSelectionText));
        OnPropertyChanged(nameof(ResearchChartInstrumentText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(ActiveArtifactKindText));
        NotifyWorkingFlowMapChanged();
        MarkPreBreakoutCommand.NotifyCanExecuteChanged();
        MarkPreCrashCommand.NotifyCanExecuteChanged();
        MarkNeutralCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowResearchObservationTools));
    }

    partial void OnPendingResearchOverlayIdsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(ResearchChartSelectionText));
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingResearchIndicatorBindingsChanged(IReadOnlyList<ResearchIndicatorBindingV1> value)
    {
        OnPropertyChanged(nameof(ResearchChartSelectionText));
        OnPropertyChanged(nameof(PendingResearchIndicatorsText));
        OnPropertyChanged(nameof(HasPendingResearchIndicatorBindings));
        OnPropertyChanged(nameof(ResearchIndicatorInspectText));
        OnPropertyChanged(nameof(ResearchIndicatorCompareText));
        OnPropertyChanged(nameof(ActiveResearchContextText));
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingResearchConditionChanged(ResearchConditionDefinitionV1? value)
    {
        OnPropertyChanged(nameof(PendingResearchConditionText));
        OnPropertyChanged(nameof(HasPendingResearchCondition));
        OnPropertyChanged(nameof(CanSearchResearchCondition));
        OnPropertyChanged(nameof(CanBindResearchConditionToDraft));
        OnPropertyChanged(nameof(CanSaveResearchReference));
        OnPropertyChanged(nameof(ResearchConditionValidityBadgeText));
        OnPropertyChanged(nameof(CanUseObservationInDesign));
        SearchResearchConditionLocalCommand.NotifyCanExecuteChanged();
        SearchResearchConditionTsdCommand.NotifyCanExecuteChanged();
        BindResearchConditionToDraftCommand.NotifyCanExecuteChanged();
        SaveResearchReferenceACommand.NotifyCanExecuteChanged();
        SaveResearchReferenceBCommand.NotifyCanExecuteChanged();
        UseObservationInDesignCommand.NotifyCanExecuteChanged();
    }

    partial void OnResearchConditionSearchResultChanged(ResearchConditionSearchResultV1? value)
    {
        OnPropertyChanged(nameof(HasResearchConditionSearchResult));
        OnPropertyChanged(nameof(ResearchConditionSearchSummaryText));
        OnPropertyChanged(nameof(ResearchConditionValidityBadgeText));
    }

    partial void OnIsResearchConditionSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearchResearchCondition));
        SearchResearchConditionLocalCommand.NotifyCanExecuteChanged();
        SearchResearchConditionTsdCommand.NotifyCanExecuteChanged();
    }

    partial void OnResearchExperimentEvidenceChanged(ResearchExperimentEvidenceV1? value)
    {
        OnPropertyChanged(nameof(HasResearchExperimentEvidence));
        OnPropertyChanged(nameof(ResearchExperimentStatusText));
        OnPropertyChanged(nameof(ResearchFormulaText));
        OnPropertyChanged(nameof(ResearchEvidenceReadyForGeneration));
        OnPropertyChanged(nameof(CanGenerateFourCandidates));
        OnPropertyChanged(nameof(CanGenerateCanonicalPaperStrategy));
        GenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        GenerateCanonicalPaperStrategyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsResearchExperimentRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunResearchExperiment));
        OnPropertyChanged(nameof(ResearchExperimentStatusText));
        RunResearchExperimentCommand.NotifyCanExecuteChanged();
    }

    private sealed record ResearchSampleIdentity(
        ResearchChartSelectionV1 Selection,
        ResearchEventLabelKindV1 Label);
}
