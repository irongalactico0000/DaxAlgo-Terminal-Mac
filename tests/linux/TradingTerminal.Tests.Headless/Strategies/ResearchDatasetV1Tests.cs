using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

public sealed class ResearchDatasetV1Tests
{
    [Fact]
    public void Observation_and_future_outcome_are_separate_and_canonical()
    {
        var dataset = Dataset(Selection());

        Assert.Empty(ResearchDatasetValidatorV1.Validate(dataset, requireSamples: true));
        var json = ResearchDatasetCanonicalJsonV1.Serialize(dataset);
        var restored = ResearchDatasetCanonicalJsonV1.Deserialize(json);
        Assert.Equal(dataset.DatasetId, restored.DatasetId);
        Assert.Equal(dataset.Samples, restored.Samples);
        Assert.Equal(ResearchDatasetCanonicalJsonV1.Hash(dataset), ResearchDatasetCanonicalJsonV1.Hash(restored));
    }

    [Fact]
    public void Outcome_overlap_is_rejected_as_feature_leakage()
    {
        var selection = Selection() with
        {
            OutcomeFromUtc = Selection().ObservationToUtc.AddSeconds(-1),
        };

        var issues = ResearchDatasetValidatorV1.Validate(Dataset(selection), requireSamples: true);

        Assert.Contains(issues, issue => issue.Code == "RESEARCH_OUTCOME_OVERLAP");
    }

    [Fact]
    public void Unsafe_normalization_or_random_split_is_rejected()
    {
        var dataset = Dataset(Selection()) with
        {
            LeakagePolicy = new ResearchLeakagePolicyV1(
                ExcludeOutcomeWindowFromFeatures: true,
                FitTransformsOnTrainingOnly: false,
                UseChronologicalSplits: false,
                PurgeOrEmbargo: TimeSpan.Zero),
        };

        var issues = ResearchDatasetValidatorV1.Validate(dataset);

        Assert.Contains(issues, issue => issue.Code == "RESEARCH_TRANSFORM_LEAKAGE_FORBIDDEN");
        Assert.Contains(issues, issue => issue.Code == "RESEARCH_RANDOM_SPLIT_FORBIDDEN");
    }

    [Fact]
    public void Indicator_bindings_round_trip_on_sample()
    {
        var bindings = new[]
        {
            new ResearchIndicatorBindingV1("host.ema", "ema", 21),
            new ResearchIndicatorBindingV1("host.rsi", "rsi", 14),
        };
        var sample = new ResearchEventSampleV1(
            ResearchEventSampleV1.CurrentSchemaVersion,
            "event-ema21",
            Selection(),
            ResearchEventLabelKindV1.PreBreakout,
            null,
            ResearchEventLabelSourceV1.Manual,
            Note: null,
            IndicatorBindings: bindings);
        var dataset = Dataset(Selection()) with { Samples = [sample] };

        var restored = ResearchDatasetCanonicalJsonV1.Deserialize(
            ResearchDatasetCanonicalJsonV1.Serialize(dataset));
        Assert.Single(restored.Samples);
        Assert.Equal(2, restored.Samples[0].ResolvedIndicatorBindings.Count);
        Assert.Equal(21, restored.Samples[0].ResolvedIndicatorBindings[0].Period);
        Assert.Equal("ema", restored.Samples[0].ResolvedIndicatorBindings[0].Kind);
    }

    [Fact]
    public void Condition_version_changes_when_threshold_changes()
    {
        var two = ResearchConditionDefinitionV1.VolumeMultiple(2, 20);
        var three = ResearchConditionDefinitionV1.VolumeMultiple(3, 20);
        Assert.NotEqual(two.VersionHashSha256, three.VersionHashSha256);
        Assert.Equal("volume ≥ 2× avg(20 bars)", two.SummaryText);

        var sample = new ResearchEventSampleV1(
            ResearchEventSampleV1.CurrentSchemaVersion,
            "event-cond",
            Selection(),
            ResearchEventLabelKindV1.PreBreakout,
            null,
            ResearchEventLabelSourceV1.Manual,
            Note: null,
            IndicatorBindings: [new ResearchIndicatorBindingV1("host.ema", "ema", 21)],
            Condition: two);
        var restored = ResearchDatasetCanonicalJsonV1.Deserialize(
            ResearchDatasetCanonicalJsonV1.Serialize(Dataset(Selection()) with { Samples = [sample] }));
        Assert.NotNull(restored.Samples[0].Condition);
        Assert.Equal(two.VersionHashSha256, restored.Samples[0].Condition!.VersionHashSha256);
        Assert.Contains("ema(21)", restored.Samples[0].IndicatorBindingsSummary, StringComparison.Ordinal);
    }

    private static ResearchDatasetDefinitionV1 Dataset(ResearchChartSelectionV1 selection) => new(
        ResearchDatasetDefinitionV1.CurrentSchemaVersion,
        "breakout-events",
        new string('a', 64),
        StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        ResearchLeakagePolicyV1.SafeDefault,
        [
            new ResearchEventSampleV1(
                ResearchEventSampleV1.CurrentSchemaVersion,
                "event-1",
                selection,
                ResearchEventLabelKindV1.PreBreakout,
                null,
                ResearchEventLabelSourceV1.Manual),
        ]);

    private static ResearchChartSelectionV1 Selection()
    {
        var start = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
        return new ResearchChartSelectionV1(
            new InstrumentId(42),
            "BTC-USD",
            BarSize.OneMinute,
            start,
            start.AddMinutes(10),
            start.AddMinutes(10),
            start.AddMinutes(15),
            StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape);
    }
}
