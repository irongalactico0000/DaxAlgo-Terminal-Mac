using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class StrategyDraftV1Tests
{
    [Fact]
    public void Stop_and_target_gestures_round_trip_canonically()
    {
        var draft = StrategyDraftV1.Create(Scope());
        draft = StrategyDraftGestureApplierV1.UpsertStop(draft, "stop-1", 100.25m);
        draft = StrategyDraftGestureApplierV1.UpsertTarget(draft, "target-1", 112.5m);

        Assert.Empty(StrategyDraftValidatorV1.Validate(draft));
        Assert.Equal(2, draft.Objects.Count);

        var json = StrategyDraftCanonicalJsonV1.Serialize(draft);
        var restored = StrategyDraftCanonicalJsonV1.Deserialize(json);
        Assert.Equal(draft.DraftId, restored.DraftId);
        Assert.Equal(draft.Objects, restored.Objects);
        Assert.Equal(StrategyDraftCanonicalJsonV1.Hash(draft), StrategyDraftCanonicalJsonV1.Hash(restored));
    }

    [Fact]
    public void Locked_draft_rejects_further_gestures()
    {
        var draft = StrategyDraftGestureApplierV1.UpsertStop(StrategyDraftV1.Create(Scope()), "stop-1", 99m);
        var locked = StrategyDraftGestureApplierV1.Lock(draft, Hash64('a'));

        Assert.True(locked.IsLocked);
        Assert.Throws<InvalidOperationException>(() =>
            StrategyDraftGestureApplierV1.UpsertTarget(locked, "target-1", 110m));
    }

    [Fact]
    public void Missing_instrument_or_zone_bounds_fail_closed()
    {
        var badScope = StrategyDraftV1.Create(Scope() with
        {
            InstrumentId = InstrumentId.None,
            CanonicalSymbol = " ",
        });
        Assert.Contains(StrategyDraftValidatorV1.Validate(badScope), issue => issue.Code == "STRATEGY_DRAFT_INSTRUMENT_REQUIRED");

        var draft = StrategyDraftV1.Create(Scope()) with
        {
            Objects =
            [
                new StrategyDraftObjectV1(
                    "zone-1",
                    StrategyDraftObjectKindV1.Zone,
                    StrategyDraftObjectRoleV1.Filter,
                    Price: 10m,
                    PriceHigh: 9m),
            ],
        };
        Assert.Contains(StrategyDraftValidatorV1.Validate(draft), issue => issue.Code == "STRATEGY_DRAFT_ZONE_RANGE_INVALID");
    }

    [Fact]
    public void BindResearchCondition_carries_id_and_version_hash_not_prose()
    {
        var condition = ResearchConditionDefinitionV1.VolumeMultiple(2.5, 20);
        var draft = StrategyDraftGestureApplierV1.BindResearchCondition(
            StrategyDraftV1.Create(Scope()),
            condition,
            linkedEventSampleIds: ["sample-a", "sample-a", "sample-b"]);

        Assert.Empty(StrategyDraftValidatorV1.Validate(draft));
        Assert.Equal(condition.ConditionId, draft.LinkedConditionId);
        Assert.Equal(condition.VersionHashSha256, draft.LinkedConditionVersionHashSha256);
        Assert.Equal(new[] { "sample-a", "sample-b" }, draft.LinkedEventSampleIds);
        Assert.Contains(draft.Objects, o =>
            o.Kind == StrategyDraftObjectKindV1.IndicatorThreshold &&
            o.Role == StrategyDraftObjectRoleV1.Filter &&
            o.IndicatorId == condition.ConditionId &&
            o.Threshold == 2.5m &&
            o.Note == ResearchConditionDefinitionV1.KindVolumeMultipleOfAverage);

        var restored = StrategyDraftCanonicalJsonV1.Deserialize(StrategyDraftCanonicalJsonV1.Serialize(draft));
        Assert.Equal(condition.ConditionId, restored.LinkedConditionId);
        Assert.Equal(condition.VersionHashSha256, restored.LinkedConditionVersionHashSha256);

        var asEntry = StrategyDraftGestureApplierV1.BindResearchCondition(
            StrategyDraftV1.Create(Scope()),
            condition,
            role: StrategyDraftObjectRoleV1.Entry);
        Assert.Contains(asEntry.Objects, o =>
            o.IndicatorId == condition.ConditionId &&
            o.Role == StrategyDraftObjectRoleV1.Entry);
    }

    [Fact]
    public void Pair_scope_second_leg_round_trips_and_rejects_duplicates()
    {
        var pair = StrategyDraftV1.Create(new StrategyDraftScopeV1(
            new InstrumentId(1),
            "ES",
            BarSize.OneHour,
            SecondInstrumentId: new InstrumentId(2),
            SecondCanonicalSymbol: "NQ"));
        Assert.True(pair.Scope.HasSecondLeg);
        Assert.Empty(StrategyDraftValidatorV1.Validate(pair));

        var json = StrategyDraftCanonicalJsonV1.Serialize(pair);
        var restored = StrategyDraftCanonicalJsonV1.Deserialize(json);
        Assert.Equal(pair.Scope.SecondInstrumentId, restored.Scope.SecondInstrumentId);
        Assert.Equal("NQ", restored.Scope.SecondCanonicalSymbol);

        var dup = StrategyDraftV1.Create(Scope() with
        {
            SecondInstrumentId = Scope().InstrumentId,
            SecondCanonicalSymbol = "AAPL",
        });
        Assert.Contains(
            StrategyDraftValidatorV1.Validate(dup),
            issue => issue.Code == "STRATEGY_DRAFT_SECOND_LEG_DUPLICATE");

        var incomplete = StrategyDraftV1.Create(Scope() with
        {
            SecondInstrumentId = new InstrumentId(9),
            SecondCanonicalSymbol = null,
        });
        Assert.Contains(
            StrategyDraftValidatorV1.Validate(incomplete),
            issue => issue.Code == "STRATEGY_DRAFT_SECOND_LEG_INCOMPLETE");
    }

    private static StrategyDraftScopeV1 Scope() =>
        new(new InstrumentId(42), "AAPL", BarSize.OneHour);

    private static string Hash64(char fill) => new(fill, 64);
}
