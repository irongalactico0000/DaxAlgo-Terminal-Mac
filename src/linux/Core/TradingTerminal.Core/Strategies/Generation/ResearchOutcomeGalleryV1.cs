using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Host research gallery: scan known S&amp;P-universe daily history for labeled outcome events
/// (next-day +5%, pre-crash, simple pre-breakout). Results are research evidence only — not Paper unlocks.
/// </summary>
public sealed record ResearchOutcomeGalleryScanRequestV1(
    string ScanId,
    int LookbackBars = 1_500,
    int MaxResults = 12,
    bool HydrateMissingHistory = true,
    int MaxRemoteHydrations = 40,
    BrokerKind? PreferredSource = null);

public sealed record ResearchOutcomeGalleryMatchV1(
    InstrumentId InstrumentId,
    string CanonicalSymbol,
    string CompanyName,
    BarSize Timeframe,
    BrokerKind Source,
    DateTime ObservationFromUtc,
    DateTime ObservationToUtcExclusive,
    DateTime OutcomeFromUtc,
    DateTime OutcomeToUtcExclusive,
    double OutcomeReturn,
    string LabelHint,
    double? Rsi14 = null,
    double? Ema20DistancePct = null,
    double? Atr14Pct = null,
    string? IndicatorScoreSummary = null);

public sealed record ResearchOutcomeGalleryResultV1(
    string ScanId,
    string DisplayName,
    IReadOnlyList<ResearchOutcomeGalleryMatchV1> Matches,
    int InstrumentsScanned,
    int InstrumentsWithEnoughHistory,
    string Explanation,
    int RemoteHydrationAttempts = 0,
    int RemoteHydrationSuccesses = 0,
    int RemoteHydrationFailures = 0,
    string RequestedUniverseLabel = "S&P 100",
    int RequestedUniverseSize = 0,
    int PreferredUniverseEligibleInRegistry = 0,
    bool WidenedBeyondPreferredUniverse = false,
    string CoverageSummary = "",
    string DataProvenanceSummary = "");

public interface IResearchOutcomeGalleryScanV1
{
    Task<ResearchOutcomeGalleryResultV1> ScanAsync(
        ResearchOutcomeGalleryScanRequestV1 request,
        CancellationToken cancellationToken = default);
}

/// <summary>Pure deterministic event finder shared by the store-backed scan and contract tests.</summary>
public static class ResearchOutcomeEventFinderV1
{
    public const double NextDayPlusFiveThreshold = 0.05;
    public const double PreCrashThreshold = -0.05;
    public const double PreBreakoutOutcomeThreshold = 0.03;
    public const double PreBreakoutPriorRangeMax = 0.02;
    public const int PreBreakoutLookback = 5;
    public const int MinimumBars = 8;

    public static bool TryDescribeScan(string scanId, out string displayName, out string explanationPrefix)
    {
        switch (scanId)
        {
            case "next-day-plus-5":
                displayName = "Next-day +5% gallery";
                explanationPrefix = "Daily closes with next-day return ≥ +5%";
                return true;
            case "pre-crash":
                displayName = "Pre-crash windows";
                explanationPrefix = "Daily closes with next-day return ≤ −5%";
                return true;
            case "pre-breakout":
                displayName = "Pre-breakout windows";
                explanationPrefix =
                    $"Prior {PreBreakoutLookback}-day range ≤ {PreBreakoutPriorRangeMax:P0} then next-day return ≥ +{PreBreakoutOutcomeThreshold:P0}";
                return true;
            default:
                displayName = string.Empty;
                explanationPrefix = string.Empty;
                return false;
        }
    }

    public static IReadOnlyList<ResearchOutcomeGalleryMatchV1> FindEvents(
        string scanId,
        InstrumentId instrumentId,
        string canonicalSymbol,
        string companyName,
        IReadOnlyList<OhlcvBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count < MinimumBars)
            return Array.Empty<ResearchOutcomeGalleryMatchV1>();

