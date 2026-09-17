using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

public sealed partial class StrategyAuthoringViewModel
{
    public IReadOnlyList<string> ResearchScreenUniverseOptions { get; } =
        ["S&P 100", "Available instruments"];
    public IReadOnlyList<string> ResearchScreenMetricOptions { get; } =
        ["Estimated traded value", "Trading volume", "Percent change"];
    public IReadOnlyList<int> ResearchScreenTopNOptions { get; } = [5, 10, 20, 50];
    public IReadOnlyList<string> ResearchScreenBarSizeOptions { get; } = ["1h", "1D", "15m"];

    [ObservableProperty] private string _researchScreenUniverseId = "S&P 100";
    [ObservableProperty] private string _researchScreenMetricChoice = "Estimated traded value";
    [ObservableProperty] private int _researchScreenTopN = 10;
    [ObservableProperty] private int _researchScreenLookbackBars = 24;
    [ObservableProperty] private string _researchScreenBarSizeChoice = "1h";
    [ObservableProperty] private bool _isResearchScreening;
    [ObservableProperty] private ResearchMarketScreenResultV1? _researchMarketScreenResult;
    [ObservableProperty] private ResearchMarketScreenRowV1? _selectedResearchScreenRow;
    [ObservableProperty] private string _researchScreenViewMode = "Table";

    public ObservableCollection<ResearchMarketScreenRowV1> ResearchScreenRows { get; } = [];
    public ObservableCollection<ResearchMarketScreenRowV1> ResearchScreenSelectedRows { get; } = [];

    /// <summary>Selected ranked rows for Compare tiles (capped at 3 hosts in Studio).</summary>
    public int ResearchScreenSelectedCount => ResearchScreenSelectedRows.Count;

    public bool HasResearchMarketScreenResult => ResearchMarketScreenResult is not null;
    public bool HasResearchScreenRows => ResearchScreenRows.Count > 0;
    public bool CanRunResearchScreen => _researchMarketScreener is not null && !IsResearchScreening;
    public bool CanOpenSelectedScreenCharts => ResearchScreenSelectedRows.Count > 0
        || SelectedResearchScreenRow is not null;
    public bool CanOpenResearchMarketStructure =>
        !string.IsNullOrWhiteSpace(
            ResearchChartInstrumentText ?? SelectedResearchScreenRow?.CanonicalSymbol);

    public string ResearchScreenResultHeaderText => ResearchMarketScreenResult?.ResultHeader
        ?? "No screen yet — Rank uses one bar size and one shared completed-bar window.";

    public string ResearchScreenCoverageText => ResearchMarketScreenResult?.CoverageSummary
        ?? "No screen yet. Choose universe, metric, bar size, and Top N, then Rank.";

    public string ResearchScreenMetricDefinitionText => ResearchMarketScreenResult?.MetricDefinition
        ?? "Estimated traded value = Σ (volume × close). Exact traded value needs trade prints. All instruments share one bar size.";

    public string ResearchScreenProvenanceText => ResearchMarketScreenResult?.DataProvenanceSummary ?? "";

    public string ResearchScreenExclusionSummaryText
    {
        get
        {
            var result = ResearchMarketScreenResult;
            if (result is null || result.Exclusions.Count == 0)
                return "";
            var sample = string.Join("; ", result.Exclusions.Take(3)
                .Select(static e => $"{e.CanonicalSymbol}: {e.Reason}"));
            var more = result.Exclusions.Count > 3 ? $" (+{result.Exclusions.Count - 3} more)" : "";
            return $"Excluded {result.Exclusions.Count}: {sample}{more}";
        }
    }

    /// <summary>Active indicators on the linked research instrument — formula + parameters.</summary>
    public string ResearchIndicatorInspectText
    {
        get
        {
            var symbol = SelectedResearchScreenRow?.CanonicalSymbol ?? ResearchChartInstrumentText ?? "No instrument";
            if (!HasPendingResearchIndicatorBindings)
            {
                return $"{symbol}: no chart indicators enabled. Toggle SMA/EMA/RSI/MACD/BB on the chart, then inspect here.";
            }

            var lines = PendingResearchIndicatorBindings.Select(static b =>
                $"{b.DisplayLabel}: {b.FormulaDescription}");
            return $"{symbol} indicators:\n" + string.Join("\n", lines);
        }
    }

