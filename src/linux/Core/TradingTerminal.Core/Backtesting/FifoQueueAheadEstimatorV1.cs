namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// FIFO-ahead estimate v1 for Validate queue position.
/// Join with opposite L1 size; each later size decrease at the touch clears ahead volume.
/// Estimate only — not exchange MBO / Nautilus matching.
/// </summary>
public static class FifoQueueAheadEstimatorV1
{
    /// <summary>Opposite size observed when a passive limit joins the book.</summary>
    public static long Join(long oppositeSizeAtJoin) =>
        oppositeSizeAtJoin < 0 ? 0 : oppositeSizeAtJoin;

    /// <summary>
    /// Reduce queue ahead by volume that traded through the touch
    /// (proxied as opposite-size decrease between ticks).
    /// </summary>
    public static long Consume(long queueAhead, long previousOppositeSize, long currentOppositeSize)
    {
        if (queueAhead <= 0)
            return 0;

        var tradedThrough = previousOppositeSize - currentOppositeSize;
        if (tradedThrough <= 0)
            return queueAhead;

        var next = queueAhead - tradedThrough;
        return next < 0 ? 0 : next;
    }

    public static bool IsCleared(long queueAhead) => queueAhead <= 0;
}
