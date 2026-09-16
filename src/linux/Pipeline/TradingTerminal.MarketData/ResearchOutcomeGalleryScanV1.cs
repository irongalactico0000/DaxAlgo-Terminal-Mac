using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Scans S&amp;P 100 (then wider registry) symbols against local bars for research outcome
/// events. Prefers daily history, then falls back to 1H / 15m so Simulated Mac installs still
/// get gallery hits. Research evidence only — not Paper.
/// </summary>
public sealed class ResearchOutcomeGalleryScanV1 : IResearchOutcomeGalleryScanV1
{
    /// <summary>
    /// Prefer daily (scan labels say “next day”), then fall back to coarser local history so
    /// Mac/Simulated installs that only keep 1H bars still produce gallery hits for capture.
    /// </summary>
    private static readonly BarSize[] PreferredBarSizes =
    [
        BarSize.OneDay,
        BarSize.OneHour,
        BarSize.FifteenMinutes,
    ];

    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _registry;
    private readonly IMarketDataRepository? _repository;
    private readonly IBrokerSelector? _selector;

    public ResearchOutcomeGalleryScanV1(IMarketDataStore store, IInstrumentRegistry registry)
        : this(store, registry, repository: null, selector: null)
    {
    }

    public ResearchOutcomeGalleryScanV1(
        IMarketDataStore store,
        IInstrumentRegistry registry,
        IMarketDataRepository? repository,
        IBrokerSelector? selector)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _repository = repository;
        _selector = selector;
    }

    public async Task<ResearchOutcomeGalleryResultV1> ScanAsync(
        ResearchOutcomeGalleryScanRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ResearchOutcomeEventFinderV1.TryDescribeScan(request.ScanId, out var displayName, out var prefix))
            throw new ArgumentException($"Unknown research scan id '{request.ScanId}'.", nameof(request));
        if (request.LookbackBars is < 30 or > 5_000)
            throw new ArgumentOutOfRangeException(nameof(request), "Lookback bars must be between 30 and 5,000.");
        if (request.MaxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum results must be between 1 and 50.");
        if (request.MaxRemoteHydrations is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum remote hydrations must be between 0 and 500.");

        var universe = Sp100Sp500Catalog.Sp100
            .Select(static item => item.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedUniverseSize = universe.Count;
        var names = Sp100Sp500Catalog.Sp100
            .ToDictionary(static item => item.Symbol, static item => item.Name, StringComparer.OrdinalIgnoreCase);

        var all = _registry.All()
            .Where(static instrument => !instrument.Id.IsNone)
            .OrderBy(static instrument => instrument.CanonicalSymbol, StringComparer.Ordinal)
            .ThenBy(static instrument => instrument.Id.Value)
            .ToArray();
        var sp100Eligible = all
            .Where(instrument => universe.Contains(instrument.CanonicalSymbol))
            .ToArray();
        // Scan S&P 100 first; if none have usable local bars, widen to the rest of the registry
        // (Simulated Mac installs often only keep AAPL/MSFT hourly history).
        var preferredPass = sp100Eligible.Length > 0;
        var eligible = preferredPass ? sp100Eligible : all;
        var widenedBeyondPreferred = !preferredPass;
        var matches = new List<ResearchOutcomeGalleryMatchV1>();
        var withHistory = 0;
        var hydrationAttempts = 0;
        var hydrationSuccesses = 0;
        var hydrationFailures = 0;
        var timeframeCounts = new Dictionary<BarSize, int>();
        var sourceCounts = new Dictionary<BrokerKind, int>();

        async Task ScanBatchAsync(IReadOnlyList<Instrument> batch)
        {
            foreach (var instrument in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (usable, sizeUsed, hydAttempt, hydOk, hydFail) = await LoadUsableBarsAsync(
                    instrument,
                    request,
                    cancellationToken).ConfigureAwait(false);
                hydrationAttempts += hydAttempt;
                hydrationSuccesses += hydOk;
                hydrationFailures += hydFail;

                if (usable.Count < ResearchOutcomeEventFinderV1.MinimumBars || sizeUsed is null)
                    continue;

                withHistory++;
                timeframeCounts[sizeUsed.Value] = timeframeCounts.GetValueOrDefault(sizeUsed.Value) + 1;
                var symbol = instrument.CanonicalSymbol;
                names.TryGetValue(symbol, out var companyName);
                companyName ??= symbol;
                var batchMatches = ResearchOutcomeEventFinderV1.FindEvents(
                    request.ScanId,
                    instrument.Id,
                    symbol,
                    companyName,
                    usable);
                foreach (var match in batchMatches)
                    sourceCounts[match.Source] = sourceCounts.GetValueOrDefault(match.Source) + 1;
                matches.AddRange(batchMatches);
            }
        }

        await ScanBatchAsync(eligible).ConfigureAwait(false);
        if (withHistory == 0 && sp100Eligible.Length > 0 && !ReferenceEquals(eligible, all))
        {
            eligible = all;
            widenedBeyondPreferred = true;
            await ScanBatchAsync(all.Where(instrument => !universe.Contains(instrument.CanonicalSymbol)).ToArray())
                .ConfigureAwait(false);
        }

        // Simulated series often calm near the tip — if we have history but no threshold events,
        // one deeper local pass (still capped) recovers older ±5% / breakout windows.
        var allowSimulatedRefresh = request.HydrateMissingHistory;
        if (matches.Count == 0 && withHistory > 0 && request.LookbackBars < 3_000)
        {
            request = request with { LookbackBars = 3_000, HydrateMissingHistory = false };
            matches.Clear();
            withHistory = 0;
            timeframeCounts.Clear();
            sourceCounts.Clear();
            await ScanBatchAsync(eligible).ConfigureAwait(false);
        }

        // DevSim local store is often a calm random walk that never hits ±5%. When the store
        // still yields zero events, force a fresh Simulated historical pull (synthetic series
        // plants demo jumps) so the research gallery is usable offline.
        if (matches.Count == 0 &&
            allowSimulatedRefresh &&
            _repository is not null &&
            _selector is not null)
        {
            matches.Clear();
            withHistory = 0;
            timeframeCounts.Clear();
            sourceCounts.Clear();
            var refreshBatch = eligible.Take(Math.Max(1, request.MaxRemoteHydrations)).ToArray();
            foreach (var instrument in refreshBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (usable, sizeUsed, hydAttempt, hydOk, hydFail) = await LoadUsableBarsAsync(
                    instrument,
                    request with { HydrateMissingHistory = true, PreferredSource = BrokerKind.Simulated },
                    cancellationToken,
                    preferFreshHistorical: true).ConfigureAwait(false);
                hydrationAttempts += hydAttempt;
                hydrationSuccesses += hydOk;
                hydrationFailures += hydFail;
                if (usable.Count < ResearchOutcomeEventFinderV1.MinimumBars || sizeUsed is null)
                    continue;

                withHistory++;
                timeframeCounts[sizeUsed.Value] = timeframeCounts.GetValueOrDefault(sizeUsed.Value) + 1;
                var symbol = instrument.CanonicalSymbol;
                names.TryGetValue(symbol, out var companyName);
                companyName ??= symbol;
                var refreshMatches = ResearchOutcomeEventFinderV1.FindEvents(
                    request.ScanId,
                    instrument.Id,
                    symbol,
                    companyName,
                    usable);
                foreach (var match in refreshMatches)
                    sourceCounts[match.Source] = sourceCounts.GetValueOrDefault(match.Source) + 1;
                matches.AddRange(refreshMatches);
            }
        }

        var ranked = matches
            .OrderByDescending(static match => Math.Abs(match.OutcomeReturn))
            .ThenByDescending(static match => match.OutcomeFromUtc)
            .ThenBy(static match => match.CanonicalSymbol, StringComparer.Ordinal)
            .Take(request.MaxResults)
            .ToArray();

        var hydration = hydrationAttempts == 0
            ? string.Empty
            : $" Missing local history triggered {hydrationAttempts} connected-broker request(s): " +
              $"{hydrationSuccesses} supplied enough bars and {hydrationFailures} did not.";
        var timeframes = timeframeCounts.Count == 0
            ? "no usable local bars"
            : string.Join(", ", timeframeCounts
                .OrderBy(static pair => Array.IndexOf(PreferredBarSizes, pair.Key))
                .Select(static pair => $"{pair.Value}×{pair.Key.ToDisplayString()}"));
        var sources = sourceCounts.Count == 0
            ? "none"
            : string.Join(", ", sourceCounts
                .OrderByDescending(static pair => pair.Value)
                .Select(static pair => $"{pair.Value}×{DescribeBrokerSource(pair.Key)}"));
        var coverageSummary =
            $"Requested universe: S&P 100 ({requestedUniverseSize} symbols). " +
            $"Registry had {sp100Eligible.Length} of those instruments available. " +
            (widenedBeyondPreferred
                ? $"Coverage widened beyond S&P 100 — scanned {eligible.Length} registry symbols; {withHistory} had usable history."
                : $"Scanned {eligible.Length} S&P 100-eligible instruments; {withHistory} had usable history.") +
            $" Returned {ranked.Length} event(s).";
        var dataProvenanceSummary =
            $"Event bar sources: {sources}. History used: {timeframes}." +
            (hydrationAttempts == 0
                ? " No connected-broker hydration was required for the returned events."
                : hydration);
        var explanation =
            $"{prefix}. {coverageSummary} {dataProvenanceSummary} " +
            "Gallery hits are research evidence for B/C/N labeling — not a trading signal and not Paper approval.";

        return new ResearchOutcomeGalleryResultV1(
            request.ScanId,
            displayName,
            ranked,
            eligible.Length,
            withHistory,
            explanation,
            hydrationAttempts,
            hydrationSuccesses,
            hydrationFailures,
            RequestedUniverseLabel: "S&P 100",
            RequestedUniverseSize: requestedUniverseSize,
            PreferredUniverseEligibleInRegistry: sp100Eligible.Length,
            WidenedBeyondPreferredUniverse: widenedBeyondPreferred,
            CoverageSummary: coverageSummary,
            DataProvenanceSummary: dataProvenanceSummary);
    }

    private static string DescribeBrokerSource(BrokerKind source) => source switch
    {
        BrokerKind.Simulated => "Simulated local history",
        _ => $"{source} bars",
    };

    private async Task<(IReadOnlyList<OhlcvBar> Bars, BarSize? Size, int HydAttempts, int HydOk, int HydFail)>
        LoadUsableBarsAsync(
            Instrument instrument,
            ResearchOutcomeGalleryScanRequestV1 request,
            CancellationToken cancellationToken,
            bool preferFreshHistorical = false)
    {
        var hydAttempts = 0;
        var hydOk = 0;
        var hydFail = 0;

        foreach (var size in PreferredBarSizes)
        {
            if (!preferFreshHistorical)
            {
                var recent = await _store.GetRecentBarsAsync(
                    instrument.Id,
                    size,
                    request.LookbackBars,
                    request.PreferredSource,
                    cancellationToken).ConfigureAwait(false);

                var fromStore = SelectOneProvenance(recent, size, request.PreferredSource, request.LookbackBars);
                if (fromStore.Count >= ResearchOutcomeEventFinderV1.MinimumBars)
                    return (fromStore, size, hydAttempts, hydOk, hydFail);
            }

            // Remote hydrate only for daily — that is the intended research contract.
            if (size != BarSize.OneDay ||
                !request.HydrateMissingHistory ||
                _repository is null ||
                _selector is null ||
                hydAttempts >= request.MaxRemoteHydrations ||
                !TryResolveHistoricalRoute(instrument, request.PreferredSource, out var broker, out var contract))
            {
                continue;
            }

            hydAttempts++;
            try
            {
                var duration = TimeSpan.FromDays(request.LookbackBars + 5);
                var fetched = await _repository.GetHistoricalBarsAsync(
                    contract,
                    broker,
                    BarSize.OneDay,
                    duration,
                    cancellationToken).ConfigureAwait(false);
                var canonical = fetched.Select(bar => OhlcvBar.FromBar(
                    bar,
                    instrument.Id,
                    BarSize.OneDay,
                    broker,
                    isFinal: true)).ToArray();
                var usable = SelectOneProvenance(canonical, BarSize.OneDay, broker, request.LookbackBars);
                if (usable.Count >= ResearchOutcomeEventFinderV1.MinimumBars)
                {
                    hydOk++;
                    return (usable, BarSize.OneDay, hydAttempts, hydOk, hydFail);
                }

                hydFail++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or
                NotSupportedException or
                TimeoutException or
                IOException or
                HttpRequestException)
            {
                hydFail++;
            }
        }

        return (Array.Empty<OhlcvBar>(), null, hydAttempts, hydOk, hydFail);
    }

    private static IReadOnlyList<OhlcvBar> SelectOneProvenance(
        IReadOnlyList<OhlcvBar> bars,
        BarSize size,
        BrokerKind? preferred,
        int take)
    {
        if (bars.Count == 0)
            return Array.Empty<OhlcvBar>();

        IEnumerable<OhlcvBar> ordered = bars
            .Where(bar => bar.Size == size)
            .OrderBy(static bar => bar.OpenTimeUtc);

        if (preferred is { } broker)
        {
            var filtered = ordered.Where(bar => bar.Source == broker).ToArray();
            if (filtered.Length > 0)
                ordered = filtered;
        }
        else
        {
            var bySource = ordered
                .GroupBy(static bar => bar.Source)
                .OrderByDescending(static group => group.Count())
                .FirstOrDefault();
            if (bySource is not null)
                ordered = bySource;
        }

        return ordered.TakeLast(take).ToArray();
    }

    private bool TryResolveHistoricalRoute(
        Instrument instrument,
        BrokerKind? preferredSource,
        out BrokerKind broker,
        out Contract contract)
    {
        broker = default;
        contract = null!;
        if (_selector is null) return false;

        var connected = preferredSource is { } preferred
            ? _selector.IsConnected(preferred) ? new[] { preferred } : []
            : _selector.Connected.OrderBy(static candidate => candidate).ToArray();
        foreach (var candidate in connected)
        {
            if (!_selector.IsAvailable(candidate) || !_selector.IsConnected(candidate)) continue;
            if (!_selector.Get(candidate).MarketDataCapabilities.SupportsHistoricalBars) continue;
            var symbol = _registry.ToBrokerSymbol(instrument.Id, candidate);
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            broker = candidate;
            contract = new Contract(
                symbol,
                SecTypeFor(instrument.AssetClass),
                instrument.Exchange,
                instrument.Currency,
                instrument.Exchange);
            return true;
        }

        return false;
    }

    private static string SecTypeFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Equity => "STK",
        AssetClass.Future => "FUT",
        AssetClass.Forex => "CASH",
        AssetClass.Crypto => "CRYPTO",
        AssetClass.Index => "IND",
        _ => "STK",
    };
}
