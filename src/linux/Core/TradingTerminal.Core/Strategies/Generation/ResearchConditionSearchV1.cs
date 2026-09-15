using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>One historical bar index where a research condition fired (R09).</summary>
public sealed record ResearchConditionHitV1(
    DateTimeOffset BarTimeUtc,
    double ConditionMetric,
    double Threshold,
    double ForwardReturn,
    bool ForwardPositive);

/// <summary>Result of scanning a bar series for a condition (V06).</summary>
public sealed record ResearchConditionSearchResultV1(
    string SchemaVersion,
    string ConditionVersionHashSha256,
    string ConditionSummary,
    string DataSource,
    string Symbol,
    int UniverseBars,
    int EvaluatedBars,
    int HitCount,
    int PositiveForwardCount,
    int NegativeForwardCount,
    int InsufficientDataCount,
    bool? LiveMeetsCondition,
    IReadOnlyList<ResearchConditionHitV1> Hits,
    string Note)
{
    public const string CurrentSchemaVersion = "research-condition-search/v1";
}

/// <summary>Pure evaluator — shared by local store and TSD bar paths.</summary>
public static class ResearchConditionEvaluatorV1
{
    public const int DefaultForwardBars = 5;

    public static ResearchConditionSearchResultV1 SearchVolumeMultiple(
        ResearchConditionDefinitionV1 condition,
        IReadOnlyList<(DateTimeOffset TimeUtc, double Volume, double Close)> bars,
        string dataSource,
        string symbol,
        int forwardBars = DefaultForwardBars,
        int maxHits = 40)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(bars);
        if (condition.Kind != ResearchConditionDefinitionV1.KindVolumeMultipleOfAverage)
            throw new ArgumentException($"Unsupported condition kind '{condition.Kind}'.", nameof(condition));

        var lookback = condition.LookbackBars;
        var threshold = condition.Threshold;
        var hits = new List<ResearchConditionHitV1>();
        var hitCount = 0;
        var positive = 0;
        var negative = 0;
        var evaluated = 0;
        var insufficient = 0;

        for (var i = 0; i < bars.Count; i++)
        {
            if (i < lookback)
            {
                insufficient++;
                continue;
            }

            var avg = 0d;
            for (var j = i - lookback; j < i; j++)
                avg += Math.Max(0d, bars[j].Volume);
            avg /= lookback;
            if (avg <= double.Epsilon)
            {
                insufficient++;
                continue;
            }

            evaluated++;
            var metric = bars[i].Volume / avg;
            if (metric + 1e-12 < threshold)
                continue;

            hitCount++;
            var forwardIdx = i + forwardBars;
            double forwardReturn = 0;
            var hasForward = forwardIdx < bars.Count && bars[i].Close > 0;
            if (hasForward)
                forwardReturn = (bars[forwardIdx].Close - bars[i].Close) / bars[i].Close;

            var positiveForward = hasForward && forwardReturn > 0;
            if (hasForward)
            {
                if (positiveForward) positive++;
                else negative++;
            }

            if (hits.Count < maxHits)
            {
                hits.Add(new ResearchConditionHitV1(
                    bars[i].TimeUtc,
                    Round(metric),
                    threshold,
                    hasForward ? Round(forwardReturn) : 0,
                    positiveForward));
            }
        }

        bool? live = null;
        if (bars.Count > lookback)
        {
            var i = bars.Count - 1;
            var avg = 0d;
            for (var j = i - lookback; j < i; j++)
                avg += Math.Max(0d, bars[j].Volume);
            avg /= lookback;
            if (avg > double.Epsilon)
                live = bars[i].Volume / avg + 1e-12 >= threshold;
        }

        return new ResearchConditionSearchResultV1(
            ResearchConditionSearchResultV1.CurrentSchemaVersion,
            condition.VersionHashSha256,
            condition.SummaryText,
            dataSource,
            symbol,
            bars.Count,
            evaluated,
            hitCount,
            positive,
            negative,
            insufficient,
            live,
            hits,
            Note: hitCount == 0
                ? "No bars met the condition in this universe."
                : $"Showing {hits.Count}/{hitCount} hits (cap {maxHits}). Forward = {forwardBars} bars. Live uses last bar vs prior {lookback}-bar avg.");
    }

    private static double Round(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

public interface IResearchConditionSearchV1
{
    Task<ResearchConditionSearchResultV1> SearchLocalAsync(
        ResearchConditionDefinitionV1 condition,
        InstrumentId instrumentId,
        string symbol,
        BarSize timeframe,
        int recentBarCount = 500,
        CancellationToken cancellationToken = default);

    Task<ResearchConditionSearchResultV1> SearchTsdAsync(
        ResearchConditionDefinitionV1 condition,
        string symbol,
        string interval = "1m",
        int limit = 500,
        CancellationToken cancellationToken = default);
}
