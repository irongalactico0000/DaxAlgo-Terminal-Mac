using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace TradingTerminal.Charts;

/// <summary>
/// Avalonia host for the reusable chart VM and native renderer. It preserves the Windows panel's
/// feature gates and lifecycle without WebView2 or a network/runtime dependency.
/// </summary>
public partial class ChartsPanel : UserControl
{
    public static readonly StyledProperty<ChartsPanelFeatures> FeaturesProperty =
        AvaloniaProperty.Register<ChartsPanel, ChartsPanelFeatures>(
            nameof(Features), defaultValue: ChartsPanelFeatures.Full);

    public ChartsPanelFeatures Features
    {
        get => GetValue(FeaturesProperty);
        set => SetValue(FeaturesProperty, value);
    }

    static ChartsPanel()
    {
        FeaturesProperty.Changed.AddClassHandler<ChartsPanel>((panel, _) => panel.ApplyFeatureGates());
    }

    private readonly NativeChartSurface _surface;
    private readonly Control _toolbar;
    private readonly Control _indicatorsPanel;
    private readonly Control _statusBar;
    private readonly Control _researchShellWorkflow;
    private readonly Control _researchShellDraftWorkflow;
    private readonly Control _researchCaptureClassic;
    private readonly Control _researchCompareSteps;
    private readonly Control _chartMarketViews;
    private readonly ToggleButton _optionsToggle;
    private ChartsViewModel? _viewModel;
    private ChartsViewModel? _readyViewModel;
    private Window? _host;
    private bool _loaded;

    public ChartsPanel()
    {
        InitializeComponent();
        _surface = this.FindControl<NativeChartSurface>("ChartSurface")!;
        _toolbar = this.FindControl<Control>("Toolbar")!;
        _indicatorsPanel = this.FindControl<Control>("IndicatorsPanel")!;
        _statusBar = this.FindControl<Control>("StatusBar")!;
        _researchShellWorkflow = this.FindControl<Control>("ResearchShellWorkflow")!;
        _researchShellDraftWorkflow = this.FindControl<Control>("ResearchShellDraftWorkflow")!;
        _researchCaptureClassic = this.FindControl<Control>("ResearchCaptureClassic")!;
        _researchCompareSteps = this.FindControl<Control>("ResearchCompareSteps")!;
        _chartMarketViews = this.FindControl<Control>("ChartMarketViews")!;
        _optionsToggle = this.FindControl<ToggleButton>("OptionsToggle")!;

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        _surface.ResearchRangeSelected += OnResearchRangeSelected;
        _surface.PriceClicked += OnPriceClicked;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _loaded = true;
        ApplyFeatureGates();
        Rebind();

        if (_host is null && TopLevel.GetTopLevel(this) is Window host)
        {
            _host = host;
            _host.Closed += OnHostClosed;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_loaded)
            Rebind();
    }

    private void Rebind()
    {
        if (ReferenceEquals(_viewModel, DataContext))
            return;

        Unbind();
        _viewModel = DataContext as ChartsViewModel;
        if (_viewModel is null)
            return;

        _viewModel.SnapshotReady += OnSnapshotReady;
        _viewModel.CandleUpdated += OnCandleUpdated;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplySurfaceInteractionState();
        ApplyFeatureGates();

        if (!ReferenceEquals(_readyViewModel, _viewModel))
        {
            _readyViewModel = _viewModel;
            _ = _viewModel.NotifyChartReadyAsync();
        }
    }

    private void ApplyFeatureGates()
    {
        var features = Features;
        _toolbar.IsVisible = features.Toolbar;
        _statusBar.IsVisible = features.Status;
        _optionsToggle.IsChecked = features.OptionsRail;
        _indicatorsPanel.IsVisible = features.Indicators;
        _researchShellWorkflow.IsVisible = features.ResearchShellWorkflow;
        _researchShellDraftWorkflow.IsVisible = features.ResearchShellWorkflow;
        _researchCaptureClassic.IsVisible = !features.ResearchCompareLabels;
        _researchCompareSteps.IsVisible = features.ResearchCompareLabels;
        _chartMarketViews.IsVisible = features.MarketViews;
        // Research embed: keep indicators available via ⚙, but start with a full-width chart.
        if (features.ResearchCompareLabels)
            _optionsToggle.IsChecked = false;

        if (_viewModel is not null && !features.Indicators)
        {
            _viewModel.ShowSma = false;
            _viewModel.ShowEma = false;
            _viewModel.ShowRsi = false;
            _viewModel.ShowMacd = false;
            _viewModel.ShowBollinger = false;
            _viewModel.ShowStochastic = false;
            _viewModel.ShowAtr = false;
            _viewModel.ShowVwap = false;
            _viewModel.ShowAdx = false;
        }
    }

