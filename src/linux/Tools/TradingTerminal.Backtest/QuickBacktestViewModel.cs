using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
#if WINDOWS
using System.Windows;
#endif
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Risk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.Sandbox;
using TradingTerminal.UI;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.Backtest;

/// <summary>How the Quick-backtest sources its replay data.</summary>
public enum QuickBacktestDataMode
{
    /// <summary>Pull the real historical trade tape from a broker that exposes one (Binance via
    /// <c>aggTrades</c>) and synthesize a tight L1 quote around each real print. Tape-primary
    /// strategies (SigmaIcFlow) then run at full <see cref="FeedQuality.RealTape"/> quality — a
    /// genuine backtest. Depth/OBI still cannot participate (the engine does not replay L2).</summary>
    FullTapeRealTrades,

    /// <summary>Pull exact completed OHLCV bars and also derive four deterministic L1 observations
    /// per bar for the fill model. Portable to any historical-bar broker, but there are no real
    /// prints, so a tape-primary strategy runs in discounted synthetic-L1 mode (q ≈ 0.4).</summary>
    BarSynthetic,
}

/// <summary>
/// Exact canonical strategy and parameter snapshot that completed a Quick Backtest and can be handed
/// to the Paper runner. Paper still performs its own account/risk admission; this receipt only prevents
/// the desktop workflow from silently reverting to different strategy parameters between the two windows.
/// </summary>
public sealed record QuickBacktestPaperLaunchRequest(
    StrategyKernelRegistration Registration,
    IReadOnlyDictionary<string, object?> TestedParameters,
    DateTime CompletedUtc,
    string ResultSummary,
    HistoricalValidationEvidenceV1? ValidationEvidence = null);

/// <summary>
/// One-click backtest launched from the Strategy-catalog "Quick backtest" item. Customised so a
/// tape-primary strategy (SigmaIcFlow) can be backtested <em>properly</em>: with
/// <see cref="QuickBacktestDataMode.FullTapeRealTrades"/> + Binance it pulls the real historical tape
/// (<c>/api/v3/aggTrades</c>, exact aggressor from the maker flag), synthesizes a one-tick L1 around
/// each real print so the fill model + cost gate have a spread, and replays both through the shared
/// engine (<see cref="IBacktestSession"/>) — every flow signal runs at full quality. Falls back to
/// bar-synthesized ticks for brokers without a historical tape.
/// </summary>
public sealed partial class QuickBacktestViewModel : ViewModelBase, IDisposable
{
    private readonly IBacktestStrategyRegistry _registry;
    private readonly IBacktestSession _session;
    private readonly IBrokerSelector _brokers;
    private readonly IStrategyKernelRegistry _kernelRegistry;
    private readonly IInstrumentRegistry _instrumentRegistry;
    private readonly ILogger<QuickBacktestViewModel> _logger;
    private CancellationTokenSource? _runCts;

    private BacktestStrategyOption? _option;
    private StrategyKernelRegistration? _kernelOption;
    private IReadOnlyList<CanonicalBacktestSelection> _canonicalSelections = [];
    private QuickBacktestPaperLaunchRequest? _paperLaunchRequest;
    private HistoricalValidationContextV1? _validationContext;
    private string? _appliedExecutionFidelityToken;
    private double _executionLatencyMs;
    private long _maxFillQuantityPerTouch;
    private bool _capToOppositeL1Size;

