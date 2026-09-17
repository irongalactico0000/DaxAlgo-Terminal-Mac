using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.App.Authoring;

/// <summary>Severity for Design → Build review items.</summary>
public enum DesignReviewSeverityV1
{
    Ready = 0,
    Warning = 1,
    Required = 2,
}

/// <summary>One checklist row in Review &amp; continue.</summary>
public sealed record DesignReviewItemV1(
    string Id,
    string Title,
    DesignReviewSeverityV1 Severity,
    string Detail,
    string FocusField)
{
    public string SeverityGlyph =>
        Severity switch
        {
            DesignReviewSeverityV1.Ready => "✓",
            DesignReviewSeverityV1.Warning => "!",
            DesignReviewSeverityV1.Required => "✗",
            _ => "?",
        };

    public bool BlocksContinue => Severity == DesignReviewSeverityV1.Required;
}

/// <summary>Canonical Design draft used for hashing and review (content-addressed).</summary>
public sealed record DesignDraftCanonicalV1(
    string Instrument,
    string Timeframe,
    string EvaluationTiming,
    string EntrySummary,
    string ExitSummary,
    string SizingSummary,
    IReadOnlyList<DesignRiskLimitCanonicalV1> RiskLimits,
    string OrdersSummary,
    string? LinkedFindingId,
    string? EntryNotes,
    string? ExitNotes,
    string? RiskNotes);

public sealed record DesignRiskLimitCanonicalV1(
    string Type,
    string Value,
    string Unit,
    string Scope,
    string Action);

/// <summary>Result of evaluating a Design draft for Build readiness.</summary>
public sealed record DesignReviewResultV1(
    string DraftHashSha256,
    IReadOnlyList<DesignReviewItemV1> Items)
{
    public bool HasRequired =>
        Items.Any(static i => i.Severity == DesignReviewSeverityV1.Required);

    public bool HasWarnings =>
        Items.Any(static i => i.Severity == DesignReviewSeverityV1.Warning);

    public bool CanContinueToBuild(bool warningsAccepted) =>
        !HasRequired && (!HasWarnings || warningsAccepted);
}

/// <summary>
/// Pure Design → Build review evaluator. Required blocks Continue; warnings need explicit accept.
/// </summary>
public static class DesignDraftReviewEvaluatorV1
{
    public static string HashDraft(DesignDraftCanonicalV1 draft) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(draft);

