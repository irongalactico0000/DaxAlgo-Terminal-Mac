using TradingTerminal.Backtest.Engine.Execution;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.MarketData;
using Xunit;
using EngineFill = TradingTerminal.Backtest.Engine.Execution.L1TouchFillModel;
using EngineOrder = TradingTerminal.Backtest.Engine.Execution.WorkingOrder;

namespace TradingTerminal.Tests.Headless.Backtest;

public sealed class L1TouchFillModelPartialsTests
{
    [Fact]
    public void Engine_max_per_touch_caps_fill_quantity()
    {
        var model = new EngineFill(slippageTicks: 0, maxFillQuantityPerTouch: 4);
        var contract = Contract.UsStock("TEST");
        var order = new EngineOrder
        {
            Request = new OrderRequest(
                "c1",
                contract,
                OrderSide.Buy,
                OrderType.Market,
                Quantity: 10),
            Instrument = new InstrumentId(1),
            BrokerOrderId = "BT-1",
        };
        var tick = new Tick(DateTime.UtcNow, Bid: 99, Ask: 100, BidSize: 1, AskSize: 1);

        Assert.True(model.TryFill(order, tick, tickSize: 0.01, out var price, out var qty));
        Assert.Equal(100, price);
        Assert.Equal(4, qty);
    }

    [Fact]
    public void Session_max_per_touch_caps_fill_quantity()
    {
        var model = new L1FillModel(tickSize: 0.01, slippageTicks: 0, maxFillQuantityPerTouch: 4);
        var contract = Contract.UsStock("TEST");
        var order = new PendingOrder
        {
            Request = new OrderRequest(
                "c1",
                contract,
                OrderSide.Buy,
                OrderType.Market,
                Quantity: 10),
            BrokerOrderId = "BT-1",
        };
        var tick = new Tick(DateTime.UtcNow, Bid: 99, Ask: 100, BidSize: 1, AskSize: 1);

        Assert.True(model.TryFill(order, tick, out var price, out var qty));
        Assert.Equal(100, price);
        Assert.Equal(4, qty);
    }

    [Fact]
    public void Session_zero_max_fills_full_remaining()
    {
        var model = new L1FillModel(tickSize: 0.01, slippageTicks: 0, maxFillQuantityPerTouch: 0);
        var order = new PendingOrder
        {
            Request = new OrderRequest(
                "c1",
                Contract.UsStock("TEST"),
                OrderSide.Buy,
                OrderType.Market,
                Quantity: 10),
            BrokerOrderId = "BT-1",
        };
        var tick = new Tick(DateTime.UtcNow, Bid: 99, Ask: 100, BidSize: 1, AskSize: 1);

        Assert.True(model.TryFill(order, tick, out _, out var qty));
        Assert.Equal(10, qty);
    }

    [Fact]
    public void Session_opposite_l1_size_caps_buy_to_ask_size()
    {
        var model = new L1FillModel(
            tickSize: 0.01,
            slippageTicks: 0,
            maxFillQuantityPerTouch: 0,
            capToOppositeL1Size: true);
        var order = new PendingOrder
        {
            Request = new OrderRequest(
                "c1",
                Contract.UsStock("TEST"),
                OrderSide.Buy,
                OrderType.Market,
                Quantity: 10),
            BrokerOrderId = "BT-1",
        };
        var tick = new Tick(DateTime.UtcNow, Bid: 99, Ask: 100, BidSize: 50, AskSize: 3);

        Assert.True(model.TryFill(order, tick, out _, out var qty));
        Assert.Equal(3, qty);
    }

    [Fact]
    public void Session_opposite_l1_size_zero_means_no_fill()
    {
        var model = new L1FillModel(
            tickSize: 0.01,
            slippageTicks: 0,
            maxFillQuantityPerTouch: 0,
            capToOppositeL1Size: true);
        var order = new PendingOrder
        {
            Request = new OrderRequest(
                "c1",
                Contract.UsStock("TEST"),
                OrderSide.Sell,
                OrderType.Market,
                Quantity: 10),
            BrokerOrderId = "BT-1",
        };
        var tick = new Tick(DateTime.UtcNow, Bid: 99, Ask: 100, BidSize: 0, AskSize: 100);

        Assert.False(model.TryFill(order, tick, out _, out var qty));
        Assert.Equal(0, qty);
    }

    [Fact]
    public void Engine_opposite_l1_size_caps_with_max_per_touch()
    {
        var model = new EngineFill(slippageTicks: 0, maxFillQuantityPerTouch: 4, capToOppositeL1Size: true);
        var order = new EngineOrder
        {
            Request = new OrderRequest(
                "c1",
                Contract.UsStock("TEST"),
                OrderSide.Buy,
                OrderType.Market,
                Quantity: 10),
            Instrument = new InstrumentId(1),
            BrokerOrderId = "BT-1",
        };
        var tick = new Tick(DateTime.UtcNow, Bid: 99, Ask: 100, BidSize: 1, AskSize: 2);

        Assert.True(model.TryFill(order, tick, tickSize: 0.01, out _, out var qty));
        Assert.Equal(2, qty);
    }
}

public sealed class ResearchCompareMetricsTests
{
    [Fact]
    public void Volume_vs_avg_and_ema_slope_compute_from_window()
    {
        var id = new InstrumentId(1);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new List<OhlcvBar>();
        for (var i = 0; i < 30; i++)
        {
            var close = 100 + i * 0.5;
            bars.Add(new OhlcvBar(
                id,
                BarSize.OneHour,
                start.AddHours(i),
                close - 0.1,
                close + 0.1,
                close - 0.2,
                close,
                Volume: 1000 + i * 10,
                BrokerKind.Simulated,
                IsFinal: true));
        }

        var vol = ResearchMarketScreenerV1.ComputeVolumeVsAvg(bars);
        Assert.NotNull(vol);
        Assert.True(vol > 1.0);

        var slope = ResearchMarketScreenerV1.ComputeEma20SlopePct(bars);
        Assert.NotNull(slope);
        Assert.True(slope > 0);
    }
}
