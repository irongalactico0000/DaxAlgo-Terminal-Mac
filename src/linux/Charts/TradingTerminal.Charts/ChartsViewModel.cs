using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.UI;
using TradingTerminal.UI.Presets;
using static TradingTerminal.Core.MarketData.Indicators;

namespace TradingTerminal.Charts;

/// <summary>
/// Drives the TradingView-style Charts window. Mirrors <c>CorrelationMatrixViewModel</c>: pulls the
/// broker instrument universe + historical bars from <see cref="IMarketDataRepository"/>, computes
/// indicators in C# (so chart / backtest / live numbers agree), and streams the forming candle from
/// <see cref="IMarketDataHub"/>. The window renders everything via Lightweight Charts in a WebView2 —
/// this VM holds no view code, it just raises <see cref="SnapshotReady"/> / <see cref="CandleUpdated"/>.
/// </summary>
public sealed partial class ChartsViewModel : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<ChartsViewModel> _logger;

    /// <summary>Non-null when this view-model lives inside a strategy window rather than the
    /// standalone tool — see <see cref="ChartsEmbedOptions"/>.</summary>
    private readonly ChartsEmbedOptions? _embed;

    private IReadOnlyList<TradableInstrument> _allInstruments = Array.Empty<TradableInstrument>();
    private bool _chartReady;
    private CancellationTokenSource? _loadCts;
    private IDisposable? _liveSub;
    private IDisposable? _ingestHandle;
    private int _disposeState;

    // Retained for CSV export; refreshed on every successful reload.
    private IReadOnlyList<Bar> _lastBars = Array.Empty<Bar>();
    private ChartSnapshot? _lastSnapshot;

    /// <summary>Set when a live candle arrived while paused, so resume can catch up exactly
    /// (full reload) instead of splicing a stale <c>series.update</c>.</summary>
    private bool _pausedDirty;
    private bool _applyingPreset;

    private static readonly IReadOnlyList<ChartTimeframe> AllTimeframes = new[]
    {
        new ChartTimeframe("1m",  BarSize.OneMinute,      TimeSpan.FromDays(2)),
        new ChartTimeframe("5m",  BarSize.FiveMinutes,    TimeSpan.FromDays(5)),
        new ChartTimeframe("15m", BarSize.FifteenMinutes, TimeSpan.FromDays(15)),
        new ChartTimeframe("1h",  BarSize.OneHour,        TimeSpan.FromDays(60)),
        new ChartTimeframe("1D",  BarSize.OneDay,         TimeSpan.FromDays(365)),
    };

    public ChartsViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<ChartsViewModel> logger,
        ChartsEmbedOptions? embed = null)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _logger = logger;
        _embed = embed;

        Timeframes = new ObservableCollection<ChartTimeframe>(AllTimeframes);
        // Embedded (inside a strategy window): the host pins the instrument; the timeframe defaults to
        // the strategy warm-up granularity (1m) since the gated-off toolbar leaves nothing to change it
        // with. Nothing loads either way until the WebView reports ready.
        SelectedTimeframe =
            Timeframes.FirstOrDefault(t => t.BarSize == (embed?.BarSize ?? BarSize.OneHour))
            ?? Timeframes.First(t => t.BarSize == BarSize.OneMinute);
        Instruments = new ObservableCollection<TradableInstrument>();
        PresetNames = new ObservableCollection<string>(_presetStore.Names);
        LoadUserIndicators();

        if (embed is not null)
        {
            SelectedInstrument = embed.Instrument;
            return; // no picker ⇒ no broker-universe swap; the host owns the selection
        }
        _ = LoadInstrumentsAsync();
    }

    private void LoadUserIndicators()
    {
        var store = new FileUserChartIndicatorStore();
        UserIndicatorsPath = store.FilePath;
        UserIndicators.Clear();
        foreach (var definition in store.LoadOrSeed())
        {
            var toggle = new UserChartIndicatorToggle(definition);
            toggle.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(UserChartIndicatorToggle.IsEnabled))
                {
                    QueueReload();
                    NotifyResearchSpaceChanged();
                }
            };
            UserIndicators.Add(toggle);
        }
    }

    public ObservableCollection<ChartTimeframe> Timeframes { get; }
    public ObservableCollection<TradableInstrument> Instruments { get; }
    public ObservableCollection<string> PresetNames { get; }

    /// <summary>Series styles the JS side knows how to render (see index.html · setData).</summary>
    public IReadOnlyList<string> ChartTypes { get; } = new[] { "Candles", "Bars", "Line", "Area" };

    [ObservableProperty] private TradableInstrument? _selectedInstrument;
    [ObservableProperty] private ChartTimeframe? _selectedTimeframe;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _selectedChartType = "Candles";
    [ObservableProperty] private bool _showSma = true;
    [ObservableProperty] private bool _showEma = true;
    [ObservableProperty] private bool _showRsi;
    [ObservableProperty] private bool _showMacd;
    [ObservableProperty] private bool _showBollinger;
    [ObservableProperty] private bool _showStochastic;
    [ObservableProperty] private bool _showAtr;
    [ObservableProperty] private bool _showVwap;
    [ObservableProperty] private bool _showAdx;
    [ObservableProperty] private int _emaPeriod = 50;
    [ObservableProperty] private string _status = "Loading instruments…";
    [ObservableProperty] private string _userIndicatorsPath = string.Empty;

    public ObservableCollection<UserChartIndicatorToggle> UserIndicators { get; } = [];

    /// <summary>
    /// Applies host chat-catalog overlay ids (see <c>AuthoredChartChoiceCatalogV1</c>) onto the
    /// native chart toggles so famous indicators render immediately without waiting for authored
    /// visualizer codegen. Unknown ids are ignored; candles-only clears indicator overlays.
    /// By default existing toggles stay on (additive OR). Pass <paramref name="replaceExisting"/>
    /// when Research space handoff must match the kept indicator set exactly.
    /// <see cref="EmaPeriod"/> is only written when EMA was previously off (or replace mode) so
    /// casual previews do not retune an operator's period.
    /// </summary>
    public void ApplyHostOverlayIds(IEnumerable<string> overlayIds, bool replaceExisting = false)
    {
        var idList = overlayIds as IList<string> ?? overlayIds.ToList();
        var idSet = idList
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (idSet.Count == 0 || (idSet.Count == 1 && idSet.Contains("candles")))
        {
            ShowSma = false;
            ShowEma = false;
            ShowRsi = false;
            ShowMacd = false;
            ShowBollinger = false;
            ShowStochastic = false;
            ShowAtr = false;
            ShowVwap = false;
            ShowAdx = false;
            foreach (var user in UserIndicators)
                user.IsEnabled = false;
            NotifyResearchSpaceChanged();
            return;
        }

        var state = TradingTerminal.Core.Strategies.Generation.NativeChartOverlaySelectionV1
            .FromHostOverlayIds(idList);
        var emaWasOff = !ShowEma;
        if (replaceExisting)
        {
            ShowSma = state.ShowSma;
            ShowEma = state.ShowEma;
            ShowRsi = state.ShowRsi;
            ShowMacd = state.ShowMacd;
            ShowBollinger = state.ShowBollinger;
            ShowStochastic = state.ShowStochastic;
            ShowAtr = state.ShowAtr;
            ShowVwap = state.ShowVwap;
            ShowAdx = state.ShowAdx;
            if (state.ShowEma && state.EmaPeriod > 0)
                EmaPeriod = state.EmaPeriod;
            foreach (var user in UserIndicators)
            {
                user.IsEnabled = idSet.Contains(user.Definition.Id) ||
                                 (user.Definition.Alias is { } alias && idSet.Contains(alias));
            }
        }
        else
        {
            ShowSma = ShowSma || state.ShowSma;
            ShowEma = ShowEma || state.ShowEma;
            ShowRsi = ShowRsi || state.ShowRsi;
            ShowMacd = ShowMacd || state.ShowMacd;
            ShowBollinger = ShowBollinger || state.ShowBollinger;
            ShowStochastic = ShowStochastic || state.ShowStochastic;
            ShowAtr = ShowAtr || state.ShowAtr;
            ShowVwap = ShowVwap || state.ShowVwap;
            ShowAdx = ShowAdx || state.ShowAdx;
            if (emaWasOff && state.ShowEma && state.EmaPeriod > 0)
                EmaPeriod = state.EmaPeriod;

            foreach (var user in UserIndicators)
            {
                if (idSet.Contains(user.Definition.Id) ||
                    (user.Definition.Alias is { } alias && idSet.Contains(alias)))
                {
                    user.IsEnabled = true;
                }
            }
        }

        NotifyResearchSpaceChanged();
    }

    /// <summary>
    /// Raised when instrument or indicator toggles change so Strategy Builder Research can mirror
    /// the live chart as one shared space (without waiting for Keep).
    /// </summary>
    public event EventHandler? ResearchSpaceChanged;

    private void NotifyResearchSpaceChanged()
    {
        NotifyResearchIndicatorSummaryChanged();
        ResearchSpaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Exact indicator settings currently enabled on this chart (research handoff R05).
    /// Periods are the values the chart actually computes with — not catalog bucket ids.
    /// </summary>
    public IReadOnlyList<ResearchIndicatorBindingV1> CaptureActiveIndicatorBindings()
    {
        var list = new List<ResearchIndicatorBindingV1>();
        if (ShowSma)
            list.Add(new("host.sma", "sma", 20));
        if (ShowEma)
            list.Add(new("host.ema", "ema", EmaPeriod <= 0 ? 50 : EmaPeriod));
        if (ShowRsi)
            list.Add(new("host.rsi", "rsi", 14));
        if (ShowMacd)
            list.Add(new("host.macd", "macd", 0));
        if (ShowBollinger)
            list.Add(new("host.bollinger", "bollinger", 20));
        if (ShowStochastic)
            list.Add(new("host.stochastic", "stochastic", 14));
        if (ShowAtr)
            list.Add(new("host.atr", "atr", 14));
        if (ShowVwap)
            list.Add(new("host.vwap", "vwap", 0));
        if (ShowAdx)
            list.Add(new("host.adx", "adx", 14));
        foreach (var user in UserIndicators.Where(static item => item.IsEnabled))
        {
            var def = user.Definition;
            list.Add(new(
                $"user.{def.Id}",
                def.Kind.ToString().ToLowerInvariant(),
                def.Period));
        }

        return list.Count == 0 ? Array.Empty<ResearchIndicatorBindingV1>() : list.AsReadOnly();
    }

    /// <summary>
    /// Snapshot of host overlay ids currently enabled on this chart (research handoff).
    /// Includes enabled user-indicator catalog ids. Prefer
    /// <see cref="CaptureActiveIndicatorBindings"/> for exact periods.
    /// </summary>
    public IReadOnlyList<string> CaptureActiveOverlayIds()
    {
        var bindings = CaptureActiveIndicatorBindings();
        if (bindings.Count == 0)
            return Array.AsReadOnly(new[] { "candles" });

        var ids = new List<string>();
        foreach (var b in bindings)
        {
            if (b.BindingId.StartsWith("user.", StringComparison.Ordinal))
            {
                ids.Add(b.BindingId["user.".Length..]);
                continue;
            }

            ids.Add(b.Kind switch
            {
                "sma" => "sma-20",
                "ema" => b.Period <= 20 ? "ema-20" : b.Period == 50 ? "ema-50" : $"ema-{b.Period}",
                "rsi" => "rsi-14",
                "macd" => "macd-12-26-9",
                "bollinger" => "bollinger-20",
                "stochastic" => "stochastic-14-3-3",
                "atr" => "atr-14",
                "vwap" => "vwap",
                "adx" => "adx-14",
                _ => b.BindingId,
            });
        }

        return ids.AsReadOnly();
    }

    private string? _pendingHostPreferredSymbol;
    private BarSize? _pendingHostBarSize;
    private ChartTimeRange? _pendingHostObservation;
    private ChartTimeRange? _pendingHostOutcome;

    /// <summary>Selects an instrument by symbol when the host research gallery hands off a match.</summary>
    public void ApplyHostPreferredSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        _pendingHostPreferredSymbol = symbol.Trim();
        TryApplyPendingHostInstrument();
    }

    /// <summary>
    /// Host similar-history A→B: select symbol/TF and load an explicit From–To window (no orders).
    /// </summary>
    public void ApplyHostHistoryWindow(
        string? symbol,
        BarSize timeframe,
        DateTime fromUtc,
        DateTime toUtc)
    {
        _pendingHostPreferredSymbol = string.IsNullOrWhiteSpace(symbol) ? _pendingHostPreferredSymbol : symbol.Trim();
        _pendingHostBarSize = timeframe;
        HistoryFromText = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        HistoryToText = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        TryApplyPendingHostInstrument();
        TryApplyPendingHostTimeframe();
        if (CanLoadExplicitHistory)
            LoadExplicitHistoryCommand.Execute(null);
        else
            QueueReload();
    }

    /// <summary>
    /// Host gallery handoff: switch symbol/timeframe and pre-fill observation→outcome ranges for capture.
    /// </summary>
    public void ApplyHostResearchCapture(
        string? symbol,
        BarSize timeframe,
        DateTime observationFromUtc,
        DateTime observationToUtcExclusive,
        DateTime outcomeFromUtc,
        DateTime outcomeToUtcExclusive)
    {
        _pendingHostPreferredSymbol = string.IsNullOrWhiteSpace(symbol) ? _pendingHostPreferredSymbol : symbol.Trim();
        _pendingHostBarSize = timeframe;
        _pendingHostObservation = new ChartTimeRange(
            new DateTimeOffset(DateTime.SpecifyKind(observationFromUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(observationToUtcExclusive, DateTimeKind.Utc)));
        _pendingHostOutcome = new ChartTimeRange(
            new DateTimeOffset(DateTime.SpecifyKind(outcomeFromUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(outcomeToUtcExclusive, DateTimeKind.Utc)));
        TryApplyPendingHostInstrument();
        TryApplyPendingHostTimeframe();
        // Ranges apply after history loads so the brush overlays the correct bars.
    }

    private void TryApplyPendingHostInstrument()
    {
        if (_pendingHostPreferredSymbol is null || _allInstruments.Count == 0)
            return;

        var symbol = _pendingHostPreferredSymbol;
        var match = _allInstruments.FirstOrDefault(item =>
            string.Equals(item.Contract.Symbol, symbol, StringComparison.OrdinalIgnoreCase) ||
            item.DisplayName.Contains(symbol, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            // Keep pending until catalogue refresh — do not fall through to BTCUSD/default.
            Status = $"Waiting for instrument '{symbol}' in the chart catalogue…";
            return;
        }

        // Clear only after a successful match so LoadInstrumentsAsync cannot overwrite
        // Research Studio's ranked-row selection with a remembered default.
        _pendingHostPreferredSymbol = null;
        if (!string.Equals(
                SelectedInstrument?.Contract.Symbol,
                match.Contract.Symbol,
                StringComparison.OrdinalIgnoreCase))
        {
            SelectedInstrument = match;
        }

        InstrumentSearchText = match.DisplayName;
    }

    /// <summary>Host/research preferred symbol still awaiting catalogue match, if any.</summary>
    public string? PendingHostPreferredSymbol => _pendingHostPreferredSymbol;

    private void TryApplyPendingHostTimeframe()
    {
        if (_pendingHostBarSize is not { } size)
            return;
        var tf = Timeframes.FirstOrDefault(item => item.BarSize == size);
        if (tf is null)
            return;
        _pendingHostBarSize = null;
        SelectedTimeframe = tf;
    }

    private void TryApplyPendingHostResearchRanges()
    {
        if (_pendingHostObservation is null || _pendingHostOutcome is null || !HasData)
            return;

        ResearchObservationRange = _pendingHostObservation;
        ResearchOutcomeRange = _pendingHostOutcome;
        ResearchSelectionStep = ChartResearchSelectionStep.None;
        _pendingHostObservation = null;
        _pendingHostOutcome = null;
        Status =
            $"Host gallery capture loaded for {SelectedInstrument?.Contract.Symbol}. Review observation→outcome, then Send to Builder.";
        NotifyResearchSelectionStateChanged();
    }

    /// <summary>Display pause: live candle pushes stop; the hub subscription keeps running so
    /// resume is instant (a dirty flag triggers one exact catch-up reload).</summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>True once the current load produced at least one bar — drives the CSV button.</summary>
    [ObservableProperty] private bool _hasData;

    // ── Presets (named chart setups; unlike other tools these include symbol + interval) ────────
    /// <summary>Editable preset-picker text: type a name and Save, or pick an existing preset to apply.</summary>
    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string? _selectedPreset;

    /// <summary>Raised after a history load with the full chart payload (candles + volume + indicators).</summary>
    public event EventHandler<ChartSnapshot>? SnapshotReady;

    /// <summary>Raised on each live forming/closed candle for the active instrument.</summary>
    public event EventHandler<ChartCandle>? CandleUpdated;

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "tool.charts";

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(TradableInstrument? value)
    {
        if (value is not null &&
            !string.Equals(InstrumentSearchText, value.DisplayName, StringComparison.Ordinal))
        {
            InstrumentSearchText = value.DisplayName;
        }

        ResetResearchSelection();
        ClearExplicitHistoryWindow();
        DraftSentToBuilder = false;
        HistoricalBacktestRoomOpened = false;
        DraftPlacementMode = ChartInteractionMode.Pan;
        NotifyStrategyDraftStateChanged();
        NotifyResearchShellStateChanged();
        QueueReload();
        NotifyResearchSpaceChanged();
    }

    partial void OnSelectedTimeframeChanged(ChartTimeframe? value)
    {
        ResetResearchSelection();
        ClearExplicitHistoryWindow();
        DraftSentToBuilder = false;
        HistoricalBacktestRoomOpened = false;
        DraftPlacementMode = ChartInteractionMode.Pan;
        NotifyStrategyDraftStateChanged();
        NotifyResearchShellStateChanged();
        QueueReload();
    }
    partial void OnSelectedChartTypeChanged(string value) => QueueReload();
    partial void OnShowSmaChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowEmaChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowRsiChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowMacdChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowBollingerChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowStochasticChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowAtrChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowVwapChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnShowAdxChanged(bool value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }
    partial void OnEmaPeriodChanged(int value)
    {
        QueueReload();
        NotifyResearchSpaceChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (value)
        {
            Status = $"⏸ Paused — live updates buffer in the background ({SelectedInstrument?.DisplayName}).";
            return;
        }
        Status = $"Resumed — {SelectedInstrument?.DisplayName}.";
        if (_pausedDirty)
        {
            _pausedDirty = false;
            QueueReload();
        }
    }

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null) return;
        PresetName = value;
        if (_presetStore.Get(value) is { } preset) ApplyPreset(preset);
    }

    /// <summary>Called by the window once the WebView2 page has loaded and can receive data.</summary>
    public Task NotifyChartReadyAsync()
    {
        if (Volatile.Read(ref _disposeState) != 0) return Task.CompletedTask;
        _chartReady = true;
        return ReloadAsync();
    }

    private void QueueReload()
    {
        if (Volatile.Read(ref _disposeState) == 0 && _chartReady && !_applyingPreset)
            _ = ReloadAsync();
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0)
            {
                Status = "No instruments — connect a broker first.";
                return;
            }
            _allInstruments = list;
            TryApplyPendingHostInstrument();
            SelectedInstrument ??=
                InstrumentPickerFilter.Remembered(InstrumentPersistKey, _allInstruments, i => i.Contract.Symbol)
                ?? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                ?? _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "AAPL")
                ?? _allInstruments.FirstOrDefault();
            ApplyFilter();
            Status = $"{_allInstruments.Count} instruments.";
            TryApplyPendingHostTimeframe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Charts: instrument load failed");
            Status = $"Instrument load failed: {ex.Message}";
        }
    }

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="_allInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(_allInstruments, InstrumentSearchText, SelectedInstrument,
            MaxInstrumentsDisplayed, i => i.DisplayName));

    private async Task ReloadAsync()
    {
        if (Volatile.Read(ref _disposeState) != 0) return;

        var instrument = SelectedInstrument;
        var tf = SelectedTimeframe;
        if (instrument is null || tf is null) return;

        // Publish the next source atomically so each reload/dispose caller owns exactly one source.
        // Capture the token before publication: another reload can cancel and dispose the source as
        // soon as it becomes current, but an already-captured token remains safe to observe.
        var nextCts = new CancellationTokenSource();
        var ct = nextCts.Token;
        var previousCts = Interlocked.Exchange(ref _loadCts, nextCts);
        try { previousCts?.Cancel(); }
        finally { previousCts?.Dispose(); }

        // Dispose may have won between the entry check and the exchange above. Withdraw the source
        // only if it is still ours; otherwise the reload/dispose that replaced it owns cleanup.
        if (Volatile.Read(ref _disposeState) != 0)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCts, null, nextCts), nextCts))
            {
                nextCts.Cancel();
                nextCts.Dispose();
            }
            return;
        }

        StopLive();

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }

        Status = HasExplicitHistoryRange
            ? $"Loading {instrument.DisplayName} ({tf.Label}) {_explicitHistoryFromUtc:u} → {_explicitHistoryToUtc:u}…"
            : $"Loading {instrument.DisplayName} ({tf.Label})…";
        try
        {
            IReadOnlyList<Bar> bars;
            if (HasExplicitHistoryRange &&
                _explicitHistoryFromUtc is { } from &&
                _explicitHistoryToUtc is { } to)
            {
                bars = await _repository.GetHistoricalBarsAsync(
                           instrument.Contract,
                           broker,
                           tf.BarSize,
                           from.UtcDateTime,
                           to.UtcDateTime,
                           ct)
                       ?? Array.Empty<Bar>();
            }
            else
            {
                var lookback = ResolveHistoryLookback(tf);
                bars = await _repository.GetHistoricalBarsAsync(
                           instrument.Contract, broker, tf.BarSize, lookback, ct)
                       ?? Array.Empty<Bar>();
            }

            var candles = new ChartCandle[bars.Count];
            var volume = new ChartVolume[bars.Count];
            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                var t = ToEpoch(b.TimestampUtc);
                candles[i] = new ChartCandle(t, b.Open, b.High, b.Low, b.Close);
                volume[i] = new ChartVolume(t, b.Volume, b.Close >= b.Open ? "#26a69a80" : "#ef535080");
            }

            var bollinger = ShowBollinger ? Bollinger(bars, 20, 2d) : null;
            (ChartLinePoint[] K, ChartLinePoint[] D)? stochastic = ShowStochastic
                ? Stochastic(bars, 14, 3)
                : null;
            var custom = BuildCustomSeries(bars);
            var snapshot = new ChartSnapshot(
                Symbol: instrument.DisplayName,
                Timeframe: tf.Label,
                ChartType: SelectedChartType,
                Candles: candles,
                Volume: volume,
                Sma: ShowSma ? Sma(bars, 20) : null,
                Ema: ShowEma ? Ema(bars, EmaPeriod <= 0 ? 50 : EmaPeriod) : null,
                Rsi: ShowRsi ? Rsi(bars, 14) : null,
                Macd: ShowMacd ? Macd(bars, 12, 26, 9) : null,
                BollingerMid: bollinger?.Mid,
                BollingerUpper: bollinger?.Upper,
                BollingerLower: bollinger?.Lower,
                StochasticK: stochastic?.K,
                StochasticD: stochastic?.D,
                Atr: ShowAtr ? Atr(bars, 14) : null,
                Vwap: ShowVwap ? Vwap(bars) : null,
                Adx: ShowAdx ? Adx(bars, 14) : null,
                CustomSeries: custom);

            if (ct.IsCancellationRequested) return;
            _lastBars = bars;
            _lastSnapshot = snapshot;
            HasData = bars.Count > 0;
            SnapshotReady?.Invoke(this, snapshot);
            TryApplyPendingHostResearchRanges();
            Status = bars.Count == 0
                ? $"No history for {instrument.DisplayName} — is the broker connected and streaming?"
                : HasExplicitHistoryRange
                    ? $"{instrument.DisplayName} · {tf.Label} · {bars.Count} bars · explicit range"
                    : $"{instrument.DisplayName} · {tf.Label} · {bars.Count} bars";

            NotifyResearchShellStateChanged();
            StartLive(instrument, broker, tf.BarSize);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Charts: load failed for {Symbol}", instrument.Contract.Symbol);
            Status = $"Load failed: {ex.Message}";
        }
    }

    private void StartLive(TradableInstrument instrument, BrokerKind broker, BarSize size)
    {
        try
        {
            var id = _ingest.Resolve(instrument.Contract, broker);
            _ingestHandle = _ingest.SubscribeBars(instrument.Contract, broker, size);
            _liveSub = _hub.Bars(id, size).Subscribe(bar =>
                _ = UiThread.RunAsync(() =>
                {
                    if (IsPaused) { _pausedDirty = true; return; }
                    CandleUpdated?.Invoke(this,
                        new ChartCandle(ToEpoch(bar.OpenTimeUtc), bar.Open, bar.High, bar.Low, bar.Close));
                }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Charts: live subscription failed (continuing with history only)");
        }
    }

    private void StopLive()
    {
        _liveSub?.Dispose(); _liveSub = null;
        _ingestHandle?.Dispose(); _ingestHandle = null;
    }

    private BrokerKind ResolveBroker(TradableInstrument instrument)
    {
        if (_selector.IsConnected(instrument.Broker)) return instrument.Broker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker first.");
        return connected[0];
    }

    private static long ToEpoch(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // ── Presets ──────────────────────────────────────────────────────────────────────────────────

    private readonly ToolPresetStore<ChartsPreset> _presetStore = new("charts");

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0) return;
        _presetStore.Save(name, new ChartsPreset(
            SelectedInstrument?.Contract.Symbol, SelectedTimeframe?.Label, SelectedChartType,
            ShowSma, ShowEma, ShowRsi, ShowMacd, ShowBollinger, EmaPeriod));
        RefreshPresetNames(selected: name);
        _logger.LogInformation("Charts: preset '{Name}' saved", name);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var name = SelectedPreset ?? PresetName.Trim();
        if (string.IsNullOrEmpty(name) || !_presetStore.Delete(name)) return;
        RefreshPresetNames(selected: null);
        _logger.LogInformation("Charts: preset '{Name}' deleted", name);
    }

    /// <summary>Applies a preset behind <see cref="_applyingPreset"/> so the individual property
    /// changes don't each fire a reload; one reload runs at the end.</summary>
    private void ApplyPreset(ChartsPreset preset)
    {
        _applyingPreset = true;
        try
        {
            if (preset.Symbol is { Length: > 0 } symbol &&
                _allInstruments.FirstOrDefault(i => i.Contract.Symbol == symbol) is { } match)
            {
                SelectedInstrument = match;
                ApplyFilter();   // keep the hide-until-search combo showing the new selection
            }
            if (preset.Timeframe is { Length: > 0 } label &&
                Timeframes.FirstOrDefault(t => t.Label == label) is { } tf)
                SelectedTimeframe = tf;
            if (preset.ChartType is { Length: > 0 } type && ChartTypes.Contains(type))
                SelectedChartType = type;
            ShowSma = preset.ShowSma;
            ShowEma = preset.ShowEma;
            ShowRsi = preset.ShowRsi;
            ShowMacd = preset.ShowMacd;
            ShowBollinger = preset.ShowBollinger;
            if (preset.EmaPeriod > 0)
                EmaPeriod = preset.EmaPeriod;
        }
        finally
        {
            _applyingPreset = false;
        }
        QueueReload();
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG stays view-side) ──────────────────

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var bars = _lastBars;
        var snap = _lastSnapshot;
        if (bars.Count == 0 || snap is null) return;

        var sma = ToMap(snap.Sma);
        var ema = ToMap(snap.Ema);
        var rsi = ToMap(snap.Rsi);
        var macd = snap.Macd?.ToDictionary(m => m.Time);

        var sb = new StringBuilder();
        sb.Append("time_utc,open,high,low,close,volume");
        if (sma is not null) sb.Append(",sma20");
        if (ema is not null) sb.Append(",ema50");
        if (rsi is not null) sb.Append(",rsi14");
        if (macd is not null) sb.Append(",macd,macd_signal,macd_hist");
        sb.AppendLine();

        foreach (var b in bars)
        {
            var t = ToEpoch(b.TimestampUtc);
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{b.TimestampUtc:O},{b.Open},{b.High},{b.Low},{b.Close},{b.Volume}"));
            if (sma is not null) AppendOptional(sb, sma, t);
            if (ema is not null) AppendOptional(sb, ema, t);
            if (rsi is not null) AppendOptional(sb, rsi, t);
            if (macd is not null)
                sb.Append(macd.TryGetValue(t, out var m)
                    ? string.Create(CultureInfo.InvariantCulture, $",{m.Macd},{m.Signal},{m.Hist}")
                    : ",,,");
            sb.AppendLine();
        }

        try
        {
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"chart-{SymbolToken()}-{snap.Timeframe}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, sb.ToString());
            Status = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Charts: CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }

        static Dictionary<long, double>? ToMap(ChartLinePoint[]? pts) =>
            pts?.ToDictionary(p => p.Time, p => p.Value);

        static void AppendOptional(StringBuilder sb, Dictionary<long, double> map, long t) =>
            sb.Append(map.TryGetValue(t, out var v)
                ? string.Create(CultureInfo.InvariantCulture, $",{v}")
                : ",");
    }

    private string SymbolToken() =>
        (SelectedInstrument?.Contract.Symbol ?? "chart").Replace('/', '-').Replace(':', '-');

    // ── Indicators (computed in C# over closes; reuse Core primitives) ──────────────────────────

    private static ChartLinePoint[] Sma(IReadOnlyList<Bar> bars, int period)
    {
        var ind = new SimpleMovingAverage(period);
        var pts = new List<ChartLinePoint>(bars.Count);
        foreach (var b in bars) { ind.Push(b.Close); if (ind.IsReady) pts.Add(new ChartLinePoint(ToEpoch(b.TimestampUtc), Round(ind.Value))); }
        return pts.ToArray();
    }

    private static ChartLinePoint[] Ema(IReadOnlyList<Bar> bars, int period)
    {
        var ind = new ExponentialMovingAverage(period);
        var pts = new List<ChartLinePoint>(bars.Count);
        foreach (var b in bars) { ind.Push(b.Close); if (ind.IsReady) pts.Add(new ChartLinePoint(ToEpoch(b.TimestampUtc), Round(ind.Value))); }
        return pts.ToArray();
    }

    private static ChartLinePoint[] Rsi(IReadOnlyList<Bar> bars, int period)
    {
        var ind = new RelativeStrengthIndex(period);
        var pts = new List<ChartLinePoint>(bars.Count);
        foreach (var b in bars) { ind.Push(b.Close); if (ind.IsReady) pts.Add(new ChartLinePoint(ToEpoch(b.TimestampUtc), Round(ind.Value))); }
        return pts.ToArray();
    }

    private static MacdPoint[] Macd(IReadOnlyList<Bar> bars, int fast, int slow, int signal)
    {
        var emaFast = new ExponentialMovingAverage(fast);
        var emaSlow = new ExponentialMovingAverage(slow);
        var emaSig = new ExponentialMovingAverage(signal);
        var pts = new List<MacdPoint>(bars.Count);
        foreach (var b in bars)
        {
            emaFast.Push(b.Close);
            emaSlow.Push(b.Close);
            if (!emaSlow.IsReady) continue;
            var macd = emaFast.Value - emaSlow.Value;
            emaSig.Push(macd);
            var sig = emaSig.Value;
            pts.Add(new MacdPoint(ToEpoch(b.TimestampUtc), Round(macd), Round(sig), Round(macd - sig)));
        }
        return pts.ToArray();
    }

    private static BollingerBands Bollinger(IReadOnlyList<Bar> bars, int period, double stdDev)
    {
        var mid = new List<ChartLinePoint>(bars.Count);
        var upper = new List<ChartLinePoint>(bars.Count);
        var lower = new List<ChartLinePoint>(bars.Count);
        var window = new Queue<double>(period);
        double sum = 0d;
        double sumSq = 0d;
        foreach (var bar in bars)
        {
            window.Enqueue(bar.Close);
            sum += bar.Close;
            sumSq += bar.Close * bar.Close;
            if (window.Count > period)
            {
                var removed = window.Dequeue();
                sum -= removed;
                sumSq -= removed * removed;
            }

            if (window.Count < period)
                continue;

            var mean = sum / period;
            var variance = Math.Max(0d, (sumSq / period) - (mean * mean));
            var band = Math.Sqrt(variance) * stdDev;
            var t = ToEpoch(bar.TimestampUtc);
            mid.Add(new ChartLinePoint(t, Round(mean)));
            upper.Add(new ChartLinePoint(t, Round(mean + band)));
            lower.Add(new ChartLinePoint(t, Round(mean - band)));
        }

        return new BollingerBands(mid.ToArray(), upper.ToArray(), lower.ToArray());
    }

    private IReadOnlyList<ChartNamedSeries> BuildCustomSeries(IReadOnlyList<Bar> bars)
    {
        var series = new List<ChartNamedSeries>();
        foreach (var toggle in UserIndicators.Where(static item => item.IsEnabled))
        {
            var def = toggle.Definition;
            ChartLinePoint[] points = def.Kind switch
            {
                TradingTerminal.Core.Strategies.Generation.UserChartIndicatorKindV1.Sma => Sma(bars, def.Period),
                TradingTerminal.Core.Strategies.Generation.UserChartIndicatorKindV1.Ema => Ema(bars, def.Period),
                TradingTerminal.Core.Strategies.Generation.UserChartIndicatorKindV1.Rsi => Rsi(bars, def.Period),
                TradingTerminal.Core.Strategies.Generation.UserChartIndicatorKindV1.Atr => Atr(bars, def.Period),
                _ => Array.Empty<ChartLinePoint>(),
            };
            if (points.Length == 0) continue;
            var oscillator = def.Kind is TradingTerminal.Core.Strategies.Generation.UserChartIndicatorKindV1.Rsi
                or TradingTerminal.Core.Strategies.Generation.UserChartIndicatorKindV1.Atr;
            series.Add(new ChartNamedSeries(def.Id, def.DisplayName, oscillator, points));
        }

        return series;
    }

    private static (ChartLinePoint[] K, ChartLinePoint[] D) Stochastic(IReadOnlyList<Bar> bars, int period, int smooth)
    {
        var rawK = new List<ChartLinePoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (i + 1 < period) continue;
            var slice = bars.Skip(i + 1 - period).Take(period).ToArray();
            var high = slice.Max(static bar => bar.High);
            var low = slice.Min(static bar => bar.Low);
            var range = high - low;
            var k = range <= 1e-12 ? 50d : 100d * (bars[i].Close - low) / range;
            rawK.Add(new ChartLinePoint(ToEpoch(bars[i].TimestampUtc), Round(k)));
        }

        var d = Smooth(rawK, smooth);
        return (rawK.ToArray(), d);
    }

    private static ChartLinePoint[] Smooth(IReadOnlyList<ChartLinePoint> source, int period)
    {
        var ind = new SimpleMovingAverage(period);
        var pts = new List<ChartLinePoint>(source.Count);
        foreach (var point in source)
        {
            ind.Push(point.Value);
            if (ind.IsReady)
                pts.Add(new ChartLinePoint(point.Time, Round(ind.Value)));
        }

        return pts.ToArray();
    }

    private static ChartLinePoint[] Atr(IReadOnlyList<Bar> bars, int period)
    {
        var pts = new List<ChartLinePoint>(bars.Count);
        double atr = 0;
        for (var i = 1; i < bars.Count; i++)
        {
            var tr = Math.Max(
                bars[i].High - bars[i].Low,
                Math.Max(
                    Math.Abs(bars[i].High - bars[i - 1].Close),
                    Math.Abs(bars[i].Low - bars[i - 1].Close)));
            if (i < period)
            {
                atr += tr;
                if (i == period - 1)
                {
                    atr /= period;
                    pts.Add(new ChartLinePoint(ToEpoch(bars[i].TimestampUtc), Round(atr)));
                }

                continue;
            }

            atr = ((atr * (period - 1)) + tr) / period;
            pts.Add(new ChartLinePoint(ToEpoch(bars[i].TimestampUtc), Round(atr)));
        }

        return pts.ToArray();
    }

    private static ChartLinePoint[] Vwap(IReadOnlyList<Bar> bars)
    {
        var pts = new List<ChartLinePoint>(bars.Count);
        double cumPv = 0;
        double cumVol = 0;
        DateTime? sessionDay = null;
        foreach (var bar in bars)
        {
            var day = bar.TimestampUtc.Date;
            if (sessionDay is null || day != sessionDay)
            {
                sessionDay = day;
                cumPv = 0;
                cumVol = 0;
            }

            var typical = (bar.High + bar.Low + bar.Close) / 3d;
            var volume = Math.Max(0, bar.Volume);
            cumPv += typical * volume;
            cumVol += volume;
            if (cumVol <= 0) continue;
            pts.Add(new ChartLinePoint(ToEpoch(bar.TimestampUtc), Round(cumPv / cumVol)));
        }

        return pts.ToArray();
    }

    private static ChartLinePoint[] Adx(IReadOnlyList<Bar> bars, int period)
    {
        var pts = new List<ChartLinePoint>(bars.Count);
        if (bars.Count < period + 2) return pts.ToArray();

        double prevTr = 0, prevPlusDm = 0, prevMinusDm = 0, prevAdx = 0;
        for (var i = 1; i < bars.Count; i++)
        {
            var upMove = bars[i].High - bars[i - 1].High;
            var downMove = bars[i - 1].Low - bars[i].Low;
            var plusDm = upMove > downMove && upMove > 0 ? upMove : 0;
            var minusDm = downMove > upMove && downMove > 0 ? downMove : 0;
            var tr = Math.Max(
                bars[i].High - bars[i].Low,
                Math.Max(
                    Math.Abs(bars[i].High - bars[i - 1].Close),
                    Math.Abs(bars[i].Low - bars[i - 1].Close)));

            if (i < period)
            {
                prevTr += tr;
                prevPlusDm += plusDm;
                prevMinusDm += minusDm;
                continue;
            }

            if (i == period)
            {
                prevTr += tr;
                prevPlusDm += plusDm;
                prevMinusDm += minusDm;
            }
            else
            {
                prevTr = prevTr - (prevTr / period) + tr;
                prevPlusDm = prevPlusDm - (prevPlusDm / period) + plusDm;
                prevMinusDm = prevMinusDm - (prevMinusDm / period) + minusDm;
            }

            if (prevTr <= 1e-12) continue;
            var plusDi = 100d * prevPlusDm / prevTr;
            var minusDi = 100d * prevMinusDm / prevTr;
            var diSum = plusDi + minusDi;
            var dx = diSum <= 1e-12 ? 0 : 100d * Math.Abs(plusDi - minusDi) / diSum;
            if (i == period * 2 - 1)
                prevAdx = dx;
            else if (i > period * 2 - 1)
                prevAdx = ((prevAdx * (period - 1)) + dx) / period;
            else
                continue;

            pts.Add(new ChartLinePoint(ToEpoch(bars[i].TimestampUtc), Round(prevAdx)));
        }

        return pts.ToArray();
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

        // Remember the instrument the user was last charting so the window reopens on it — but never
        // from an embedded panel, whose instrument belongs to the strategy, not to the standalone tool.
        if (_embed is null)
            LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument?.Contract.Symbol);
        var loadCts = Interlocked.Exchange(ref _loadCts, null);
        loadCts?.Cancel();
        loadCts?.Dispose();
        StopLive();
    }
}

