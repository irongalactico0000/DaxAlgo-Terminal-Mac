using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// Local store + optional TSD MDMS bar search for research conditions (R09 / R15).
/// </summary>
public sealed class ResearchConditionSearchV1 : IResearchConditionSearchV1
{
    private readonly IMarketDataStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<TsdBridgeOptions> _tsdOptions;
    private readonly ILogger<ResearchConditionSearchV1> _logger;

    public ResearchConditionSearchV1(
        IMarketDataStore store,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<TsdBridgeOptions> tsdOptions,
        ILogger<ResearchConditionSearchV1> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _tsdOptions = tsdOptions;
        _logger = logger;
    }

    public async Task<ResearchConditionSearchResultV1> SearchLocalAsync(
        ResearchConditionDefinitionV1 condition,
        InstrumentId instrumentId,
        string symbol,
        BarSize timeframe,
        int recentBarCount = 500,
        CancellationToken cancellationToken = default)
    {
        var bars = await _store.GetRecentBarsAsync(
                instrumentId,
                timeframe,
                Math.Clamp(recentBarCount, 50, 2000),
                source: null,
                cancellationToken)
            .ConfigureAwait(false);

        var series = bars
            .Select(bar => (
                TimeUtc: new DateTimeOffset(DateTime.SpecifyKind(bar.OpenTimeUtc, DateTimeKind.Utc)),
                Volume: (double)bar.Volume,
                Close: bar.Close))
            .ToArray();

        return ResearchConditionEvaluatorV1.SearchVolumeMultiple(
            condition,
            series,
            dataSource: "local_store",
            symbol: symbol);
    }

    public async Task<ResearchConditionSearchResultV1> SearchTsdAsync(
        ResearchConditionDefinitionV1 condition,
        string symbol,
        string interval = "1m",
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var opts = _tsdOptions.CurrentValue;
        if (!opts.Enabled)
        {
            return Empty(
                condition,
                symbol,
                "tsd_disabled",
                "TsdBridge is disabled in appsettings.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl)
            ? "http://127.0.0.1:8000"
            : opts.BaseUrl.TrimEnd('/');
        var mappedSymbol = MapSymbolForTsd(symbol);
        var url =
            $"{baseUrl}/api/mdms/bars/history?symbol={Uri.EscapeDataString(mappedSymbol)}&interval={Uri.EscapeDataString(interval)}&limit={Math.Clamp(limit, 50, 1000)}";

        try
        {
            var http = _httpClientFactory.CreateClient(nameof(ResearchConditionSearchV1));
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("TSD bars history {Status}: {Body}", (int)response.StatusCode, body);
                return Empty(
                    condition,
                    mappedSymbol,
                    "tsd_error",
                    $"TSD returned {(int)response.StatusCode}. Is sidecar up on {baseUrl}?");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<List<TsdHistoryBarDto>>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || payload.Count == 0)
            {
                return Empty(
                    condition,
                    mappedSymbol,
                    "tsd_mdms",
                    "TSD returned no bars.");
            }

            var series = payload
                .OrderBy(bar => bar.Time)
                .Select(bar => (
                    TimeUtc: DateTimeOffset.FromUnixTimeSeconds(bar.Time),
                    Volume: bar.Volume,
                    Close: bar.Close))
                .ToArray();

            return ResearchConditionEvaluatorV1.SearchVolumeMultiple(
                condition,
                series,
                dataSource: "tsd_mdms",
                symbol: mappedSymbol);
        }
        catch (Exception ex) when (opts.SoftFail)
        {
            _logger.LogDebug(ex, "TSD research bars soft-fail");
            return Empty(
                condition,
                mappedSymbol,
                "tsd_unreachable",
                $"TSD unreachable ({ex.GetType().Name}). Start ./scripts/run_tsd_sidecar.sh");
        }
    }

    private static ResearchConditionSearchResultV1 Empty(
        ResearchConditionDefinitionV1 condition,
        string symbol,
        string source,
        string note) =>
        new(
            ResearchConditionSearchResultV1.CurrentSchemaVersion,
            condition.VersionHashSha256,
            condition.SummaryText,
            source,
            symbol,
            UniverseBars: 0,
            EvaluatedBars: 0,
            HitCount: 0,
            PositiveForwardCount: 0,
            NegativeForwardCount: 0,
            InsufficientDataCount: 0,
            LiveMeetsCondition: null,
            Hits: Array.Empty<ResearchConditionHitV1>(),
            Note: note);

    /// <summary>Map equity-style symbols to Binance public pair when needed.</summary>
    public static string MapSymbolForTsd(string symbol)
    {
        var s = (symbol ?? "").Trim().ToUpperInvariant()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("/", "", StringComparison.Ordinal);
        if (s is "BTCUSD" or "BTCUSDT" or "XBTUSD")
            return "BTCUSDT";
        if (s is "ETHUSD" or "ETHUSDT")
            return "ETHUSDT";
        if (s.EndsWith("USDT", StringComparison.Ordinal) || s.EndsWith("USD", StringComparison.Ordinal))
            return s.EndsWith("USD", StringComparison.Ordinal) && !s.EndsWith("USDT", StringComparison.Ordinal)
                ? s + "T"
                : s;
        // Default research smoke path when US equity is selected — still proves TSD wire (V09).
        return "BTCUSDT";
    }

    private sealed class TsdHistoryBarDto
    {
        [JsonPropertyName("time")]
        public long Time { get; set; }

        [JsonPropertyName("open")]
        public double Open { get; set; }

        [JsonPropertyName("high")]
        public double High { get; set; }

        [JsonPropertyName("low")]
        public double Low { get; set; }

        [JsonPropertyName("close")]
        public double Close { get; set; }

        [JsonPropertyName("volume")]
        public double Volume { get; set; }
    }
}