    public static DesignReviewResultV1 Evaluate(DesignDraftCanonicalV1 draft)
    {
        var items = new List<DesignReviewItemV1>();

        AddPresence(
            items,
            id: "instrument",
            title: "Instrument",
            focus: "instrument",
            readyWhen: !IsBlank(draft.Instrument),
            requiredDetail: "Choose an instrument before Build.");

        AddPresence(
            items,
            id: "timeframe",
            title: "Timeframe / data",
            focus: "timeframe",
            readyWhen: !IsBlank(draft.Timeframe) && !IsChoose(draft.Timeframe),
            requiredDetail: "Choose a timeframe.");

        AddPresence(
            items,
            id: "evaluation",
            title: "Evaluation timing",
            focus: "evaluation",
            readyWhen: !IsBlank(draft.EvaluationTiming) && !IsChoose(draft.EvaluationTiming),
            severityIfMissing: DesignReviewSeverityV1.Warning,
            missingDetail: "Evaluation timing not set — default may be assumed later.");

        if (!IsBlank(draft.EntrySummary))
        {
            items.Add(new DesignReviewItemV1(
                "entry",
                "Entry condition",
                DesignReviewSeverityV1.Ready,
                draft.EntrySummary,
                "entry"));
        }
        else
        {
            items.Add(new DesignReviewItemV1(
                "entry",
                "Entry condition",
                DesignReviewSeverityV1.Required,
                "Entry condition missing — set operands in Design.",
                "entry"));
        }

        if (!IsBlank(draft.ExitSummary))
        {
            items.Add(new DesignReviewItemV1(
                "exit",
                "Exit rule",
                DesignReviewSeverityV1.Ready,
                draft.ExitSummary,
                "exit"));
        }
        else
        {
            items.Add(new DesignReviewItemV1(
                "exit",
                "Exit rule",
                DesignReviewSeverityV1.Warning,
                "Exit rule missing — you may continue after accepting warnings.",
                "exit"));
        }

        if (!IsBlank(draft.SizingSummary))
        {
            items.Add(new DesignReviewItemV1(
                "sizing",
                "Position sizing",
                DesignReviewSeverityV1.Ready,
                draft.SizingSummary,
                "sizing"));
        }
        else
        {
            items.Add(new DesignReviewItemV1(
                "sizing",
                "Position sizing",
                DesignReviewSeverityV1.Required,
                "Position sizing missing — set quantity before Build.",
                "sizing"));
        }

        if (draft.RiskLimits.Count == 0)
        {
            items.Add(new DesignReviewItemV1(
                "risk",
                "Risk limits",
                DesignReviewSeverityV1.Required,
                "No risk limits defined — add at least one (e.g. daily loss).",
                "risk"));
        }
        else if (draft.RiskLimits.Any(static l => !IsCompleteLimit(l)))
        {
            items.Add(new DesignReviewItemV1(
                "risk",
                "Risk limits",
                DesignReviewSeverityV1.Required,
                "One or more risk limits are incomplete (need type, value, unit, scope, action).",
                "risk"));
        }
        else
        {
            items.Add(new DesignReviewItemV1(
                "risk",
                "Risk limits",
                DesignReviewSeverityV1.Ready,
                string.Join("; ", draft.RiskLimits.Select(FormatLimit)),
                "risk"));
        }

        if (!IsBlank(draft.OrdersSummary))
        {
            items.Add(new DesignReviewItemV1(
                "orders",
                "Order instructions",
                DesignReviewSeverityV1.Ready,
                draft.OrdersSummary,
                "orders"));
        }
        else
        {
            items.Add(new DesignReviewItemV1(
                "orders",
                "Order instructions",
                DesignReviewSeverityV1.Warning,
                "Order type / TIF not set — you may continue after accepting warnings.",
                "orders"));
        }

        if (!string.IsNullOrWhiteSpace(draft.LinkedFindingId))
        {
            items.Add(new DesignReviewItemV1(
                "research",
                "Research evidence",
                DesignReviewSeverityV1.Ready,
                $"Linked finding: {draft.LinkedFindingId.Trim()} (optional; does not block Build).",
                "research"));
        }
        else
        {
            items.Add(new DesignReviewItemV1(
                "research",
                "Research evidence",
                DesignReviewSeverityV1.Warning,
                "No linked research finding — optional; does not block Build.",
                "research"));
        }

        return new DesignReviewResultV1(HashDraft(draft), items);
    }

    private static void AddPresence(
        List<DesignReviewItemV1> items,
        string id,
        string title,
        string focus,
        bool readyWhen,
        string? requiredDetail = null,
        DesignReviewSeverityV1 severityIfMissing = DesignReviewSeverityV1.Required,
        string? missingDetail = null)
    {
        if (readyWhen)
        {
            items.Add(new DesignReviewItemV1(id, title, DesignReviewSeverityV1.Ready, "Set", focus));
            return;
        }

        items.Add(new DesignReviewItemV1(
            id,
            title,
            severityIfMissing,
            missingDetail ?? requiredDetail ?? "Missing",
            focus));
    }

    private static bool IsCompleteLimit(DesignRiskLimitCanonicalV1 limit) =>
        !IsBlank(limit.Type) &&
        !IsBlank(limit.Value) &&
        decimal.TryParse(limit.Value.Trim(), out var v) &&
        v > 0 &&
        !IsBlank(limit.Unit) &&
        !IsBlank(limit.Scope) &&
        !IsBlank(limit.Action);

    private static string FormatLimit(DesignRiskLimitCanonicalV1 limit) =>
        $"{limit.Type} {limit.Value} {limit.Unit} · {limit.Scope} · {limit.Action}";

    private static bool IsBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.TrimStart().StartsWith("Unresolved", StringComparison.OrdinalIgnoreCase);

    private static bool IsChoose(string? value) =>
        string.Equals(value?.Trim(), "Choose…", StringComparison.Ordinal) ||
        string.Equals(value?.Trim(), "Choose...", StringComparison.OrdinalIgnoreCase);
}