/// <summary>A selectable timeframe — label, the canonical <see cref="BarSize"/>, and how much history to pull.</summary>
public sealed record ChartTimeframe(string Label, BarSize BarSize, TimeSpan Lookback);

/// <summary>
/// How an embedding host — a composed strategy window — wants the <see cref="ChartsViewModel"/> born:
/// the pinned instrument (null = wait for the host to assign one) and the fixed timeframe (default 1m,
/// the strategy warm-up granularity). Passed as an <c>ActivatorUtilities</c> argument. The standalone
/// window resolves the view-model without this and keeps today's behaviour: persisted instrument,
/// broker-universe picker, 1h default.
/// </summary>
public sealed record ChartsEmbedOptions(TradableInstrument? Instrument = null, BarSize BarSize = BarSize.OneMinute);

/// <summary>A named snapshot of the Charts window's setup, persisted per user by
/// <see cref="ToolPresetStore{T}"/> (LocalAppData\DaxAlgo Terminal\tool-presets\charts.json).
/// Unlike the other tools, chart presets deliberately include symbol + interval — a preset here is
/// "my SPY hourly setup", not just view toggles. All fields are optional so older files apply.</summary>
public sealed record ChartsPreset(
    string? Symbol,
    string? Timeframe,
    string? ChartType,
    bool ShowSma,
    bool ShowEma,
    bool ShowRsi,
    bool ShowMacd,
    bool ShowBollinger = false,
    int EmaPeriod = 50);

