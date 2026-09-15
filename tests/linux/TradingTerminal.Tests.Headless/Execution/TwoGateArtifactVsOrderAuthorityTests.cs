using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Headless.Execution;

/// <summary>
/// Two-gate rule: authoring artifact approval (draft lock / condition bind) never bypasses order-time risk.
/// </summary>
public sealed class TwoGateArtifactVsOrderAuthorityTests
{
    private static readonly DateTimeOffset Now = DateTime.UnixEpoch.AddHours(1);

    [Fact]
    public void Locked_condition_bound_draft_still_subject_to_order_time_risk_refusal()
    {
        var condition = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var draft = StrategyDraftGestureApplierV1.BindResearchCondition(
            StrategyDraftV1.Create(new StrategyDraftScopeV1(new InstrumentId(42), "AAPL", BarSize.OneHour)),
            condition);
        var locked = StrategyDraftGestureApplierV1.Lock(draft, new string('b', 64));

        locked.IsLocked.Should().BeTrue();
        locked.LinkedConditionId.Should().Be(condition.ConditionId);
        locked.LinkedConditionVersionHashSha256.Should().Be(condition.VersionHashSha256);

        var decision = RiskPolicy.Evaluate(
            Submit(new OrderTerms(OrderSide.Buy, OrderType.Market, new ScaledQuantity(2, 0))),
            Context(limits: Limits(maximumOrderQuantity: new ScaledQuantity(1, 0))));

        decision.IsAllowed.Should().BeFalse();
        decision.Code.Should().Be(RiskDecisionCode.MaximumOrderQuantityExceeded);
    }

    private static SubmitOrderCommand Submit(OrderTerms terms)
    {
        var metadata = new ExecutionCommandMetadata(
            new CommandId("command-two-gate"),
            new CorrelationId("correlation-two-gate"),
            causationId: null,
            new TradingAccountId("account-two-gate"),
            new StrategyId("strategy-two-gate"),
            new StrategyVersion("version-two-gate"),
            new VenueId("venue-two-gate"),
            new InstrumentId(101),
            ExecutionEnvironment.Backtest,
            Now,
            expectedOrderSequence: 0);
        var clientOrderId = new ClientOrderId("client-two-gate");
        var signedUnits = terms.Side == OrderSide.Buy
            ? terms.Quantity
            : new ScaledQuantity(-terms.Quantity.Coefficient, terms.Quantity.Scale);
        var mapping = new CanonicalInstructionMappingContext(
            new IntentId("intent-two-gate"),
            bucketId: null,
            new LegId("leg-two-gate"),
            new ExecutionLeaseId("lease-two-gate"),
            new FencingToken(1),
            TradeIntentQuantityMode.Delta,
            signedUnits,
            currentPosition: ScaledQuantity.Zero,
            protectiveStopPrice: null,
            profitTargetPrice: null,
            estimatedRoundTripCostPerUnit: ScaledMoney.Zero,
            strategyNoteId: 1,
            policyVersion: "two-gate-policy");
        CanonicalOrderInstructionMapper.TryCreate(
            metadata,
            clientOrderId,
            terms,
            mapping,
            out var instruction).Should().Be(OrderDomainFault.None);
        return new SubmitOrderCommand(
            metadata,
            new OrderId("order-two-gate"),
            clientOrderId,
            terms,
            instruction!);
    }

    private static RiskEvaluationContext Context(RiskLimits? limits = null) => new(
        limits ?? Limits(),
        RiskControlMode.Active,
        killSwitchActive: false,
        currentPositionQuantity: ScaledQuantity.Zero,
        currentBuyReservedQuantity: ScaledQuantity.Zero,
        currentSellReservedQuantity: ScaledQuantity.Zero,
        currentGrossReservedNotional: ScaledMoney.Zero,
        existingOrderSignedReservation: ScaledQuantity.Zero,
        existingOrderGrossReservation: ScaledMoney.Zero,
        existingOrderFilledQuantity: ScaledQuantity.Zero,
        availableBuyingPower: new ScaledMoney(1_000_000, 0),
        dailyNetRealizedPnl: ScaledMoney.Zero,
        currentEquity: new ScaledMoney(1_000, 0),
        peakEquity: new ScaledMoney(1_000, 0),
        marketPrice: new ScaledPrice(100, 0),
        exposureCommandsInWindow: 0,
        evaluatedAtUtc: Now);

    private static RiskLimits Limits(ScaledQuantity? maximumOrderQuantity = null) => new(
        maximumOrderQuantity ?? new ScaledQuantity(1_000_000, 0),
        maximumAbsolutePosition: new ScaledQuantity(1_000_000, 0),
        maximumGrossNotional: new ScaledMoney(1_000_000, 0),
        minimumBuyingPower: ScaledMoney.Zero,
        maximumDailyLoss: new ScaledMoney(100, 0),
        maximumDrawdown: new ScaledMoney(100, 0),
        maximumExposureCommandsPerWindow: 100,
        rateLimitWindow: TimeSpan.FromMinutes(1));
}
