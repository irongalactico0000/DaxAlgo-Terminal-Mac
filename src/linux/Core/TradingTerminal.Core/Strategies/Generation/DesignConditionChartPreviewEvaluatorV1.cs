using System.Globalization;
using System.Text.RegularExpressions;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Pure Design → chart-hit evaluator. Reuses volume search for linked research conditions and
/// evaluates EMA/SMA crossover / level rules from Design form operands so markers track the form.
/// </summary>
public static class DesignConditionChartPreviewEvaluatorV1
{
    private static readonly Regex OperandPattern = new(
        @"^\s*(ema|sma)\s*\(\s*(\d+)\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryEvaluateVolumeCondition(
        ResearchConditionDefinitionV1 condition,
        IReadOnlyList<(DateTimeOffset TimeUtc, double Volume, double Close)> bars,
        string dataSource,
        string symbol,
        out ResearchConditionSearchResultV1 result)
    {
        result = ResearchConditionEvaluatorV1.SearchVolumeMultiple(condition, bars, dataSource, symbol);
        return true;
    }

    /// <summary>
    /// Parse Design operands like <c>ema(20)</c> / <c>sma(50)</c> and evaluate crossover or level rules.
    /// Returns false when the operands are not chart-previewable yet (blocked kinds).
    /// </summary>
    public static bool TryEvaluateDesignOperands(
        string leftOperand,
        string operatorKey,
        string rightOperand,
        IReadOnlyList<(DateTimeOffset TimeUtc, double Close)> bars,
        string dataSource,
        string symbol,
        out ResearchConditionSearchResultV1 result,
        int forwardBars = ResearchConditionEvaluatorV1.DefaultForwardBars,
        int maxHits = 40)
    {
        result = EmptyBlocked(dataSource, symbol, leftOperand, operatorKey, rightOperand);
        if (!TryParseSeriesOperand(leftOperand, out var leftKind, out var leftPeriod))
            return false;
        if (!TryParseSeriesOperand(rightOperand, out var rightKind, out var rightPeriod) &&
            !TryParseNumber(rightOperand, out _))
        {
            return false;
        }

        var op = NormalizeOperator(operatorKey);
        if (op is null)
            return false;

        var left = ComputeSeries(bars, leftKind, leftPeriod);
        double[] right;
        string summary;
        if (TryParseSeriesOperand(rightOperand, out rightKind, out rightPeriod))
        {
            right = ComputeSeries(bars, rightKind, rightPeriod);
            summary = $"{leftKind}({leftPeriod}) {op} {rightKind}({rightPeriod})";
        }
        else if (TryParseNumber(rightOperand, out var level))
        {
            right = Enumerable.Repeat(level, bars.Count).ToArray();
            summary = $"{leftKind}({leftPeriod}) {op} {level:0.####}";
        }
        else
        {
            return false;
        }

        var hits = new List<ResearchConditionHitV1>();
        var hitCount = 0;
        var positive = 0;
        var negative = 0;
        var evaluated = 0;
        var warm = Math.Max(leftPeriod, rightPeriod > 0 ? rightPeriod : 1);

        for (var i = 0; i < bars.Count; i++)
        {
            if (i < warm || double.IsNaN(left[i]) || double.IsNaN(right[i]))
                continue;
            if (i == 0 || double.IsNaN(left[i - 1]) || double.IsNaN(right[i - 1]))
                continue;

            evaluated++;
            var fired = op switch
            {
                "crosses above" => left[i - 1] <= right[i - 1] && left[i] > right[i],
                "crosses below" => left[i - 1] >= right[i - 1] && left[i] < right[i],
                "is above" => left[i] > right[i],
                "is below" => left[i] < right[i],
                "equals" => Math.Abs(left[i] - right[i]) <= 1e-9 * Math.Max(1, Math.Abs(right[i])),
                _ => false,
            };
            if (!fired)
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
                    Round(left[i] - right[i]),
                    0,
                    hasForward ? Round(forwardReturn) : 0,
                    positiveForward));
            }
        }

        result = new ResearchConditionSearchResultV1(
            ResearchConditionSearchResultV1.CurrentSchemaVersion,
            ConditionVersionHashSha256: HashPreview(summary),
            ConditionSummary: summary,
            dataSource,
            symbol,
            bars.Count,
            evaluated,
            hitCount,
            positive,
            negative,
            InsufficientDataCount: Math.Min(warm, bars.Count),
            LiveMeetsCondition: null,
            hits,
            Note: hitCount == 0
                ? "No bars met the Design condition in this universe."
                : $"Showing {hits.Count}/{hitCount} hits (cap {maxHits}). Forward = {forwardBars} bars.");
        return true;
    }

    public static bool TryParseSeriesOperand(string operand, out string kind, out int period)
    {
        kind = "";
        period = 0;
        if (string.IsNullOrWhiteSpace(operand))
            return false;
        var trimmed = operand.Trim();
        if (string.Equals(trimmed, "close", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "price", StringComparison.OrdinalIgnoreCase))
        {
            kind = "close";
            period = 1;
            return true;
        }

        var match = OperandPattern.Match(trimmed);
        if (!match.Success)
            return false;
        kind = match.Groups[1].Value.Trim().ToLowerInvariant();
        period = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return period > 0;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string? NormalizeOperator(string operatorKey)
    {
        var op = (operatorKey ?? "").Trim().ToLowerInvariant();
        return op switch
        {
            "crosses above" or "crosses below" or "is above" or "is below" or "equals" => op,
            _ => null,
        };
    }

    private static double[] ComputeSeries(
        IReadOnlyList<(DateTimeOffset TimeUtc, double Close)> bars,
        string kind,
        int period)
    {
        var closes = bars.Select(static b => b.Close).ToArray();
        return kind switch
        {
            "close" or "price" => closes.Select(static c => c).ToArray(),
            "sma" => SeriesFromStreaming(closes, period, sma: true),
            _ => SeriesFromStreaming(closes, period, sma: false),
        };
    }

    /// <summary>
    /// Same streaming SMA/EMA as Charts (<see cref="TradingTerminal.Core.MarketData.Indicators"/>).
    /// </summary>
    private static double[] SeriesFromStreaming(IReadOnlyList<double> closes, int period, bool sma)
    {
        var result = new double[closes.Count];
        Array.Fill(result, double.NaN);
        if (period < 1 || closes.Count == 0)
            return result;

        if (sma)
        {
            var ind = new Indicators.SimpleMovingAverage(period);
            for (var i = 0; i < closes.Count; i++)
            {
                ind.Push(closes[i]);
                if (ind.IsReady)
                    result[i] = ind.Value;
            }
        }
        else
        {
            var ind = new Indicators.ExponentialMovingAverage(period);
            for (var i = 0; i < closes.Count; i++)
            {
                ind.Push(closes[i]);
                if (ind.IsReady)
                    result[i] = ind.Value;
            }
        }

        return result;
    }

    private static ResearchConditionSearchResultV1 EmptyBlocked(
        string dataSource,
        string symbol,
        string left,
        string op,
        string right) =>
        new(
            ResearchConditionSearchResultV1.CurrentSchemaVersion,
            ConditionVersionHashSha256: HashPreview($"{left}|{op}|{right}"),
            ConditionSummary: $"{left} {op} {right}",
            dataSource,
            symbol,
            UniverseBars: 0,
            EvaluatedBars: 0,
            HitCount: 0,
            PositiveForwardCount: 0,
            NegativeForwardCount: 0,
            InsufficientDataCount: 0,
            LiveMeetsCondition: null,
            Hits: Array.Empty<ResearchConditionHitV1>(),
            Note: "Design condition is not chart-previewable yet (need ema(n)/sma(n) operands).");

    private static string HashPreview(string summary)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("design-condition-preview|" + summary));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static double Round(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
