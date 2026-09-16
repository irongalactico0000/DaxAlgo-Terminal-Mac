using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Named in-app research reference (canon R13): selection + indicators + condition + last search.
/// Not a chart image upload — restore without re-uploading local files.
/// </summary>
public sealed record ResearchAnalysisReferenceV1(
    string SchemaVersion,
    string ReferenceId,
    string Label,
    DateTimeOffset SavedAtUtc,
    ResearchConditionDefinitionV1 Condition,
    ResearchConditionSearchResultV1? SearchResult,
    ResearchChartSelectionV1? Selection,
    IReadOnlyList<ResearchIndicatorBindingV1> IndicatorBindings)
{
    public const string CurrentSchemaVersion = "research-analysis-reference/v1";

    public string ConditionVersionShort => Condition.VersionShort;

    public string SummaryText =>
        $"{Label} · {Condition.SummaryText} · ver {ConditionVersionShort} · " +
        $"hits {(SearchResult?.HitCount.ToString() ?? "n/a")} · " +
        $"bindings {IndicatorBindings.Count}";
}

public static class ResearchAnalysisReferenceCanonicalJsonV1
{
    public static string Serialize(ResearchAnalysisReferenceV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static string SerializeMany(IReadOnlyList<ResearchAnalysisReferenceV1> values) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(values);

    public static ResearchAnalysisReferenceV1 Deserialize(string json)
    {
        var value = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchAnalysisReferenceV1>(json)
            ?? throw new ArgumentException("Reference JSON was empty.", nameof(json));
        if (value.SchemaVersion != ResearchAnalysisReferenceV1.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported research reference schema.", nameof(json));
        if (string.IsNullOrWhiteSpace(value.ReferenceId))
            throw new ArgumentException("ReferenceId is required.", nameof(json));
        return value;
    }

    public static IReadOnlyList<ResearchAnalysisReferenceV1> DeserializeMany(string json)
    {
        var values = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchAnalysisReferenceV1[]>(json)
            ?? Array.Empty<ResearchAnalysisReferenceV1>();
        foreach (var value in values)
        {
            if (value.SchemaVersion != ResearchAnalysisReferenceV1.CurrentSchemaVersion)
                throw new ArgumentException("Unsupported research reference schema.", nameof(json));
        }

        return values;
    }
}
