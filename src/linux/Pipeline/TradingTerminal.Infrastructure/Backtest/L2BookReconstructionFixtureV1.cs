using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Journey F1: available book is explicit at each replay timestamp.
/// Uses real snapshots when supplied; otherwise reconstructed L1 ladders.
/// </summary>
public static class L2BookReconstructionFixtureV1
{
    public const string ReportId = "book-at-each-timestamp";
    public const string DataModeToken =
        "L2BookReconstructionV1|book=reconstructed-l1-ladder-v1|fixture=timeline-t0-t1";

    public static L2BookReconstructionReportV1 RunTwoTimestampTimeline()
    {
        var t0 = new DateTime(2026, 3, 1, 16, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(1);
        Tick[] quotes =
        [
            new(t0, Bid: 100.00, Ask: 100.01, BidSize: 10, AskSize: 20),
            new(t1, Bid: 100.02, Ask: 100.03, BidSize: 15, AskSize: 25),
        ];

        var books = L2BookReconstructionV1.ReconstructTimeline(quotes, ladderLevels: 3, tickSize: 0.01);
        if (books.Count != 2)
            throw new InvalidOperationException($"Expected 2 books, got {books.Count}.");
        if (books[0].Mode != L2BookReconstructionV1.ModeReconstructedLadder ||
            books[1].Mode != L2BookReconstructionV1.ModeReconstructedLadder)
        {
            throw new InvalidOperationException("Expected reconstructed ladders without real depth.");
        }

        if (books[0].Book.BestAsk != 100.01 || books[0].Book.BestAskSize != 20 ||
            books[0].Book.Asks.Count != 3 ||
            books[1].Book.BestBid != 100.02 || books[1].Book.BestBidSize != 15)
        {
            throw new InvalidOperationException("Book levels do not match L1 quotes at each timestamp.");
        }

        // Real snapshot at t1 wins over reconstruction.
        var realT1 = new DepthSnapshot(
            t1,
            Bids: [new DepthLevel(100.02, 99)],
            Asks: [new DepthLevel(100.03, 88), new DepthLevel(100.04, 50)]);
        var withReal = L2BookReconstructionV1.ReconstructTimeline(
            quotes,
            realDepthByTime: [realT1],
            ladderLevels: 3,
            tickSize: 0.01);
        if (withReal[1].Mode != L2BookReconstructionV1.ModeRealSnapshot ||
            withReal[1].Book.BestAskSize != 88)
        {
            throw new InvalidOperationException("Real depth at t1 must override reconstructed ladder.");
        }

        // Session path: EnableL2BookWalk installs reconstructed book before fill.
        var clock = new SimulatedClock();
        clock.SetTo(t0);
        var contract = Contract.UsStock("MSFT");
        var bookHolder = new SimulatedOrderBook?[1];
        var fillModel = new L2BookWalkFillModel(c => bookHolder[0]?.LastDepth(c));
        var book = new SimulatedOrderBook(clock, fillModel);
        bookHolder[0] = book;
        var events = new List<OrderEvent>();
        using var _ = book.Events.Subscribe(events.Add);

        var ladder0 = books[0].Book;
        book.OnDepth(contract, ladder0);
        book.Submit(new OrderRequest(
            "c-recon-ioc",
            contract,
            OrderSide.Buy,
            OrderType.Limit,
            Quantity: 35,
            LimitPrice: 100.02,
            TimeInForce: TimeInForce.Ioc));
        book.OnTick(contract, quotes[0]);

        var filled = events.Where(e => e.LastFillQuantity > 0).Sum(e => e.LastFillQuantity);
        // Walk: 20 @ 100.01 + 15 (of 30) @ 100.02 = 35
        if (filled != 35)
            throw new InvalidOperationException($"Expected fill 35 from reconstructed book, got {filled}.");

        return new L2BookReconstructionReportV1(
            Timestamps: books.Select(b => b.TimestampUtc).ToArray(),
            Modes: books.Select(b => b.Mode).ToArray(),
            BestAsks: books.Select(b => b.Book.BestAsk).ToArray(),
            BestAskSizes: books.Select(b => b.Book.BestAskSize).ToArray(),
            RealSnapshotOverrideAskSize: withReal[1].Book.BestAskSize,
            SessionFillQuantity: filled,
            DataModeToken: DataModeToken,
            HonestyNote:
                "Book is explicit at each quote timestamp (reconstructed L1 ladder or real snapshot). " +
                "Not full L2 incremental MBO rebuild; not Nautilus matching.");
    }

    public static string Summary(L2BookReconstructionReportV1 report) =>
        $"Book@timestamps · {report.Timestamps.Count} steps · fill {report.SessionFillQuantity} · {report.Modes[0]}";
}

public sealed record L2BookReconstructionReportV1(
    IReadOnlyList<DateTime> Timestamps,
    IReadOnlyList<string> Modes,
    IReadOnlyList<double> BestAsks,
    IReadOnlyList<long> BestAskSizes,
    long RealSnapshotOverrideAskSize,
    long SessionFillQuantity,
    string DataModeToken,
    string HonestyNote);
