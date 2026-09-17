using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>Visual / semantic kind of one chart-authored draft object.</summary>
public enum StrategyDraftObjectKindV1
{
    HorizontalLevel = 0,
    Zone = 1,
    IndicatorThreshold = 2,
    Entry = 3,
    ProtectiveStop = 4,
    ProfitTarget = 5,
}

/// <summary>How the object participates in the strategy draft (may differ from visual kind).</summary>
public enum StrategyDraftObjectRoleV1
{
    Reference = 0,
    Entry = 1,
    ProtectiveStop = 2,
    ProfitTarget = 3,
    Filter = 4,
}

/// <summary>
/// One chart-authored object before TradeIR lock. Prices are decimal display values at the draft
/// boundary; lowering to exact scaled TradeIR terms happens at lock time, not here.
/// </summary>
public sealed record StrategyDraftObjectV1(
    string ObjectId,
    StrategyDraftObjectKindV1 Kind,
    StrategyDraftObjectRoleV1 Role,
    decimal? Price = null,
    decimal? PriceHigh = null,
    string? IndicatorId = null,
    decimal? Threshold = null,
    string? Note = null);

/// <summary>Market context required before draft gestures are accepted.</summary>
public sealed record StrategyDraftScopeV1(
    InstrumentId InstrumentId,
    string CanonicalSymbol,
    BarSize Timeframe,
    DateTimeOffset? HistoryFromUtc = null,
    DateTimeOffset? HistoryToUtc = null);

/// <summary>
/// Editable strategy draft authored from Charts (and optionally linked to research samples).
/// Unlocked drafts are not runnable. A lock records the TradeIR content hash; it does not place orders.
/// </summary>
public sealed record StrategyDraftV1(
    string SchemaVersion,
    string DraftId,
    StrategyDraftScopeV1 Scope,
    IReadOnlyList<StrategyDraftObjectV1> Objects,
    IReadOnlyList<string> LinkedEventSampleIds,
    bool IsLocked,
    string? LockedTradeIrHashSha256,
    string? LinkedConditionId = null,
    string? LinkedConditionVersionHashSha256 = null)
{
    public const string CurrentSchemaVersion = "strategy-draft/v1";

    public static StrategyDraftV1 Create(StrategyDraftScopeV1 scope, string? draftId = null) =>
        new(
            CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(draftId) ? Guid.NewGuid().ToString("N") : draftId.Trim(),
            scope,
            Array.Empty<StrategyDraftObjectV1>(),
            Array.Empty<string>(),
            IsLocked: false,
            LockedTradeIrHashSha256: null,
            LinkedConditionId: null,
            LinkedConditionVersionHashSha256: null);
}

public sealed record StrategyDraftIssueV1(string Code, string Path, string Message);

public static class StrategyDraftCanonicalJsonV1
{
    public static string Serialize(StrategyDraftV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static StrategyDraftV1 Deserialize(string json)
    {
        var value = ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategyDraftV1>(json);
        StrategyDraftValidatorV1.RequireStructurallyValid(value);
        return value;
    }

    public static string Hash(StrategyDraftV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);
}

public static class StrategyDraftValidatorV1
{
    public static IReadOnlyList<StrategyDraftIssueV1> Validate(StrategyDraftV1? draft)
    {
        var issues = new List<StrategyDraftIssueV1>();
        if (draft is null)
        {
            issues.Add(new("STRATEGY_DRAFT_REQUIRED", "draft", "A strategy draft is required."));
            return issues;
        }

        if (draft.SchemaVersion != StrategyDraftV1.CurrentSchemaVersion)
            issues.Add(new("STRATEGY_DRAFT_SCHEMA_UNSUPPORTED", "schemaVersion", "The strategy draft schema is unsupported."));
        if (string.IsNullOrWhiteSpace(draft.DraftId))
            issues.Add(new("STRATEGY_DRAFT_ID_REQUIRED", "draftId", "A draft id is required."));

        ValidateScope(draft.Scope, "scope", issues);

        var objects = draft.Objects ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < objects.Count; index++)
            ValidateObject(objects[index], $"objects[{index}]", ids, issues);

        var sampleIds = draft.LinkedEventSampleIds ?? [];
        var linked = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sampleIds.Count; index++)
        {
            var sampleId = sampleIds[index];
            if (string.IsNullOrWhiteSpace(sampleId))
            {
                issues.Add(new("STRATEGY_DRAFT_SAMPLE_ID_REQUIRED", $"linkedEventSampleIds[{index}]", "Linked event sample ids cannot be empty."));
                continue;
            }

            if (!linked.Add(sampleId.Trim()))
                issues.Add(new("STRATEGY_DRAFT_SAMPLE_ID_DUPLICATE", $"linkedEventSampleIds[{index}]", "Linked event sample ids must be unique."));
        }

