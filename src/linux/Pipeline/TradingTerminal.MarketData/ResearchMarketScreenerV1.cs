using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.Infrastructure.MarketData;

/// <summary>
/// Ranks registry instruments by trading volume or estimated traded value over a shared
/// completed-bar window at one required bar size. Optionally hydrates missing history from a
/// connected broker (Simulated/IB/…). Top N is never padded; mixed bar sizes are not ranked together.
/// </summary>
public sealed class ResearchMarketScreenerV1 : IResearchMarketScreenerV1
{
    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _registry;
    private readonly IMarketDataRepository? _repository;
    private readonly IBrokerSelector? _selector;

    public ResearchMarketScreenerV1(IMarketDataStore store, IInstrumentRegistry registry)
        : this(store, registry, repository: null, selector: null)
    {
    }

    public ResearchMarketScreenerV1(
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

    public async Task<ResearchMarketScreenResultV1> ScreenAsync(
        ResearchMarketScreenRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TopN is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "Top N must be between 1 and 100.");
        if (request.LookbackBars is < 2 or > 5_000)
            throw new ArgumentOutOfRangeException(nameof(request), "Lookback bars must be between 2 and 5,000.");
        if (request.MaxRemoteHydrations is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(request), "Max remote hydrations must be between 0 and 500.");

        EnsureUniverseRegistered(request.UniverseId);

        var requiredSize = request.RequiredBarSize;
        var barSpan = requiredSize.ToTimeSpan();
        var calculatedUtc = DateTime.UtcNow;
        var barSizeLabel = requiredSize.ToDisplayString();
        var metricDefinition = DescribeMetric(request.Metric, request.LookbackBars, barSizeLabel);
        var (universeId, universeLabel, requestedSize, candidates, notInRegistry) =
            ResolveUniverse(request.UniverseId);

        var exclusions = new List<ResearchMarketScreenExclusionV1>();
        foreach (var symbol in notInRegistry.OrderBy(static s => s, StringComparer.Ordinal))
        {
            exclusions.Add(new ResearchMarketScreenExclusionV1(
                symbol,
                "Not in local instrument registry (universe member unsupported here)."));
        }

        var hydrationAttempts = 0;
        var hydrationSuccesses = 0;
        var hydrationLimitReached = false;
        var loaded = new List<(Instrument Instrument, IReadOnlyList<OhlcvBar> Completed)>();

        foreach (var instrument in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bars = await _store.GetRecentBarsAsync(
                    instrument.Id,
                    requiredSize,
                    Math.Max(request.LookbackBars + 8, request.LookbackBars * 2),
                    source: null,
                    cancellationToken)
                .ConfigureAwait(false);

            var wallFloor = FloorToBarOpen(calculatedUtc, barSpan);
            var completed = SelectCompleted(bars, wallFloor, barSpan);

            if (completed.Length < request.LookbackBars &&
                request.HydrateMissingHistory &&
                _repository is not null &&
                _selector is not null)
            {
                if (hydrationAttempts >= request.MaxRemoteHydrations)
                {
                    hydrationLimitReached = true;
                }
                else if (TryResolveHistoricalRoute(instrument, out var broker, out var contract))
                {
                    hydrationAttempts++;
                    try
                    {
                        var duration = TimeSpan.FromTicks(
                            checked(barSpan.Ticks * (request.LookbackBars + 8)));
                        var fetched = await _repository.GetHistoricalBarsAsync(
                                contract,
                                broker,
                                requiredSize,
                                duration,
                                cancellationToken)
                            .ConfigureAwait(false);
                        var canonical = fetched.Select(bar => OhlcvBar.FromBar(
                                bar,
                                instrument.Id,
                                requiredSize,
                                broker,
                                isFinal: true))
                            .ToArray();
                        completed = SelectCompleted(canonical, wallFloor, barSpan);
                        if (completed.Length >= request.LookbackBars)
                            hydrationSuccesses++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is
                        InvalidOperationException or
                        NotSupportedException or
                        TimeoutException or
                        IOException or
                        HttpRequestException)
                    {
                        exclusions.Add(new ResearchMarketScreenExclusionV1(
                            instrument.CanonicalSymbol,
                            $"Hydration failed ({broker}): {ex.Message}"));
                        continue;
                    }
                }
            }

            if (bars.Count == 0 && completed.Length == 0)
            {
                exclusions.Add(new ResearchMarketScreenExclusionV1(
                    instrument.CanonicalSymbol,
                    $"No local {barSizeLabel} history."));
                continue;
            }

            if (completed.Length < request.LookbackBars)
            {
                exclusions.Add(new ResearchMarketScreenExclusionV1(
                    instrument.CanonicalSymbol,
                    $"Insufficient {barSizeLabel} history: need {request.LookbackBars} completed bars, had {completed.Length}."));
                continue;
            }

            loaded.Add((instrument, completed));
        }

        // Shared ranking clock: latest last-completed end among eligible instruments so the
        // ranking window is the most recent common period (stale-only instruments are excluded).
        var windowToExclusive = loaded.Count == 0
            ? FloorToBarOpen(calculatedUtc, barSpan)
            : loaded.Max(x => x.Completed[^1].OpenTimeUtc.Add(barSpan));
        var windowFrom = windowToExclusive - (barSpan * request.LookbackBars);

        var rows = new List<ResearchMarketScreenRowV1>(loaded.Count);
        foreach (var (instrument, completed) in loaded)
        {
            var instrumentEnd = completed[^1].OpenTimeUtc.Add(barSpan);
            if (instrumentEnd < windowToExclusive - barSpan)
            {
                exclusions.Add(new ResearchMarketScreenExclusionV1(
                    instrument.CanonicalSymbol,
                    $"History ends {instrumentEnd:u}, before shared ranking end {windowToExclusive:u}."));
                continue;
            }

            var window = completed
                .Where(b => b.OpenTimeUtc.Add(barSpan) <= windowToExclusive)
                .TakeLast(request.LookbackBars)
                .ToArray();

            if (window.Length < request.LookbackBars)
            {
                exclusions.Add(new ResearchMarketScreenExclusionV1(
                    instrument.CanonicalSymbol,
                    $"Could not align {request.LookbackBars} completed {barSizeLabel} bars to shared window ending {windowToExclusive:u} (aligned {window.Length})."));
                continue;
            }

            // Require the window to cover the shared lookback start (allow one-bar slack).
            if (window[0].OpenTimeUtc > windowFrom + barSpan)
            {
                exclusions.Add(new ResearchMarketScreenExclusionV1(
                    instrument.CanonicalSymbol,
                    $"Shared window starts {windowFrom:u}; instrument window starts later at {window[0].OpenTimeUtc:u}."));
                continue;
            }

            var volumeSum = window.Sum(static b => (double)b.Volume);
            var estimatedTradedValue = window.Sum(static b => (double)b.Volume * b.Close);
            var first = window[0];
            var last = window[^1];
            var pct = first.Close == 0
                ? (double?)null
                : ((double)last.Close - (double)first.Close) / (double)first.Close * 100.0;

            var (metricValue, metricLabel) = request.Metric switch
            {
                ResearchScreenMetricV1.TradingVolume => (volumeSum, "volume (shares/contracts)"),
                ResearchScreenMetricV1.TradedValue =>
                    (estimatedTradedValue, "estimated traded value (Σ volume × close)"),
                ResearchScreenMetricV1.PercentChange =>
                    (pct ?? double.NegativeInfinity, "percent change (close)"),
                _ => (volumeSum, "volume"),
            };

            if (double.IsNegativeInfinity(metricValue) || double.IsNaN(metricValue))
            {
                exclusions.Add(new ResearchMarketScreenExclusionV1(
                    instrument.CanonicalSymbol,
                    "Metric could not be computed (missing close or invalid values)."));
                continue;
            }

            rows.Add(new ResearchMarketScreenRowV1(
                Rank: 0,
                CanonicalSymbol: instrument.CanonicalSymbol,
                DisplayName: instrument.CanonicalSymbol,
                MetricValue: metricValue,
                MetricLabel: metricLabel,
                MetricDefinition: metricDefinition,
                BarsUsed: window.Length,
                BarSizeLabel: barSizeLabel,
                WindowFromUtc: window[0].OpenTimeUtc,
                WindowToUtcExclusive: window[^1].OpenTimeUtc.Add(barSpan),
                VolumeSum: volumeSum,
                TradedValueSum: estimatedTradedValue,
                PercentChange: pct));
        }

        var ranked = rows
            .OrderByDescending(static r => r.MetricValue)
            .ThenBy(static r => r.CanonicalSymbol, StringComparer.Ordinal)
            .Take(request.TopN)
            .Select((row, index) => row with { Rank = index + 1 })
            .ToArray();

        var withHistory = loaded.Count;
        var resultHeader =
            $"Top {request.TopN} by {ShortMetricName(request.Metric)} · " +
            $"last {request.LookbackBars} completed {barSizeLabel} bars · " +
            $"returned {ranked.Length} · {withHistory} with usable history of {requestedSize} requested";

        var hydrationNote = hydrationAttempts == 0
            ? ""
            : $" Hydrated {hydrationSuccesses}/{hydrationAttempts} remote request(s).";
        var limitNote = hydrationLimitReached
            ? $" Remote hydration capped at {request.MaxRemoteHydrations}."
            : "";

        var coverage =
            $"{resultHeader}.{hydrationNote}{limitNote} " +
            $"Universe: {universeLabel}. Available instruments matched {candidates.Count}; " +
            $"{notInRegistry.Count} universe symbols not registered. " +
            $"Shared window {windowFrom:u} → {windowToExclusive:u} ({barSizeLabel} only). " +
            $"Excluded {exclusions.Count}. Returned {ranked.Length} of Top {request.TopN} — not padded.";

        var provenance =
            $"Metric: {metricDefinition}. " +
            "All ranked instruments use the same required bar size and the same completed-bar window. " +
            "Σ(volume × close) is estimated traded value — exact traded value needs trade prints or a provider value field. " +
            "Missing or thin history is excluded with a reason (optional connected-broker hydration). Top N is not padded.";

        return new ResearchMarketScreenResultV1(
            universeId,
            universeLabel,
            requestedSize,
            candidates.Count,
            loaded.Count,
            notInRegistry.Count,
            hydrationAttempts,
            hydrationSuccesses,
            request.Metric,
            metricDefinition,
            request.TopN,
            calculatedUtc,
            barSizeLabel,
            request.LookbackBars,
            windowFrom,
            windowToExclusive,
            resultHeader,
            ranked,
            exclusions,
            coverage,
            provenance);
    }

