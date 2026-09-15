using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Avalonia.Shell;

public partial class MainWindow : Window
{
    private TradingTerminal.App.Avalonia.Theming.IThemeManager? _themeManager;
    private Window? _executionBooksWindow;
    private Window? _executionConsoleWindow;
    private Window? _liveExecutionConsoleWindow;
    private Window? _paperStrategyRunnerWindow;
    private TradingTerminal.Charts.ChartsWindow? _researchChartWindow;
    private TradingTerminal.Charts.ChartsViewModel? _researchChartViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } services) return;
        _themeManager = services.GetRequiredService<TradingTerminal.App.Avalonia.Theming.IThemeManager>();
        _themeManager.ThemesChanged += OnThemesChanged;
        RebuildThemeMenu();
        RebuildCliMenus();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_themeManager is not null) _themeManager.ThemesChanged -= OnThemesChanged;
        Opened -= OnWindowOpened;
        Closed -= OnWindowClosed;
    }

    private void OnThemesChanged(object? sender, EventArgs e) =>
        global::Avalonia.Threading.Dispatcher.UIThread.Post(RebuildThemeMenu);

    private void RebuildThemeMenu()
    {
        if (_themeManager is null) return;

        ThemeMenu.ItemsSource = _themeManager.Themes.Select(theme =>
        {
            var item = new MenuItem
            {
                Header = (theme.Id == _themeManager.CurrentThemeId ? "✓ " : string.Empty) + theme.Name,
                Tag = theme.Id,
            };
            item.Click += OnApplyTheme;
            return item;
        }).ToArray();
    }

    private void OnApplyTheme(object? sender, RoutedEventArgs e)
    {
        if (_themeManager is null || (sender as MenuItem)?.Tag is not string themeId) return;
        _themeManager.Apply(themeId);
        RebuildThemeMenu();
    }

    private void RebuildCliMenus()
    {
        MenuItem[] BuildItems() => (Vm?.CliLaunchChoices ?? []).Select(choice =>
        {
            var item = new MenuItem
            {
                Header = choice.MenuHeader,
                IsEnabled = choice.IsAvailable,
                Tag = choice,
            };
            item.Click += OnLaunchCli;
            return item;
        }).ToArray();

        CliMenu.ItemsSource = BuildItems();
        FabCliMenu.ItemsSource = BuildItems();
    }

    private void OnLaunchCli(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is CliLaunchChoice choice)
            Vm?.LaunchCli(choice);
    }

    private void OnThemeStudio(object? sender, RoutedEventArgs e)
    {
        if (_themeManager is null) return;
        var view = new TradingTerminal.App.Avalonia.Theming.ThemeStudioView(_themeManager);
        var window = new Window
        {
            Title = "Theme Studio",
            Width = 900,
            Height = 760,
            MinWidth = 720,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = view,
        };
        window.Closed += (_, _) => RebuildThemeMenu();
        ShowDisposing(window, view.DataContext);
        Vm?.ActivityLog.Append("Settings", "INFO", "Opened Theme Studio.");
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnExecutionBooks(object? sender, RoutedEventArgs e)
    {
        if (_executionBooksWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if ((Application.Current as App)?.Services is not { } services) return;
        var viewModel = services.GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperExecutionBooksViewModel>();
        var window = services.GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperExecutionBooksWindow>();
        window.DataContext = viewModel;
        _executionBooksWindow = window;
        window.Closed += (_, _) => _executionBooksWindow = null;
        window.Show();
        Vm?.ActivityLog.Append("Execution", "INFO", "Opened persistent Paper execution books.");
    }

    private async void OnExecutionConsole(object? sender, RoutedEventArgs e)
    {
        if (_executionConsoleWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if ((Application.Current as App)?.Services is not { } services) return;
        TradingTerminal.App.Avalonia.Execution.PaperExecutionBookSessionLease? bookLease = null;
        try
        {
            var books = services.GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperExecutionBookManager>();
            bookLease = books.AcquireSelectedSession();
            var session = bookLease.Session;
            var viewModel = new TradingTerminal.UI.Execution.PaperExecutionConsoleViewModel(
                session.Client,
                session);
            if (bookLease.Book.PrimarySymbol.Length != 0)
                viewModel.SelectedInstrument = viewModel.Instruments.FirstOrDefault(instrument =>
                    string.Equals(instrument.Symbol, bookLease.Book.PrimarySymbol, StringComparison.OrdinalIgnoreCase))
                    ?? viewModel.SelectedInstrument;
            var window = services.GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperExecutionConsoleWindow>();
            window.Title = $"Paper Execution Console — {bookLease.Book.Name}";
            window.DataContext = viewModel;
            _executionConsoleWindow = window;
            var ownedLease = bookLease;
            bookLease = null;
            window.Closed += (_, _) =>
            {
                _executionConsoleWindow = null;
                ownedLease.Dispose();
            };
            ShowDisposing(window, viewModel);
            Vm?.ActivityLog.Append("Execution", "INFO",
                $"Opened Paper Execution Console for {ownedLease.Book.Name}/{ownedLease.Book.AccountId}.");
        }
        catch (Exception exception)
        {
            bookLease?.Dispose();
            Vm?.ActivityLog.Append("Execution", "ERROR",
                $"Paper execution remained unavailable: {exception.Message}");
            await new TradingTerminal.App.Avalonia.Execution.PaperExecutionUnavailableWindow(exception.Message)
                .ShowDialog(this);
        }
    }

    private void OnLiveExecutionConsole(object? sender, RoutedEventArgs e)
    {
        if (_liveExecutionConsoleWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if ((Application.Current as App)?.Services is not { } services) return;
        try
        {
            var viewModel = services.GetRequiredService<TradingTerminal.ExecutionUi.ExecutionConsoleViewModel>();
            var window = services.GetRequiredService<
                TradingTerminal.App.Avalonia.Execution.ExecutionConsoleWindow>();
            window.DataContext = viewModel;
            window.AttachSidecarConfirms(services);
            _liveExecutionConsoleWindow = window;
            window.Closed += (_, _) => _liveExecutionConsoleWindow = null;
            ShowDisposing(window, viewModel);
            Vm?.ActivityLog.Append("Execution", "INFO",
                "Lane 2 · Opened Execution Console. Paste your API keys; every venue starts PAPER; LIVE is armed per venue with Keychain confirmation. Not a pooled/ETF book.");
        }
        catch (Exception exception)
        {
            // The books and engine are app-lifetime, so a window that fails to open must leave them
            // running and say why rather than taking the shell down.
            _liveExecutionConsoleWindow = null;
            Vm?.ActivityLog.Append("Execution", "ERROR",
                $"The Execution Console did not open and no order route was created: {exception.Message}");
        }
    }

    private async void OnPaperStrategyRunner(object? sender, RoutedEventArgs e) =>
        await OpenPaperStrategyRunnerAsync(initialStrategy: null);

    private async Task OpenPaperStrategyRunnerAsync(
        TradingTerminal.UI.Strategies.StrategyKernelRegistration? initialStrategy,
        IReadOnlyDictionary<string, object?>? initialParameters = null,
        string? requiredBookId = null,
        bool autoStart = false)
    {
        if (_paperStrategyRunnerWindow is { } existing)
        {
            if (requiredBookId is not null && existing.DataContext is
                TradingTerminal.App.Avalonia.Execution.PaperStrategyRunnerViewModel bound &&
                !string.Equals(bound.BookId, requiredBookId, StringComparison.Ordinal))
            {
                existing.Close();
            }
            else
            {
                TradingTerminal.App.Avalonia.Execution.PaperStrategyRunnerViewModel? existingViewModel = null;
                if (initialStrategy is not null && existing.DataContext is
                    TradingTerminal.App.Avalonia.Execution.PaperStrategyRunnerViewModel prepared)
                {
                    existingViewModel = prepared;
                    if (initialParameters is not null &&
                        !prepared.TryPrepareTestedStrategy(initialStrategy, initialParameters, out var reason))
                    {
                        Vm?.ActivityLog.Append("Backtest", "WARN", reason);
                        autoStart = false;
                    }
                    else if (initialParameters is null)
                    {
                        prepared.SelectedStrategy = prepared.Strategies.FirstOrDefault(choice =>
                            string.Equals(choice.Id, initialStrategy.Id, StringComparison.Ordinal));
                    }
                }
                existing.Activate();
                if (autoStart && existingViewModel is not null)
                {
                    existingViewModel.MarkOpenedFromValidatePaper();
                    await TryAutoStartPaperStrategyAsync(existingViewModel);
                }
                return;
            }
        }
        if ((Application.Current as App)?.Services is not { } services) return;
        TradingTerminal.App.Avalonia.Execution.PaperExecutionBookSessionLease? bookLease = null;
        try
        {
            var books = services.GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperExecutionBookManager>();
            bookLease = books.AcquireSelectedSession();
            var window = services.GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperStrategyRunnerWindow>();
            TradingTerminal.App.Avalonia.Harness.AuthoredUnitHarnessSession.OpenStrategy(
                initialStrategy,
                services.GetRequiredService<TradingTerminal.Infrastructure.Backtest.IBacktestStrategyRegistry>(),
                services.GetRequiredService<TradingTerminal.Core.MarketData.IMarketDataHub>(),
                services.GetRequiredService<TradingTerminal.Core.Time.IClock>(),
                services.GetRequiredService<TradingTerminal.UI.Logging.InMemoryLogSink>(),
                bookLease,
                services.GetRequiredService<TradingTerminal.Core.MarketData.IInstrumentRegistry>(),
                services.GetRequiredService<TradingTerminal.UI.Strategies.IStrategyKernelRegistry>(),
                services.GetRequiredService<TradingTerminal.Core.MarketData.IMarketDataIngest>(),
                services.GetRequiredService<TradingTerminal.Core.Brokers.IBrokerSelector>(),
                testedParameters: initialParameters,
                executionClient: services.GetService<TradingTerminal.ExecutionUi.IExecutionClient>(),
                owner: this,
                window: window,
                openedFromValidatePaper: autoStart);
            var viewModel = (TradingTerminal.App.Avalonia.Execution.PaperStrategyRunnerViewModel)window.DataContext!;
            _paperStrategyRunnerWindow = window;
            var ownedLease = bookLease;
            bookLease = null;
            window.Closed += (_, _) =>
            {
                _paperStrategyRunnerWindow = null;
                ownedLease.Dispose();
            };
            ShowDisposing(window, viewModel);
            Vm?.ActivityLog.Append("Harness", "INFO",
                $"Opened for {ownedLease.Book.Name}/{ownedLease.Book.AccountId}.");
            if (autoStart)
                await TryAutoStartPaperStrategyAsync(viewModel);
        }
        catch (Exception exception)
        {
            bookLease?.Dispose();
            Vm?.ActivityLog.Append("Harness", "ERROR",
                $"Paper harness remained unavailable: {exception.Message}");
            await new TradingTerminal.App.Avalonia.Execution.PaperExecutionUnavailableWindow(exception.Message)
                .ShowDialog(this);
        }
    }

    private async Task TryAutoStartPaperStrategyAsync(
        TradingTerminal.App.Avalonia.Execution.PaperStrategyRunnerViewModel viewModel)
    {
        if (!viewModel.StartCommand.CanExecute(null))
        {
            Vm?.ActivityLog.Append("Harness", "WARN",
                "Harness opened with tested parameters, but Start was not ready (feed/book/strategy gate).");
            return;
        }

        try
        {
            await viewModel.StartCommand.ExecuteAsync(null);
            Vm?.ActivityLog.Append("Harness", "INFO",
                "Auto-started Harness after Validate → Paper handoff.");
        }
        catch (Exception exception)
        {
            Vm?.ActivityLog.Append("Harness", "ERROR",
                $"Harness auto-start failed: {exception.Message}");
        }
    }

    private async void OnReconnect(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.BeginBusy("Reconnecting brokers", "Re-arming each configured broker connection...");
        try { await vm.ReconnectAllAsync(); }
        finally { vm.EndBusy(); }
    }

    private async void OnStartQuestDb(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } services) return;
        var launcher = services.GetRequiredService<IQuestDbLauncher>();
        if (!launcher.IsApplicable)
        {
            Vm?.ActivityLog.Append("QuestDB", "INFO", "QuestDB is not the configured market-data backend.");
            return;
        }

        Vm?.BeginBusy("Starting QuestDB", "Preparing the market-data runtime and tick persistence...");
        Vm?.ActivityLog.Append("QuestDB", "INFO", "Starting QuestDB...");
        try
        {
            var ready = await launcher.StartAsync();
            Vm?.ActivityLog.Append("QuestDB", ready ? "INFO" : "WARN",
                ready ? "QuestDB is ready and tick persistence is active." : "QuestDB did not become ready.");
        }
        catch (Exception ex)
        {
            Vm?.ActivityLog.Append("QuestDB", "ERROR", $"QuestDB startup failed: {ex.Message}");
        }
        finally
        {
            Vm?.EndBusy();
        }
    }

    private void OnToggleActivityLog(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsLogVisible = !vm.IsLogVisible;
    }

    /// <summary>Shows a tool/strategy window and — matching the WPF shell — disposes its view-model
    /// when the window closes if the VM owns resources (timers / hub subscriptions / pumps). Without
    /// this the VM (and its render timer + feed buffers) would be pinned for the app's life — RAM
    /// never drops after Close (memory-safety pattern 5).</summary>
    private static void ShowDisposing(Window window, object? viewModel)
    {
        if (viewModel is IDisposable disposable)
            window.Closed += (_, _) => disposable.Dispose();
        window.Show();
    }

    private void OnCharts(object? sender, RoutedEventArgs e)
    {
        OpenResearchChart();
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Charts.");
    }

    /// <summary>
    /// Dev/smoke entry used by <c>--preview-overlays=...</c> so chat-catalog ids can open the live
    /// native chart without depending on Avalonia menu automation.
    /// </summary>
    public void PreviewHostChartOverlays(IReadOnlyList<string> overlayIds, bool startResearchCapture = false)
    {
        try
        {
            OpenResearchChart(hostOverlayIds: overlayIds, startResearchCapture: startResearchCapture);
            Vm?.ActivityLog.Append(
                "Charts",
                "INFO",
                startResearchCapture
                    ? $"Previewed host research chart (overlays: {string.Join(", ", overlayIds)})."
                    : $"Previewed host chart overlays: {string.Join(", ", overlayIds)}.");
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    "/tmp/daxalgo-preview-overlays.log",
                    $"{DateTime.UtcNow:O} PreviewHostChartOverlays FAILED: {ex}\n");
            }
            catch
            {
                // ignore diagnostic write failures
            }

            Vm?.ActivityLog.Append(
                "Charts",
                "ERROR",
                $"Host chart overlay preview failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens Strategy Builder and auto-labels research samples from local Simulated history
    /// (<c>--preview-research-auto</c> smoke / clickable suggestion chips).
    /// </summary>
    public async Task PreviewResearchAutoCollectAsync(string scanId = "next-day-plus-5")
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        var window = CreateAuthoringWindow(vm);
        WireResearchChartRequest(vm, window);
        ShowDisposing(window, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", $"Opened Strategy authoring for research auto-collect ({scanId}).");
        await vm.AutoCollectLocalResearchSamplesCommand.ExecuteAsync(scanId);
        try
        {
            var gallery = vm.ResearchOutcomeGalleryResult;
            File.AppendAllText(
                "/tmp/daxalgo-preview-overlays.log",
                $"{DateTime.UtcNow:O} PreviewResearchAutoCollect result: scan={scanId} " +
                $"matches={gallery?.Matches.Count ?? -1} instrumentsScanned={gallery?.InstrumentsScanned ?? -1} " +
                $"withHistory={gallery?.InstrumentsWithEnoughHistory ?? -1} " +
                $"samples={vm.ResearchEventSampleCount} status={vm.AiStatus}\n" +
                $"explanation={gallery?.Explanation}\n");
        }
        catch
        {
            // Preview diagnostics must never break auto-collect.
        }
    }

    /// <summary>
    /// Dev/smoke: stage Place STOP/TARGET fields+lines, Send draft to Builder, write PNG evidence under
    /// <paramref name="outputDirectory"/>. Does not place orders. Used by <c>--preview-draft-e2e</c>.
    /// </summary>
    public async Task PreviewDraftPlaceE2EAsync(string? outputDirectory = null)
    {
        var outDir = string.IsNullOrWhiteSpace(outputDirectory)
            ? "/Users/kimsunghyun/DaxAlgo-Terminal-Mac-Integrated/tmp/draft-audit"
            : outputDirectory.Trim();
        Directory.CreateDirectory(outDir);

        void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(outDir, "preview-draft-e2e.log"),
                    $"{DateTime.UtcNow:O} {message}\n");
            }
            catch { /* diagnostics */ }
        }

        try
        {
            PreviewHostChartOverlays(["ema-20", "rsi-14"]);
            var chartVm = _researchChartViewModel
                ?? throw new InvalidOperationException("Charts view-model was not created.");
            var chartWindow = _researchChartWindow
                ?? throw new InvalidOperationException("Charts window was not created.");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !chartVm.HasData)
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                await Task.Delay(200);
            }

            if (!chartVm.HasData)
                throw new InvalidOperationException("Charts did not load bars in time for draft E2E preview.");

            chartVm.ArmPlaceStopCommand.Execute(null);
            await PumpUiAsync();
            await SaveWindowPngAsync(chartWindow, Path.Combine(outDir, "10-armed-place-stop.png"));
            Log("saved 10-armed-place-stop.png");

            var mid = chartVm.LastLoadedClosePrice
                ?? throw new InvalidOperationException("No last close available to place draft levels.");
            var stop = decimal.Round(mid * 0.985m, 4, MidpointRounding.AwayFromZero);
            var target = decimal.Round(mid * 1.015m, 4, MidpointRounding.AwayFromZero);
            if (stop <= 0m || target <= 0m || stop >= target)
                throw new InvalidOperationException($"Invalid derived stop/target from mid={mid}.");

            chartVm.ApplyClickedDraftPrice(stop);
            await PumpUiAsync();
            chartVm.ArmPlaceTargetCommand.Execute(null);
            chartVm.ApplyClickedDraftPrice(target);
            await PumpUiAsync();
            await SaveWindowPngAsync(chartWindow, Path.Combine(outDir, "11-placed-stop-target-lines.png"));
            Log($"saved 11-placed-stop-target-lines.png stop={chartVm.DraftStopPriceText} target={chartVm.DraftTargetPriceText}");

            if (!chartVm.CanSendStrategyDraft)
                throw new InvalidOperationException("Draft was not ready to send after placing stop/target.");

            chartVm.SendStrategyDraftCommand.Execute(null);
            await PumpUiAsync();
            await Task.Delay(400);
            await PumpUiAsync();

            Window? builder = null;
            foreach (var window in ((Application.Current as App)?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                     ?? Array.Empty<Window>())
            {
                if (window is Settings.StrategyAuthoringWindow)
                {
                    builder = window;
                    break;
                }
            }

            if (builder is null)
                throw new InvalidOperationException("Strategy Builder did not open after Send draft.");

            builder.Activate();
            await PumpUiAsync();
            await SaveWindowPngAsync(builder, Path.Combine(outDir, "12-builder-chart-strategy-draft.png"));
            Log("saved 12-builder-chart-strategy-draft.png");

            File.WriteAllText(
                Path.Combine(outDir, "RESULT.txt"),
                "PASS draft E2E preview: armed → placed stop/target → Builder PNG evidence written.\n");
            Vm?.ActivityLog.Append("Charts", "INFO", $"Draft E2E preview evidence → {outDir}");
        }
        catch (Exception ex)
        {
            Log($"FAIL {ex}");
            try
            {
                File.WriteAllText(Path.Combine(outDir, "RESULT.txt"), $"FAIL {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { /* diagnostics */ }
            Vm?.ActivityLog.Append("Charts", "ERROR", $"Draft E2E preview failed: {ex.Message}");
            throw;
        }
    }

    private static async Task PumpUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task SaveWindowPngAsync(Window window, string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
            var scale = window.RenderScaling;
            var width = Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(path);
        });
        await Task.Yield();
    }

    private void OpenResearchChart(
        TradingTerminal.App.Authoring.StrategyAuthoringViewModel? targetViewModel = null,
        Settings.StrategyAuthoringWindow? targetWindow = null,
        IReadOnlyList<string>? hostOverlayIds = null,
        bool startResearchCapture = false,
        string? preferredSymbol = null,
        TradingTerminal.Core.Strategies.Generation.ResearchOutcomeGalleryMatchV1? galleryMatch = null,
        DateTime? historyFromUtc = null,
        DateTime? historyToUtc = null,
        TradingTerminal.Core.Domain.BarSize? historyBarSize = null)
    {
        static void PreviewLog(string message)
        {
            try
            {
                File.AppendAllText(
                    "/tmp/daxalgo-preview-overlays.log",
                    $"{DateTime.UtcNow:O} OpenResearchChart: {message}\n");
            }
            catch
            {
                // Preview diagnostics must never break chart open.
            }
        }

        if ((Application.Current as App)?.Services is not { } services)
        {
            PreviewLog("aborted — Application.Current.Services unavailable");
            return;
        }

        var authoringViewModel = targetViewModel;
        var authoringWindow = targetWindow;

        PreviewLog(
            hostOverlayIds is { Count: > 0 }
                ? $"resolving Charts services for overlays [{string.Join(',', hostOverlayIds)}]"
                : "resolving Charts services");

        // Reuse one Charts window for gallery focus so Research boxes do not spawn a stack of windows.
        var reuseExisting = _researchChartWindow is { IsVisible: true } && _researchChartViewModel is not null;
        var chartViewModel = reuseExisting
            ? _researchChartViewModel!
            : services.GetRequiredService<TradingTerminal.Charts.ChartsViewModel>();
        PreviewLog(reuseExisting ? "ChartsViewModel reused" : "ChartsViewModel resolved");
        if (hostOverlayIds is { Count: > 0 })
        {
            chartViewModel.ApplyHostOverlayIds(hostOverlayIds);
            PreviewLog(
                $"overlays applied SMA={chartViewModel.ShowSma} EMA={chartViewModel.ShowEma}/{chartViewModel.EmaPeriod} RSI={chartViewModel.ShowRsi} MACD={chartViewModel.ShowMacd} BB={chartViewModel.ShowBollinger}");
        }

        if (galleryMatch is not null)
        {
            chartViewModel.ApplyHostResearchCapture(
                galleryMatch.CanonicalSymbol,
                galleryMatch.Timeframe,
                galleryMatch.ObservationFromUtc,
                galleryMatch.ObservationToUtcExclusive,
                galleryMatch.OutcomeFromUtc,
                galleryMatch.OutcomeToUtcExclusive);
            PreviewLog($"gallery capture applied: {galleryMatch.CanonicalSymbol} return={galleryMatch.OutcomeReturn:P1}");
        }
        else if (historyFromUtc is { } fromUtc &&
                 historyToUtc is { } toUtc &&
                 historyBarSize is { } barSize)
        {
            chartViewModel.ApplyHostHistoryWindow(
                preferredSymbol,
                barSize,
                fromUtc,
                toUtc);
            PreviewLog($"similar-history window applied: {preferredSymbol} {fromUtc:u}→{toUtc:u}");
        }
        else if (!string.IsNullOrWhiteSpace(preferredSymbol))
        {
            chartViewModel.ApplyHostPreferredSymbol(preferredSymbol);
            PreviewLog($"preferred symbol applied: {preferredSymbol}");
        }

        if (reuseExisting)
        {
            if (startResearchCapture)
                ArmHostResearchCapture(chartViewModel, PreviewLog);
            _researchChartWindow!.Activate();
            PreviewLog("Activated existing Charts window for gallery focus");
            return;
        }

        var chartWindow = services.GetRequiredService<TradingTerminal.Charts.ChartsWindow>();
        PreviewLog("ChartsWindow resolved");
        chartWindow.DataContext = chartViewModel;
        // ChartsWindow.axaml used CenterOwner; without an owner Avalonia can leave the window
        // off-screen / non-visible on macOS. Force CenterScreen and Show(owner).
        chartWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _researchChartWindow = chartWindow;
        _researchChartViewModel = chartViewModel;

        EventHandler<TradingTerminal.Charts.ResearchChartSelectionRequestedEventArgs>? selectionHandler = null;
        selectionHandler = (_, args) =>
        {
            (authoringViewModel, authoringWindow) = EnsureAuthoringForResearchChart(services, authoringViewModel, authoringWindow);
            authoringViewModel.SetResearchChartSelection(
                args.Selection,
                args.ActiveOverlayIds,
                args.IndicatorBindings);
            authoringWindow.Activate();
            // Keep the single Charts window open for further gallery box focus.
        };
        chartViewModel.ResearchSelectionRequested += selectionHandler;

        EventHandler<TradingTerminal.Charts.StrategyDraftRequestedEventArgs>? draftHandler = null;
        draftHandler = (_, args) =>
        {
            (authoringViewModel, authoringWindow) = EnsureAuthoringForResearchChart(services, authoringViewModel, authoringWindow);
            authoringViewModel.SetStrategyDraft(args.Draft);
            authoringWindow.Activate();
        };
        chartViewModel.StrategyDraftRequested += draftHandler;

        EventHandler? lockHandler = null;
        lockHandler = (_, _) =>
        {
            (authoringViewModel, authoringWindow) = EnsureAuthoringForResearchChart(services, authoringViewModel, authoringWindow);
            if (!authoringViewModel.TryLockPendingStrategyDraftToActiveTradeIr(out var message))
            {
                chartViewModel.Status = message;
                authoringWindow.Activate();
            }
            else
            {
                chartViewModel.MarkStrategyDraftLocked(
                    string.IsNullOrWhiteSpace(message)
                        ? "Draft locked to TradeIR. Next: Historical BT."
                        : $"{message} Next: Historical BT.");
                _researchChartWindow?.Activate();
            }
        };
        chartViewModel.StrategyDraftLockRequested += lockHandler;

        EventHandler? historicalHandler = null;
        historicalHandler = (_, _) =>
        {
            (authoringViewModel, authoringWindow) = EnsureAuthoringForResearchChart(services, authoringViewModel, authoringWindow);
            if (!authoringViewModel.TryCreateHistoricalValidationContext(out _, out var blocker) ||
                !authoringViewModel.CanRunHistoricalValidation)
            {
                // Consent-preserving assist: open Build, auto-Compile when ready, stop before Register.
                authoringViewModel.PrepareHistoricalValidationAssist();
                var message = string.IsNullOrWhiteSpace(authoringViewModel.Status)
                    ? (string.IsNullOrWhiteSpace(blocker)
                        ? authoringViewModel.DescribeHistoricalValidationBlocker()
                        : blocker)
                    : authoringViewModel.Status;
                if (string.IsNullOrWhiteSpace(message))
                    message = "Compile and register a TradeIR hash before Historical BT (Validate/Studio).";
                authoringViewModel.Status = message;
                chartViewModel.MarkHistoricalBacktestBlocked(message);
                authoringWindow.Activate();
                return;
            }

            OpenAuthoringHistoricalValidation(authoringViewModel);
            var opened =
                string.IsNullOrWhiteSpace(authoringViewModel.Status)
                    ? "Historical validation opened from Charts research shell."
                    : authoringViewModel.Status;
            chartViewModel.MarkHistoricalBacktestRoomOpened(opened);
        };
        chartViewModel.HistoricalBacktestRequested += historicalHandler;

        chartWindow.Closed += (_, _) =>
        {
            PreviewLog("Charts window closed");
            chartViewModel.ResearchSelectionRequested -= selectionHandler;
            chartViewModel.StrategyDraftRequested -= draftHandler;
            chartViewModel.StrategyDraftLockRequested -= lockHandler;
            chartViewModel.HistoricalBacktestRequested -= historicalHandler;
            if (ReferenceEquals(_researchChartWindow, chartWindow))
            {
                _researchChartWindow = null;
                _researchChartViewModel = null;
            }
        };
        if (chartViewModel is IDisposable disposable)
            chartWindow.Closed += (_, _) => disposable.Dispose();
        chartWindow.Show(this);
        chartWindow.Activate();
        if (startResearchCapture)
            ArmHostResearchCapture(chartViewModel, PreviewLog);
        PreviewLog(
            $"Show(owner)+Activate Charts IsVisible={chartWindow.IsVisible} Width={chartWindow.Width} Height={chartWindow.Height} researchCapture={startResearchCapture}");
    }

    private (
        TradingTerminal.App.Authoring.StrategyAuthoringViewModel ViewModel,
        Settings.StrategyAuthoringWindow Window)
        EnsureAuthoringForResearchChart(
            IServiceProvider services,
            TradingTerminal.App.Authoring.StrategyAuthoringViewModel? targetViewModel,
            Settings.StrategyAuthoringWindow? targetWindow)
    {
        if (targetViewModel is null || targetWindow is null || !targetWindow.IsVisible)
        {
            targetViewModel = services.GetRequiredService<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
            targetWindow = CreateAuthoringWindow(targetViewModel);
            WireResearchChartRequest(targetViewModel, targetWindow);
            ShowDisposing(targetWindow, targetViewModel);
        }

        return (targetViewModel, targetWindow);
    }

    private static void ArmHostResearchCapture(
        TradingTerminal.Charts.ChartsViewModel chartViewModel,
        Action<string> previewLog)
    {
        void StartCapture()
        {
            chartViewModel.SeedCaptureWindowsFromRecentBars();
            previewLog(
                chartViewModel.CanSendResearchSelection
                    ? "research capture seeded (observation+outcome visible)"
                    : "research capture armed (observation brush)");
        }

        if (chartViewModel.HasData)
        {
            StartCapture();
            return;
        }

        EventHandler<TradingTerminal.Charts.ChartSnapshot>? snapshotHandler = null;
        snapshotHandler = (_, _) =>
        {
            chartViewModel.SnapshotReady -= snapshotHandler;
            StartCapture();
        };
        chartViewModel.SnapshotReady += snapshotHandler;
        previewLog("research capture waiting for chart history SnapshotReady");
    }

    private Settings.StrategyAuthoringWindow CreateAuthoringWindow(
        TradingTerminal.App.Authoring.StrategyAuthoringViewModel viewModel) => new()
    {
        DataContext = viewModel,
        ShowSimulatedDataBanner = Vm?.IsSimulatedActive == true,
    };

    private void WireResearchChartRequest(
        TradingTerminal.App.Authoring.StrategyAuthoringViewModel viewModel,
        Settings.StrategyAuthoringWindow window)
    {
        EventHandler? requestHandler = null;
        EventHandler? validationHandler = null;
        EventHandler? paperHandler = null;
        EventHandler<TradingTerminal.Core.Strategies.Generation.HostChartOverlayPreviewRequestedEventArgs>? overlayHandler = null;
        requestHandler = (_, _) => OpenResearchChart(viewModel, window);
        validationHandler = (_, _) => OpenAuthoringHistoricalValidation(viewModel);
        paperHandler = (_, _) => _ = OpenAuthoringPaperAsync(viewModel);
        overlayHandler = (_, args) =>
        {
            OpenResearchChart(
                viewModel,
                window,
                args.OverlayIds,
                startResearchCapture: args.StartResearchCapture && args.GalleryMatch is null,
                preferredSymbol: args.PreferredSymbol,
                galleryMatch: args.GalleryMatch,
                historyFromUtc: args.HistoryFromUtc,
                historyToUtc: args.HistoryToUtc,
                historyBarSize: args.HistoryBarSize);
            Vm?.ActivityLog.Append(
                "Charts",
                "INFO",
                args.StartResearchCapture
                    ? $"Previewing host research chart from chat (overlays: {(args.OverlayIds.Count == 0 ? "none" : string.Join(", ", args.OverlayIds))}; symbol: {args.PreferredSymbol ?? "default"})."
                    : $"Previewing host chart overlays from chat: {string.Join(", ", args.OverlayIds)}.");
        };
        window.ResearchChartRequested += requestHandler;
        window.HistoricalValidationRequested += validationHandler;
        window.PaperHandoffRequested += paperHandler;
        viewModel.HostChartOverlayPreviewRequested += overlayHandler;
        window.Closed += (_, _) =>
        {
            window.ResearchChartRequested -= requestHandler;
            window.HistoricalValidationRequested -= validationHandler;
            window.PaperHandoffRequested -= paperHandler;
            viewModel.HostChartOverlayPreviewRequested -= overlayHandler;
        };
    }

    private void OpenAuthoringHistoricalValidation(
        TradingTerminal.App.Authoring.StrategyAuthoringViewModel authoring)
    {
        if ((Application.Current as App)?.Services is not { } services)
            return;
        if (!authoring.TryCreateHistoricalValidationContext(out var context, out var reason) || context is null)
        {
            if (!string.IsNullOrWhiteSpace(reason)) authoring.Status = reason;
            return;
        }

        var registration = services
            .GetRequiredService<TradingTerminal.UI.Strategies.IStrategyKernelRegistry>()
            .Find(authoring.AuthoredUnitSpecification!.UnitId);
        if (registration is null)
        {
            authoring.Status = "The exact compiled strategy is no longer registered.";
            return;
        }

        var backtest = services.GetRequiredService<TradingTerminal.Backtest.QuickBacktestViewModel>();
        var window = services.GetRequiredService<TradingTerminal.Backtest.AvaloniaUi.QuickBacktestAvaloniaWindow>();
        window.DataContext = backtest;
        window.Title = $"Historical validation — {registration.DisplayName}";

        Action<TradingTerminal.Backtest.QuickBacktestPaperLaunchRequest>? completed = null;
        Action<TradingTerminal.Backtest.QuickBacktestPaperLaunchRequest>? paper = null;
        completed = request =>
        {
            if (request.ValidationEvidence is { } evidence &&
                !authoring.AcceptHistoricalValidationEvidence(evidence, request.TestedParameters, out var rejection))
                authoring.Status = rejection;
        };
        paper = request =>
        {
            if (request.ValidationEvidence is null)
            {
                authoring.Status = "This replay is not bound to the current Strategy Workspace.";
                return;
            }
            _ = OpenAuthoringPaperAsync(authoring, request.Registration, request.TestedParameters);
        };
        backtest.HistoricalValidationCompleted += completed;
        backtest.PaperLaunchRequested += paper;
        window.Closed += (_, _) =>
        {
            backtest.HistoricalValidationCompleted -= completed;
            backtest.PaperLaunchRequested -= paper;
        };
        if (!backtest.Initialize(registration, context))
        {
            authoring.Status = backtest.Status;
            window.Close();
            backtest.Dispose();
            return;
        }
        ShowDisposing(window, backtest);
    }

    private async Task OpenAuthoringPaperAsync(
        TradingTerminal.App.Authoring.StrategyAuthoringViewModel authoring,
        TradingTerminal.UI.Strategies.StrategyKernelRegistration? registration = null,
        IReadOnlyDictionary<string, object?>? testedParameters = null)
    {
        if ((Application.Current as App)?.Services is not { } services) return;
        registration ??= authoring.AuthoredUnitSpecification is { } specification
            ? services.GetRequiredService<TradingTerminal.UI.Strategies.IStrategyKernelRegistry>().Find(specification.UnitId)
            : null;
        testedParameters ??= authoring.ValidatedPaperParameters;
        if (registration is null || testedParameters is null)
        {
            authoring.Status = "The exact validated strategy and tested parameters are unavailable. Run historical validation again.";
            return;
        }

        try
        {
            var books = services
                .GetRequiredService<TradingTerminal.App.Avalonia.Execution.PaperExecutionBookManager>();
            var primarySymbol =
                registration.AuthoredSpecification.Instruments.FirstOrDefault()?.UserText
                ?? books.SelectedBook.PrimarySymbol;
            if (string.IsNullOrWhiteSpace(primarySymbol))
            {
                authoring.Status =
                    "Admit→Paper needs a primary symbol on the authored unit or a selected Paper book.";
                return;
            }

            var handoff = TradingTerminal.App.Avalonia.Execution.ValidatedPaperBookHandoff.EnsureAdmittedBook(
                books,
                registration,
                primarySymbol);
            if (!handoff.IsSuccess)
            {
                authoring.Status = handoff.Message;
                return;
            }

            var book = books.SelectedBook;
            if (!authoring.BindValidatedPaperBook(book.Id, book.AccountId, out var reason))
            {
                authoring.Status = reason;
                return;
            }
            await OpenPaperStrategyRunnerAsync(registration, testedParameters, book.Id, autoStart: true);
            authoring.Status =
                $"Bound Paper book {book.Name} (in-process admit). Open Harness auto-started; watch BOOK POSITION for OMS qty after fills.";
        }
        catch (Exception exception)
        {
            authoring.Status = $"The admitted Paper book could not be opened: {exception.Message}";
        }
    }

    private void OnVolumeFootprint(object? sender, RoutedEventArgs e)
    {
        // Real ported window — the portable VolumeFootprintViewModel streams the trade tape off the hub.
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.VolumeFootprint.VolumeFootprintViewModel>();
        ShowDisposing(new TradingTerminal.VolumeFootprint.AvaloniaUi.VolumeFootprintAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Volume Footprint.");
    }

    private void OnOrderBook(object? sender, RoutedEventArgs e)
    {
        // Real ported window — the portable OrderBookViewModel streams live L2 depth off the hub.
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.OrderBook.OrderBookViewModel>();
        ShowDisposing(new TradingTerminal.OrderBook.AvaloniaUi.OrderBookAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Order Book.");
    }

    private void OnHeatmap(object? sender, RoutedEventArgs e)
    {
        // Real ported window — the portable BookmapHeatmapViewModel streams depth + trades off the hub.
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Heatmap.BookmapHeatmapViewModel>();
        ShowDisposing(new TradingTerminal.Heatmap.AvaloniaUi.BookmapHeatmapAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Bookmap + VolBook.");
    }

    private void OnBubbleChart(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.BubbleChart.BubbleChartViewModel>();
        var window = sp.GetRequiredService<TradingTerminal.BubbleChart.BubbleChartWindow>();
        window.DataContext = vm;
        ShowDisposing(window, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened Volume bubble line (experimental).");
    }

    private void OnSurfaceLab(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.SurfaceLab.SurfaceLabViewModel>();
        var window = sp.GetRequiredService<TradingTerminal.SurfaceLab.SurfaceLabWindow>();
        window.DataContext = vm;
        ShowDisposing(window, vm);
        Vm?.ActivityLog.Append("Charts", "INFO", "Opened 3D Surface Lab.");
    }

    private void OnStationarity(object? sender, RoutedEventArgs e)
    {
        var vm = new MachineLearning.StationarityViewModel();
        ShowDisposing(new MachineLearning.StationarityWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("ML", "INFO", "Opened Stationarity & Differencing.");
    }

    private void OnArimaGarch(object? sender, RoutedEventArgs e)
    {
        var vm = new MachineLearning.ArimaGarchViewModel();
        ShowDisposing(new MachineLearning.ArimaGarchWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("ML", "INFO", "Opened ARIMA & GARCH.");
    }

    private void OnKalman(object? sender, RoutedEventArgs e)
    {
        var vm = new MachineLearning.KalmanViewModel();
        ShowDisposing(new MachineLearning.KalmanWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("ML", "INFO", "Opened Kalman Filter.");
    }

    private void OnCorrelation(object? sender, RoutedEventArgs e)
    {
        var vm = new Tools.CorrelationViewModel();
        ShowDisposing(new Tools.CorrelationWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Correlation Matrix.");
    }

    private void OnQuantConnectBacktest(object? sender, RoutedEventArgs e) => OpenQuantConnect(0);
    private void OnQuantConnectProjects(object? sender, RoutedEventArgs e) => OpenQuantConnect(1);
    private void OnQuantConnectData(object? sender, RoutedEventArgs e) => OpenQuantConnect(2);
    private void OnQuantConnectSettings(object? sender, RoutedEventArgs e) => OpenQuantConnect(3);

    private void OpenQuantConnect(int tab)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.QuantConnect.QuantConnectViewModel>();
        vm.SelectedTabIndex = tab;
        ShowDisposing(new TradingTerminal.QuantConnect.AvaloniaUi.QuantConnectAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("QuantConnect", "INFO", "Opened QuantConnect / LEAN.");
    }

    private void OnBacktest(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Backtest.BacktestViewModel>();
        ShowDisposing(new TradingTerminal.Backtest.AvaloniaUi.BacktestAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Backtest.");
    }

    private void OnLiveCorrelation(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Correlation.LiveCorrelationMatrixViewModel>();
        ShowDisposing(new TradingTerminal.Correlation.AvaloniaUi.LiveCorrelationAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Live correlation matrix.");
    }

    private void OnLseBacktest(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.LseBacktest.LseBacktestViewModel>();
        ShowDisposing(new TradingTerminal.LseBacktest.AvaloniaUi.LseBacktestAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("LSE", "INFO", "Opened LSE backtester.");
    }

    private void OnRecorder(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Recording.TickRecorderViewModel>();
        ShowDisposing(new TradingTerminal.Recording.AvaloniaUi.TickRecorderAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Record live ticks.");
    }

    private void OnBacktestStudio(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.BacktestStudio.BacktestStudioViewModel>();
        ShowDisposing(new TradingTerminal.BacktestStudio.AvaloniaUi.BacktestStudioAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Backtest Studio.");
    }

    private void OnAdvancedRegime(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.AdvancedMarketRegime.AdvancedMarketRegimeViewModel>();
        ShowDisposing(new TradingTerminal.AdvancedMarketRegime.AvaloniaUi.AdvancedMarketRegimeAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Advanced market regime.");
    }

    private void OnPaperLab(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.PaperLab.PaperLabViewModel>();
        ShowDisposing(new TradingTerminal.Ai.PaperLab.AvaloniaUi.PaperLabAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened Paper Lab.");
    }

    private void OnMarketAnalyst(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.MarketAnalyst.AiAnalystViewModel>();
        ShowDisposing(new TradingTerminal.Ai.MarketAnalyst.AvaloniaUi.AiAnalystAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened AI market analyst.");
    }

    private void OnFactorResearch(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.FactorResearch.FactorResearchViewModel>();
        ShowDisposing(new TradingTerminal.Ai.FactorResearch.AvaloniaUi.FactorResearchAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened Factor research.");
    }

    private void OnMlFeatures(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.MlFeatures.MlFeaturesViewModel>();
        ShowDisposing(new TradingTerminal.Ai.MlFeatures.AvaloniaUi.MlFeaturesAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened ML features.");
    }

    private void OnBacktestAnalysis(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.Ai.BacktestAnalysis.BacktestAnalysisViewModel>();
        ShowDisposing(new TradingTerminal.Ai.BacktestAnalysis.AvaloniaUi.BacktestAnalysisAvaloniaWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("AI", "INFO", "Opened Backtest analysis.");
    }

    private void OnArchiveSettings(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Archive.ArchiveSettingsViewModel>();
        ShowDisposing(new Settings.ArchiveSettingsWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Data", "INFO", "Opened Market-data archive.");
    }

    private void OnArchiveHistory(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Archive.ArchiveActivityViewModel>();
        ShowDisposing(new Settings.ArchiveActivityWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Data", "INFO", "Opened Archive history.");
    }

    private void OnInstantOffload(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Archive.ArchiveActivityViewModel>();
        ShowDisposing(new Settings.ArchiveActivityWindow { DataContext = vm }, vm);
        if (vm.InstantOffloadCommand.CanExecute(null)) vm.InstantOffloadCommand.Execute(null);
        Vm?.ActivityLog.Append("Data", "INFO", "Started instant archive offload.");
    }

    private void OnNotifications(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Notifications.NotificationsSettingsViewModel>();
        ShowDisposing(new Settings.NotificationsSettingsWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Settings", "INFO", "Opened Notifications.");
    }

    private void OnResearchSettings(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Research.ResearchSettingsViewModel>();
        ShowDisposing(new Settings.ResearchSettingsWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Settings", "INFO", "Opened Research settings.");
    }

    private void OnAiProvidersSettings(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Authoring.AiProvidersSettingsViewModel>();
        ShowDisposing(new Settings.AiProvidersSettingsWindow { DataContext = vm }, vm);
        Vm?.ActivityLog.Append("Settings", "INFO", "Opened AI provider settings.");
    }

    private void OnSupport(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        sp.GetRequiredService<TradingTerminal.App.Support.ISupportPrompt>().Show(this);
        Vm?.ActivityLog.Append("Help", "INFO", "Opened Support.");
    }

    private void OnAuthoring(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        var window = CreateAuthoringWindow(vm);
        WireResearchChartRequest(vm, window);
        ShowDisposing(window, vm);
        Vm?.ActivityLog.Append("Tools", "INFO", "Opened Strategy authoring.");
    }

    private void OnPluginManager(object? sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Services is not { } sp) return;
        var vm = sp.GetRequiredService<TradingTerminal.App.Plugins.PluginManagerViewModel>();
        var view = sp.GetRequiredService<TradingTerminal.App.Plugins.PluginManagerView>();
        view.DataContext = vm;
        var window = new Window
        {
            Title = "Strategy Manager",
            Width = 940,
            Height = 680,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = view,
        };
        ShowDisposing(window, vm);
        Vm?.ActivityLog.Append("Plugins", "INFO", "Opened Strategy Manager.");
    }

    // Opens the source paper for a research-derived strategy (the 📄 pill). URL is on the button's Tag.
    private void OnOpenResearchPaper(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url && !string.IsNullOrWhiteSpace(url))
            OpenUrl(url);
    }

    private void OnOpenCatalogLink(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string url
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            OpenUrl(uri.AbsoluteUri);
    }

    private void OnMarketplace(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://daxalgo.com/marketplace");
        Vm?.ActivityLog.Append(
            "Marketplace",
            "INFO",
            "Lane 3 · Opened marketplace site (strategy as software). Install via Strategy Manager → Install from URL or --install-open-package=. Not follow/pool/ETF execution; your keys and books only after install.");
    }

    private async void OnCopySelectedLogs(object? sender, RoutedEventArgs e)
    {
        IEnumerable<TradingTerminal.UI.Logging.LogEntry> rows =
            ActivityLogList.SelectedItems?.OfType<TradingTerminal.UI.Logging.LogEntry>().ToArray() is { Length: > 0 } selected
                ? selected
                : Vm?.VisibleLog ?? [];
        await CopyLogsAsync(rows);
    }

    private async void OnCopyAllLogs(object? sender, RoutedEventArgs e) =>
        await CopyLogsAsync(Vm?.VisibleLog ?? []);

    private void OnClearLogs(object? sender, RoutedEventArgs e) => Vm?.ActivityLog.Entries.Clear();

    private async Task CopyLogsAsync(IEnumerable<TradingTerminal.UI.Logging.LogEntry> rows)
    {
        var value = string.Join(Environment.NewLine, rows.Select(entry =>
            $"{entry.TimestampUtc:HH:mm:ss}  {entry.Source,-20}  {entry.Level,-5}  {entry.Message}"));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (string.IsNullOrWhiteSpace(value) || clipboard is null) return;
        await clipboard.SetTextAsync(value);
    }

    // Opens the selected strategy through the plug-in seam — IStrategyFactory.Create(id). The shell
    // never names a concrete strategy: each strategy project ships its own Avalonia view + registration.
    // The VM is disposed on window close (it owns the render timer + hub subscriptions).
    private async void OnOpenStrategy(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } shell || shell.SelectedCatalogItem is not { } item) return;
        var services = (Application.Current as App)?.Services;
        if (services is null) return;

        if (item.Kind == TradingTerminal.UI.Strategies.CatalogItemKind.Visualizer)
        {
            var registration = services
                .GetRequiredService<TradingTerminal.UI.Strategies.IVisualizerRegistry>()
                .Find(item.Id);
            if (registration is null)
            {
                shell.ActivityLog.Append("Visualizers", "WARN",
                    $"'{item.Name}' has no runnable visualizer registered behind its catalog card.");
                return;
            }

            shell.BeginBusy("Opening visualizer", $"Starting {item.Name} and warming its data feed...");
            try
            {
                if (registration.AuthoredSpecification is not null)
                {
                    await TradingTerminal.App.Avalonia.Harness.UnitHarnessShell.OpenVisualizerAsync(
                        registration,
                        services.GetRequiredService<IMarketDataHub>(),
                        services.GetRequiredService<TradingTerminal.Core.Time.IClock>(),
                        shell.ActivityLog,
                        services.GetRequiredService<IMarketDataIngest>(),
                        services.GetRequiredService<IInstrumentRegistry>(),
                        services.GetRequiredService<IBrokerSelector>(),
                        owner: this);
                }
                else
                {
                    await TradingTerminal.UI.Avalonia.Controls.Render.AuthoredVisualizerSession.OpenAsync(
                        item.Name,
                        registration.Create,
                        services.GetRequiredService<IMarketDataHub>(),
                        services.GetRequiredService<TradingTerminal.Core.Time.IClock>(),
                        shell.ActivityLog,
                        owner: this);
                }
                shell.ActivityLog.Append("Visualizers", "INFO", $"Opened '{item.Name}' in harness shell.");
            }
            catch (Exception ex)
            {
                shell.ActivityLog.Append("Visualizers", "ERROR",
                    $"Could not open '{item.Name}': {ex.Message}");
            }
            finally
            {
                shell.EndBusy();
            }

            return;
        }

        if (item.StrategyKernel is { } authoredStrategy)
        {
            await OpenPaperStrategyRunnerAsync(authoredStrategy);
            return;
        }

        if (shell.SelectedStrategy is not { } selected) return;

        Window? window = null;
        object? strategyVm = null;
        try
        {
            var host = services.GetRequiredService<IStrategyFactory>().Create(selected.Id);
            window = host.View as Window;
            strategyVm = host.ViewModel;
        }
        catch (KeyNotFoundException)
        {
            // The selected plug-in did not register a compatible view.
        }

        if (window is not null)
        {
            ShowDisposing(window, strategyVm);
            shell.ActivityLog.Append("Shell", "INFO", $"Opened '{selected.DisplayName}' strategy window.");
        }
        else
        {
            shell.ActivityLog.Append("Shell", "WARN",
                $"'{selected.DisplayName}' has no Avalonia view registered by its installed plug-in.");
        }
    }

    private void OnQuickBacktest(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedCatalogItem is not { HasQuickBacktest: true } item ||
            (Application.Current as App)?.Services is not { } sp) return;

        var vm = sp.GetRequiredService<TradingTerminal.Backtest.QuickBacktestViewModel>();
        var window = sp.GetRequiredService<TradingTerminal.Backtest.AvaloniaUi.QuickBacktestAvaloniaWindow>();
        window.DataContext = vm;
        window.Title = $"Quick backtest - {item.Name}";
        ShowDisposing(window, vm);
        if (item.StrategyKernel is { } authored)
            vm.Initialize(authored);
        else if (item.Strategy is { } strategy)
            vm.Initialize(
                strategy.BacktestStrategyId,
                strategy.DisplayName,
                strategy.DataRequirement.HasFlag(StrategyDataRequirement.TradeTape));
        else
            return;
        void OpenTestedInPaper(TradingTerminal.Backtest.QuickBacktestPaperLaunchRequest request) =>
            _ = OpenPaperStrategyRunnerAsync(request.Registration, request.TestedParameters);
        vm.PaperLaunchRequested += OpenTestedInPaper;
        window.Closed += (_, _) => vm.PaperLaunchRequested -= OpenTestedInPaper;
        Vm.ActivityLog.Append("Backtest", "INFO", $"Opened quick backtest for '{item.Name}'.");
    }

    private async void OnEditStrategyCard(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedCatalogItem is not { } item) return;

        var editor = new TradingTerminal.UI.Strategies.StrategyPresentationEditorViewModel(item);
        var window = new TradingTerminal.App.Avalonia.Strategies.StrategyPresentationEditorWindow
        {
            DataContext = editor,
        };
        if (!await window.ShowDialog<bool>(this)) return;

        var presentation = editor.Build();
        TradingTerminal.UI.Strategies.StrategyPresentationStore.Save(item.Id, presentation);
        item.Apply(presentation);
        Vm.ActivityLog.Append("Strategies", "INFO", $"Updated catalog presentation for '{item.Name}'.");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
        }
        catch { /* best-effort: a missing/blocked browser shouldn't crash the shell */ }
    }
}
