using System.Linq;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

public enum ResearchEventLabelKindV1
{
    PreBreakout = 0,
    PreCrash = 1,
    Neutral = 2,
    Custom = 3,
}

public enum ResearchEventLabelSourceV1
{
    Manual = 0,
    RuleSuggestedHumanReviewed = 1,
    Imported = 2,
}

/// <summary>
/// Exact indicator settings captured from the host chart for research continuity (R05/R12).
/// Prefer this over host overlay catalog ids when computing or restoring.
/// </summary>
public sealed record ResearchIndicatorBindingV1(
    string BindingId,
    string Kind,
    int Period)
{
    public string DisplayLabel => Period > 0 ? $"{Kind}({Period})" : Kind;
}

/// <summary>
/// One host-owned chart brush. The observation window is the only interval feature computation may
/// read; the future outcome window exists solely to assign or review the label.
/// </summary>
public sealed record ResearchChartSelectionV1(
    InstrumentId InstrumentId,
    string CanonicalSymbol,
    BarSize Timeframe,
    DateTimeOffset ObservationFromUtc,
    DateTimeOffset ObservationToUtc,
    DateTimeOffset OutcomeFromUtc,
    DateTimeOffset OutcomeToUtc,
    StrategyDataRequirement RequiredData);

public sealed record ResearchEventSampleV1(
    string SchemaVersion,
    string EventSampleId,
    ResearchChartSelectionV1 Selection,
    ResearchEventLabelKindV1 Label,
    string? CustomLabel,
    ResearchEventLabelSourceV1 LabelSource,
    string? Note = null,
    IReadOnlyList<ResearchIndicatorBindingV1>? IndicatorBindings = null,
    ResearchConditionDefinitionV1? Condition = null)
{
    public const string CurrentSchemaVersion = "research-event-sample/v1";

    public IReadOnlyList<ResearchIndicatorBindingV1> ResolvedIndicatorBindings =>
        IndicatorBindings ?? Array.Empty<ResearchIndicatorBindingV1>();

    public string IndicatorBindingsSummary =>
        ResolvedIndicatorBindings.Count == 0
            ? "indicators: (none captured)"
            : "indicators: " + string.Join(", ", ResolvedIndicatorBindings.Select(static b => b.DisplayLabel));

    public string ConditionSummary =>
        Condition is null
            ? "condition: (none)"
            : $"condition: {Condition.SummaryText} · ver {Condition.VersionShort}";
}

/// <summary>Fail-closed policies required before a labeled dataset can drive feature research.</summary>
public sealed record ResearchLeakagePolicyV1(
    bool ExcludeOutcomeWindowFromFeatures,
    bool FitTransformsOnTrainingOnly,
    bool UseChronologicalSplits,
    TimeSpan PurgeOrEmbargo)
{
    public static ResearchLeakagePolicyV1 SafeDefault { get; } = new(
        ExcludeOutcomeWindowFromFeatures: true,
        FitTransformsOnTrainingOnly: true,
        UseChronologicalSplits: true,
        PurgeOrEmbargo: TimeSpan.Zero);
}

public sealed record ResearchDatasetDefinitionV1(
    string SchemaVersion,
    string DatasetId,
    string WorkspaceRevisionHashSha256,
    StrategyDataRequirement RequiredData,
    ResearchLeakagePolicyV1 LeakagePolicy,
    IReadOnlyList<ResearchEventSampleV1> Samples)
{
    public const string CurrentSchemaVersion = "research-dataset/v1";
}

public sealed record ResearchDatasetIssueV1(string Code, string Path, string Message);

public static class ResearchDatasetCanonicalJsonV1
{
    public static string Serialize(ResearchDatasetDefinitionV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static ResearchDatasetDefinitionV1 Deserialize(string json)
    {
        var value = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchDatasetDefinitionV1>(json);
        ResearchDatasetValidatorV1.RequireStructurallyValid(value);
        return value;
    }

    public static string Hash(ResearchDatasetDefinitionV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);
}

public static class ResearchDatasetValidatorV1
{
    public static IReadOnlyList<ResearchDatasetIssueV1> Validate(
        ResearchDatasetDefinitionV1? dataset,
        bool requireSamples = false)
    {
        var issues = new List<ResearchDatasetIssueV1>();
        if (dataset is null)
        {
            issues.Add(new("RESEARCH_DATASET_REQUIRED", "dataset", "A research dataset definition is required."));
            return issues;
        }

        if (dataset.SchemaVersion != ResearchDatasetDefinitionV1.CurrentSchemaVersion)
            issues.Add(new("RESEARCH_DATASET_SCHEMA_UNSUPPORTED", "schemaVersion", "The research dataset schema is unsupported."));
        if (string.IsNullOrWhiteSpace(dataset.DatasetId))
            issues.Add(new("RESEARCH_DATASET_ID_REQUIRED", "datasetId", "A dataset id is required."));
        if (!IsSha256(dataset.WorkspaceRevisionHashSha256))
            issues.Add(new("RESEARCH_WORKSPACE_HASH_INVALID", "workspaceRevisionHashSha256", "The exact workspace revision hash is required."));
        if (dataset.RequiredData == StrategyDataRequirement.None)
            issues.Add(new("RESEARCH_DATA_REQUIRED", "requiredData", "At least one market-data dependency is required."));
        if (dataset.LeakagePolicy is null)
            issues.Add(new("RESEARCH_LEAKAGE_POLICY_REQUIRED", "leakagePolicy", "A leakage policy is required."));
        else
        {
            if (!dataset.LeakagePolicy.ExcludeOutcomeWindowFromFeatures)
                issues.Add(new("RESEARCH_OUTCOME_LEAKAGE_FORBIDDEN", "leakagePolicy.excludeOutcomeWindowFromFeatures", "Future outcome data must never enter feature computation."));
            if (!dataset.LeakagePolicy.FitTransformsOnTrainingOnly)
                issues.Add(new("RESEARCH_TRANSFORM_LEAKAGE_FORBIDDEN", "leakagePolicy.fitTransformsOnTrainingOnly", "Normalization must be fitted on training data only."));
            if (!dataset.LeakagePolicy.UseChronologicalSplits)
                issues.Add(new("RESEARCH_RANDOM_SPLIT_FORBIDDEN", "leakagePolicy.useChronologicalSplits", "Research samples require chronological splits."));
            if (dataset.LeakagePolicy.PurgeOrEmbargo < TimeSpan.Zero)
                issues.Add(new("RESEARCH_EMBARGO_INVALID", "leakagePolicy.purgeOrEmbargo", "Purge or embargo duration cannot be negative."));
        }

        var samples = dataset.Samples ?? [];
        if (requireSamples && samples.Count == 0)
            issues.Add(new("RESEARCH_SAMPLES_REQUIRED", "samples", "At least one labeled event sample is required."));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var path = $"samples[{index}]";
            ValidateSample(sample, path, dataset.RequiredData, issues);
            if (!string.IsNullOrWhiteSpace(sample.EventSampleId) && !ids.Add(sample.EventSampleId))
                issues.Add(new("RESEARCH_SAMPLE_ID_DUPLICATE", $"{path}.eventSampleId", "Event sample ids must be unique."));
        }
        return issues;
    }

