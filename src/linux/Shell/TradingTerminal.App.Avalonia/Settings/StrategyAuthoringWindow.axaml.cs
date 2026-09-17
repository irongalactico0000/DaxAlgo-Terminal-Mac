using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DaxAlgo.Package;
using TradingTerminal.App.Authoring;
using TradingTerminal.App.Plugins;
using TradingTerminal.Charts;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI;

namespace TradingTerminal.App.Avalonia.Settings;

public partial class StrategyAuthoringWindow : Window
{
    private INotifyCollectionChanged? _messages;
    private StrategyAuthoringViewModel? _layoutViewModel;

    public event EventHandler? ResearchChartRequested;
    public event EventHandler? DetachResearchChartRequested;
    public event EventHandler? HistoricalValidationRequested;
    public event EventHandler? PaperHandoffRequested;

    public StrategyAuthoringWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Activated += OnActivatedAsBuilder;
        Closed += OnClosed;
    }

    /// <summary>Shared Charts VM currently bound into the Research workspace (embedded or after detach).</summary>
    public ChartsViewModel? ResearchChartsViewModel { get; private set; }

    public bool HasEmbeddedResearchChart =>
        ResearchChartHost.Content is not null && ResearchChartsViewModel is not null;

    public bool ShowSimulatedDataBanner
    {
        get => SimulatedDataBanner.IsVisible;
        set => SimulatedDataBanner.IsVisible = value;
    }

    /// <summary>
    /// Strategy Builder must not embed Research charts — route through Research Studio instead.
    /// Kept as a no-op so older shell call sites fail closed instead of mixing workspaces.
    /// </summary>
    public void BindResearchChart(ChartsViewModel chartsViewModel)
    {
        ArgumentNullException.ThrowIfNull(chartsViewModel);
        ClearResearchChartEmbed(keepViewModel: true);
        ResearchChartRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearResearchChartEmbed(bool keepViewModel = true)
    {
        ResearchChartHost.Content = null;
        ResearchChartPlaceholder.IsVisible = true;
        if (!keepViewModel)
            ResearchChartsViewModel = null;
        if (DataContext is StrategyAuthoringViewModel authoring)
            authoring.NotifyEmbeddedResearchChartChanged(embedded: false);
        ApplyResearchWorkspaceColumns();
    }

    private void SyncBoundInstrument(ChartsViewModel chartsViewModel)
    {
        if (DataContext is not StrategyAuthoringViewModel authoring) return;
        authoring.SetBoundResearchChartInstrument(chartsViewModel.SelectedInstrument?.Contract.Symbol);
    }

    private void OnActivatedAsBuilder(object? sender, EventArgs e)
    {
        if (DataContext is not StrategyAuthoringViewModel viewModel) return;
        if (viewModel.IsResearchStudioShell)
            viewModel.IsResearchStudioShell = false;
        if (viewModel.IsResearchStage && viewModel.OpenDesignScreenCommand.CanExecute(null))
            viewModel.OpenDesignScreenCommand.Execute(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachMessages();
        DetachLayoutViewModel();

        if (DataContext is StrategyAuthoringViewModel viewModel)
        {
            // Hosting window owns the shell flag — Builder never shows Research chrome.
            viewModel.IsResearchStudioShell = false;
            if (viewModel.IsResearchStage && viewModel.OpenDesignScreenCommand.CanExecute(null))
                viewModel.OpenDesignScreenCommand.Execute(null);
            _messages = viewModel.Messages;
            _messages.CollectionChanged += OnMessagesChanged;
            _layoutViewModel = viewModel;
            _layoutViewModel.PropertyChanged += OnLayoutViewModelPropertyChanged;
            ScrollTranscriptToEnd();
            ApplyResearchWorkspaceColumns();
            Title = "DaxAlgo — Strategy Builder";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachMessages();
        DetachLayoutViewModel();
        ClearResearchChartEmbed(keepViewModel: false);
        DataContextChanged -= OnDataContextChanged;
        Activated -= OnActivatedAsBuilder;
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
            _layoutViewModel.PropertyChanged -= OnLayoutViewModelPropertyChanged;
        _layoutViewModel = null;
    }

    private void OnLayoutViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StrategyAuthoringViewModel.MainWorkspaceColumnDefinitions)
            or nameof(StrategyAuthoringViewModel.IsResearchStage)
            or nameof(StrategyAuthoringViewModel.ShowDesignInspector)
            or null)
        {
            ApplyResearchWorkspaceColumns();
        }
    }

    /// <summary>
    /// Research must give the chart the star column; Hyperion stays Auto + fixed width.
    /// ColumnDefinitions is not a bindable DP in Avalonia, so apply it from code.
    /// </summary>
    private void ApplyResearchWorkspaceColumns()
    {
        if (MainWorkspaceGrid is null) return;
        var definitions = _layoutViewModel?.MainWorkspaceColumnDefinitions ?? "Auto,*,4,Auto";
        if (string.Equals(MainWorkspaceGrid.ColumnDefinitions.ToString(), definitions, StringComparison.Ordinal))
            return;
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

    private void OnUseStarter(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StrategyStarterBrief brief } && DataContext is StrategyAuthoringViewModel viewModel)
            viewModel.UseStarterPromptCommand.Execute(brief);
    }

    private void OnToggleWorkflowHelp(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StrategyAuthoringViewModel viewModel)
            viewModel.ToggleWorkflowHelp();
    }

    private void OnDeleteSession(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } session } && DataContext is StrategyAuthoringViewModel viewModel)
            viewModel.DeleteSavedSessionCommand.Execute(session);
    }

    private void OnLaunchCli(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } adapter } && DataContext is StrategyAuthoringViewModel viewModel)
            viewModel.LaunchCliCommand.Execute(adapter);
    }

    private void OnResearchChartRequested(object? sender, RoutedEventArgs e) =>
        ResearchChartRequested?.Invoke(this, EventArgs.Empty);

    private void OnDetachResearchChartRequested(object? sender, RoutedEventArgs e) =>
        DetachResearchChartRequested?.Invoke(this, EventArgs.Empty);

    private void OnHistoricalValidationRequested(object? sender, RoutedEventArgs e) =>
        HistoricalValidationRequested?.Invoke(this, EventArgs.Empty);

    private void OnPaperHandoffRequested(object? sender, RoutedEventArgs e) =>
        PaperHandoffRequested?.Invoke(this, EventArgs.Empty);

    private async void OnExportOpenPackage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StrategyAuthoringViewModel viewModel) return;
        if (!viewModel.TryGetOpenPackageExport(out var specification, out var sources, out var reason))
        {
            viewModel.Status = reason;
            return;
        }

        var suggested = $"{specification.UnitId}{DaxPackage.StrategyExtension}";
        var path = await UiFile.SaveAsync(
            "DaxAlgo strategy package",
            [DaxPackage.StrategyExtension.TrimStart('.')],
            suggested);
        if (path is null) return;

        _ = AuthoredUnitOpenPackageExporter.TryWrite(path, specification, sources, out var message);
        viewModel.Status = message;
    }

    private async void OnCopyChat(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StrategyAuthoringViewModel viewModel) return;

        var transcript = string.Join(
            Environment.NewLine + Environment.NewLine,
            viewModel.Messages.Select(FormatChatEntry));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (string.IsNullOrWhiteSpace(transcript) || clipboard is null) return;
        await clipboard.SetTextAsync(transcript);
    }

    private static string FormatChatEntry(AuthoringMessage message)
    {
        if (message.IsUser) return $"User: {message.Text}";
        if (message.IsAssistant) return $"Assistant: {message.Text}";

        var body = message.Kind == AuthoringMessage.KindPlan
            ? message.PlanSnapshotText()
            : string.Join(
                Environment.NewLine,
                new[] { message.ToolTitle, message.Text, message.ToolDetail }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal));
        return $"System: {body}";
    }

    /// <summary>
    /// Avalonia AutoCompleteBox often needs an explicit populate on focus so click-open shows
    /// recent/available instruments (MinimumPrefixLength=0 alone is unreliable).
    /// </summary>
    private void OnDesignInstrumentSearchGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is not AutoCompleteBox box)
            return;
        if (DataContext is StrategyAuthoringViewModel vm)
            _ = vm.EnsureDesignInstrumentCatalogueAsync();

        // Re-assign ItemsSource to force dropdown population when the catalogue already loaded.
        var items = box.ItemsSource;
        box.ItemsSource = null;
        box.ItemsSource = items;
        box.IsDropDownOpen = true;
    }
}

/// <summary>Lets the Avalonia parameter workbench select the correct editor without UI-specific VM code.</summary>
public sealed class ParameterKindMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ParameterKind kind || parameter is not string expected)
            return false;

        return expected.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => Enum.TryParse<ParameterKind>(candidate, out var parsed) && parsed == kind);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts collection counts to visibility without relying on implicit numeric coercion.</summary>
public sealed class PositiveCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
