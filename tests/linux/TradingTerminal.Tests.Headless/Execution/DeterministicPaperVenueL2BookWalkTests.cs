using FluentAssertions;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Headless.Execution;

/// <summary>
/// Paper venue Validate-parity: canonical IOC book-walk (80 fill / 20 cancel / avg 100.00625).
/// </summary>
public sealed class DeterministicPaperVenueL2BookWalkTests
{
    private static readonly InstrumentId Instrument = new(101);
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Paper_venue_ioc_book_walk_matches_canonical_fixture()
    {
        var fixture = CreateOms(new PaperFillFidelityOptions(EnableL2BookWalk: true), "l2");
        var depth = new DepthSnapshot(
            Now.UtcDateTime,
            Bids: [new DepthLevel(99.99, 1000)],
            Asks:
            [
                new DepthLevel(100.00, 30),
                new DepthLevel(100.01, 50),
                new DepthLevel(100.02, 1000),
            ]);
        fixture.Venue.OnMarket(new PaperMarketSnapshot(
            Instrument,
            bid: new ScaledPrice(9999, 2),
            ask: new ScaledPrice(10000, 2),
            bidAvailableQuantity: ScaledQuantity.FromWhole(1000),
            askAvailableQuantity: ScaledQuantity.FromWhole(1000),
            observedAtUtc: Now,
            depth: depth));

        var submit = SubmitIocBuy100At100_01("l2");
        var result = fixture.Oms.Submit(submit, RiskContext(), new OrderCommandContext(
            new CausationId("cause-paper-l2-ioc"),
            new DeduplicationKey("dedupe-paper-l2-ioc")));

        result.IsSuccess.Should().BeTrue(result.Reason);
        result.Projection!.FilledQuantity.Should().Be(ScaledQuantity.FromWhole(80));
        result.Projection.State.Should().Be(OrderLifecycleState.Cancelled);
        ExecutionNumericBoundary.ToDecimal(result.Projection.AverageFillPrice)
            .Should().Be(100.00625m);

        var expected = L2BookWalkFillV1.CanonicalIocBuyLimit100At100_01();
        expected.FilledQuantity.Should().Be(80);
        expected.AverageFillPrice.Should().Be(100.00625);
    }

    [Fact]
    public void Default_fidelity_ignores_depth_and_uses_l1_available()
    {
        var fixture = CreateOms(PaperFillFidelityOptions.Default, "l1");
        var depth = new DepthSnapshot(
            Now.UtcDateTime,
            Bids: [new DepthLevel(99.99, 1000)],
            Asks:
            [
                new DepthLevel(100.00, 30),
                new DepthLevel(100.01, 50),
                new DepthLevel(100.02, 1000),
            ]);
        fixture.Venue.OnMarket(new PaperMarketSnapshot(
            Instrument,
            bid: new ScaledPrice(9999, 2),
            ask: new ScaledPrice(10000, 2),
            bidAvailableQuantity: ScaledQuantity.FromWhole(10),
            askAvailableQuantity: ScaledQuantity.FromWhole(10),
            observedAtUtc: Now,
            depth: depth));

        var submit = SubmitIocBuy100At100_01("l1");
        var result = fixture.Oms.Submit(submit, RiskContext(), new OrderCommandContext(
            new CausationId("cause-paper-l1-ioc"),
            new DeduplicationKey("dedupe-paper-l1-ioc")));

        result.IsSuccess.Should().BeTrue(result.Reason);
        // Without book-walk, L1 ask available (10) caps the fill; IOC cancels the rest.
        result.Projection!.FilledQuantity.Should().Be(ScaledQuantity.FromWhole(10));
        result.Projection.State.Should().Be(OrderLifecycleState.Cancelled);
        ExecutionNumericBoundary.ToDecimal(result.Projection.AverageFillPrice).Should().Be(100m);
    }

