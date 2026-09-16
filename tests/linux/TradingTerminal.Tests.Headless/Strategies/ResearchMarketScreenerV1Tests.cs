using FluentAssertions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.MarketData;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class ResearchMarketScreenerV1Tests
{
    [Fact]
    public async Task Screen_ranks_by_estimated_traded_value_on_shared_hourly_window()
    {
        var high = Instrument.New("AAPL", AssetClass.Equity, "NASDAQ") with { Id = new InstrumentId(1) };
        var mid = Instrument.New("MSFT", AssetClass.Equity, "NASDAQ") with { Id = new InstrumentId(2) };
        var thin = Instrument.New("ZZZZ", AssetClass.Equity, "NASDAQ") with { Id = new InstrumentId(3) };
        var registry = Substitute.For<IInstrumentRegistry>();
        registry.All().Returns([high, mid, thin]);
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(
                high.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => VolumeBars(high.Id, count: 40, volumes: Repeat(100L, 40)));
        store.GetRecentBarsAsync(
                mid.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => VolumeBars(mid.Id, count: 40, volumes: Repeat(50L, 40), close: 10));
        store.GetRecentBarsAsync(
                thin.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OhlcvBar>());

        var result = await new ResearchMarketScreenerV1(store, registry).ScreenAsync(
            new ResearchMarketScreenRequestV1(
                "registry",
                ResearchScreenMetricV1.TradedValue,
                TopN: 10,
                LookbackBars: 3,
                RequiredBarSize: BarSize.OneHour));

        result.Rows.Should().HaveCount(2);
        result.Rows.Should().OnlyContain(r => r.BarSizeLabel == "1h");
        result.Rows[0].CanonicalSymbol.Should().Be("AAPL");
        result.Rows[0].Rank.Should().Be(1);
        result.ResultHeader.Should().Contain("estimated traded value");
        result.ResultHeader.Should().Contain("3 completed 1h bars");
        result.ResultHeader.Should().Contain("returned 2");
        result.ResultHeader.Should().Contain("with usable history of 3 requested");
        result.CoverageSummary.Should().Contain("Excluded");
        result.CoverageSummary.Should().Contain("Available instruments matched");
        result.MetricDefinition.Should().Contain("Estimated traded value");
        result.Exclusions.Should().Contain(e => e.CanonicalSymbol == "ZZZZ");
        result.HydrationAttempts.Should().Be(0);
        result.Rows[0].WindowToUtcExclusive.Should().Be(result.Rows[1].WindowToUtcExclusive);
        result.Rows[0].WindowFromUtc.Should().Be(result.Rows[1].WindowFromUtc);
    }

    [Fact]
    public async Task Screen_does_not_mix_hourly_and_daily_bars()
    {
        var hourlyOnly = Instrument.New("MSFT", AssetClass.Equity, "NASDAQ") with { Id = new InstrumentId(1) };
        var dailyOnly = Instrument.New("AAPL", AssetClass.Equity, "NASDAQ") with { Id = new InstrumentId(2) };
        var registry = Substitute.For<IInstrumentRegistry>();
        registry.All().Returns([hourlyOnly, dailyOnly]);
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(
                hourlyOnly.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => VolumeBars(hourlyOnly.Id, count: 30, volumes: Repeat(10L, 30)));
        store.GetRecentBarsAsync(
                dailyOnly.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OhlcvBar>());
        store.GetRecentBarsAsync(
                dailyOnly.Id, BarSize.OneDay, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => VolumeBars(dailyOnly.Id, count: 30, volumes: Repeat(1_000_000L, 30), size: BarSize.OneDay));

        var result = await new ResearchMarketScreenerV1(store, registry).ScreenAsync(
            new ResearchMarketScreenRequestV1(
                "registry",
                ResearchScreenMetricV1.TradedValue,
                TopN: 10,
                LookbackBars: 24,
                RequiredBarSize: BarSize.OneHour));

        result.Rows.Should().ContainSingle().Which.CanonicalSymbol.Should().Be("MSFT");
        result.Rows.Should().OnlyContain(r => r.BarSizeLabel == "1h");
        result.Exclusions.Should().Contain(e =>
            e.CanonicalSymbol == "AAPL" && e.Reason.Contains("No local 1h", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Screen_separates_volume_from_estimated_traded_value()
    {
        var manyShares = Instrument.New("LOW", AssetClass.Equity, "NYSE") with { Id = new InstrumentId(1) };
        var fewExpensive = Instrument.New("HIGH", AssetClass.Equity, "NYSE") with { Id = new InstrumentId(2) };
        var registry = Substitute.For<IInstrumentRegistry>();
        registry.All().Returns([manyShares, fewExpensive]);
        var store = Substitute.For<IMarketDataStore>();
        store.GetRecentBarsAsync(
                manyShares.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => VolumeBars(manyShares.Id, count: 20, volumes: Repeat(1000L, 20), close: 1));
        store.GetRecentBarsAsync(
                fewExpensive.Id, BarSize.OneHour, Arg.Any<int>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => VolumeBars(fewExpensive.Id, count: 20, volumes: Repeat(10L, 20), close: 200));

        var byVolume = await new ResearchMarketScreenerV1(store, registry).ScreenAsync(
            new ResearchMarketScreenRequestV1("registry", ResearchScreenMetricV1.TradingVolume, 5, 3));
        var byValue = await new ResearchMarketScreenerV1(store, registry).ScreenAsync(
            new ResearchMarketScreenRequestV1("registry", ResearchScreenMetricV1.TradedValue, 5, 3));

        byVolume.Rows[0].CanonicalSymbol.Should().Be("LOW");
        byValue.Rows[0].CanonicalSymbol.Should().Be("HIGH");
        byVolume.Rows[0].VolumeSum.Should().Be(3000);
        byValue.Rows[0].TradedValueSum.Should().Be(6000);
        byValue.Rows[0].MetricLabel.Should().Contain("estimated traded value");
    }

    private static IReadOnlyList<long> Repeat(long value, int count) =>
        Enumerable.Repeat(value, count).ToArray();

    private static IReadOnlyList<OhlcvBar> VolumeBars(
        InstrumentId id,
        int count,
        IReadOnlyList<long> volumes,
        double close = 20,
        BarSize size = BarSize.OneHour)
    {
        // Anchor far enough in the past that FloorToBarOpen(UtcNow) still includes these completed bars.
        var start = DateTime.UtcNow.AddHours(-(count + 5));
        start = new DateTime(start.Ticks - (start.Ticks % size.ToTimeSpan().Ticks), DateTimeKind.Utc);
        return Enumerable.Range(0, count).Select(index => new OhlcvBar(
            id,
            size,
            start.Add(size.ToTimeSpan() * index),
            close,
            close,
            close,
            close,
            volumes[Math.Min(index, volumes.Count - 1)],
            BrokerKind.InteractiveBrokers,
            true)).ToArray();
    }
}