    private void EnsureUniverseRegistered(string universeId)
    {
        if (!string.Equals(universeId, "sp100", StringComparison.OrdinalIgnoreCase))
            return;

        var broker = _selector?.Connected.FirstOrDefault() ?? BrokerKind.Simulated;
        foreach (var contract in Sp100Sp500Catalog.Sp100Contracts)
        {
            try { _registry.ResolveOrCreate(contract, broker); }
            catch
            {
                // Keep ranking resilient if a single symbol cannot register.
            }
        }
    }

    private (string Id, string Label, int Requested, IReadOnlyList<Instrument> Candidates, IReadOnlyList<string> NotInRegistry)
        ResolveUniverse(string universeId)
    {
        var all = _registry.All()
            .Where(static i => !i.Id.IsNone)
            .OrderBy(static i => i.CanonicalSymbol, StringComparer.Ordinal)
            .ToArray();

        if (string.Equals(universeId, "registry", StringComparison.OrdinalIgnoreCase))
            return ("registry", "Available instruments", all.Length, all, Array.Empty<string>());

        var sp100 = Sp100Sp500Catalog.Sp100
            .Select(static item => item.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bySymbol = all.ToDictionary(static i => i.CanonicalSymbol, StringComparer.OrdinalIgnoreCase);
        var eligible = new List<Instrument>();
        var missing = new List<string>();
        foreach (var symbol in sp100.OrderBy(static s => s, StringComparer.Ordinal))
        {
            if (bySymbol.TryGetValue(symbol, out var instrument))
                eligible.Add(instrument);
            else
                missing.Add(symbol);
        }

        return ("sp100", "S&P 100", sp100.Count, eligible, missing);
    }

    private bool TryResolveHistoricalRoute(
        Instrument instrument,
        out BrokerKind broker,
        out Contract contract)
    {
        broker = default;
        contract = null!;
        if (_selector is null) return false;

        foreach (var candidate in _selector.Connected.OrderBy(static c => c))
        {
            if (!_selector.IsAvailable(candidate) || !_selector.IsConnected(candidate)) continue;
            if (!_selector.Get(candidate).MarketDataCapabilities.SupportsHistoricalBars) continue;
            var symbol = _registry.ToBrokerSymbol(instrument.Id, candidate);
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = instrument.CanonicalSymbol;
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

    private static OhlcvBar[] SelectCompleted(
        IReadOnlyList<OhlcvBar> bars,
        DateTime wallFloor,
        TimeSpan barSpan) =>
        bars
            .Where(b => b.OpenTimeUtc.Add(barSpan) <= wallFloor)
            .OrderBy(static b => b.OpenTimeUtc)
            .ToArray();

    private static DateTime FloorToBarOpen(DateTime utc, TimeSpan barSpan)
    {
        if (barSpan <= TimeSpan.Zero)
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var ticks = utc.Ticks - (utc.Ticks % barSpan.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string SecTypeFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Equity => "STK",
        AssetClass.Future => "FUT",
        AssetClass.Forex => "CASH",
        AssetClass.Crypto => "CRYPTO",
        AssetClass.Option => "OPT",
        AssetClass.Index => "IND",
        _ => "UNKNOWN",
    };

    private static string ShortMetricName(ResearchScreenMetricV1 metric) =>
        metric switch
        {
            ResearchScreenMetricV1.TradingVolume => "trading volume",
            ResearchScreenMetricV1.TradedValue => "estimated traded value",
            ResearchScreenMetricV1.PercentChange => "percent change",
            _ => "metric",
        };

    private static string DescribeMetric(ResearchScreenMetricV1 metric, int lookbackBars, string barSizeLabel) =>
        metric switch
        {
            ResearchScreenMetricV1.TradingVolume =>
                $"Trading volume = Σ volume over the last {lookbackBars} completed {barSizeLabel} bars (shares/contracts).",
            ResearchScreenMetricV1.TradedValue =>
                $"Estimated traded value = Σ (volume × close) over the last {lookbackBars} completed {barSizeLabel} bars. Exact traded value requires transaction values.",
            ResearchScreenMetricV1.PercentChange =>
                $"Percent change = (last close − first close) / first close over the last {lookbackBars} completed {barSizeLabel} bars.",
            _ => "Undefined metric",
        };
}
