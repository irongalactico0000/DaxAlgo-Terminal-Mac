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
    public bool CanSendResearchSelection =>
        SelectedInstrument is not null && SelectedTimeframe is not null &&
        ResearchObservationRange is not null && ResearchOutcomeRange is not null;
    public string ResearchSelectionSummary => ResearchSelectionStep switch
    {
        ChartResearchSelectionStep.Observation => "Drag the observation window (features may read this interval).",
        ChartResearchSelectionStep.Outcome => "Drag a later, non-overlapping outcome window (label only).",
        _ when CanSendResearchSelection => "Observation and future outcome are ready to send to Strategy Builder.",
        _ => "Capture an observation window and a separate future outcome window.",
    };

    public event EventHandler<ResearchChartSelectionRequestedEventArgs>? ResearchSelectionRequested;

    [RelayCommand]
    private void StartResearchSelection()
    {
        if (!HasData)
        {
            Status = "Load chart history before capturing a research event.";
            return;
        }

        DraftPlacementMode = ChartInteractionMode.Pan;
        ResearchObservationRange = null;
        ResearchOutcomeRange = null;
        ResearchSelectionStep = ChartResearchSelectionStep.Observation;
        Status = "Research capture: drag the observation window.";
        NotifyResearchShellStateChanged();
    }

    /// <summary>
    /// Seeds observation→outcome from the two most recent completed bars so capture is visible
    /// immediately (host research-scan path). User can re-brush or Send to Builder.
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
            $"Capture ready on {SelectedInstrument?.Contract.Symbol}: observation→outcome seeded from recent bars. " +
            "Send to Builder to label B/C/N, or Cancel and re-brush. No backtest.";
        NotifyResearchSelectionStateChanged();
    }

    [RelayCommand]
    private void CancelResearchSelection()
    {
        ResetResearchSelection();
        Status = "Research capture cancelled; chart dragging pans again.";
    }

    [RelayCommand(CanExecute = nameof(CanSendResearchSelectionAction))]
    private void SendResearchSelection()
    {
        if (SelectedInstrument is not { } instrument || SelectedTimeframe is not { } timeframe ||
            ResearchObservationRange is not { } observation || ResearchOutcomeRange is not { } outcome)
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
            ? "Research selection sent to Strategy Builder for B/C/N labeling."
            : $"Research selection sent with [{string.Join(", ", bindings.Select(static b => b.DisplayLabel))}].";
    }

    private bool CanSendResearchSelectionAction() => CanSendResearchSelection;

    public void SelectResearchRange(ChartTimeRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (ResearchSelectionStep == ChartResearchSelectionStep.Observation)
        {
            ResearchObservationRange = range;
            ResearchOutcomeRange = null;
            ResearchSelectionStep = ChartResearchSelectionStep.Outcome;
            Status = "Observation fixed. Drag a later, non-overlapping future outcome window.";
            return;
        }

        if (ResearchSelectionStep != ChartResearchSelectionStep.Outcome || ResearchObservationRange is not { } observation)
            return;
        if (range.StartUtc < observation.EndUtcExclusive)
        {
            Status = "Outcome rejected: it must start at or after the observation window ends.";
            return;
        }

        ResearchOutcomeRange = range;
        ResearchSelectionStep = ChartResearchSelectionStep.None;
        Status = "Observation and future outcome selected. Send them to Strategy Builder.";
    }

    private void ResetResearchSelection()
    {
        ResearchSelectionStep = ChartResearchSelectionStep.None;
        ResearchObservationRange = null;
        ResearchOutcomeRange = null;
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
        OnPropertyChanged(nameof(ResearchSelectionSummary));
        SendResearchSelectionCommand.NotifyCanExecuteChanged();
    }
}