    private void OnSnapshotReady(object? sender, ChartSnapshot snapshot)
    {
        void Apply()
        {
            _surface.Snapshot = snapshot;
            _surface.Message = snapshot.Candles.Length == 0
                ? $"No history for {snapshot.Symbol} ({snapshot.Timeframe})\n" +
                  "Connect a broker and stream this instrument, or pick another one.\n" +
                  "Every broker serves bars — the Simulated broker always works offline."
                : string.Empty;
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void OnCandleUpdated(object? sender, ChartCandle candle)
    {
        if (Dispatcher.UIThread.CheckAccess()) _surface.UpdateCandle(candle);
        else Dispatcher.UIThread.Post(() => _surface.UpdateCandle(candle));
    }

    private void OnResearchRangeSelected(object? sender, ChartRangeSelectedEventArgs e) =>
        _viewModel?.SelectResearchRange(e.Range);

    private void OnPriceClicked(object? sender, ChartPriceClickedEventArgs e) =>
        _viewModel?.ApplyClickedDraftPrice(e.Price);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChartsViewModel.IsResearchRangeSelectionEnabled) or
            nameof(ChartsViewModel.ResearchObservationRange) or
            nameof(ChartsViewModel.ResearchOutcomeRange) or
            nameof(ChartsViewModel.ConditionHitMarkers) or
            nameof(ChartsViewModel.DraftPlacementMode) or
            nameof(ChartsViewModel.DraftStopPrice) or
            nameof(ChartsViewModel.DraftTargetPrice) or
            nameof(ChartsViewModel.DraftStopPriceText) or
            nameof(ChartsViewModel.DraftTargetPriceText))
        {
            if (Dispatcher.UIThread.CheckAccess()) ApplySurfaceInteractionState();
            else Dispatcher.UIThread.Post(ApplySurfaceInteractionState);
        }
    }

    private void ApplySurfaceInteractionState()
    {
        if (_viewModel?.IsResearchRangeSelectionEnabled == true)
            _surface.InteractionMode = ChartInteractionMode.SelectResearchRange;
        else if (_viewModel is not null &&
                 _viewModel.DraftPlacementMode is ChartInteractionMode.PlaceStop or ChartInteractionMode.PlaceTarget)
            _surface.InteractionMode = _viewModel.DraftPlacementMode;
        else
            _surface.InteractionMode = ChartInteractionMode.Pan;

        _surface.ObservationRange = _viewModel?.ResearchObservationRange;
        _surface.OutcomeRange = _viewModel?.ResearchOutcomeRange;
        _surface.ConditionHitMarkers = _viewModel?.ConditionHitMarkers ?? Array.Empty<ChartConditionHitMarker>();
        _surface.DraftStopPrice = _viewModel?.DraftStopPrice;
        _surface.DraftTargetPrice = _viewModel?.DraftTargetPrice;
    }

    private async void ExportPng_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var symbol = _viewModel?.SelectedInstrument?.Contract.Symbol ?? "chart";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"chart-{FileToken(symbol)}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
            },
        });
        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            var scale = TopLevel.GetTopLevel(_surface)?.RenderScaling ?? 1d;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(_surface.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(_surface.Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(_surface);
            bitmap.Save(stream);
            await stream.FlushAsync();
            if (_viewModel is not null)
                _viewModel.Status = $"Snapshot saved → {file.Name}";
        }
        catch (Exception ex)
        {
            if (_viewModel is not null)
                _viewModel.Status = $"Snapshot failed: {ex.Message}";
        }
    }

    private static string FileToken(string value) =>
        value.Replace('/', '-').Replace(':', '-');

    private void OnHostClosed(object? sender, EventArgs e)
    {
        if (_host is not null)
            _host.Closed -= OnHostClosed;
        _host = null;
        Unbind();
        _surface.ResearchRangeSelected -= OnResearchRangeSelected;
        _surface.PriceClicked -= OnPriceClicked;
        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;
    }

    private void Unbind()
    {
        if (_viewModel is not null)
        {
            _viewModel.SnapshotReady -= OnSnapshotReady;
            _viewModel.CandleUpdated -= OnCandleUpdated;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = null;
        ApplySurfaceInteractionState();
    }
}