// ── JSON bridge DTOs (camelCase via the window's serializer) → Lightweight Charts shapes ─────────
public sealed record ChartCandle(long Time, double Open, double High, double Low, double Close);
public sealed record ChartVolume(long Time, double Value, string Color);
public sealed record ChartLinePoint(long Time, double Value);
public sealed record MacdPoint(long Time, double Macd, double Signal, double Hist);
public sealed record BollingerBands(
    ChartLinePoint[] Mid,
    ChartLinePoint[] Upper,
    ChartLinePoint[] Lower);
public sealed record ChartSnapshot(
    string Symbol,
    string Timeframe,
    string ChartType,
    ChartCandle[] Candles,
    ChartVolume[] Volume,
    ChartLinePoint[]? Sma,
    ChartLinePoint[]? Ema,
    ChartLinePoint[]? Rsi,
    MacdPoint[]? Macd,
    ChartLinePoint[]? BollingerMid = null,
    ChartLinePoint[]? BollingerUpper = null,
    ChartLinePoint[]? BollingerLower = null,
    ChartLinePoint[]? StochasticK = null,
    ChartLinePoint[]? StochasticD = null,
    ChartLinePoint[]? Atr = null,
    ChartLinePoint[]? Vwap = null,
    ChartLinePoint[]? Adx = null,
    IReadOnlyList<ChartNamedSeries>? CustomSeries = null);

/// <summary>A named custom/user indicator series drawn on price or a 0–100 oscillator pane.</summary>
public sealed record ChartNamedSeries(
    string Id,
    string Label,
    bool OscillatorPane,
    ChartLinePoint[] Points);

/// <summary>One user-defined indicator checkbox row on the Charts options rail.</summary>
public sealed partial class UserChartIndicatorToggle : ObservableObject
{
    public UserChartIndicatorToggle(TradingTerminal.Core.Strategies.Generation.UserChartIndicatorDefinitionV1 definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public TradingTerminal.Core.Strategies.Generation.UserChartIndicatorDefinitionV1 Definition { get; }

    public string DisplayName => Definition.DisplayName;

    [ObservableProperty] private bool _isEnabled;
}
