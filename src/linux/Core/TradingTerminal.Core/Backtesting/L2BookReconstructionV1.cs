using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// Explicit book reconstruction for Validate Journey F1.
/// When a real L2 snapshot is available it is used as-is; otherwise a deterministic
/// ladder is built from the L1 quote so every replay timestamp has an inspectable book.
/// Not Nautilus matching / MBO.
/// </summary>
public static class L2BookReconstructionV1
{
    public const string ModeRealSnapshot = "L2-snapshot";
    public const string ModeReconstructedLadder = "reconstructed-l1-ladder-v1";

    public readonly record struct BookAtTime(
        DateTime TimestampUtc,
        DepthSnapshot Book,
        string Mode);

    /// <summary>
    /// Prefer a real snapshot when present; otherwise build a finite ladder from L1.
    /// </summary>
    public static BookAtTime At(
        DateTime timestampUtc,
        Tick quote,
        DepthSnapshot? realSnapshot = null,
        int ladderLevels = 3,
        double tickSize = 0.01)
    {
        if (realSnapshot is not null &&
            (realSnapshot.Bids.Count > 0 || realSnapshot.Asks.Count > 0))
        {
            var stamped = realSnapshot.TimestampUtc == default
                ? realSnapshot with { TimestampUtc = timestampUtc }
                : realSnapshot;
            return new BookAtTime(timestampUtc, stamped, ModeRealSnapshot);
        }

        return new BookAtTime(
            timestampUtc,
            FromL1(timestampUtc, quote, ladderLevels, tickSize),
            ModeReconstructedLadder);
    }

    /// <summary>
    /// Deterministic N-level ladder: best size = L1 size; deeper levels grow by +50% size
    /// and step by <paramref name="tickSize"/>.
    /// </summary>
    public static DepthSnapshot FromL1(
        DateTime timestampUtc,
        Tick quote,
        int ladderLevels = 3,
        double tickSize = 0.01)
    {
        if (ladderLevels < 1)
            throw new ArgumentOutOfRangeException(nameof(ladderLevels));
        if (!(tickSize > 0) || !double.IsFinite(tickSize))
            throw new ArgumentOutOfRangeException(nameof(tickSize));

        var bidSize = Math.Max(1L, quote.BidSize);
        var askSize = Math.Max(1L, quote.AskSize);
        var bids = new DepthLevel[ladderLevels];
        var asks = new DepthLevel[ladderLevels];
        for (var i = 0; i < ladderLevels; i++)
        {
            var sizeScale = 1d + (0.5d * i);
            bids[i] = new DepthLevel(
                Math.Round(quote.Bid - (i * tickSize), 10),
                Math.Max(1L, (long)Math.Round(bidSize * sizeScale)));
            asks[i] = new DepthLevel(
                Math.Round(quote.Ask + (i * tickSize), 10),
                Math.Max(1L, (long)Math.Round(askSize * sizeScale)));
        }

        return new DepthSnapshot(timestampUtc, bids, asks);
    }

    /// <summary>
    /// Build the explicit book series for a quote timeline (optional aligned real depth).
    /// </summary>
    public static IReadOnlyList<BookAtTime> ReconstructTimeline(
        IReadOnlyList<Tick> quotes,
        IReadOnlyList<DepthSnapshot>? realDepthByTime = null,
        int ladderLevels = 3,
        double tickSize = 0.01)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        var depthByTs = new Dictionary<DateTime, DepthSnapshot>();
        if (realDepthByTime is not null)
        {
            foreach (var d in realDepthByTime)
                depthByTs[d.TimestampUtc] = d;
        }

        var books = new List<BookAtTime>(quotes.Count);
        foreach (var q in quotes)
        {
            depthByTs.TryGetValue(q.TimestampUtc, out var real);
            books.Add(At(q.TimestampUtc, q, real, ladderLevels, tickSize));
        }

        return books;
    }
}
