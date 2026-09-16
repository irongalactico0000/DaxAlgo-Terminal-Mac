using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Editable research condition (canon R08). Version changes when parameters change.
/// First slice: volume vs trailing average; later kinds reuse the same version surface.
/// </summary>
public sealed record ResearchConditionDefinitionV1(
    string SchemaVersion,
    string ConditionId,
    string Kind,
    double Threshold,
    int LookbackBars,
    string? LeftBindingId = null,
    string Operator = "gte")
{
    public const string CurrentSchemaVersion = "research-condition/v1";
    public const string KindVolumeMultipleOfAverage = "volume_multiple_of_avg";

    public static ResearchConditionDefinitionV1 VolumeMultiple(double multiple, int lookbackBars) =>
        new(
            CurrentSchemaVersion,
            ConditionId: $"volx{lookbackBars}",
            Kind: KindVolumeMultipleOfAverage,
            Threshold: multiple,
            LookbackBars: lookbackBars,
            LeftBindingId: "bar_volume",
            Operator: "gte");

    public string SummaryText => Kind switch
    {
        KindVolumeMultipleOfAverage =>
            $"volume ≥ {Threshold:0.##}× avg({LookbackBars} bars)",
        _ => $"{Kind} {Operator} {Threshold:0.##} (lookback {LookbackBars})",
    };

    public string VersionHashSha256 => ResearchConditionCanonicalJsonV1.Hash(this);

    public string VersionShort => VersionHashSha256.Length >= 12
        ? VersionHashSha256[..12]
        : VersionHashSha256;
}

public static class ResearchConditionCanonicalJsonV1
{
    public static string Serialize(ResearchConditionDefinitionV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static ResearchConditionDefinitionV1 Deserialize(string json)
    {
        var value = ExecutableStrategyDefinitionCanonicalJson.Deserialize<ResearchConditionDefinitionV1>(json)
            ?? throw new ArgumentException("Condition JSON was empty.", nameof(json));
        if (value.SchemaVersion != ResearchConditionDefinitionV1.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported research condition schema.", nameof(json));
        if (string.IsNullOrWhiteSpace(value.Kind))
            throw new ArgumentException("Condition kind is required.", nameof(json));
        if (value.LookbackBars < 1)
            throw new ArgumentException("LookbackBars must be >= 1.", nameof(json));
        if (value.Threshold <= 0)
            throw new ArgumentException("Threshold must be > 0.", nameof(json));
        return value;
    }

    public static string Hash(ResearchConditionDefinitionV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);
}
