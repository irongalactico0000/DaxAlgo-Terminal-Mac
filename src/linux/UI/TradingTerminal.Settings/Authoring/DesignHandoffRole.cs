using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>How a Research condition is reused when handed to Strategy Builder.</summary>
public enum DesignHandoffConditionRole
{
    Entry = 0,
    Exit = 1,
    Filter = 2,
}

public static class DesignHandoffConditionRoleLabels
{
    public static IReadOnlyList<string> Options { get; } = ["Entry", "Exit", "Filter"];

    public static string Label(DesignHandoffConditionRole role) => role switch
    {
        DesignHandoffConditionRole.Exit => "Exit",
        DesignHandoffConditionRole.Filter => "Filter",
        _ => "Entry",
    };

    public static DesignHandoffConditionRole Parse(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "exit" => DesignHandoffConditionRole.Exit,
            "filter" => DesignHandoffConditionRole.Filter,
            _ => DesignHandoffConditionRole.Entry,
        };

    public static StrategyDraftObjectRoleV1 ToDraftRole(DesignHandoffConditionRole role) =>
        role switch
        {
            DesignHandoffConditionRole.Entry => StrategyDraftObjectRoleV1.Entry,
            // Exit is expressed on DesignExitCondition; draft object stays Filter until a dedicated Exit role exists.
            DesignHandoffConditionRole.Exit => StrategyDraftObjectRoleV1.Filter,
            _ => StrategyDraftObjectRoleV1.Filter,
        };
}
