using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.Charts;

public enum ChartResearchSelectionStep
{
    None,
    Observation,
    Outcome,
}

public sealed partial class ChartsViewModel
{
    [ObservableProperty] private ChartResearchSelectionStep _researchSelectionStep;
    [ObservableProperty] private ChartTimeRange? _researchObservationRange;
    [ObservableProperty] private ChartTimeRange? _researchOutcomeRange;

    /// <summary>Condition-search / Design-preview hits drawn on the native Research chart.</summary>
    [ObservableProperty] private IReadOnlyList<ChartConditionHitMarker> _conditionHitMarkers =
        Array.Empty<ChartConditionHitMarker>();

    /// <summary>Validate simulated fills — separate layer from condition triangles.</summary>
    [ObservableProperty] private IReadOnlyList<ChartFillHitMarker> _fillHitMarkers =
        Array.Empty<ChartFillHitMarker>();

    public bool HasConditionHitMarkers => ConditionHitMarkers.Count > 0;
    public bool HasFillHitMarkers => FillHitMarkers.Count > 0;

    public bool IsResearchRangeSelectionEnabled => ResearchSelectionStep != ChartResearchSelectionStep.None;
    public bool HasResearchObservationRange => ResearchObservationRange is not null;
    public bool HasResearchOutcomeRange => ResearchOutcomeRange is not null;
    public bool CanSendResearchSelection => CanKeepResearchSetup && HasResearchOutcomeRange;

    /// <summary>Observation interval is enough to save; outcome remains optional for similarity search.</summary>
    public bool CanKeepResearchSetup =>
        SelectedInstrument is not null && SelectedTimeframe is not null &&
        ResearchObservationRange is not null;

    /// <summary>Observation exists and no outcome yet — offer optional Add outcome.</summary>
    public bool CanStartResearchResultSelection =>
        HasResearchObservationRange && !HasResearchOutcomeRange &&
        ResearchSelectionStep != ChartResearchSelectionStep.Outcome;

    public string ResearchSelectionSummary => ResearchSelectionStep switch
    {
        ChartResearchSelectionStep.Observation => "Drag across the chart to select an observation.",
        ChartResearchSelectionStep.Outcome => "Optional: drag a later amber outcome, or Find similar now.",
        _ when CanKeepResearchSetup && HasResearchOutcomeRange => "Observation + outcome ready.",
        _ when CanKeepResearchSetup => "Observation ready — Find similar, or add an outcome.",
        _ => "Select a period on the chart, then Find similar.",
    };

    /// <summary>
    /// Compact selection chrome: period, bar count, and exact indicators on the chart.
    /// </summary>
    public string ResearchSelectionDetailSummary
    {
        get
        {
            if (ResearchObservationRange is not { } observation)
            {
                return ResearchSelectionStep == ChartResearchSelectionStep.Observation
                    ? "Drag to select an observation"
                    : ResearchSelectionStep == ChartResearchSelectionStep.Outcome
                        ? "Drag to add an outcome period"
                        : "No period selected";
            }

            var bars = CountBarsInRange(observation);
            var period =
                $"{observation.StartUtc:yyyy-MM-dd HH:mm} → {observation.EndUtcExclusive:yyyy-MM-dd HH:mm}";
            var bindings = CaptureActiveIndicatorBindings();
            var indicators = bindings.Count == 0
                ? "no indicators"
                : string.Join(", ", bindings.Select(static b => b.DisplayLabel));
            var outcome = HasResearchOutcomeRange ? " · +outcome" : string.Empty;
            var barPart = bars > 0
                ? (bars == 1 ? " · 1 bar" : $" · {bars} bars")
                : string.Empty;
            return $"{period}{barPart} · {indicators}{outcome}";
        }
    }

    public event EventHandler<ResearchChartSelectionRequestedEventArgs>? ResearchSelectionRequested;
    public event EventHandler? ResearchFindSimilarRequested;

