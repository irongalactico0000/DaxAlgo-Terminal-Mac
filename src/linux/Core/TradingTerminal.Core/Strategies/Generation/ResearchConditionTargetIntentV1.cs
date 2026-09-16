namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Phase 5 Next 3 — map a condition search/monitor verdict to a paper target size.
/// Research/monitor/strategy must use the same <see cref="ResearchConditionSearchResultV1.LiveMeetsCondition"/>
/// for the same bars + condition version; this only decides target quantity, not venue submit.
/// </summary>
public static class ResearchConditionTargetIntentV1
{
    public const string MeetAction = "target";
    public const string FlatAction = "flat";
    public const string SkipAction = "skip";

    /// <summary>
    /// When live meets → <paramref name="meetTargetUnits"/>; when false → flat;
    /// when null (insufficient data) → skip (no inventing exposure).
    /// </summary>
    public static (string Action, double TargetUnits) PlanTargetUnits(
        bool? liveMeetsCondition,
        double meetTargetUnits,
        double flatUnits = 0d)
    {
        if (liveMeetsCondition is null)
            return (SkipAction, 0d);
        if (liveMeetsCondition.Value)
            return (MeetAction, meetTargetUnits);
        return (FlatAction, flatUnits);
    }
}