    /// <summary>Compare selected ranked rows with numeric strip (return, vol/avg, EMA slope).</summary>
    public string ResearchIndicatorCompareText
    {
        get
        {
            if (ResearchScreenSelectedRows.Count < 2)
            {
                return ResearchScreenSelectedRows.Count == 1
                    ? "Select a second ranked instrument (Select on tiles), then open Compare for a numeric strip."
                    : "Multi-select ranked rows (Select), then Compare — shared window metrics for each case.";
            }

            var window = SelectedResearchScreenRow is { } focus
                ? $"{focus.WindowFromUtc:u} → {focus.WindowToUtcExclusive:u} ({focus.BarSizeLabel})"
                : ResearchScreenSelectedRows[0] is { } first
                    ? $"{first.WindowFromUtc:u} → {first.WindowToUtcExclusive:u} ({first.BarSizeLabel})"
                    : "shared ranking window";
            var indicators = HasPendingResearchIndicatorBindings
                ? string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))
                : "chart indicators when opened";
            var lines = ResearchScreenSelectedRows
                .OrderBy(static r => r.Rank)
                .Select(FormatCompareStripLine);
            return
                $"Compare {ResearchScreenSelectedRows.Count} cases on {window}.\n" +
                $"Shared settings: same bar size; indicators on focus chart: {indicators}.\n" +
                "Numeric strip (from ranking window bars):\n" +
                string.Join("\n", lines) +
                "\nOpen a tile to focus the live chart. Method: return %, last vol÷avg vol, EMA(20) slope % over 5 bars.";
        }
    }

    public bool HasResearchIndicatorCompare => ResearchScreenSelectedRows.Count >= 2;

    public bool IsResearchCompareView =>
        string.Equals(ResearchScreenViewMode, "Grid", StringComparison.OrdinalIgnoreCase);

    private static string FormatCompareStripLine(ResearchMarketScreenRowV1 r)
    {
        var ret = r.PercentChange is { } p ? $"{p:0.00}%" : "n/a";
        var vol = r.VolumeVsAvg is { } v ? $"{v:0.00}×avg" : "n/a";
        var ema = r.Ema20SlopePct is { } e ? $"{e:0.00}%" : "n/a";
        return $"#{r.Rank} {r.CanonicalSymbol}: ret {ret} · vol {vol} · EMA20Δ {ema}";
    }

    private static string NormalizeResearchScreenUniverseId(string? labelOrId)
    {
        if (string.IsNullOrWhiteSpace(labelOrId))
            return "sp100";
        var value = labelOrId.Trim();
        if (value.Contains("available", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "registry", StringComparison.OrdinalIgnoreCase))
            return "registry";
        return "sp100";
    }

    public string ResearchScreenCalculatedText => ResearchMarketScreenResult is { } result
        ? $"Calculated {result.CalculatedUtc:u} · {result.BarSizeLabel} · window {result.WindowFromUtc:u}→{result.WindowToUtcExclusive:u}"
        : "";

    partial void OnResearchMarketScreenResultChanged(ResearchMarketScreenResultV1? value)
    {
        var priorSymbol = SelectedResearchScreenRow?.CanonicalSymbol;
        ResearchScreenRows.Clear();
        ResearchScreenSelectedRows.Clear();
        if (value is not null)
        {
            foreach (var row in value.Rows)
                ResearchScreenRows.Add(row);
        }

        // Keep chart/instrument context across a refreshed ranking; rematch the open symbol if still present.
        SelectedResearchScreenRow = string.IsNullOrWhiteSpace(priorSymbol)
            ? null
            : ResearchScreenRows.FirstOrDefault(r =>
                string.Equals(r.CanonicalSymbol, priorSymbol, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(HasResearchMarketScreenResult));
        OnPropertyChanged(nameof(HasResearchScreenRows));
        OnPropertyChanged(nameof(ResearchScreenResultHeaderText));
        OnPropertyChanged(nameof(ResearchScreenCoverageText));
        OnPropertyChanged(nameof(ResearchScreenMetricDefinitionText));
        OnPropertyChanged(nameof(ResearchScreenProvenanceText));
        OnPropertyChanged(nameof(ResearchScreenExclusionSummaryText));
        OnPropertyChanged(nameof(ResearchScreenCalculatedText));
        OnPropertyChanged(nameof(CanOpenSelectedScreenCharts));
        OnPropertyChanged(nameof(CanOpenResearchMarketStructure));
        OnPropertyChanged(nameof(ActiveResearchContextText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(ResearchIndicatorInspectText));
        OnPropertyChanged(nameof(ResearchIndicatorCompareText));
        OnPropertyChanged(nameof(HasResearchIndicatorCompare));
        OpenSelectedScreenChartsCommand.NotifyCanExecuteChanged();
        OpenResearchOrderBookCommand.NotifyCanExecuteChanged();
        OpenResearchVolumeFootprintCommand.NotifyCanExecuteChanged();
        OpenResearchBookmapCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsResearchScreeningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunResearchScreen));
        RunResearchMarketScreenCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunResearchScreen))]
    private async Task RunResearchMarketScreenAsync()
    {
        if (_researchMarketScreener is null)
        {
            Status = "Market screener is not wired in this build.";
            return;
        }

        IsResearchScreening = true;
        Status = "Ranking instruments on a shared completed-bar window…";
        try
        {
            var metric = ResearchScreenMetricChoice switch
            {
                "Trading volume" => ResearchScreenMetricV1.TradingVolume,
                "Percent change" => ResearchScreenMetricV1.PercentChange,
                _ => ResearchScreenMetricV1.TradedValue,
            };
            var barSize = BarSizeExtensions.ParseOrDefault(ResearchScreenBarSizeChoice, BarSize.OneHour);
            var universeId = NormalizeResearchScreenUniverseId(ResearchScreenUniverseId);
            var result = await _researchMarketScreener.ScreenAsync(
                new ResearchMarketScreenRequestV1(
                    universeId,
                    metric,
                    ResearchScreenTopN,
                    ResearchScreenLookbackBars,
                    barSize),
                CancellationToken.None).ConfigureAwait(true);
            ResearchMarketScreenResult = result;
            ResearchScreenViewMode = "Table";
            AiStatus = result.ResultHeader;
            Status = result.CoverageSummary;
            Append(AuthoringMessage.Tool(
                "Ok",
                "Market screen",
                $"{result.ResultHeader} · {result.MetricDefinition}"));
        }
        catch (Exception ex)
        {
            Status = $"Market screen failed: {ex.Message}";
            AiStatus = Status;
        }
        finally
        {
            IsResearchScreening = false;
        }
    }

    [RelayCommand]
    private void ToggleResearchScreenRowSelection(ResearchMarketScreenRowV1? row)
    {
        if (row is null) return;
        var existing = ResearchScreenSelectedRows.FirstOrDefault(r =>
            string.Equals(r.CanonicalSymbol, row.CanonicalSymbol, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            ResearchScreenSelectedRows.Remove(existing);
        else
            ResearchScreenSelectedRows.Add(row);
        OnPropertyChanged(nameof(CanOpenSelectedScreenCharts));
        OnPropertyChanged(nameof(CanOpenResearchMarketStructure));
        OnPropertyChanged(nameof(ResearchIndicatorCompareText));
        OnPropertyChanged(nameof(HasResearchIndicatorCompare));
        OnPropertyChanged(nameof(ResearchScreenSelectedCount));
        OpenSelectedScreenChartsCommand.NotifyCanExecuteChanged();
        OpenResearchOrderBookCommand.NotifyCanExecuteChanged();
        OpenResearchVolumeFootprintCommand.NotifyCanExecuteChanged();
        OpenResearchBookmapCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedScreenCharts))]
    private void OpenSelectedScreenCharts()
    {
        if (ResearchScreenSelectedRows.Count == 0 && SelectedResearchScreenRow is null)
            return;

        var target = SelectedResearchScreenRow is not null &&
                     (ResearchScreenSelectedRows.Count == 0 ||
                      ResearchScreenSelectedRows.Any(r =>
                          string.Equals(r.CanonicalSymbol, SelectedResearchScreenRow.CanonicalSymbol,
                              StringComparison.OrdinalIgnoreCase)))
            ? SelectedResearchScreenRow
            : ResearchScreenSelectedRows.Count > 0
                ? ResearchScreenSelectedRows[^1]
                : null;
        if (target is null) return;
        OpenScreenRowOnChart(target);
    }

    [RelayCommand]
    private void OpenResearchScreenRow(ResearchMarketScreenRowV1? row)
    {
        if (row is null) return;
        if (!ResearchScreenSelectedRows.Any(r =>
                string.Equals(r.CanonicalSymbol, row.CanonicalSymbol, StringComparison.OrdinalIgnoreCase)))
            ResearchScreenSelectedRows.Add(row);
        OnPropertyChanged(nameof(CanOpenSelectedScreenCharts));
        OnPropertyChanged(nameof(CanOpenResearchMarketStructure));
        OpenSelectedScreenChartsCommand.NotifyCanExecuteChanged();
        OpenScreenRowOnChart(row);
    }

    /// <summary>
    /// Open one ranked instrument on the main chart. Ranking rows stay; shared indicator overlays apply.
    /// A later Rank refresh does not clear this chart binding.
    /// </summary>
    private void OpenScreenRowOnChart(ResearchMarketScreenRowV1 row)
    {
        SelectedResearchScreenRow = row;
        // Bind Hyperion immediately to the ranked symbol; chart catch-up must not revert this.
        SetBoundResearchChartInstrument(row.CanonicalSymbol);
        var overlays = PendingResearchOverlayIds.Count > 0
            ? PendingResearchOverlayIds.ToArray()
            : new[] { "sma-20", "ema-50" };
        var barSize = BarSizeExtensions.ParseOrDefault(ResearchScreenBarSizeChoice, BarSize.OneHour);
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                overlays,
                preferredSymbol: row.CanonicalSymbol,
                historyFromUtc: row.WindowFromUtc,
                historyToUtc: row.WindowToUtcExclusive,
                historyBarSize: barSize));
        ResearchScreenViewMode = "Detail";
        Status =
            $"Opened #{row.Rank} {row.CanonicalSymbol} ({row.MetricLabel}={row.MetricValue:N0}). " +
            "Chart and Hyperion follow this instrument.";
        OnPropertyChanged(nameof(ActiveResearchContextText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(ResearchIndicatorInspectText));
        OnPropertyChanged(nameof(ResearchIndicatorCompareText));
        OnPropertyChanged(nameof(CanOpenResearchMarketStructure));
        OpenResearchOrderBookCommand.NotifyCanExecuteChanged();
        OpenResearchVolumeFootprintCommand.NotifyCanExecuteChanged();
        OpenResearchBookmapCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SetResearchScreenViewMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        var normalized = mode.Trim();
        if (string.Equals(normalized, "Grid", StringComparison.OrdinalIgnoreCase))
        {
            if (ResearchScreenSelectedRows.Count < 2)
            {
                ResearchScreenViewMode = "Table";
                Status =
                    "Compare needs at least two selected ranked instruments (use Select on tiles). " +
                    "Then tap Compare for the numeric strip and multi-chart tiles.";
                return;
            }

            ResearchScreenViewMode = "Grid";
            Status =
                $"Compare: strip + {Math.Min(3, ResearchScreenSelectedRows.Count)} chart tiles " +
                $"(of {ResearchScreenSelectedRows.Count} selected) — return %, vol÷avg, EMA(20) slope. " +
                "Open a ranked tile to focus the single chart.";
            OnPropertyChanged(nameof(IsResearchCompareView));
            OnPropertyChanged(nameof(ResearchIndicatorCompareText));
            OnPropertyChanged(nameof(ResearchScreenSelectedCount));
            return;
        }

        ResearchScreenViewMode = normalized;
        OnPropertyChanged(nameof(IsResearchCompareView));
        OnPropertyChanged(nameof(ResearchScreenSelectedCount));
    }

    partial void OnResearchScreenViewModeChanged(string value) =>
        OnPropertyChanged(nameof(IsResearchCompareView));

    [RelayCommand(CanExecute = nameof(CanOpenResearchMarketStructure))]
    private void OpenResearchOrderBook() =>
        RequestResearchMarketStructure(ResearchMarketStructureViewKind.OrderBook);

    [RelayCommand(CanExecute = nameof(CanOpenResearchMarketStructure))]
    private void OpenResearchVolumeFootprint() =>
        RequestResearchMarketStructure(ResearchMarketStructureViewKind.VolumeFootprint);

    [RelayCommand(CanExecute = nameof(CanOpenResearchMarketStructure))]
    private void OpenResearchBookmap() =>
        RequestResearchMarketStructure(ResearchMarketStructureViewKind.Bookmap);

    private void RequestResearchMarketStructure(ResearchMarketStructureViewKind kind)
    {
        var symbol = ResearchChartInstrumentText ?? SelectedResearchScreenRow?.CanonicalSymbol;
        if (string.IsNullOrWhiteSpace(symbol)) return;
        HostResearchMarketStructureRequested?.Invoke(
            this,
            new HostResearchMarketStructureRequestedEventArgs(kind, symbol.Trim()));
        Status = $"Opening {kind} for {symbol} (same research instrument context).";
    }

    partial void OnSelectedResearchScreenRowChanged(ResearchMarketScreenRowV1? value)
    {
        OnPropertyChanged(nameof(ActiveResearchContextText));
        OnPropertyChanged(nameof(ResearchLinkedContextText));
        OnPropertyChanged(nameof(ResearchIndicatorInspectText));
        OnPropertyChanged(nameof(ResearchIndicatorCompareText));
        OnPropertyChanged(nameof(HasResearchIndicatorCompare));
        OnPropertyChanged(nameof(CanOpenResearchMarketStructure));
        OpenResearchOrderBookCommand.NotifyCanExecuteChanged();
        OpenResearchVolumeFootprintCommand.NotifyCanExecuteChanged();
        OpenResearchBookmapCommand.NotifyCanExecuteChanged();
    }
}