    [RelayCommand]
    private void StartResearchSelection()
    {
        if (!HasData)
        {
            Status = "Load chart history before selecting a period.";
            return;
        }

        DraftPlacementMode = ChartInteractionMode.Pan;
        ResearchObservationRange = null;
        ResearchOutcomeRange = null;
        ResearchSelectionStep = ChartResearchSelectionStep.Observation;
        Status = "Drag across the chart to select an observation.";
        NotifyResearchShellStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartResearchResultSelection))]
    private void StartResearchResultSelection()
    {
        if (ResearchObservationRange is null)
            return;

        DraftPlacementMode = ChartInteractionMode.Pan;
        ResearchOutcomeRange = null;
        ResearchSelectionStep = ChartResearchSelectionStep.Outcome;
        Status = "Optional: drag a later amber outcome period.";
        NotifyResearchSelectionStateChanged();
    }

    /// <summary>
    /// Seeds observation→outcome from the two most recent completed bars so capture is visible
    /// immediately (host research-scan path). User can re-brush or save / Find similar.
    /// </summary>
    public void SeedCaptureWindowsFromRecentBars()
    {
        if (_lastBars.Count < 3)
        {
            if (StartResearchSelectionCommand.CanExecute(null))
                StartResearchSelectionCommand.Execute(null);
            return;
        }

        var setup = _lastBars[^3];
        var outcome = _lastBars[^2];
        var next = _lastBars[^1];
        ResearchObservationRange = new ChartTimeRange(
            new DateTimeOffset(DateTime.SpecifyKind(setup.TimestampUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(outcome.TimestampUtc, DateTimeKind.Utc)));
        ResearchOutcomeRange = new ChartTimeRange(
            new DateTimeOffset(DateTime.SpecifyKind(outcome.TimestampUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(next.TimestampUtc, DateTimeKind.Utc)));
        ResearchSelectionStep = ChartResearchSelectionStep.None;
        Status =
            $"Observation seeded on {SelectedInstrument?.Contract.Symbol} from recent bars. " +
            "Find similar, Save observation, or Reselect.";
        NotifyResearchSelectionStateChanged();
    }

    [RelayCommand]
    private void CancelResearchSelection()
    {
        ResetResearchSelection();
        Status = "Selection cleared; chart dragging pans again.";
    }

    [RelayCommand(CanExecute = nameof(CanKeepResearchSetupAction))]
    private void SendResearchSelection()
    {
        if (SelectedInstrument is not { } instrument || SelectedTimeframe is not { } timeframe ||
            ResearchObservationRange is not { } observation)
            return;

        if (ResearchOutcomeRange is not { } outcome)
        {
            Status =
                "Select an outcome interval on the chart before saving (observation alone is not enough). " +
                "Outcome is not invented automatically.";
            return;
        }

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException exception) { Status = exception.Message; return; }

        // Declare only what this chart observation actually used — not Depth/Tape by default.
        var requiredData = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
        var selection = new ResearchChartSelectionV1(
            _ingest.Resolve(instrument.Contract, broker),
            instrument.Contract.Symbol,
            timeframe.BarSize,
            observation.StartUtc,
            observation.EndUtcExclusive,
            outcome.StartUtc,
            outcome.EndUtcExclusive,
            requiredData);
        ResearchDatasetValidatorV1.RequireValidSelection(selection);
        var bindings = CaptureActiveIndicatorBindings();
        var overlays = CaptureActiveOverlayIds();
        ResearchSelectionRequested?.Invoke(
            this,
            new ResearchChartSelectionRequestedEventArgs(selection, overlays, bindings));
        Status = bindings.Count == 0
            ? "Observation saved (no indicators on). Toggle indicators on the chart, then Find similar."
            : $"Observation saved with [{string.Join(", ", bindings.Select(static b => b.DisplayLabel))}].";
    }

    /// <summary>
    /// Outcome is optional for pattern search. Prefer an explicit user-selected outcome.
    /// Do not invent one from nearby bars — that falsely implies a studied return window.
    /// </summary>
    private void EnsureResearchOutcomeAfterSetup(ChartTimeRange observation)
    {
        // Intentionally empty: callers must require HasResearchOutcomeRange.
        _ = observation;
    }

    [RelayCommand]
    private void RequestFindSimilarCharts()
    {
        if (!HasData)
        {
            Status = "Load chart history before finding similar charts.";
            return;
        }

        // Include exact indicator settings automatically with the observation when both ranges exist.
        if (CanSendResearchSelection)
            SendResearchSelection();
        else if (CanKeepResearchSetup && !HasResearchOutcomeRange)
        {
            Status = "Select an outcome interval before Find similar can save this observation.";
            ResearchFindSimilarRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ResearchFindSimilarRequested?.Invoke(this, EventArgs.Empty);
        Status = "Finding similar charts — requires an applied condition in Research Inspector.";
    }

    private bool CanSendResearchSelectionAction() => CanSendResearchSelection;
    private bool CanKeepResearchSetupAction() => CanKeepResearchSetup;

    public void SelectResearchRange(ChartTimeRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (ResearchSelectionStep == ChartResearchSelectionStep.Observation)
        {
            ResearchObservationRange = range;
            ResearchOutcomeRange = null;
            // Stop drag mode — outcome is offered as an explicit optional action, not a forced step.
            ResearchSelectionStep = ChartResearchSelectionStep.None;
            Status = "Observation selected. Add an outcome if needed, or Find similar.";
            return;
        }

        if (ResearchSelectionStep != ChartResearchSelectionStep.Outcome || ResearchObservationRange is not { } observation)
            return;
        if (range.StartUtc < observation.EndUtcExclusive)
        {
            Status = "Outcome must start at or after the observation ends.";
            return;
        }

        ResearchOutcomeRange = range;
        ResearchSelectionStep = ChartResearchSelectionStep.None;
        Status = "Observation + outcome marked. Find similar or Save observation.";
    }

    private void ResetResearchSelection()
    {
        ResearchSelectionStep = ChartResearchSelectionStep.None;
        ResearchObservationRange = null;
        ResearchOutcomeRange = null;
        ConditionHitMarkers = Array.Empty<ChartConditionHitMarker>();
        FillHitMarkers = Array.Empty<ChartFillHitMarker>();
    }

    private int CountBarsInRange(ChartTimeRange range)
    {
        if (_lastBars.Count == 0)
            return 0;

        var count = 0;
        foreach (var bar in _lastBars)
        {
            var ts = new DateTimeOffset(DateTime.SpecifyKind(bar.TimestampUtc, DateTimeKind.Utc));
            if (ts >= range.StartUtc && ts < range.EndUtcExclusive)
                count++;
        }

        return count;
    }

    partial void OnResearchSelectionStepChanged(ChartResearchSelectionStep value) =>
        NotifyResearchSelectionStateChanged();

    partial void OnResearchObservationRangeChanged(ChartTimeRange? value) =>
        NotifyResearchSelectionStateChanged();

    partial void OnResearchOutcomeRangeChanged(ChartTimeRange? value) =>
        NotifyResearchSelectionStateChanged();

    partial void OnConditionHitMarkersChanged(IReadOnlyList<ChartConditionHitMarker> value) =>
        OnPropertyChanged(nameof(HasConditionHitMarkers));

    partial void OnFillHitMarkersChanged(IReadOnlyList<ChartFillHitMarker> value) =>
        OnPropertyChanged(nameof(HasFillHitMarkers));

    /// <summary>
    /// Replace condition-hit markers from Research search or Design preview.
    /// Pass empty to clear. Does not mutate observation/outcome ranges or fill markers.
    /// </summary>
    public void ApplyConditionHitMarkers(IReadOnlyList<ResearchConditionHitV1>? hits)
    {
        if (hits is null || hits.Count == 0)
        {
            ConditionHitMarkers = Array.Empty<ChartConditionHitMarker>();
            return;
        }

        ConditionHitMarkers = hits
            .Select(static hit => new ChartConditionHitMarker(hit.BarTimeUtc, hit.ForwardPositive))
            .ToArray();
        Status = $"Condition markers: {ConditionHitMarkers.Count} hit(s) on chart.";
    }

    /// <summary>
    /// Replace fill markers from Validate last-run trades.
    /// Pass empty to clear. Does not clear condition markers.
    /// </summary>
    public void ApplyFillHitMarkers(IReadOnlyList<ValidationChartFillV1>? fills)
    {
        if (fills is null || fills.Count == 0)
        {
            FillHitMarkers = Array.Empty<ChartFillHitMarker>();
            return;
        }

        FillHitMarkers = fills
            .Select(static f => new ChartFillHitMarker(f.TimeUtc, f.Price, f.IsEntry, f.IsBuy))
            .ToArray();
        Status = $"Fill markers: {FillHitMarkers.Count} fill(s) on chart (separate from condition layer).";
    }

    private void NotifyResearchSelectionStateChanged()
    {
        OnPropertyChanged(nameof(IsResearchRangeSelectionEnabled));
        OnPropertyChanged(nameof(HasResearchObservationRange));
        OnPropertyChanged(nameof(HasResearchOutcomeRange));
        OnPropertyChanged(nameof(CanSendResearchSelection));
        OnPropertyChanged(nameof(CanKeepResearchSetup));
        OnPropertyChanged(nameof(CanStartResearchResultSelection));
        OnPropertyChanged(nameof(ResearchSelectionSummary));
        OnPropertyChanged(nameof(ResearchSelectionDetailSummary));
        SendResearchSelectionCommand.NotifyCanExecuteChanged();
        StartResearchResultSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refresh the selection summary when indicator toggles change on the chart.</summary>
    internal void NotifyResearchIndicatorSummaryChanged() =>
        OnPropertyChanged(nameof(ResearchSelectionDetailSummary));
}
