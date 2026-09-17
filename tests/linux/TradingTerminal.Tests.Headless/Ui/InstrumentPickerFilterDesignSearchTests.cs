using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;
using Xunit;

namespace TradingTerminal.Tests.Headless.Ui;

public sealed class InstrumentPickerFilterDesignSearchTests
{
    private static SignalInstrument Row(string symbol, string exchange, BrokerKind? broker = null) =>
        new(
            broker is null ? symbol : $"{symbol}  ·  {BrokerInstrumentUniverse.BrokerLabel(broker.Value)}",
            "US Stocks",
            new Contract(symbol, "STK", exchange, "USD", exchange),
            broker);

    [Fact]
    public void Browse_mode_pins_recent_then_keeps_selection()
    {
        var aapl = Row("AAPL", "NASDAQ", BrokerKind.InteractiveBrokers);
        var msft = Row("MSFT", "NASDAQ", BrokerKind.InteractiveBrokers);
        var spy = Row("SPY", "ARCA");
        var all = new[] { aapl, msft, spy };

        var shown = InstrumentPickerFilter.VisibleSearchingSymbolAndVenue(
            all, term: aapl.DisplayName, selected: aapl, cap: 80, recentSymbol: "MSFT");

        Assert.Equal(msft, shown[0]);
        Assert.Contains(aapl, shown);
    }

    [Fact]
    public void Typed_search_matches_symbol_and_venue_labels()
    {
        var aapl = Row("AAPL", "NASDAQ", BrokerKind.InteractiveBrokers);
        var btc = Row("BTCUSDT", "BINANCE");
        var all = new[] { aapl, btc };

        Assert.Contains(aapl, InstrumentPickerFilter.VisibleSearchingSymbolAndVenue(all, "NASDAQ", null, 80));
        Assert.Contains(aapl, InstrumentPickerFilter.VisibleSearchingSymbolAndVenue(all, "IB", null, 80));
        Assert.Contains(btc, InstrumentPickerFilter.VisibleSearchingSymbolAndVenue(all, "BTC", null, 80));
        Assert.Empty(InstrumentPickerFilter.VisibleSearchingSymbolAndVenue(all, "ZZZNOPE", null, 80));
    }
}