    public static void RequireStructurallyValid(ResearchDatasetDefinitionV1 dataset)
    {
        var issues = Validate(dataset);
        if (issues.Count > 0)
            throw new ArgumentException(string.Join(" ", issues.Select(issue => $"{issue.Code}: {issue.Message}")), nameof(dataset));
    }

    public static void RequireValidSelection(ResearchChartSelectionV1 selection)
    {
        var issues = new List<ResearchDatasetIssueV1>();
        ValidateSelection(selection, "selection", issues);
        if (issues.Count > 0)
            throw new ArgumentException(string.Join(" ", issues.Select(issue => $"{issue.Code}: {issue.Message}")), nameof(selection));
    }

    private static void ValidateSample(
        ResearchEventSampleV1? sample,
        string path,
        StrategyDataRequirement datasetData,
        ICollection<ResearchDatasetIssueV1> issues)
    {
        if (sample is null)
        {
            issues.Add(new("RESEARCH_SAMPLE_REQUIRED", path, "An event sample is required."));
            return;
        }
        if (sample.SchemaVersion != ResearchEventSampleV1.CurrentSchemaVersion)
            issues.Add(new("RESEARCH_SAMPLE_SCHEMA_UNSUPPORTED", $"{path}.schemaVersion", "The event sample schema is unsupported."));
        if (string.IsNullOrWhiteSpace(sample.EventSampleId))
            issues.Add(new("RESEARCH_SAMPLE_ID_REQUIRED", $"{path}.eventSampleId", "An event sample id is required."));
        ValidateSelection(sample.Selection, $"{path}.selection", issues);
        if ((sample.Selection.RequiredData & datasetData) != sample.Selection.RequiredData)
            issues.Add(new("RESEARCH_SAMPLE_DATA_UNDECLARED", $"{path}.selection.requiredData", "The dataset must declare every sample data dependency."));
        if (sample.Label == ResearchEventLabelKindV1.Custom && string.IsNullOrWhiteSpace(sample.CustomLabel))
            issues.Add(new("RESEARCH_CUSTOM_LABEL_REQUIRED", $"{path}.customLabel", "A custom label name is required."));
        if (sample.Label != ResearchEventLabelKindV1.Custom && !string.IsNullOrWhiteSpace(sample.CustomLabel))
            issues.Add(new("RESEARCH_CUSTOM_LABEL_UNEXPECTED", $"{path}.customLabel", "Standard labels cannot carry a custom label name."));
    }

    private static void ValidateSelection(
        ResearchChartSelectionV1? selection,
        string path,
        ICollection<ResearchDatasetIssueV1> issues)
    {
        if (selection is null)
        {
            issues.Add(new("RESEARCH_SELECTION_REQUIRED", path, "A host-owned chart selection is required."));
            return;
        }
        if (selection.InstrumentId.IsNone)
            issues.Add(new("RESEARCH_INSTRUMENT_REQUIRED", $"{path}.instrumentId", "A canonical instrument is required."));
        if (string.IsNullOrWhiteSpace(selection.CanonicalSymbol))
            issues.Add(new("RESEARCH_SYMBOL_REQUIRED", $"{path}.canonicalSymbol", "A canonical symbol is required for display."));
        if (selection.RequiredData == StrategyDataRequirement.None)
            issues.Add(new("RESEARCH_SELECTION_DATA_REQUIRED", $"{path}.requiredData", "The chart selection must declare its raw data dependency."));
        if (selection.ObservationFromUtc >= selection.ObservationToUtc)
            issues.Add(new("RESEARCH_OBSERVATION_WINDOW_INVALID", $"{path}.observation", "The observation window must have positive duration."));
        if (selection.OutcomeFromUtc < selection.ObservationToUtc)
            issues.Add(new("RESEARCH_OUTCOME_OVERLAP", $"{path}.outcomeFromUtc", "The future outcome window cannot overlap the feature observation window."));
        if (selection.OutcomeFromUtc >= selection.OutcomeToUtc)
            issues.Add(new("RESEARCH_OUTCOME_WINDOW_INVALID", $"{path}.outcome", "The future outcome window must have positive duration."));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