    public QuickBacktestViewModel(
        IBacktestStrategyRegistry registry,
        IBacktestSession session,
        IBrokerSelector brokers,
        IStrategyKernelRegistry kernelRegistry,
        IInstrumentRegistry instrumentRegistry,
        ILogger<QuickBacktestViewModel> logger)
    {
        _registry = registry;
        _session = session;
        _brokers = brokers;
        _kernelRegistry = kernelRegistry;
        _instrumentRegistry = instrumentRegistry;
        _logger = logger;

        BarSizes = new ObservableCollection<BarSize>(new[]
        {
            BarSize.OneMinute, BarSize.ThreeMinutes, BarSize.FiveMinutes,
            BarSize.FifteenMinutes, BarSize.OneHour, BarSize.OneDay,
        });
        Lookbacks = new ObservableCollection<LookbackOption>(new[]
        {
            new LookbackOption("Last 1 hour", TimeSpan.FromHours(1)),
            new LookbackOption("Last 2 hours", TimeSpan.FromHours(2)),
            new LookbackOption("Last 4 hours", TimeSpan.FromHours(4)),
            new LookbackOption("Last 12 hours", TimeSpan.FromHours(12)),
            new LookbackOption("Last 1 day", TimeSpan.FromDays(1)),
            new LookbackOption("Last 1 week", TimeSpan.FromDays(7)),
            new LookbackOption("Last 1 month", TimeSpan.FromDays(30)),
            new LookbackOption("Last 1 year", TimeSpan.FromDays(365)),
        });
        DataModes = new ObservableCollection<QuickBacktestDataMode>(new[]
        {
            QuickBacktestDataMode.FullTapeRealTrades, QuickBacktestDataMode.BarSynthetic,
        });
        Brokers = new ObservableCollection<BrokerKind>(_brokers.AvailableKinds);
        Instruments = new ObservableCollection<SignalInstrument>();

        Trades = new ObservableCollection<Trade>();
        EquityCurve = new ObservableCollection<EquityPoint>();

        _selectedBroker = PickDefaultBroker();
        _selectedBarSize = BarSize.OneHour;
        _selectedDataMode = QuickBacktestDataMode.BarSynthetic;
        _selectedLookback = Lookbacks.First(l => l.Duration == TimeSpan.FromDays(365));
        RebuildInstrumentsFor(_selectedBroker);
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<BarSize> BarSizes { get; }
    public ObservableCollection<LookbackOption> Lookbacks { get; }
    public ObservableCollection<QuickBacktestDataMode> DataModes { get; }
    public ObservableCollection<BrokerKind> Brokers { get; }
    public ObservableCollection<Trade> Trades { get; }
    public ObservableCollection<EquityPoint> EquityCurve { get; }

    /// <summary>Display name of the live strategy being backtested — shown in the header.</summary>
    [ObservableProperty] private string _strategyDisplayName = "Strategy";

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private BarSize _selectedBarSize;
    [ObservableProperty] private LookbackOption _selectedLookback;
    [ObservableProperty] private BrokerKind _selectedBroker;
    [ObservableProperty] private QuickBacktestDataMode _selectedDataMode;

    /// <summary>Per-fill cost in basis points (round-turn modelled per side). Binance spot taker ≈ 7.5 bps.</summary>
    [ObservableProperty] private double _feeBps = 7.5;

    /// <summary>Price increment for the synthetic L1 spread + the fill model. Crypto USDT pairs ≈ 0.01.</summary>
    [ObservableProperty] private double _tickSize = 0.01;

    /// <summary>Safety cap on how many real prints the full-tape pull fetches (each REST page = 1000).</summary>
    [ObservableProperty] private int _maxTrades = 150_000;

    /// <summary>Risk cap applied before every simulated order dispatch.</summary>
    [ObservableProperty] private long _maxPositionPerSymbol = 100;

    /// <summary>Realised-loss cap applied by the replay risk manager.</summary>
    [ObservableProperty] private double _maxDailyLoss = 10_000d;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _feedQuality;
    [ObservableProperty] private StrategyParametersViewModel? _parameters;

    [ObservableProperty] private BacktestStatistics? _stats;
    [ObservableProperty] private double _totalPnl;

    public bool IsFullTape => SelectedDataMode == QuickBacktestDataMode.FullTapeRealTrades;
    public bool IsBarSynthetic => SelectedDataMode == QuickBacktestDataMode.BarSynthetic;
    public bool IsAuthoredStrategy => _kernelOption is not null;
    public bool CanSelectInstrument => !IsAuthoredStrategy && !IsRunning;
    public bool CanSelectBarSize => !IsAuthoredStrategy && !IsRunning;
    public IReadOnlyList<ParameterEditorItem> EditableParameters => Parameters?.Items
        .Where(static item => !item.IsInstrument)
        .ToArray() ?? [];
    public bool HasStrategyParameters => EditableParameters.Count != 0;
    public bool CanEditParameters => HasStrategyParameters && !IsRunning;
    public bool CanRunTestedStrategyInPaper => _paperLaunchRequest is not null && !IsRunning;
    public string ReviewedInstrumentSummary => _canonicalSelections.Count == 0
        ? SelectedInstrument?.DisplayName ?? "No instrument selected"
        : string.Join(" · ", _canonicalSelections.Select(item => item.Instrument.CanonicalSymbol));

    partial void OnSelectedDataModeChanged(QuickBacktestDataMode value)
    {
        OnPropertyChanged(nameof(IsFullTape));
        OnPropertyChanged(nameof(IsBarSynthetic));
    }

    partial void OnParametersChanged(StrategyParametersViewModel? value)
    {
        OnPropertyChanged(nameof(EditableParameters));
        OnPropertyChanged(nameof(HasStrategyParameters));
        OnPropertyChanged(nameof(CanEditParameters));
    }

    partial void OnSelectedInstrumentChanged(SignalInstrument? value) =>
        OnPropertyChanged(nameof(ReviewedInstrumentSummary));

    partial void OnSelectedBrokerChanged(BrokerKind value)
    {
        if (_kernelOption?.AuthoredSpecification.Instruments is { Count: > 0 } requested)
            BindCanonicalInstruments(requested, value);
        else
            RebuildInstrumentsFor(value);
    }

    /// <summary>Raised after a run completes so the view can redraw the ScottPlot equity curve.</summary>
    public event EventHandler? EquityCurveUpdated;
    public event Action<QuickBacktestPaperLaunchRequest>? PaperLaunchRequested;
    public event Action<QuickBacktestPaperLaunchRequest>? HistoricalValidationCompleted;

    /// <summary>
    /// Binds this window to a live strategy by its engine-side backtest id and kicks off the first run.
    /// <paramref name="preferFullTape"/> (true for tape-primary strategies like SigmaIcFlow) defaults
    /// the window to Binance + real-tape mode + a liquid crypto pair so the auto-run is a proper backtest.
    /// Returns false (with a status message) when the strategy has no backtest counterpart.
    /// </summary>
    public bool Initialize(string? backtestStrategyId, string displayName, bool preferFullTape)
    {
        ClearPaperLaunchRequest();
        _validationContext = null;
        _appliedExecutionFidelityToken = null;
        _executionLatencyMs = 0;
        _maxFillQuantityPerTouch = 0;
        _capToOppositeL1Size = false;
        _kernelOption = null;
        _canonicalSelections = [];
        Parameters = null;
        NotifyAuthoredSelectionState();
        OnPropertyChanged(nameof(ReviewedInstrumentSummary));
        StrategyDisplayName = displayName;

        if (string.IsNullOrWhiteSpace(backtestStrategyId))
        {
            Status = $"'{displayName}' has no backtest counterpart wired up yet.";
            return false;
        }

        _option = _registry.Find(backtestStrategyId);
        if (_option is null)
        {
            Status = $"No backtest strategy registered for id '{backtestStrategyId}'.";
            return false;
        }
        Parameters = StrategyParametersViewModel.FromSchema(_option.Schema);

        if (preferFullTape && _brokers.IsAvailable(BrokerKind.Binance))
        {
            SelectedBroker = BrokerKind.Binance;          // rebuilds Instruments to the crypto universe
            SelectedDataMode = QuickBacktestDataMode.FullTapeRealTrades;
            SelectedLookback = Lookbacks.First(l => l.Duration == TimeSpan.FromHours(2));
            SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "BTCUSDT") ?? Instruments.FirstOrDefault();
        }

        if (RunCommand.CanExecute(null)) RunCommand.Execute(null);
        return true;
    }