        return scanId switch
        {
            "next-day-plus-5" => FindNextDayReturnEvents(
                instrumentId, canonicalSymbol, companyName, bars,
                minReturnInclusive: NextDayPlusFiveThreshold,
                maxReturnInclusive: null,
                labelHint: "next-day +5%"),
            "pre-crash" => FindNextDayReturnEvents(
                instrumentId, canonicalSymbol, companyName, bars,
                minReturnInclusive: null,
                maxReturnInclusive: PreCrashThreshold,
                labelHint: "next-day ≤ −5%"),
            "pre-breakout" => FindPreBreakoutEvents(instrumentId, canonicalSymbol, companyName, bars),
            _ => Array.Empty<ResearchOutcomeGalleryMatchV1>(),
        };
    }

    private static IReadOnlyList<ResearchOutcomeGalleryMatchV1> FindNextDayReturnEvents(
        InstrumentId instrumentId,
        string canonicalSymbol,
        string companyName,
        IReadOnlyList<OhlcvBar> bars,
        double? minReturnInclusive,
        double? maxReturnInclusive,
        string labelHint)
    {
        var matches = new List<ResearchOutcomeGalleryMatchV1>();
        for (var i = 0; i < bars.Count - 1; i++)
        {
            var setup = bars[i];
            var outcome = bars[i + 1];
            if (setup.Close <= 0 || !double.IsFinite(setup.Close) || !double.IsFinite(outcome.Close))
                continue;

            var ret = outcome.Close / setup.Close - 1.0;
            if (!double.IsFinite(ret))
                continue;
            if (minReturnInclusive is { } min && ret < min)
                continue;
            if (maxReturnInclusive is { } max && ret > max)
                continue;

            matches.Add(CreateMatch(
                instrumentId, canonicalSymbol, companyName, setup, outcome, ret, labelHint, bars, i));
        }

        return matches;
    }

    private static IReadOnlyList<ResearchOutcomeGalleryMatchV1> FindPreBreakoutEvents(
        InstrumentId instrumentId,
        string canonicalSymbol,
        string companyName,
        IReadOnlyList<OhlcvBar> bars)
    {
        var matches = new List<ResearchOutcomeGalleryMatchV1>();
        for (var i = PreBreakoutLookback - 1; i < bars.Count - 1; i++)
        {
            var setup = bars[i];
            var outcome = bars[i + 1];
            if (setup.Close <= 0 || !double.IsFinite(setup.Close) || !double.IsFinite(outcome.Close))
                continue;

            var windowStart = i - (PreBreakoutLookback - 1);
            var high = double.NegativeInfinity;
            var low = double.PositiveInfinity;
            for (var j = windowStart; j <= i; j++)
            {
                high = Math.Max(high, bars[j].High);
                low = Math.Min(low, bars[j].Low);
            }

            if (!double.IsFinite(high) || !double.IsFinite(low) || setup.Close <= 0)
                continue;
            var priorRange = (high - low) / setup.Close;
            if (priorRange > PreBreakoutPriorRangeMax)
                continue;

            var ret = outcome.Close / setup.Close - 1.0;
            if (!double.IsFinite(ret) || ret < PreBreakoutOutcomeThreshold)
                continue;

            matches.Add(CreateMatch(
                instrumentId, canonicalSymbol, companyName, setup, outcome, ret,
                "pre-breakout (tight range → +3%+)", bars, i));
        }

        return matches;
    }

    private static ResearchOutcomeGalleryMatchV1 CreateMatch(
        InstrumentId instrumentId,
        string canonicalSymbol,
        string companyName,
        OhlcvBar setup,
        OhlcvBar outcome,
        double ret,
        string labelHint,
        IReadOnlyList<OhlcvBar> bars,
        int setupIndex)
    {
        var outcomeEnd = setupIndex + 2 < bars.Count
            ? bars[setupIndex + 2].OpenTimeUtc
            : outcome.OpenTimeUtc + setup.Size.ToTimeSpan();

        var rsi = TryRsi14(bars, setupIndex);
        var emaDist = TryEma20DistancePct(bars, setupIndex);
        var atrPct = TryAtr14Pct(bars, setupIndex);
        var summary = FormatIndicatorScores(rsi, emaDist, atrPct);

        return new ResearchOutcomeGalleryMatchV1(
            instrumentId,
            canonicalSymbol,
            companyName,
            setup.Size,
            setup.Source,
            setup.OpenTimeUtc,
            outcome.OpenTimeUtc,
            outcome.OpenTimeUtc,
            outcomeEnd,
            ret,
            labelHint,
            rsi,
            emaDist,
            atrPct,
            summary);
    }

    private static string? FormatIndicatorScores(double? rsi, double? emaDistPct, double? atrPct)
    {
        var parts = new List<string>(3);
        if (rsi is { } r) parts.Add($"RSI {r:0}");
        if (emaDistPct is { } e) parts.Add($"EMA20 {e:+0.0;-0.0}%");
        if (atrPct is { } a) parts.Add($"ATR {a:0.0}%");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static double? TryRsi14(IReadOnlyList<OhlcvBar> bars, int index)
    {
        const int period = 14;
        if (index < period) return null;
        double gain = 0, loss = 0;
        for (var i = index - period + 1; i <= index; i++)
        {
            var delta = bars[i].Close - bars[i - 1].Close;
            if (delta >= 0) gain += delta;
            else loss -= delta;
        }

        if (loss <= 1e-12) return 100;
        var rs = gain / loss;
        return 100.0 - (100.0 / (1.0 + rs));
    }

    private static double? TryEma20DistancePct(IReadOnlyList<OhlcvBar> bars, int index)
    {
        const int period = 20;
        if (index + 1 < period || bars[index].Close <= 0) return null;
        double ema = 0;
        for (var i = 0; i < period; i++)
            ema += bars[i].Close;
        ema /= period;
        var k = 2.0 / (period + 1);
        for (var i = period; i <= index; i++)
            ema = bars[i].Close * k + ema * (1 - k);
        return (bars[index].Close / ema - 1.0) * 100.0;
    }

    private static double? TryAtr14Pct(IReadOnlyList<OhlcvBar> bars, int index)
    {
        const int period = 14;
        if (index < period || bars[index].Close <= 0) return null;
        double sum = 0;
        for (var i = index - period + 1; i <= index; i++)
        {
            var prevClose = bars[i - 1].Close;
            var tr = Math.Max(
                bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - prevClose), Math.Abs(bars[i].Low - prevClose)));
            sum += tr;
        }

        return sum / period / bars[index].Close * 100.0;
    }
}