        if (draft.IsLocked)
        {
            if (!IsSha256(draft.LockedTradeIrHashSha256))
            {
                issues.Add(new(
                    "STRATEGY_DRAFT_LOCK_HASH_REQUIRED",
                    "lockedTradeIrHashSha256",
                    "A locked draft must record the exact TradeIR content hash."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(draft.LockedTradeIrHashSha256))
        {
            issues.Add(new(
                "STRATEGY_DRAFT_LOCK_HASH_UNEXPECTED",
                "lockedTradeIrHashSha256",
                "An unlocked draft cannot carry a TradeIR lock hash."));
        }

        if (!string.IsNullOrWhiteSpace(draft.LinkedConditionVersionHashSha256) &&
            !IsSha256(draft.LinkedConditionVersionHashSha256))
        {
            issues.Add(new(
                "STRATEGY_DRAFT_CONDITION_HASH_INVALID",
                "linkedConditionVersionHashSha256",
                "Linked condition version must be a 64-character lowercase hex SHA-256."));
        }

        if (!string.IsNullOrWhiteSpace(draft.LinkedConditionId) &&
            string.IsNullOrWhiteSpace(draft.LinkedConditionVersionHashSha256))
        {
            issues.Add(new(
                "STRATEGY_DRAFT_CONDITION_HASH_REQUIRED",
                "linkedConditionVersionHashSha256",
                "A linked condition id requires its version hash (no prose-only binding)."));
        }

        return issues;
    }

    public static void RequireStructurallyValid(StrategyDraftV1 draft)
    {
        var issues = Validate(draft);
        if (issues.Count > 0)
            throw new ArgumentException(string.Join(" ", issues.Select(issue => $"{issue.Code}: {issue.Message}")), nameof(draft));
    }

    public static void RequireUnlocked(StrategyDraftV1 draft)
    {
        RequireStructurallyValid(draft);
        if (draft.IsLocked)
            throw new InvalidOperationException("A locked strategy draft cannot be mutated; unlock or create a new draft revision.");
    }

    private static void ValidateScope(
        StrategyDraftScopeV1? scope,
        string path,
        ICollection<StrategyDraftIssueV1> issues)
    {
        if (scope is null)
        {
            issues.Add(new("STRATEGY_DRAFT_SCOPE_REQUIRED", path, "Draft scope (instrument + timeframe) is required."));
            return;
        }

        if (scope.InstrumentId.IsNone)
            issues.Add(new("STRATEGY_DRAFT_INSTRUMENT_REQUIRED", $"{path}.instrumentId", "A canonical instrument is required."));
        if (string.IsNullOrWhiteSpace(scope.CanonicalSymbol))
            issues.Add(new("STRATEGY_DRAFT_SYMBOL_REQUIRED", $"{path}.canonicalSymbol", "A canonical symbol is required for display."));
        if (scope.HistoryFromUtc is { } from && scope.HistoryToUtc is { } to && from >= to)
            issues.Add(new("STRATEGY_DRAFT_HISTORY_RANGE_INVALID", $"{path}.history", "History from must be earlier than history to."));
    }

    private static void ValidateObject(
        StrategyDraftObjectV1? value,
        string path,
        ISet<string> ids,
        ICollection<StrategyDraftIssueV1> issues)
    {
        if (value is null)
        {
            issues.Add(new("STRATEGY_DRAFT_OBJECT_REQUIRED", path, "A draft object is required."));
            return;
        }

        if (string.IsNullOrWhiteSpace(value.ObjectId))
            issues.Add(new("STRATEGY_DRAFT_OBJECT_ID_REQUIRED", $"{path}.objectId", "A draft object id is required."));
        else if (!ids.Add(value.ObjectId.Trim()))
            issues.Add(new("STRATEGY_DRAFT_OBJECT_ID_DUPLICATE", $"{path}.objectId", "Draft object ids must be unique."));

        switch (value.Kind)
        {
            case StrategyDraftObjectKindV1.Zone:
                if (value.Price is null || value.PriceHigh is null)
                    issues.Add(new("STRATEGY_DRAFT_ZONE_PRICES_REQUIRED", path, "A zone requires Price and PriceHigh."));
                else if (value.Price >= value.PriceHigh)
                    issues.Add(new("STRATEGY_DRAFT_ZONE_RANGE_INVALID", path, "Zone Price must be lower than PriceHigh."));
                break;
            case StrategyDraftObjectKindV1.IndicatorThreshold:
                if (string.IsNullOrWhiteSpace(value.IndicatorId))
                    issues.Add(new("STRATEGY_DRAFT_INDICATOR_REQUIRED", $"{path}.indicatorId", "An indicator threshold requires IndicatorId."));
                if (value.Threshold is null)
                    issues.Add(new("STRATEGY_DRAFT_THRESHOLD_REQUIRED", $"{path}.threshold", "An indicator threshold requires Threshold."));
                break;
            case StrategyDraftObjectKindV1.HorizontalLevel:
            case StrategyDraftObjectKindV1.Entry:
            case StrategyDraftObjectKindV1.ProtectiveStop:
            case StrategyDraftObjectKindV1.ProfitTarget:
                if (value.Price is null)
                    issues.Add(new("STRATEGY_DRAFT_PRICE_REQUIRED", $"{path}.price", "This draft object requires a Price."));
                break;
            default:
                issues.Add(new("STRATEGY_DRAFT_KIND_UNSUPPORTED", $"{path}.kind", "The draft object kind is unsupported."));
                break;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Pure mutators for chart gestures → draft objects. UI calls these; locking to TradeIR is a separate step.
/// </summary>
public static class StrategyDraftGestureApplierV1
{
    public static StrategyDraftV1 UpsertLevel(
        StrategyDraftV1 draft,
        string objectId,
        StrategyDraftObjectKindV1 kind,
        StrategyDraftObjectRoleV1 role,
        decimal price,
        string? note = null)
    {
        StrategyDraftValidatorV1.RequireUnlocked(draft);
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("A draft object id is required.", nameof(objectId));
        if (kind is StrategyDraftObjectKindV1.Zone or StrategyDraftObjectKindV1.IndicatorThreshold)
            throw new ArgumentException("Use the dedicated zone or indicator helpers for that kind.", nameof(kind));

        var next = new StrategyDraftObjectV1(
            objectId.Trim(),
            kind,
            role,
            Price: price,
            Note: note);
        return ReplaceObject(draft, next);
    }

    public static StrategyDraftV1 UpsertStop(StrategyDraftV1 draft, string objectId, decimal price, string? note = null) =>
        UpsertLevel(draft, objectId, StrategyDraftObjectKindV1.ProtectiveStop, StrategyDraftObjectRoleV1.ProtectiveStop, price, note);

    public static StrategyDraftV1 UpsertTarget(StrategyDraftV1 draft, string objectId, decimal price, string? note = null) =>
        UpsertLevel(draft, objectId, StrategyDraftObjectKindV1.ProfitTarget, StrategyDraftObjectRoleV1.ProfitTarget, price, note);

    /// <summary>
    /// Bind a versioned research condition by id+hash (and IndicatorThreshold object). Not a formula re-narration.
    /// </summary>
    public static StrategyDraftV1 BindResearchCondition(
        StrategyDraftV1 draft,
        ResearchConditionDefinitionV1 condition,
        IReadOnlyList<string>? linkedEventSampleIds = null,
        StrategyDraftObjectRoleV1 role = StrategyDraftObjectRoleV1.Filter)
    {
        ArgumentNullException.ThrowIfNull(condition);
        StrategyDraftValidatorV1.RequireUnlocked(draft);

        if (role is not (StrategyDraftObjectRoleV1.Entry or StrategyDraftObjectRoleV1.Filter or StrategyDraftObjectRoleV1.Reference))
            role = StrategyDraftObjectRoleV1.Filter;

        var thresholdObject = new StrategyDraftObjectV1(
            ObjectId: $"condition:{condition.ConditionId}",
            Kind: StrategyDraftObjectKindV1.IndicatorThreshold,
            Role: role,
            IndicatorId: condition.ConditionId,
            Threshold: (decimal)condition.Threshold,
            Note: condition.Kind);

        var sampleIds = linkedEventSampleIds is { Count: > 0 }
            ? linkedEventSampleIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : draft.LinkedEventSampleIds ?? Array.Empty<string>();

        var withObject = ReplaceObject(draft, thresholdObject);
        return withObject with
        {
            LinkedEventSampleIds = sampleIds,
            LinkedConditionId = condition.ConditionId,
            LinkedConditionVersionHashSha256 = condition.VersionHashSha256,
        };
    }

    public static StrategyDraftV1 RemoveObject(StrategyDraftV1 draft, string objectId)
    {
        StrategyDraftValidatorV1.RequireUnlocked(draft);
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("A draft object id is required.", nameof(objectId));

        var remaining = (draft.Objects ?? [])
            .Where(item => !string.Equals(item.ObjectId, objectId.Trim(), StringComparison.Ordinal))
            .ToArray();
        return draft with { Objects = remaining };
    }

    public static StrategyDraftV1 Lock(StrategyDraftV1 draft, string tradeIrHashSha256)
    {
        StrategyDraftValidatorV1.RequireStructurallyValid(draft);
        if (draft.IsLocked)
            throw new InvalidOperationException("The strategy draft is already locked.");
        if (tradeIrHashSha256 is not { Length: 64 } ||
            tradeIrHashSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A 64-character lowercase hex TradeIR content hash is required.", nameof(tradeIrHashSha256));
        }

        return draft with
        {
            IsLocked = true,
            LockedTradeIrHashSha256 = tradeIrHashSha256,
        };
    }

    private static StrategyDraftV1 ReplaceObject(StrategyDraftV1 draft, StrategyDraftObjectV1 next)
    {
        var list = (draft.Objects ?? []).ToList();
        var index = list.FindIndex(item => string.Equals(item.ObjectId, next.ObjectId, StringComparison.Ordinal));
        if (index < 0)
            list.Add(next);
        else
            list[index] = next;

        var updated = draft with { Objects = list };
        StrategyDraftValidatorV1.RequireStructurallyValid(updated);
        return updated;
    }
}
