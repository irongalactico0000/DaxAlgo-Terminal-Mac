using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>Mutable bookkeeping for an order resting in the simulated order book.</summary>
public sealed class PendingOrder
{
    public required OrderRequest Request { get; init; }
    public required string BrokerOrderId { get; init; }
    public long FilledQuantity { get; set; }
    public double TotalFillValue { get; set; }
    public OrderState State { get; set; } = OrderState.Working;

    /// <summary>Earliest sim clock when this order may fill (submit time + latency).</summary>
    public DateTime EarliestFillUtc { get; set; }

    /// <summary>FIFO-ahead estimate remaining (passive limits). 0 = cleared / not used.</summary>
    public long QueueAhead { get; set; }

    /// <summary>True after first opposite-size sample for queue estimate.</summary>
    public bool QueueJoined { get; set; }

    /// <summary>Last opposite L1 size used to estimate traded-through volume.</summary>
    public long LastOppositeSize { get; set; }

    public double? AveragePrice =>
        FilledQuantity == 0 ? null : TotalFillValue / FilledQuantity;
}
