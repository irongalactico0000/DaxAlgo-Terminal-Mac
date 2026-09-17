using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Authoring;
using TradingTerminal.Charts;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.App.Avalonia.Settings;

/// <summary>
/// Chart-first Research Studio — separate from Strategy Builder stages.
/// Saves observations and findings without requiring a strategy object.
/// </summary>
public partial class ResearchStudioWindow : Window
{
    private INotifyCollectionChanged? _messages;
    private ChartsPanel? _researchChartPanel;
    private StrategyAuthoringViewModel? _layoutViewModel;
    private readonly List<(Border Frame, ChartsViewModel Vm)> _compareTiles = [];

    public event EventHandler? ResearchChartRequested;
    public event EventHandler? DetachResearchChartRequested;

    public ResearchStudioWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Activated += OnActivatedAsStudio;
        Closed += OnClosed;
    }

    public ChartsViewModel? ResearchChartsViewModel { get; private set; }

    public bool HasEmbeddedResearchChart =>
        ResearchChartHost.Content is not null && ResearchChartsViewModel is not null;

    public bool ShowSimulatedDataBanner
    {
        get => SimulatedDataBanner.IsVisible;
        set => SimulatedDataBanner.IsVisible = value;
    }

    public void BindResearchChart(ChartsViewModel chartsViewModel)
    {
        ArgumentNullException.ThrowIfNull(chartsViewModel);
        if (ResearchChartsViewModel is not null)
            ResearchChartsViewModel.PropertyChanged -= OnResearchChartsPropertyChanged;
        ResearchChartsViewModel = chartsViewModel;
        chartsViewModel.PropertyChanged += OnResearchChartsPropertyChanged;
        _researchChartPanel ??= new ChartsPanel();
        _researchChartPanel.Features = ChartsPanelFeatures.ResearchInBuilder;
        _researchChartPanel.DataContext = chartsViewModel;
        ResearchChartHost.Content = _researchChartPanel;
        ResearchChartPlaceholder.IsVisible = false;
        SyncBoundInstrumentFromChart(chartsViewModel);
        if (DataContext is StrategyAuthoringViewModel authoring)
        {
            authoring.NotifyEmbeddedResearchChartChanged(embedded: true);
            authoring.SyncResearchChartIndicators(
                chartsViewModel.CaptureActiveIndicatorBindings(),
                chartsViewModel.CaptureActiveOverlayIds());
        }

        ApplyWorkspaceColumns();
        SyncCompareChartTiles();
    }

    private void OnResearchChartsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChartsViewModel.SelectedInstrument) or
            nameof(ChartsViewModel.ShowSma) or
            nameof(ChartsViewModel.ShowEma) or
            nameof(ChartsViewModel.EmaPeriod) or
            nameof(ChartsViewModel.SmaPeriod) or
            null)
        {
            if (sender is ChartsViewModel charts)
                SyncBoundInstrumentFromChart(charts);
        }
    }

    private void SyncBoundInstrumentFromChart(ChartsViewModel chartsViewModel)
    {
        if (DataContext is not StrategyAuthoringViewModel authoring) return;
        var pending = chartsViewModel.PendingHostPreferredSymbol;
        var chartSymbol = chartsViewModel.SelectedInstrument?.Contract.Symbol;
        var ranked = authoring.SelectedResearchScreenRow?.CanonicalSymbol;

        // Ranked-row handoff: keep Hyperion on the ranked symbol until the chart catches up.
        string? symbol;
        if (!string.IsNullOrWhiteSpace(pending))
            symbol = pending;
        else if (!string.IsNullOrWhiteSpace(chartSymbol) &&
                 (string.IsNullOrWhiteSpace(ranked) ||
                  string.Equals(chartSymbol, ranked, StringComparison.OrdinalIgnoreCase)))
            symbol = chartSymbol;
        else if (!string.IsNullOrWhiteSpace(ranked))
            symbol = ranked;
        else
            symbol = chartSymbol;

        authoring.SetBoundResearchChartInstrument(symbol);
        authoring.SyncResearchChartIndicators(
            chartsViewModel.CaptureActiveIndicatorBindings(),
            chartsViewModel.CaptureActiveOverlayIds());
    }

    public void ClearResearchChartEmbed(bool keepViewModel = true)
    {
        if (ResearchChartsViewModel is not null)
            ResearchChartsViewModel.PropertyChanged -= OnResearchChartsPropertyChanged;
        ResearchChartHost.Content = null;
        ResearchChartPlaceholder.IsVisible = true;
        if (!keepViewModel)
            ResearchChartsViewModel = null;
        if (DataContext is StrategyAuthoringViewModel authoring)
            authoring.NotifyEmbeddedResearchChartChanged(embedded: false);
        ApplyWorkspaceColumns();
    }

    /// <summary>
    /// Compare mode: host up to three transient chart VMs for selected ranked rows.
    /// Focus <see cref="ResearchChartsViewModel"/> stays the single-chart Research surface.
    /// </summary>
    public void SyncCompareChartTiles()
    {
        if (CompareChartTilesHost is null || ResearchChartHost is null)
            return;

        ClearCompareChartTiles();

        if (DataContext is not StrategyAuthoringViewModel authoring ||
            !authoring.IsResearchCompareView ||
            authoring.ResearchScreenSelectedRows.Count < 2)
        {
            CompareChartTilesHost.IsVisible = false;
            ResearchChartHost.IsVisible = true;
            if (ResearchChartPlaceholder is not null)
                ResearchChartPlaceholder.IsVisible = ResearchChartHost.Content is null;
            return;
        }

        if ((Application.Current as App)?.Services is not { } services)
        {
            CompareChartTilesHost.IsVisible = false;
            ResearchChartHost.IsVisible = true;
            return;
        }

        ResearchChartHost.IsVisible = false;
        if (ResearchChartPlaceholder is not null)
            ResearchChartPlaceholder.IsVisible = false;
        CompareChartTilesHost.IsVisible = true;

        var rows = authoring.ResearchScreenSelectedRows.Take(3).ToList();
        CompareChartTilesHost.Columns = Math.Max(1, rows.Count);
        CompareChartTilesHost.Rows = 1;

        var barSize = BarSizeExtensions.ParseOrDefault(
            authoring.ResearchScreenBarSizeChoice,
            BarSize.OneHour);
        IReadOnlyList<string> overlays = authoring.PendingResearchOverlayIds is { Count: > 0 } pending
            ? pending
            : ResearchChartsViewModel?.CaptureActiveOverlayIds() ?? Array.Empty<string>();

        foreach (var row in rows)
        {
            var vm = services.GetRequiredService<ChartsViewModel>();
            if (overlays.Count > 0)
                vm.ApplyHostOverlayIds(overlays, replaceExisting: true);
            vm.ApplyHostHistoryWindow(
                row.CanonicalSymbol,
                barSize,
                row.WindowFromUtc,
                row.WindowToUtcExclusive);

            var panel = new ChartsPanel
            {
                Features = ChartsPanelFeatures.Embedded,
                DataContext = vm,
            };
            var header = new TextBlock
            {
                Text = $"#{row.Rank}  {row.CanonicalSymbol}",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(4, 2, 4, 4),
            };
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(panel);
            var frame = new Border
            {
                Margin = new Thickness(2),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                Child = dock,
            };
            CompareChartTilesHost.Children.Add(frame);
            _compareTiles.Add((frame, vm));
        }
    }

    private void ClearCompareChartTiles()
    {
        foreach (var (_, vm) in _compareTiles)
        {
            try
            {
                vm.Dispose();
            }
            catch
            {
                // Best-effort teardown for transient compare hosts.
            }
        }

        _compareTiles.Clear();
        CompareChartTilesHost?.Children.Clear();
    }

    private void OnActivatedAsStudio(object? sender, EventArgs e)
    {
        if (DataContext is StrategyAuthoringViewModel viewModel)
            viewModel.IsResearchStudioShell = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachMessages();
        DetachLayoutViewModel();
        if (DataContext is StrategyAuthoringViewModel viewModel)
        {
            viewModel.IsResearchStudioShell = true;
            _messages = viewModel.Messages;
            _messages.CollectionChanged += OnMessagesChanged;
            _layoutViewModel = viewModel;
            _layoutViewModel.PropertyChanged += OnLayoutViewModelPropertyChanged;
            viewModel.ResearchScreenSelectedRows.CollectionChanged += OnResearchScreenSelectionChanged;
            ScrollTranscriptToEnd();
            ApplyWorkspaceColumns();
            Title = "DaxAlgo — Research Studio";
            if (ResearchChartsViewModel is not null)
                SyncBoundInstrumentFromChart(ResearchChartsViewModel);
            SyncCompareChartTiles();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachMessages();
        DetachLayoutViewModel();
        ClearCompareChartTiles();
        ClearResearchChartEmbed(keepViewModel: false);
        DataContextChanged -= OnDataContextChanged;
        Activated -= OnActivatedAsStudio;
        Closed -= OnClosed;
    }

    private void DetachMessages()
    {
        if (_messages is not null)
            _messages.CollectionChanged -= OnMessagesChanged;
        _messages = null;
    }

    private void DetachLayoutViewModel()
    {
        if (_layoutViewModel is not null)
        {
            _layoutViewModel.PropertyChanged -= OnLayoutViewModelPropertyChanged;
            _layoutViewModel.ResearchScreenSelectedRows.CollectionChanged -= OnResearchScreenSelectionChanged;
        }

        _layoutViewModel = null;
    }

    private void OnResearchScreenSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        SyncCompareChartTiles();

    private void OnLayoutViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StrategyAuthoringViewModel.MainWorkspaceColumnDefinitions)
            or nameof(StrategyAuthoringViewModel.ConversationColumnWidth)
            or nameof(StrategyAuthoringViewModel.HyperionCollapsed)
            or null)
            ApplyWorkspaceColumns();

        if (e.PropertyName is nameof(StrategyAuthoringViewModel.IsResearchCompareView)
            or nameof(StrategyAuthoringViewModel.ResearchScreenViewMode)
            or nameof(StrategyAuthoringViewModel.ResearchScreenSelectedCount)
            or nameof(StrategyAuthoringViewModel.ResearchScreenBarSizeChoice)
            or null)
            SyncCompareChartTiles();
    }

    private void ApplyWorkspaceColumns()
    {
        if (MainWorkspaceGrid is null || DataContext is not StrategyAuthoringViewModel vm) return;
        // Rail | Hyperion | splitter | Chart(*) — when Hyperion is collapsed, drop the splitter
        // so the chart column cannot be overlapped by a measuring Hyperion pane.
        var definitions = vm.HyperionCollapsed
            ? $"Auto,{vm.ConversationColumnWidth:0},0,*"
            : $"Auto,{vm.ConversationColumnWidth:0},4,*";
        if (!string.Equals(MainWorkspaceGrid.ColumnDefinitions.ToString(), definitions, StringComparison.Ordinal))
            MainWorkspaceGrid.ColumnDefinitions = ColumnDefinitions.Parse(definitions);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollTranscriptToEnd();

    private void ScrollTranscriptToEnd() =>
        Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Background);

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        var sendModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                           e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key != Key.Enter || !sendModifier || DataContext is not StrategyAuthoringViewModel viewModel)
            return;

        if (viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDeleteSession(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } session } && DataContext is StrategyAuthoringViewModel viewModel)
            viewModel.DeleteSavedSessionCommand.Execute(session);
    }

    private void OnResearchChartRequested(object? sender, RoutedEventArgs e) =>
        ResearchChartRequested?.Invoke(this, EventArgs.Empty);

    private void OnDetachResearchChartRequested(object? sender, RoutedEventArgs e) =>
        DetachResearchChartRequested?.Invoke(this, EventArgs.Empty);

    private async void OnCopyChat(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StrategyAuthoringViewModel viewModel) return;
        var transcript = string.Join(
            Environment.NewLine + Environment.NewLine,
            viewModel.Messages.Select(static m =>
                m.IsUser ? $"User: {m.Text}" :
                m.IsAssistant ? $"Assistant: {m.Text}" :
                $"System: {m.Text}"));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (string.IsNullOrWhiteSpace(transcript) || clipboard is null) return;
        await clipboard.SetTextAsync(transcript);
    }
}
