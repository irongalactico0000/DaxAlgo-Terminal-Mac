using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class ValidationChartFillMapperV1Tests
{
    [Fact]
    public void FromTrades_maps_entry_and_exit_as_separate_fill_markers()
    {
        var trade = new Trade(
            EntryUtc: new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            ExitUtc: new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc),
            Side: OrderSide.Buy,
            Quantity: 1,
            EntryPrice: 100,
            ExitPrice: 105,
            GrossPnl: 5);

        var fills = ValidationChartFillMapperV1.FromTrades([trade]);

        Assert.Equal(2, fills.Count);
        Assert.True(fills[0].IsEntry);
        Assert.True(fills[0].IsBuy);
        Assert.Equal(100, fills[0].Price);
        Assert.False(fills[1].IsEntry);
        Assert.False(fills[1].IsBuy);
        Assert.Equal(105, fills[1].Price);
    }

    [Fact]
    public void FromTrades_null_or_empty_yields_empty()
    {
        Assert.Empty(ValidationChartFillMapperV1.FromTrades(null));
        Assert.Empty(ValidationChartFillMapperV1.FromTrades(Array.Empty<Trade>()));
    }

    [Fact]
    public void Sell_trade_entry_is_not_buy_exit_is_buy()
    {
        var trade = new Trade(
            EntryUtc: new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            ExitUtc: new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc),
            Side: OrderSide.Sell,
            Quantity: 1,
            EntryPrice: 100,
            ExitPrice: 95,
            GrossPnl: 5);

        var fills = ValidationChartFillMapperV1.FromTrades([trade]);

        Assert.False(fills[0].IsBuy);
        Assert.True(fills[1].IsBuy);
    }
}
