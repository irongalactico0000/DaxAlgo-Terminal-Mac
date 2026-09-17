using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingTerminal.Accounts;
using TradingTerminal.App.Login;
using TradingTerminal.App.Avalonia.Composition;
using TradingTerminal.App.Avalonia.Diagnostics;
using TradingTerminal.App.Avalonia.Shell;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia;

public partial class App : Application
{
    private IHost? _host;
    private IDisposable? _pluginFaultWatchdog;

    /// <summary>The composed DI graph for the macOS terminal.</summary>
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        // Point the shared (WPF-free) UI-thread marshallers at Avalonia's dispatcher — the same
        // hooks the WPF shell sets to its Dispatcher. This is what lets the portable view-models
        // and the universal Activity Log run unchanged on Avalonia.
        InMemoryLogSink.UiPost = action => Dispatcher.UIThread.Post(action);
        UiThread.Marshal = MarshalToUiThread;
        WireFilePicker();
        CrashGuard.Install(
            "DaxAlgo Terminal for macOS",
            (source, level, message) =>
                Services?.GetService<InMemoryLogSink>()?.Append(source, level, message));

        Window? startupWindow = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime startupDesktop)
        {
            startupWindow = new Window
            {
                Width = 430,
                Height = 170,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                // Hardcoded colors: theme resources are not applied yet, so Resource brushes can
                // render as an empty black splash that looks hung.
                Background = global::Avalonia.Media.Brushes.WhiteSmoke,
                Title = "Starting DaxAlgo Terminal",
                Content = new TextBlock
                {
                    Text = "Checking strategy plugins and preparing the terminal...",
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(24),
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = global::Avalonia.Media.Brushes.Black,
                },
            };
            startupDesktop.MainWindow = startupWindow;
            startupWindow.Show();
        }

        // Finish the Avalonia lifetime before plugin discovery yields to an informed-consent dialog.
        base.OnFrameworkInitializationCompleted();
        await Task.Yield();

        // Compose the headless DI graph and resolve the root VM from it (mirrors the WPF App).
        try
        {
            var consent = startupWindow is null ? null : new TradingTerminal.App.Plugins.PluginConsentPrompt();
            _host = await Task.Run(() => ServiceConfiguration.BuildHost(consent));
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            if (startupWindow?.Content is TextBlock message)
            {
                startupWindow.Title = "DaxAlgo Terminal could not start";
                message.Text = $"Startup failed safely.\n\n{ex.Message}";
                return;
            }
            throw;
        }
        Services = _host.Services;

        var activityLog = Services.GetRequiredService<InMemoryLogSink>();
        var pluginHost = Services.GetRequiredService<
            TradingTerminal.Infrastructure.Plugins.PluginHostContext>();
        if (pluginHost.State is { } pluginFaultState)
        {
            _pluginFaultWatchdog = PluginFaultWatchdog.Attach(
                Dispatcher.UIThread,
                strikeLimit: 3,
                onStrikeOut: (plugin, reason) =>
                {
                    pluginFaultState.Quarantine(plugin, reason);
                    activityLog.Append(
                        "Plugins",
                        "Warning",
                        $"Strategy plugin '{plugin}' was quarantined after repeated faults and will " +
                        $"not load next start until re-enabled. {reason}");
                },
                log: activityLog.Append);
        }

        // Load custom themes and apply the persisted palette before any product window is created.
        Services.GetRequiredService<TradingTerminal.App.Avalonia.Theming.IThemeManager>().ApplySaved();

        // Point every instrument picker at the canonical registry instead of the hardcoded fallback
        // (mirrors the WPF shell). The registry fills at startup + as brokers connect.
        var registry = Services.GetRequiredService<TradingTerminal.Core.MarketData.IInstrumentRegistry>();
        TradingTerminal.UI.SignalInstrumentCatalog.Source = () =>
            TradingTerminal.UI.SignalInstrumentCatalog.FromRegistry(registry);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) =>
            {
                _pluginFaultWatchdog?.Dispose();
                _pluginFaultWatchdog = null;
                try
                {
                    _host?.StopAsync().GetAwaiter().GetResult();
                    _host?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Smoke / last-window Close can tear the host down more than once.
                }
                _host = null;
            };

            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Any(argument => string.Equals(
                    argument,
                    "--smoke-strategies",
                    StringComparison.OrdinalIgnoreCase)))
            {
                var diagnosticsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal",
                    "diagnostics");
                var reportPath = Path.Combine(diagnosticsDirectory, "smoke-strategies.txt");
                var exitCode = await StrategyWindowSmoke.RunAsync(
                    Services.GetRequiredService<TradingTerminal.Core.Strategies.IStrategyFactory>(),
                    reportPath,
                    pluginHost.LoadedPlugins.Select(plugin => plugin.Name));
                activityLog.Append(
                    "Diagnostics",
                    exitCode == 0 ? "Information" : "Error",
                    $"Strategy smoke finished with exit code {exitCode}; report: {reportPath}");
                startupWindow?.Close();
                desktop.Shutdown(exitCode);
                return;
            }

            var paperHandoffSmoke = args.FirstOrDefault(argument =>
                argument.StartsWith("--smoke-paper-handoff", StringComparison.OrdinalIgnoreCase));
            if (paperHandoffSmoke is not null)
            {
                var diagnosticsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal",
                    "diagnostics");
                var reportPath = paperHandoffSmoke.Contains('=', StringComparison.Ordinal)
                    ? paperHandoffSmoke.Split('=', 2)[1]
                    : Path.Combine(diagnosticsDirectory, "smoke-paper-handoff.txt");
                var exitCode = await PaperHandoffSmoke.RunAsync(Services, reportPath);
                activityLog.Append(
                    "Diagnostics",
                    exitCode == 0 ? "Information" : "Error",
                    $"Paper handoff smoke finished with exit code {exitCode}; report: {reportPath}");
                Services = null;
                startupWindow?.Close();
                desktop.Shutdown(exitCode);
                return;
            }

            var liveOmsSmoke = args.FirstOrDefault(argument =>
                argument.StartsWith("--smoke-live-oms", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--smoke-live-paper-alpaca", StringComparison.OrdinalIgnoreCase));
            if (liveOmsSmoke is not null)
            {
                var diagnosticsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal",
                    "diagnostics");
                var reportPath = liveOmsSmoke.Contains('=', StringComparison.Ordinal)
                    ? liveOmsSmoke.Split('=', 2)[1]
                    : Path.Combine(diagnosticsDirectory, "smoke-live-oms.txt");
                var exitCode = await LiveOmsSmoke.RunAsync(reportPath);
                activityLog.Append(
                    "Diagnostics",
                    exitCode == 0 ? "Information" : "Error",
                    $"Live OMS smoke finished with exit code {exitCode}; report: {reportPath}");
                Services = null;
                startupWindow?.Close();
                desktop.Shutdown(exitCode);
                return;
            }

            var researchToPaperSmoke = args.FirstOrDefault(argument =>
                argument.StartsWith("--smoke-research-to-paper", StringComparison.OrdinalIgnoreCase));
            if (researchToPaperSmoke is not null)
            {
                var diagnosticsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal",
                    "diagnostics");
                var reportPath = researchToPaperSmoke.Contains('=', StringComparison.Ordinal)
                    ? researchToPaperSmoke.Split('=', 2)[1]
                    : Path.Combine(diagnosticsDirectory, "smoke-research-to-paper.txt");
                var exitCode = await ResearchToPaperE2ESmoke.RunAsync(Services, reportPath);
                activityLog.Append(
                    "Diagnostics",
                    exitCode == 0 ? "Information" : "Error",
                    $"Research→Paper E2E smoke finished with exit code {exitCode}; report: {reportPath}");
                startupWindow?.Close();
                Services = null;
                desktop.Shutdown(exitCode);
                return;
            }

            var findingToDesignSmoke = args.FirstOrDefault(argument =>
                argument.StartsWith("--smoke-finding-to-design", StringComparison.OrdinalIgnoreCase));
            if (findingToDesignSmoke is not null)
            {
                var outDir = findingToDesignSmoke.Contains('=', StringComparison.Ordinal)
                    ? findingToDesignSmoke.Split('=', 2)[1]
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Developer",
                        "DaxAlgo-Terminal-Mac",
                        "tmp",
                        "finding-design-audit");
                var exitCode = await FindingToDesignSmoke.RunAsync(Services, outDir);
                activityLog.Append(
                    "Diagnostics",
                    exitCode == 0 ? "Information" : "Error",
                    $"Finding→Design smoke finished with exit code {exitCode}; dir: {outDir}");
                startupWindow?.Close();
                Services = null;
                desktop.Shutdown(exitCode);
                return;
            }

            var installOpenPackage = args.FirstOrDefault(argument =>
                argument.StartsWith("--install-open-package=", StringComparison.OrdinalIgnoreCase));
            if (installOpenPackage is not null)
            {
                var packageUrl = installOpenPackage.Split('=', 2)[1];
                var manager = Services.GetRequiredService<TradingTerminal.App.Plugins.PluginManagerViewModel>();
                var message = await manager.InstallOpenPackageFromUrlAsync(packageUrl);
                activityLog.Append(
                    "Marketplace",
                    message.Contains("registered", StringComparison.OrdinalIgnoreCase) ? "Information" : "Warning",
                    message);
                var diagnosticsDirectoryForInstall = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal",
                    "diagnostics");
                Directory.CreateDirectory(diagnosticsDirectoryForInstall);
                var installReportPath = Path.Combine(diagnosticsDirectoryForInstall, "install-open-package.txt");
                await File.WriteAllTextAsync(installReportPath, message);
                startupWindow?.Close();
                Services = null;
                desktop.Shutdown(message.Contains("registered", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                return;
            }

            var configuration = Services.GetRequiredService<IConfiguration>();
            var startupDevOptions = configuration
                .GetSection(DevOptions.SectionName)
                .Get<DevOptions>() ?? new DevOptions();
            var googleAuthOptions = configuration
                .GetSection(GoogleAuthOptions.SectionName)
                .Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();
            if (startupDevOptions.ResetAccountOnStart)
                AccountGateRunner.ClearStoredAccount();

            var bypassLoginRequested = args.Any(a =>
                string.Equals(a, "--bypass-login", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--no-login", StringComparison.OrdinalIgnoreCase));
            var bypassAccountLoginRequested = args.Any(a =>
                string.Equals(a, "--bypass-account-login", StringComparison.OrdinalIgnoreCase));
            var skipAccountGate = false;
#if DEBUG
            skipAccountGate = bypassLoginRequested || bypassAccountLoginRequested;
#endif

            MainWindow CreateMainWindow()
            {
                var main = new MainWindow
                {
                    DataContext = Services!.GetRequiredService<MainWindowViewModel>(),
                };
                main.Opened += (_, _) =>
                {
                    try
                    {
                        var previewArg = args.FirstOrDefault(argument =>
                            argument.StartsWith("--preview-overlays=", StringComparison.OrdinalIgnoreCase));
                        var previewResearchCapture = args.Any(argument =>
                            string.Equals(argument, "--preview-research-capture", StringComparison.OrdinalIgnoreCase));
                        var previewResearchScreen = args.Any(argument =>
                            string.Equals(argument, "--preview-research-screen", StringComparison.OrdinalIgnoreCase));
                        var previewResearchAuto = args.FirstOrDefault(argument =>
                            argument.StartsWith("--preview-research-auto", StringComparison.OrdinalIgnoreCase));
                        var previewDraftE2e = args.FirstOrDefault(argument =>
                            argument.StartsWith("--preview-draft-e2e", StringComparison.OrdinalIgnoreCase));
                        if (previewArg is not null || previewResearchCapture || previewResearchScreen || previewResearchAuto is not null || previewDraftE2e is not null)
                        {
                            var overlayIds = previewArg is null
                                ? Array.Empty<string>()
                                : previewArg
                                    .Split('=', 2, StringSplitOptions.TrimEntries)[1]
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            var autoScanId = previewResearchAuto is null
                                ? null
                                : previewResearchAuto.Contains('=', StringComparison.Ordinal)
                                    ? previewResearchAuto.Split('=', 2, StringSplitOptions.TrimEntries)[1]
                                    : "next-day-plus-5";
                            // Only default the scan id when --preview-research-auto was actually passed.
                            // Empty value after '=' still means "use the default S&P +5% scan".
                            if (previewResearchAuto is not null && string.IsNullOrWhiteSpace(autoScanId))
                                autoScanId = "next-day-plus-5";
                            var draftE2eOutDir = previewDraftE2e is null
                                ? null
                                : previewDraftE2e.Contains('=', StringComparison.Ordinal)
                                    ? previewDraftE2e.Split('=', 2, StringSplitOptions.TrimEntries)[1]
                                    : null;
                            if (overlayIds.Length > 0 || previewResearchCapture || previewResearchScreen || autoScanId is not null || previewDraftE2e is not null)
                            {
                                File.WriteAllText(
                                    "/tmp/daxalgo-preview-overlays.log",
                                    $"scheduled overlays=[{string.Join(',', overlayIds)}] researchCapture={previewResearchCapture} researchScreen={previewResearchScreen} researchAuto={autoScanId} draftE2e={previewDraftE2e is not null} at {DateTime.UtcNow:O}\n");
                                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                                {
                                    try
                                    {
                                        await Task.Delay(750);
                                        if (previewDraftE2e is not null)
                                        {
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"invoking PreviewDraftPlaceE2EAsync at {DateTime.UtcNow:O}\n");
                                            await main.PreviewDraftPlaceE2EAsync(
                                                string.IsNullOrWhiteSpace(draftE2eOutDir) ? null : draftE2eOutDir);
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"completed PreviewDraftPlaceE2EAsync at {DateTime.UtcNow:O}\n");
                                        }
                                        else if (previewResearchScreen)
                                        {
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"invoking PreviewResearchMarketScreenAsync at {DateTime.UtcNow:O}\n");
                                            await main.PreviewResearchMarketScreenAsync();
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"completed PreviewResearchMarketScreenAsync at {DateTime.UtcNow:O}\n");
                                        }
                                        else if (autoScanId is not null)
                                        {
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"invoking PreviewResearchAutoCollectAsync({autoScanId}) at {DateTime.UtcNow:O}\n");
                                            await main.PreviewResearchAutoCollectAsync(autoScanId);
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"completed PreviewResearchAutoCollectAsync at {DateTime.UtcNow:O}\n");
                                        }
                                        else
                                        {
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"invoking PreviewHostChartOverlays at {DateTime.UtcNow:O}\n");
                                            main.PreviewHostChartOverlays(overlayIds, startResearchCapture: previewResearchCapture);
                                            File.AppendAllText(
                                                "/tmp/daxalgo-preview-overlays.log",
                                                $"completed PreviewHostChartOverlays at {DateTime.UtcNow:O}\n");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        File.AppendAllText(
                                            "/tmp/daxalgo-preview-overlays.log",
                                            $"deferred failure: {ex}\n");
                                        (main.DataContext as MainWindowViewModel)?.ActivityLog.Append(
                                            "Charts",
                                            "ERROR",
                                            $"Deferred host overlay preview failed: {ex.Message}");
                                    }
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText("/tmp/daxalgo-preview-overlays.log", $"opened handler failure: {ex}\n");
                    }

                    // Skip Support modal during chart smoke previews and login-bypass runs so the
                    // catalog / Paper path stays visible for visual verification.
                    var isOverlayPreview = args.Any(argument =>
                        argument.StartsWith("--preview-overlays=", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(argument, "--preview-research-capture", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(argument, "--preview-research-screen", StringComparison.OrdinalIgnoreCase) ||
                        argument.StartsWith("--preview-research-auto", StringComparison.OrdinalIgnoreCase) ||
                        argument.StartsWith("--preview-draft-e2e", StringComparison.OrdinalIgnoreCase));
                    var skipSupportPrompt = isOverlayPreview || bypassLoginRequested || bypassAccountLoginRequested;
                    if (!skipSupportPrompt)
                    {
                        try
                        {
                            Services
                                .GetRequiredService<TradingTerminal.App.Support.ISupportPrompt>()
                                .MaybeShowOnLaunch(main);
                        }
                        catch
                        {
                            // Support prompt must never block shell startup.
                        }
                    }
                };
                return main;
            }

            LoginWindow CreateLoginWindow()
            {
                var loginVm = Services!.GetRequiredService<LoginViewModel>();
                var login = Services.GetRequiredService<LoginWindow>();
                loginVm.LoginCompleted += (_, success) => Dispatcher.UIThread.Post(() =>
                {
                    if (!success)
                    {
                        desktop.Shutdown();
                        return;
                    }

                    var main = CreateMainWindow();
                    desktop.MainWindow = main;
                    main.Show();
                    login.Close();
                });
                login.DataContext = loginVm;
                return login;
            }

            bool ShouldBypassBrokerLogin() =>
                !bypassAccountLoginRequested &&
                (startupDevOptions.BypassLogin || bypassLoginRequested);

            async Task ConnectAndShowMainAsync()
            {
                var selector = Services!.GetRequiredService<IBrokerSelector>();
                BrokerKind[] brokers = startupDevOptions.AutoConnectBrokers.Length == 0
                    ? [BrokerKind.Simulated]
                    : startupDevOptions.AutoConnectBrokers;

                foreach (var kind in brokers)
                {
                    if (!selector.IsAvailable(kind))
                    {
                        activityLog.Append(
                            "Dev",
                            "Warning",
                            $"Auto-connect skipped — broker {kind} is not available in this build.");
                        continue;
                    }

                    try
                    {
                        activityLog.Append(
                            "Dev",
                            "Information",
                            $"Login bypassed — auto-connecting {kind}…");
                        await selector.ConnectAsync(kind);
                    }
                    catch (Exception ex)
                    {
                        activityLog.Append(
                            "Dev",
                            "Error",
                            $"Auto-connect failed for {kind}: {ex.Message}");
                    }
                }

                var main = CreateMainWindow();
                desktop.MainWindow = main;
                main.Show();
            }

            if (skipAccountGate)
            {
                // Matches the Windows developer escape hatches: one flag skips only the product
                // account gate; the broader flag also skips broker login. Both are Debug-only.
                if (ShouldBypassBrokerLogin())
                {
                    await ConnectAndShowMainAsync();
                }
                else
                {
                    var login = CreateLoginWindow();
                    desktop.MainWindow = login;
                    login.Show();
                }
                startupWindow?.Close();
            }
            else
            {
                var accountGate = AccountGateRunner.CreateWindow(
                    AppEdition.Professional,
                    googleAuthOptions);
                accountGate.AccessCompleted += async granted =>
                {
                    if (!granted)
                    {
                        desktop.Shutdown();
                        return;
                    }

                    if (ShouldBypassBrokerLogin())
                    {
                        await ConnectAndShowMainAsync();
                    }
                    else
                    {
                        // Show the broker login before closing the gate so OnLastWindowClose cannot
                        // end the desktop lifetime between the two pre-shell stages.
                        var login = CreateLoginWindow();
                        desktop.MainWindow = login;
                        login.Show();
                    }
                };
                desktop.MainWindow = accountGate;
                accountGate.Show();
                startupWindow?.Close();
            }
        }
    }

    // Points the portable UiFile seam at Avalonia's StorageProvider (the cross-platform file picker),
    // so tool VMs that load/save files work on the Avalonia head as they do on WPF.
    private static void WireFilePicker()
    {
        UiFile.OpenAsync = async (desc, exts) =>
        {
            if (ActiveTopLevel()?.StorageProvider is not { } sp) return null;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(desc) { Patterns = exts.Select(e => "*." + e).ToArray() } },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
        UiFile.SaveAsync = async (desc, exts, name) =>
        {
            if (ActiveTopLevel()?.StorageProvider is not { } sp) return null;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = name,
                FileTypeChoices = new[] { new FilePickerFileType(desc) { Patterns = exts.Select(e => "*." + e).ToArray() } },
            });
            return file?.TryGetLocalPath();
        };
    }

    /// <summary>The active (or main) window to parent file dialogs to.</summary>
    private static TopLevel? ActiveTopLevel()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        return null;
    }

    // Runs the work on Avalonia's UI thread and surfaces its completion/exception back to the caller.
    private static Task MarshalToUiThread(Func<Task> work)
    {
        if (Dispatcher.UIThread.CheckAccess()) return work();

        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await work().ConfigureAwait(true); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
