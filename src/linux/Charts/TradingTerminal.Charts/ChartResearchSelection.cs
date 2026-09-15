using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.Charts;

/// <summary>The mutually exclusive left-drag / click behaviors supported by the native chart.</summary>
public enum ChartInteractionMode
{
    Pan,
    SelectResearchRange,
    PlaceStop,
    PlaceTarget,
}

/// <summary>A half-open UTC range selected over completed chart bars.</summary>
public sealed record ChartTimeRange
{
    public ChartTimeRange(DateTimeOffset startUtc, DateTimeOffset endUtcExclusive)
    {
        StartUtc = startUtc.ToUniversalTime();
        EndUtcExclusive = endUtcExclusive.ToUniversalTime();
        if (EndUtcExclusive <= StartUtc)
            throw new ArgumentException("The chart-range end must be later than its start.", nameof(endUtcExclusive));
    }

    public DateTimeOffset StartUtc { get; }
    public DateTimeOffset EndUtcExclusive { get; }
}

public sealed class ChartRangeSelectedEventArgs(ChartTimeRange range) : EventArgs
{
    public ChartTimeRange Range { get; } = range ?? throw new ArgumentNullException(nameof(range));
}

public sealed class ResearchChartSelectionRequestedEventArgs : EventArgs
{
    public ResearchChartSelectionRequestedEventArgs(
        ResearchChartSelectionV1 selection,
        IReadOnlyList<string>? activeOverlayIds = null,
        IReadOnlyList<ResearchIndicatorBindingV1>? indicatorBindings = null)
    {
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        ActiveOverlayIds = activeOverlayIds is { Count: > 0 }
            ? activeOverlayIds
            : Array.Empty<string>();
        IndicatorBindings = indicatorBindings is { Count: > 0 }
            ? indicatorBindings
            : Array.Empty<ResearchIndicatorBindingV1>();
    }

    public ResearchChartSelectionV1 Selection { get; }

    /// <summary>Host overlay catalog ids (lossy for non 20/50 EMA). Prefer <see cref="IndicatorBindings"/>.</summary>
    public IReadOnlyList<string> ActiveOverlayIds { get; }

    /// <summary>Exact indicator kind + period from the chart at send time.</summary>
    public IReadOnlyList<ResearchIndicatorBindingV1> IndicatorBindings { get; }
}

/// <summary>Host handoff for a chart-authored strategy draft (stop/target levels). Not an order path.</summary>
public sealed class StrategyDraftRequestedEventArgs(StrategyDraftV1 draft) : EventArgs
{
    public StrategyDraftV1 Draft { get; } = draft ?? throw new ArgumentNullException(nameof(draft));
}

/// <summary>Price click while Place STOP/TARGET mode is armed.</summary>
public sealed class ChartPriceClickedEventArgs(decimal price) : EventArgs
{
    public decimal Price { get; } = price;
}

/// <summary>
/// Maps a pointer position onto the host price pane. Oscillator panes are rejected so Place STOP/TARGET
/// cannot latch RSI/MACD Y coordinates as prices.
/// </summary>
public static class ChartPriceMapper
{
    public static bool TryMap(
        double pointX,
        double pointY,
        double paneLeft,
        double paneTop,
        double paneWidth,
        double paneHeight,
        double priceMin,
        double priceMax,
        out decimal price)
    {
        price = 0m;
        if (paneWidth <= 0 || paneHeight <= 0)
            return false;
        if (pointY < paneTop || pointY > paneTop + paneHeight ||
            pointX < paneLeft || pointX > paneLeft + paneWidth)
            return false;
        if (!double.IsFinite(priceMin) || !double.IsFinite(priceMax) || priceMax <= priceMin)
            return false;

        var mapped = priceMax - (pointY - paneTop) / paneHeight * (priceMax - priceMin);
        if (!double.IsFinite(mapped) || mapped <= 0)
            return false;
        price = (decimal)mapped;
        return price > 0m;
    }
}

/// <summary>Maps a horizontal drag over the visible candle window to a half-open range.</summary>
public static class ChartRangeSelectionMapper
{
    public static ChartTimeRange Map(
        IReadOnlyList<ChartCandle> candles,
        int visibleStart,
        int visibleEndExclusive,
        double chartWidth,
        double anchorX,
        double currentX)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0)
            throw new ArgumentException("At least one candle is required.", nameof(candles));
        if (visibleStart < 0 || visibleEndExclusive > candles.Count || visibleEndExclusive <= visibleStart)
            throw new ArgumentOutOfRangeException(nameof(visibleStart), "The visible candle window is invalid.");
        if (!double.IsFinite(chartWidth) || chartWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(chartWidth), "Chart width must be positive and finite.");

        var count = visibleEndExclusive - visibleStart;
        var first = IndexAt(anchorX);
        var last = IndexAt(currentX);
        var startIndex = Math.Min(first, last);
        var endIndex = Math.Max(first, last);
        var start = DateTimeOffset.FromUnixTimeSeconds(candles[startIndex].Time);
        var end = DateTimeOffset.FromUnixTimeSeconds(candles[endIndex].Time + InferInterval(candles, endIndex));
        return new ChartTimeRange(start, end);

        int IndexAt(double x)
        {
            var clamped = Math.Clamp(double.IsFinite(x) ? x : 0, 0, chartWidth);
            var relative = Math.Min(count - 1, (int)Math.Floor(clamped / chartWidth * count));
            return visibleStart + relative;
        }
    }

    private static long InferInterval(IReadOnlyList<ChartCandle> candles, int index)
    {
        var before = index > 0 ? candles[index].Time - candles[index - 1].Time : long.MaxValue;
        var after = index + 1 < candles.Count ? candles[index + 1].Time - candles[index].Time : long.MaxValue;
        var interval = Math.Min(before > 0 ? before : long.MaxValue, after > 0 ? after : long.MaxValue);
        return interval == long.MaxValue ? 1 : interval;
    }
}