    private static (DeterministicPaperVenue Venue, OrderManagementService Oms) CreateOms(
        PaperFillFidelityOptions fidelity,
        string suffix)
    {
        var venue = new DeterministicPaperVenue(fidelity);
        var store = new InMemoryOrderEventStore();
        var leases = new InMemoryExecutionLeaseStore();
        leases.Acquire(
            new ExecutionResource(
                new VenueId($"paper-{suffix}"),
                new TradingAccountId($"paper-account-{suffix}"),
                ExecutionEnvironment.SimulatedPaper),
            new ExecutionLeaseId($"paper-{suffix}-lease"),
            new RuntimeInstanceId($"paper-{suffix}-runtime"),
            Now,
            Now.AddHours(1)).IsSuccess.Should().BeTrue();
        var oms = new OrderManagementService(store, venue, leases, new FixedClock(Now.UtcDateTime));
        return (venue, oms);
    }

    private static SubmitOrderCommand SubmitIocBuy100At100_01(string suffix)
    {
        var terms = new OrderTerms(
            OrderSide.Buy,
            OrderType.Limit,
            ScaledQuantity.FromWhole(100),
            limitPrice: new ScaledPrice(10001, 2),
            timeInForce: TimeInForce.Ioc);
        var metadata = new ExecutionCommandMetadata(
            new CommandId($"command-paper-{suffix}-ioc"),
            new CorrelationId($"correlation-paper-{suffix}"),
            new CausationId($"cause-meta-paper-{suffix}"),
            new TradingAccountId($"paper-account-{suffix}"),
            new StrategyId($"strategy-paper-{suffix}"),
            new StrategyVersion("1.0.0"),
            new VenueId($"paper-{suffix}"),
            Instrument,
            ExecutionEnvironment.SimulatedPaper,
            Now,
            expectedOrderSequence: 0);
        var clientOrderId = new ClientOrderId($"client-paper-{suffix}-ioc");
        CanonicalOrderInstructionMapper.TryCreate(
            metadata,
            clientOrderId,
            terms,
            new CanonicalInstructionMappingContext(
                new IntentId($"intent-paper-{suffix}"),
                bucketId: null,
                new LegId($"leg-paper-{suffix}"),
                new ExecutionLeaseId($"paper-{suffix}-lease"),
                new FencingToken(1),
                TradeIntentQuantityMode.Delta,
                terms.Quantity,
                currentPosition: ScaledQuantity.Zero,
                protectiveStopPrice: null,
                profitTargetPrice: null,
                estimatedRoundTripCostPerUnit: ScaledMoney.Zero,
                strategyNoteId: 1,
                policyVersion: $"paper-{suffix}-policy"),
            out var instruction).Should().Be(OrderDomainFault.None);
        return new SubmitOrderCommand(
            metadata,
            new OrderId($"order-paper-{suffix}-ioc"),
            clientOrderId,
            terms,
            instruction!);
    }

    private static RiskEvaluationContext RiskContext() => new(
        new RiskLimits(
            maximumOrderQuantity: new ScaledQuantity(1_000_000, 0),
            maximumAbsolutePosition: new ScaledQuantity(1_000_000, 0),
            maximumGrossNotional: new ScaledMoney(1_000_000_000, 0),
            minimumBuyingPower: ScaledMoney.Zero,
            maximumDailyLoss: new ScaledMoney(1_000_000, 0),
            maximumDrawdown: new ScaledMoney(1_000_000, 0),
            maximumExposureCommandsPerWindow: 1000,
            rateLimitWindow: TimeSpan.FromMinutes(1)),
        RiskControlMode.Active,
        killSwitchActive: false,
        currentPositionQuantity: ScaledQuantity.Zero,
        currentBuyReservedQuantity: ScaledQuantity.Zero,
        currentSellReservedQuantity: ScaledQuantity.Zero,
        currentGrossReservedNotional: ScaledMoney.Zero,
        existingOrderSignedReservation: ScaledQuantity.Zero,
        existingOrderGrossReservation: ScaledMoney.Zero,
        existingOrderFilledQuantity: ScaledQuantity.Zero,
        availableBuyingPower: new ScaledMoney(1_000_000_000, 0),
        dailyNetRealizedPnl: ScaledMoney.Zero,
        currentEquity: new ScaledMoney(1_000_000, 0),
        peakEquity: new ScaledMoney(1_000_000, 0),
        marketPrice: new ScaledPrice(10000, 2),
        exposureCommandsInWindow: 0,
        evaluatedAtUtc: Now);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