    /// <summary>
    /// Binds Quick Backtest to one installed canonical SDK strategy. The reviewed instrument and
    /// timeframe remain fixed so the historical run cannot silently test a different artifact.
    /// </summary>
    public bool Initialize(
        StrategyKernelRegistration registration,
        HistoricalValidationContextV1? validationContext = null,
        string? appliedExecutionFidelityToken = null,
        int executionLatencyMs = 0,
        long maxFillQuantityPerTouch = 0,
        bool capToOppositeL1Size = false)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ClearPaperLaunchRequest();
        _validationContext = validationContext;
        _appliedExecutionFidelityToken = string.IsNullOrWhiteSpace(appliedExecutionFidelityToken)
            ? null
            : appliedExecutionFidelityToken.Trim();
        _executionLatencyMs = Math.Max(0, executionLatencyMs);
        _maxFillQuantityPerTouch = Math.Max(0, maxFillQuantityPerTouch);
        _capToOppositeL1Size = capToOppositeL1Size;
        StrategyDisplayName = registration.DisplayName;
        _option = null;
        _kernelOption = _kernelRegistry.Find(registration.Id);
        Parameters = _kernelOption is null
            ? null
            : StrategyParametersViewModel.FromSchema(_kernelOption.Schema);
        NotifyAuthoredSelectionState();
        if (_kernelOption is null)
        {
            Status = $"Canonical strategy '{registration.Id}' is no longer registered.";
            return false;
        }

