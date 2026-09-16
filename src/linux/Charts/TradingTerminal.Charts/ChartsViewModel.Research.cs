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

        EnsureResearchOutcomeAfterSetup(observation);

        if (ResearchOutcomeRange is not { } outcome)
            return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException exception) { Status = exception.Message; return; }

        var selection = new ResearchChartSelectionV1(
            _ingest.Resolve(instrument.Contract, broker),
            instrument.Contract.Symbol,
            timeframe.BarSize,
            observation.StartUtc,
            observation.EndUtcExclusive,
            outcome.StartUtc,
            outcome.EndUtcExclusive,
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars |
            StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape);
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
    /// Outcome is optional for pattern search. When missing, use the next bar after setup
    /// so the selection contract stays valid without implying a studied outcome.
    /// </summary>
    private void EnsureResearchOutcomeAfterSetup(ChartTimeRange observation)
    {
        if (ResearchOutcomeRange is not null)
            return;
        if (_lastBars.Count < 2)
            return;

        var afterSetup = _lastBars
            .SkipWhile(bar => new DateTimeOffset(DateTime.SpecifyKind(bar.TimestampUtc, DateTimeKind.Utc)) < observation.EndUtcExclusive)
            .Take(2)
            .ToList();
        if (afterSetup.Count < 2)
        {
            var last = _lastBars[^1];
            var prev = _lastBars[^2];
            ResearchOutcomeRange = new ChartTimeRange(
                new DateTimeOffset(DateTime.SpecifyKind(prev.TimestampUtc, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(last.TimestampUtc, DateTimeKind.Utc)));
            if (ResearchOutcomeRange.StartUtc < observation.EndUtcExclusive)
            {
                ResearchOutcomeRange = new ChartTimeRange(
                    observation.EndUtcExclusive,
                    observation.EndUtcExclusive.Add(observation.EndUtcExclusive - observation.StartUtc));
            }
            return;
        }

        ResearchOutcomeRange = new ChartTimeRange(
            new DateTimeOffset(DateTime.SpecifyKind(afterSetup[0].TimestampUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(afterSetup[1].TimestampUtc, DateTimeKind.Utc)));
    }

    [RelayCommand]
    private void RequestFindSimilarCharts()
    {
        if (!HasData)
        {
            Status = "Load chart history before finding similar charts.";
            return;
        }

        // Include exact indicator settings automatically with the observation.
        if (CanKeepResearchSetup)
            SendResearchSelection();

        ResearchFindSimilarRequested?.Invoke(this, EventArgs.Empty);
        Status = "Finding similar charts — open a match to compare.";
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