        var specification = _kernelOption.AuthoredSpecification;
        if (specification.Instruments.Count == 0)
        {
            Status = "Quick Backtest requires at least one reviewed strategy instrument.";
            return false;
        }
        if ((specification.DataRequirement & StrategyDataRequirement.Depth) != 0)
        {
            Status = "Quick Backtest cannot replay Level-2 depth yet; use a capable live Paper feed.";
            return false;
        }
        if (specification.Timeframe.BarSize is not { } interval ||
            !TryResolveBarSize(interval, out var barSize))
        {
            Status = $"The reviewed timeframe '{specification.Timeframe.UserText}' is not supported by Quick Backtest.";
            return false;
        }

        var requested = specification.Instruments[0];
        if (requested.PreferredBroker is { } preferred && _brokers.IsAvailable(preferred))
            SelectedBroker = preferred;
        SelectedBarSize = barSize;
        SelectedDataMode = QuickBacktestDataMode.BarSynthetic;
        BindCanonicalInstruments(specification.Instruments, SelectedBroker);
        if (_canonicalSelections.Count != specification.Instruments.Count)
        {
            Status = "One or more reviewed instruments are unavailable in the canonical registry for this data source.";
            return false;
        }

        ClampLookbackForSimulatedHistory(barSize);
        Status = HasStrategyParameters
            ? $"Review parameters and risk, then replay {ReviewedInstrumentSummary} on one UTC clock."
            : $"Review risk, then replay {ReviewedInstrumentSummary} on one UTC clock.";
        return true;
    }

    /// <summary>
    /// Simulated history refuses windows that would synthesize more than 5000 bars
    /// (1 year of 1-minute bars is 525600). Historical Validate must pick a fitting lookback.
    /// </summary>
    private void ClampLookbackForSimulatedHistory(BarSize barSize)
    {
        if (SelectedBroker != BrokerKind.Simulated) return;
        const int maxSyntheticBars = 5000;
        var stepTicks = Math.Max(1, barSize.ToTimeSpan().Ticks);
        var fit = Lookbacks
            .OrderByDescending(static option => option.Duration)
            .FirstOrDefault(option => option.Duration.Ticks / stepTicks <= maxSyntheticBars)
            ?? Lookbacks.First(static option => option.Duration == TimeSpan.FromHours(1));
        if (SelectedLookback.Duration > fit.Duration)
            SelectedLookback = fit;
    }

    /// <summary>Prefer Binance (real tape) → any other connected real broker → Simulated synthetic.</summary>
    private BrokerKind PickDefaultBroker()
    {
        if (_brokers.IsAvailable(BrokerKind.Binance)) return BrokerKind.Binance;
        foreach (var k in _brokers.Connected)
            if (k != BrokerKind.Simulated) return k;
        if (_brokers.IsAvailable(BrokerKind.Simulated)) return BrokerKind.Simulated;
        return Brokers.FirstOrDefault();
    }

    /// <summary>Binance shows the crypto universe; every other broker uses the shared signal catalog.</summary>
    private void RebuildInstrumentsFor(BrokerKind broker)
    {
        var keep = SelectedInstrument?.Contract.Symbol;
        Instruments.Clear();
        var source = broker == BrokerKind.Binance ? CryptoInstruments() : SignalInstrumentCatalog.All;
        foreach (var i in source) Instruments.Add(i);
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == keep) ?? Instruments.FirstOrDefault();
    }

    private void BindCanonicalInstruments(
        IReadOnlyList<AuthoredInstrumentRequestV1> requests,
        BrokerKind broker)
    {
        Instruments.Clear();
        var selections = new List<CanonicalBacktestSelection>(requests.Count);
        foreach (var request in requests)
        {
            var instrument = _instrumentRegistry.Get(request.InstrumentId);
            if (instrument is null) continue;

            var brokerSymbol = _instrumentRegistry.ToBrokerSymbol(request.InstrumentId, broker)
                ?? instrument.CanonicalSymbol;
            var contract = new Contract(
                brokerSymbol,
                SecTypeFor(instrument.AssetClass),
                instrument.Exchange,
                instrument.Currency,
                instrument.Exchange);
            var choice = new SignalInstrument(
                $"{instrument.CanonicalSymbol} · {instrument.AssetClass}",
                instrument.AssetClass.ToString(),
                contract,
                broker);
            Instruments.Add(choice);
            selections.Add(new CanonicalBacktestSelection(instrument, choice));
        }
        _canonicalSelections = selections;
        SelectedInstrument = selections.FirstOrDefault()?.Choice;
        if (selections.FirstOrDefault()?.Instrument.TickSize is > 0d)
            TickSize = selections[0].Instrument.TickSize;
        OnPropertyChanged(nameof(ReviewedInstrumentSummary));
    }

    private void NotifyAuthoredSelectionState()
    {
        OnPropertyChanged(nameof(IsAuthoredStrategy));
        OnPropertyChanged(nameof(CanSelectInstrument));
        OnPropertyChanged(nameof(CanSelectBarSize));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectInstrument));
        OnPropertyChanged(nameof(CanSelectBarSize));
        OnPropertyChanged(nameof(CanEditParameters));
        OnPropertyChanged(nameof(CanRunTestedStrategyInPaper));
        RunTestedStrategyInPaperCommand.NotifyCanExecuteChanged();
    }

    private static bool TryResolveBarSize(TimeSpan interval, out BarSize size)
    {
        foreach (var candidate in Enum.GetValues<BarSize>())
        {
            if (candidate.ToTimeSpan() != interval) continue;
            size = candidate;
            return true;
        }

        size = default;
        return false;
    }

    private static string SecTypeFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Future => "FUT",
        AssetClass.Forex => "CASH",
        AssetClass.Crypto => "CRYPTO",
        AssetClass.Option => "OPT",
        AssetClass.Index => "IND",
        _ => "STK",
    };

    private static IReadOnlyList<SignalInstrument> CryptoInstruments() => new[]
    {
        Crypto("BTCUSDT", "BTC/USDT — Bitcoin"),
        Crypto("ETHUSDT", "ETH/USDT — Ether"),
        Crypto("SOLUSDT", "SOL/USDT — Solana"),
        Crypto("BNBUSDT", "BNB/USDT — BNB"),
        Crypto("XRPUSDT", "XRP/USDT — XRP"),
        Crypto("DOGEUSDT", "DOGE/USDT — Dogecoin"),
    };

    private static SignalInstrument Crypto(string symbol, string display) =>
        new(display, "Crypto (Binance)", new Contract(symbol, "CRYPTO", "BINANCE", "USDT", PrimaryExchange: string.Empty), BrokerKind.Binance);

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (_option is null && _kernelOption is null) { Status ??= "Strategy not initialised."; return; }
        if (SelectedInstrument is null) { Status = "Pick an instrument."; return; }
        if (!_brokers.IsAvailable(SelectedBroker)) { Status = $"{SelectedBroker} is not registered. Pick another data source."; return; }
        if (TickSize <= 0) { Status = "Tick size must be greater than zero."; return; }
        if (MaxPositionPerSymbol <= 0) { Status = "Maximum position must be greater than zero."; return; }
        if (!double.IsFinite(MaxDailyLoss) || MaxDailyLoss <= 0) { Status = "Maximum daily loss must be a finite positive value."; return; }
        if (Parameters?.Parameters.Validate() is { Count: > 0 } parameterErrors)
        {
            Status = $"Strategy parameters are invalid: {string.Join(" ", parameterErrors)}";
            return;
        }

        ClampLookbackForSimulatedHistory(SelectedBarSize);

        ClearPaperLaunchRequest();
        var testedParameters = Parameters?.Parameters.ToDictionary()
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_kernelOption is not null && IsFullTape &&
            _kernelOption.DataRequirement.HasFlag(StrategyDataRequirement.Bars))
        {
            Status = "This authored strategy requires completed bars. Use bar-synthetic replay; real-tape mode does not manufacture broker candles.";
            return;
        }
        if (_kernelOption is not null && _canonicalSelections.Count > 1 && IsFullTape)
        {
            Status = "Multi-instrument Quick Backtest currently requires completed-bar replay; historical tapes cannot yet be synchronized by canonical instrument.";
            return;
        }

        IsRunning = true;
        Trades.Clear();
        EquityCurve.Clear();
        Stats = null;
        TotalPnl = 0;
        FeedQuality = null;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var contract = SelectedInstrument.Contract;
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc - SelectedLookback.Duration;
        string? quotesPath = null;
        string? tradesPath = null;

        try
        {
            var client = _brokers.Get(SelectedBroker);
            BacktestConfig config;

            if (SelectedDataMode == QuickBacktestDataMode.FullTapeRealTrades)
            {
                if (!client.MarketDataCapabilities.SupportsHistoricalTrades)
                {
                    Status = $"{SelectedBroker} does not provide historical trade tape. Choose a capable broker or use completed-bar replay.";
                    return;
                }
                Status = $"Fetching the real {contract.Symbol} tape from {SelectedBroker} (up to {MaxTrades:N0} prints)…";
                IReadOnlyList<TradeTick> tape;
                try
                {
                    tape = await client.RequestHistoricalTradesAsync(contract, fromUtc, toUtc, MaxTrades, ct).ConfigureAwait(true);
                }
                catch (NotSupportedException)
                {
                    Status = $"{SelectedBroker} has no historical trade tape. Switch the data source to Binance, " +
                             "or change the mode to bar-synthetic (degraded for tape-primary strategies).";
                    return;
                }

                if (tape.Count == 0)
                {
                    Status = $"No trades returned for '{contract.Symbol}' over {SelectedLookback.Label.ToLowerInvariant()}. " +
                             "Try a more liquid pair or a different window.";
                    return;
                }

                Status = $"Replaying {tape.Count:N0} real prints through the engine…";
                quotesPath = Path.Combine(Path.GetTempPath(), $"quick-bt-q-{Guid.NewGuid():N}.parquet");
                tradesPath = Path.Combine(Path.GetTempPath(), $"quick-bt-t-{Guid.NewGuid():N}.parquet");
                await WriteRealTapeAsync(quotesPath, tradesPath, tape, TickSize, SelectedBroker, ct).ConfigureAwait(true);

                config = new BacktestConfig(
                    Contract: contract,
                    TickDataPath: quotesPath,
                    TickSize: TickSize,
                    SlippageTicks: 1,
                    ContractMultiplier: 1,            // crypto spot: PnL is in quote currency, multiplier = 1
                    StartingCash: 100_000,
                    FeeModel: new BpsFeeModel(FeeBps),
                    Source: BacktestDataSource.ParquetFile,
                    TradeDataPath: tradesPath,
                    LatencyMs: _executionLatencyMs,
                    MaxFillQuantityPerTouch: _maxFillQuantityPerTouch,
                    CapToOppositeL1Size: _capToOppositeL1Size);
                FeedQuality = "Real tape (q = 1.0) — full quality. Depth/OBI excluded (engine is L1-only for depth).";
            }
            else
            {
                if (!client.MarketDataCapabilities.SupportsHistoricalBars)
                {
                    Status = $"{SelectedBroker} does not provide historical bars, so it cannot run this Quick Backtest.";
                    return;
                }
                IReadOnlyList<CanonicalBacktestSelection> selections = _kernelOption is null
                    ? [new CanonicalBacktestSelection(
                        new Instrument(InstrumentId.None, contract.Symbol, AssetClass.Equity, contract.Exchange, contract.Currency, TickSize, 1d),
                        SelectedInstrument!)]
                    : _canonicalSelections;
                Status = $"Fetching {SelectedBarSize} bars for {string.Join(", ", selections.Select(item => item.Instrument.CanonicalSymbol))} from {SelectedBroker}…";
                var histories = new List<(CanonicalBacktestSelection Selection, IReadOnlyList<Bar> Bars)>(selections.Count);
                foreach (var selection in selections)
                {
                    var fetchedBars = await client.RequestHistoricalBarsAsync(
                        selection.Choice.Contract,
                        SelectedBarSize,
                        SelectedLookback.Duration,
                        ct).ConfigureAwait(true);
                    if (fetchedBars.Count == 0)
                    {
                        Status = $"No bars returned for '{selection.Choice.Contract.Symbol}' from {SelectedBroker}; the basket was not partially replayed.";
                        return;
                    }
                    histories.Add((selection, fetchedBars));
                }

                var primaryBars = histories[0].Bars;
                var series = histories.Select(item => new BacktestBarSeries(
                    item.Selection.Instrument.Id,
                    item.Selection.Choice.Contract,
                    SelectedBarSize,
                    item.Bars,
                    item.Selection.Instrument.TickSize > 0d ? item.Selection.Instrument.TickSize : TickSize,
                    item.Selection.Instrument.Multiplier > 0d ? item.Selection.Instrument.Multiplier : 1d)).ToArray();
                var coverage = AnalyzeCoverage(histories);
                if (histories.Count > 1 && coverage.CommonBoundaryCount != coverage.UnionBoundaryCount)
                {
                    FeedQuality = coverage.Description;
                    Status = "The reviewed instruments do not have identical completed-bar boundaries. " +
                             "Quick Backtest stopped instead of forward-filling or evaluating a stale pair leg.";
                    return;
                }
                Status = $"Replaying {histories.Sum(item => item.Bars.Count):N0} completed bars across {histories.Count} instrument(s) on one UTC clock…";

                config = new BacktestConfig(
                    Contract: contract,
                    TickDataPath: string.Empty,
                    TickSize: TickSize,
                    SlippageTicks: 1,
                    ContractMultiplier: 1,
                    StartingCash: 100_000,
                    FeeModel: new BpsFeeModel(FeeBps),
                    Source: BacktestDataSource.ParquetFile,
                    ReplayBars: series.Length == 1 ? primaryBars : null,
                    ReplayBarSize: SelectedBarSize,
                    ReplayBarSeries: series.Length > 1 ? series : null,
                    LatencyMs: _executionLatencyMs,
                    MaxFillQuantityPerTouch: _maxFillQuantityPerTouch,
                    CapToOppositeL1Size: _capToOppositeL1Size);
                FeedQuality = _kernelOption is null
                    ? "Completed broker bars + deterministic synthetic L1 fills (q ≈ 0.4 for tape logic)."
                    : $"Reviewed SDK bars + contract-scoped synthetic L1 fills. {coverage.Description} Missing bars are not forward-filled; orders are risk-gated.";
            }

            IBacktestStrategy strategy = _kernelOption is { } authored
                ? new SdkStrategyBacktestAdapter(
                    authored.Create(),
                    _canonicalSelections.Select(item => new SdkBacktestInstrument(
                        item.Instrument.Id,
                        item.Choice.Contract)).ToArray(),
                    SelectedBarSize,
                    SelectedBroker,
                    Parameters?.Parameters.ToDictionary())
                : _option!.Create(contract, Parameters?.Parameters);
            var risk = new RiskManager(new RiskOptions
            {
                MaxPositionPerSymbol = MaxPositionPerSymbol,
                MaxDailyLoss = MaxDailyLoss,
                DefaultContractMultiplier = config.ContractMultiplier,
                ContractMultipliersBySymbol = (config.ReplayBarSeries ?? [])
                    .ToDictionary(series => series.Contract.Symbol, series => series.ContractMultiplier, StringComparer.Ordinal),
            });
            var result = await Task.Run(
                () => _session.RunAsync(config, strategy, risk, ct),
                ct).ConfigureAwait(true);

            foreach (var t in result.Trades) Trades.Add(t);
            foreach (var p in result.EquityCurve) EquityCurve.Add(p);
            Stats = result.Stats;
            TotalPnl = result.EndingCash - result.StartingCash;
            Status = $"Done. {result.Trades.Count} trades, P&L {TotalPnl.ToString("C2", CultureInfo.CurrentCulture)} " +
                     $"(fees {result.TotalFees.ToString("C2", CultureInfo.CurrentCulture)}; " +
                     $"risk max {MaxPositionPerSymbol:N0} units / " +
                     $"{MaxDailyLoss.ToString("C0", CultureInfo.CurrentCulture)} daily loss).";
            if (_kernelOption is { } canonical)
            {
                var completedUtc = DateTime.UtcNow;
                HistoricalValidationEvidenceV1? validationEvidence = null;
                if (_validationContext is { } validationContext)
                {
                    var dataMode = string.IsNullOrWhiteSpace(_appliedExecutionFidelityToken)
                        ? SelectedDataMode.ToString()
                        : $"{SelectedDataMode}|{_appliedExecutionFidelityToken}";
                    validationEvidence = new HistoricalValidationEvidenceV1(
                        HistoricalValidationEvidenceV1.CurrentSchemaVersion,
                        validationContext,
                        StrategyWorkspaceCanonicalJsonV1.HashArtifact(testedParameters
                            .OrderBy(item => item.Key, StringComparer.Ordinal)
                            .Select(item => new TestedParameterBinding(item.Key, item.Value))
                            .ToArray()),
                        fromUtc,
                        toUtc,
                        dataMode,
                        FeedQuality ?? "Historical replay completed.",
                        result.Trades.Count,
                        result.StartingCash,
                        result.EndingCash,
                        result.TotalFees,
                        completedUtc);
                    HistoricalValidationEvidenceValidatorV1.RequireValid(validationEvidence);
                }
                _paperLaunchRequest = new QuickBacktestPaperLaunchRequest(
                    canonical,
                    testedParameters,
                    completedUtc,
                    Status,
                    validationEvidence);
                if (validationEvidence is not null)
                    HistoricalValidationCompleted?.Invoke(_paperLaunchRequest);
                OnPropertyChanged(nameof(CanRunTestedStrategyInPaper));
                RunTestedStrategyInPaperCommand.NotifyCanExecuteChanged();
            }
            EquityCurveUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quick backtest run failed");
            Status = $"Failed: {ex.Message}";
#if WINDOWS
            MessageBox.Show(ex.Message, "Quick backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
#endif
        }
        finally
        {
            TryDelete(quotesPath);
            TryDelete(tradesPath);
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _runCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRunTestedStrategyInPaper))]
    private void RunTestedStrategyInPaper()
    {
        if (_paperLaunchRequest is { } request)
            PaperLaunchRequested?.Invoke(request);
    }

    private void ClearPaperLaunchRequest()
    {
        _paperLaunchRequest = null;
        OnPropertyChanged(nameof(CanRunTestedStrategyInPaper));
        RunTestedStrategyInPaperCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Cancels any in-flight run when the window closes so a long backtest doesn't outlive it.</summary>
    public void Dispose()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* run already completed and disposed the CTS */ }
        PaperLaunchRequested = null;
        HistoricalValidationCompleted = null;
    }

    private sealed record TestedParameterBinding(string Key, object? Value);

    /// <summary>
    /// Writes the real tape as two time-aligned parquets the engine merges: the trades file carries the
    /// genuine prints (price/size/aggressor) that drive every flow signal at full quality; the quotes
    /// file carries a one-tick L1 straddling each print so the order book can fill and the cost gate has
    /// a spread. Timestamps are forced strictly increasing (Binance stamps to the millisecond, so prints
    /// collide) — the clock requires monotonic time.
    /// </summary>
    private static async Task WriteRealTapeAsync(
        string quotesPath, string tradesPath, IReadOnlyList<TradeTick> tape, double tickSize, BrokerKind source, CancellationToken ct)
    {
        var half = Math.Max(tickSize, 1e-9) / 2.0;
        var lastTicks = long.MinValue;

        await using var quoteWriter = new ParquetTickWriter(quotesPath);
        await using var tradeWriter = new ParquetTradeWriter(tradesPath);

        long seq = 0;
        foreach (var p in tape)
        {
            ct.ThrowIfCancellationRequested();

            var ts = p.TimestampUtc;
            if (ts.Ticks <= lastTicks) ts = new DateTime(lastTicks + 10, DateTimeKind.Utc); // +1 microsecond
            lastTicks = ts.Ticks;

            var sizeProxy = Math.Max(1, p.Size);
            await quoteWriter.WriteAsync(new Tick(ts, p.Price - half, p.Price + half, sizeProxy, sizeProxy), ct).ConfigureAwait(false);
            await tradeWriter.WriteAsync(
                new TradePrint(InstrumentId.None, ts, ts, p.Price, p.Size, p.Aggressor, source, seq++, EventTimeApproximate: false), ct)
                .ConfigureAwait(false);
        }
    }

    private void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete temp file {Path}", path); }
    }

    private static ReplayCoverage AnalyzeCoverage(
        IReadOnlyList<(CanonicalBacktestSelection Selection, IReadOnlyList<Bar> Bars)> histories)
    {
        var timestampSets = histories
            .Select(item => item.Bars.Select(bar => bar.TimestampUtc).ToHashSet())
            .ToArray();
        var union = timestampSets.SelectMany(set => set).ToHashSet();
        var common = timestampSets.Length == 0
            ? 0
            : timestampSets.Skip(1).Aggregate(
                new HashSet<DateTime>(timestampSets[0]),
                (current, next) => { current.IntersectWith(next); return current; }).Count;
        return new ReplayCoverage(
            common,
            union.Count,
            $"Coverage: {common:N0}/{union.Count:N0} UTC bar boundaries shared by every instrument.");
    }

    private sealed record CanonicalBacktestSelection(Instrument Instrument, SignalInstrument Choice);
    private sealed record ReplayCoverage(int CommonBoundaryCount, int UnionBoundaryCount, string Description);
}

/// <summary>A named lookback window for the Quick-backtest control (label + duration).</summary>
public sealed record LookbackOption(string Label, TimeSpan Duration)
{
    public override string ToString() => Label;
}
