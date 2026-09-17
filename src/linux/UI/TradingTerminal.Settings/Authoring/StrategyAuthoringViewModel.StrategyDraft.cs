using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Host-owned chart strategy draft (stop/target). Read-only summary in Research; never submits orders.
/// Design also hosts a blank rule-editor draft (entry/exit/size/risk/orders) before Hyperion candidates.
/// </summary>
public sealed partial class StrategyAuthoringViewModel
{
    [ObservableProperty]
    private StrategyDraftV1? _pendingStrategyDraft;

    [ObservableProperty] private string _designInstrumentText = "";
    [ObservableProperty] private string _designTimeframeText = "";
    [ObservableProperty] private string _designEvaluationTimingText = "";
    [ObservableProperty] private string _designEntryRuleText = "";
    [ObservableProperty] private string _designExitRuleText = "";
    [ObservableProperty] private string _designSizingRuleText = "";
    [ObservableProperty] private string _designRiskRuleText = "";
    [ObservableProperty] private string _designOrderRuleText = "";
    [ObservableProperty] private string _pendingHyperionDesignProposalText = "";
    [ObservableProperty] private bool _awaitingHyperionDesignProposal;

    public IReadOnlyList<string> DesignTimeframeOptions { get; } =
        ["", "1m", "5m", "15m", "1h", "1D"];

    public IReadOnlyList<string> DesignEvaluationTimingOptions { get; } =
        ["", "Completed bar", "Forming bar", "Session open"];

    public bool HasDesignRuleDraft =>
        !string.IsNullOrWhiteSpace(DesignInstrumentText) ||
        !string.IsNullOrWhiteSpace(DesignTimeframeText) ||
        !string.IsNullOrWhiteSpace(DesignEvaluationTimingText) ||
        !string.IsNullOrWhiteSpace(DesignEntryRuleText) ||
        !string.IsNullOrWhiteSpace(DesignExitRuleText) ||
        !string.IsNullOrWhiteSpace(DesignSizingRuleText) ||
        !string.IsNullOrWhiteSpace(DesignRiskRuleText) ||
        !string.IsNullOrWhiteSpace(DesignOrderRuleText);

    public bool HasPendingHyperionDesignProposal =>
        !string.IsNullOrWhiteSpace(PendingHyperionDesignProposalText);

    public string DesignRuleEditorHint =>
        HasPendingFindingDesignProposal
            ? "Review the finding → Design rules proposal below. Apply writes fields; Discard keeps your draft."
            : HasResearchDesignHandoff
            ? "Linked research finding is below — Apply finding to Design rules (review first), or fill fields manually."
            : "Edit the working strategy draft here. Hyperion proposals must be Accepted before they change these fields.";

    [ObservableProperty] private string _pendingFindingDesignProposalText = "";

    public bool HasPendingFindingDesignProposal =>
        !string.IsNullOrWhiteSpace(PendingFindingDesignProposalText);

    public bool CanStageFindingAsDesignProposal =>
        HasResearchDesignHandoff &&
        !IsGenerating &&
        ResolveConditionForDraftBind() is not null;

    public bool CanAcceptFindingDesignProposal =>
        HasPendingFindingDesignProposal && !IsGenerating;

    public bool CanPromoteDesignRulesToRequest => HasDesignRuleDraft && !IsGenerating;

    public bool CanReviewDesignRules => HasDesignRuleDraft && !IsGenerating;

    public bool CanStageLastHyperionAsDesignProposal =>
        !IsGenerating &&
        Messages.Any(static m => m.IsAssistant && !string.IsNullOrWhiteSpace(m.Text));

    public bool CanAcceptHyperionDesignProposal =>
        HasPendingHyperionDesignProposal && !IsGenerating;

    public string DesignUnresolvedChecklistText
    {
        get
        {
            var missing = new List<string>();
            if (IsDesignFieldUnresolved(DesignInstrumentText))
                missing.Add("instrument");
            if (IsDesignFieldUnresolved(DesignTimeframeText))
                missing.Add("timeframe / data");
            if (IsDesignFieldUnresolved(DesignEvaluationTimingText))
                missing.Add("evaluation timing");
            if (IsDesignFieldUnresolved(DesignEntryRuleText))
                missing.Add("entry condition");
            if (IsDesignFieldUnresolved(DesignExitRuleText))
                missing.Add("exit");
            if (IsDesignFieldUnresolved(DesignSizingRuleText))
                missing.Add("sizing");
            if (IsDesignFieldUnresolved(DesignRiskRuleText))
                missing.Add("risk");
            if (IsDesignFieldUnresolved(DesignOrderRuleText))
                missing.Add("orders");

            if (missing.Count == 0)
                return "All Design fields have text. Review strategy, then Build when ready.";

            return "Unresolved before Build: " + string.Join(", ", missing) +
                   ". Choose an instrument and resolve the entry condition before review is complete.";
        }
    }

    /// <summary>Empty or explicit "Unresolved …" placeholders are not ready for Build.</summary>
    public static bool IsDesignFieldUnresolved(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.TrimStart().StartsWith("Unresolved", StringComparison.OrdinalIgnoreCase);

    public string DesignRulesReviewText
    {
        get
        {
            if (!HasDesignRuleDraft)
                return "Add instrument, timeframe, evaluation timing, and rules — then Review strategy.";

            static string Line(string key, string value) =>
                string.IsNullOrWhiteSpace(value) ? $"{key}: (unresolved)" : $"{key}: {value.Trim()}";

            return string.Join('\n', new[]
            {
                "Working strategy draft (shared by Design, Hyperion Accept, and Build):",
                Line("INSTRUMENT", DesignInstrumentText),
                Line("TIMEFRAME", DesignTimeframeText),
                Line("EVALUATION", DesignEvaluationTimingText),
                Line("ENTRY", DesignEntryRuleText),
                Line("EXIT", DesignExitRuleText),
                Line("SIZING", DesignSizingRuleText),
                Line("RISK", DesignRiskRuleText),
                Line("ORDERS", DesignOrderRuleText),
                "",
                DesignUnresolvedChecklistText,
                "Changing EMA 20 → EMA 30 here must be what Build uses for the next revision.",
            });
        }
    }

    public string LinkedResearchSummaryText
    {
        get
        {
            if (!HasResearchDesignHandoff)
                return "";
            var indicators = PendingResearchIndicatorsText;
            var condition = HasPendingResearchCondition
                ? PendingResearchConditionText
                : "no condition yet";
            return $"Finding linked · indicators: {indicators} · condition: {condition}";
        }
    }

    private void NotifyDesignDraftChanged()
    {
        OnPropertyChanged(nameof(HasDesignRuleDraft));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        OnPropertyChanged(nameof(CanReviewDesignRules));
        OnPropertyChanged(nameof(DesignRulesReviewText));
        OnPropertyChanged(nameof(DesignUnresolvedChecklistText));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        ReviewDesignRulesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInvestigateInResearchStudio));
        InvestigateInResearchStudioCommand.NotifyCanExecuteChanged();
        NotifyBuildDesignBlockerStateChanged();
        NotifyWorkingFlowMapChanged();
    }

    private void NotifyHyperionDesignProposalCommandsChanged()
    {
        OnPropertyChanged(nameof(CanStageLastHyperionAsDesignProposal));
        OnPropertyChanged(nameof(CanAcceptHyperionDesignProposal));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        OnPropertyChanged(nameof(CanReviewDesignRules));
        StageLastHyperionAsDesignProposalCommand.NotifyCanExecuteChanged();
        AcceptHyperionDesignProposalCommand.NotifyCanExecuteChanged();
        DiscardHyperionDesignProposalCommand.NotifyCanExecuteChanged();
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        ReviewDesignRulesCommand.NotifyCanExecuteChanged();
    }

    partial void OnDesignInstrumentTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignTimeframeTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignEvaluationTimingTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignEntryRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignExitRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignSizingRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignRiskRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignOrderRuleTextChanged(string value) => NotifyDesignDraftChanged();

    partial void OnPendingHyperionDesignProposalTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPendingHyperionDesignProposal));
        OnPropertyChanged(nameof(CanAcceptHyperionDesignProposal));
        AcceptHyperionDesignProposalCommand.NotifyCanExecuteChanged();
        DiscardHyperionDesignProposalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Confirms Design fields as the visible working draft before Build.
    /// Does not invent TradeIR and does not re-interpret via the LLM.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReviewDesignRules))]
    private void ReviewDesignRules()
    {
        if (!HasDesignRuleDraft) return;
        Status =
            "Working draft rules reviewed. These fields are the strategy definition for the next Build. " +
            "Ask Hyperion only if you want proposed edits — Accept is required before they replace these fields.";
        OnPropertyChanged(nameof(DesignRulesReviewText));
        NotifyWorkingFlowMapChanged();
    }

    /// <summary>
    /// Prompt path only: copies Design fields into the Hyperion composer.
    /// Does not update Design fields. After Hyperion replies, stage and Accept the proposal.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPromoteDesignRulesToRequest))]
    private void PromoteDesignRulesToRequest()
    {
        if (!HasDesignRuleDraft) return;
        static string Line(string key, string value) =>
            string.IsNullOrWhiteSpace(value) ? "" : $"{key}: {value.Trim()}";

        var body = string.Join('\n', new[]
        {
            "Design rules draft (composer prompt — Hyperion may reinterpret on Send):",
            Line("INSTRUMENT", DesignInstrumentText),
            Line("TIMEFRAME", DesignTimeframeText),
            Line("EVALUATION", DesignEvaluationTimingText),
            Line("ENTRY", DesignEntryRuleText),
            Line("EXIT", DesignExitRuleText),
            Line("SIZING", DesignSizingRuleText),
            Line("RISK", DesignRiskRuleText),
            Line("ORDERS", DesignOrderRuleText),
            "",
            "Reply with the same keys. The operator must Accept before Design fields change.",
        }.Where(static s => s.Length == 0 || !string.IsNullOrWhiteSpace(s)));

        Composer = body;
        AwaitingHyperionDesignProposal = true;
        Status =
            "Copied rules into the Hyperion prompt. After the reply, use “Stage last Hyperion reply” then Accept or Discard — " +
            "Design fields do not change until Accept.";
        NotifyWorkingFlowMapChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStageLastHyperionAsDesignProposal))]
    private void StageLastHyperionAsDesignProposal()
    {
        var last = Messages.LastOrDefault(static m => m.IsAssistant && !string.IsNullOrWhiteSpace(m.Text));
        if (last is null) return;
        PendingHyperionDesignProposalText = last.Text.Trim();
        AwaitingHyperionDesignProposal = false;
        Status =
            "Hyperion reply staged for review. Accept to write into Design fields, or Discard to keep the current draft.";
        StageLastHyperionAsDesignProposalCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAcceptHyperionDesignProposal))]
    private void AcceptHyperionDesignProposal()
    {
        if (!HasPendingHyperionDesignProposal) return;
        ApplyHyperionProposalToDesignFields(PendingHyperionDesignProposalText);
        PendingHyperionDesignProposalText = "";
        AwaitingHyperionDesignProposal = false;
        Status =
            "Accepted Hyperion proposal into Design fields. Review strategy again before Build — prior Validate evidence stays on earlier revisions.";
        NotifyDesignDraftChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAcceptHyperionDesignProposal))]
    private void DiscardHyperionDesignProposal()
    {
        PendingHyperionDesignProposalText = "";
        AwaitingHyperionDesignProposal = false;
        Status = "Discarded Hyperion proposal. Design fields unchanged.";
    }

    partial void OnPendingFindingDesignProposalTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPendingFindingDesignProposal));
        OnPropertyChanged(nameof(CanAcceptFindingDesignProposal));
        OnPropertyChanged(nameof(DesignRuleEditorHint));
        AcceptFindingDesignProposalCommand.NotifyCanExecuteChanged();
        DiscardFindingDesignProposalCommand.NotifyCanExecuteChanged();
        StageFindingAsDesignProposalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Stage keyed Design lines from the linked finding. Does not mutate Design fields until Apply.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStageFindingAsDesignProposal))]
    private void StageFindingAsDesignProposal()
    {
        if (!TryStageFindingAsDesignProposal())
        {
            Status = "Nothing to propose — link a finding with a condition first.";
            return;
        }

        Status =
            "Finding → Design rules staged for review. Apply to write fields, or Discard to keep the current draft.";
    }

    /// <returns>True when a proposal was staged.</returns>
    internal bool TryStageFindingAsDesignProposal()
    {
        var proposal = BuildFindingDesignProposalText();
        if (string.IsNullOrWhiteSpace(proposal))
            return false;
        PendingFindingDesignProposalText = proposal;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAcceptFindingDesignProposal))]
    private void AcceptFindingDesignProposal()
    {
        if (!HasPendingFindingDesignProposal) return;
        ApplyHyperionProposalToDesignFields(PendingFindingDesignProposalText);
        PendingFindingDesignProposalText = "";
        Status =
            "Applied finding into Design fields. Unresolved sizing/exit/risk stay editable — prior Validate evidence stays on earlier revisions.";
        NotifyDesignDraftChanged();
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanAcceptFindingDesignProposal))]
    private void DiscardFindingDesignProposal()
    {
        if (!HasPendingFindingDesignProposal) return;
        PendingFindingDesignProposalText = "";
        Status = "Discarded finding → Design proposal. Design fields unchanged.";
    }

    internal string? BuildFindingDesignProposalText()
    {
        var condition = ResolveConditionForDraftBind();
        if (condition is null)
            return null;

        var selection = ResolveSelectionForDraftBind();
        var instrument = selection?.CanonicalSymbol
            ?? ResearchChartInstrumentText
            ?? DesignInstrumentText;
        if (string.IsNullOrWhiteSpace(instrument))
            instrument = "(unresolved)";

        var timeframe = selection is not null
            ? selection.Timeframe.ToDisplayString()
            : DesignTimeframeText;
        if (string.IsNullOrWhiteSpace(timeframe) ||
            !DesignTimeframeOptions.Any(t => string.Equals(t, timeframe, StringComparison.Ordinal)))
        {
            timeframe = "5m";
        }

        var indicators = PendingResearchIndicatorBindings.Count > 0
            ? string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))
            : "none locked";

        var entry =
            $"When {condition.SummaryText} (condition {condition.ConditionId} · ver {condition.VersionShort}; indicators [{indicators}])";

        // Only propose fields the finding can fill. EXIT/SIZING/RISK/ORDERS stay empty
        // until the user (or Hyperion) writes real rules — do not apply "Unresolved" placeholders.
        return string.Join('\n', new[]
        {
            $"INSTRUMENT: {instrument.Trim()}",
            $"TIMEFRAME: {timeframe}",
            "EVALUATION: Completed bar",
            $"ENTRY: {entry}",
            "UNRESOLVED: exit, sizing, risk, orders — define from research or Hyperion before Build",
        });
    }

    private static void ApplyKeyedLine(string line, Action<string> assign)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return;
        var value = line[(idx + 1)..].Trim();
        // Skip empty and explicit Unresolved placeholders — leave prior field / empty draft.
        if (value.Length == 0 || IsDesignFieldUnresolved(value))
            return;
        assign(value);
    }

    private void ApplyHyperionProposalToDesignFields(string proposal)
    {
        var appliedKey = false;
        foreach (var raw in proposal.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            // Non-field notes (UNRESOLVED: …) — display-only in the proposal panel.
            if (line.StartsWith("UNRESOLVED", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("UNRESOLVED FIELDS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("INSTRUMENT", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignInstrumentText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("TIMEFRAME", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignTimeframeText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("EVALUATION", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignEvaluationTimingText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("ENTRY", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("CONDITION", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignEntryRuleText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignExitRuleText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("SIZING", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignSizingRuleText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("RISK", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignRiskRuleText = v);
                appliedKey = true;
            }
            else if (line.StartsWith("ORDERS", StringComparison.OrdinalIgnoreCase))
            {
                ApplyKeyedLine(line, v => DesignOrderRuleText = v);
                appliedKey = true;
            }
        }

        if (!appliedKey)
            DesignEntryRuleText = proposal.Trim();
    }

    public bool HasStrategyDraft => PendingStrategyDraft is not null;

    public string StrategyDraftSummaryText
    {
        get
        {
            if (PendingStrategyDraft is not { } draft)
                return "No chart strategy draft yet. On Charts, enter stop and target prices and Send draft.";

            var stop = draft.Objects.FirstOrDefault(item => item.Role == StrategyDraftObjectRoleV1.ProtectiveStop);
            var target = draft.Objects.FirstOrDefault(item => item.Role == StrategyDraftObjectRoleV1.ProfitTarget);
            return $"{draft.Scope.CanonicalSymbol} · {draft.Scope.Timeframe.ToDisplayString()} · " +
                   $"stop {FormatDraftPrice(stop?.Price)} · target {FormatDraftPrice(target?.Price)} · " +
                   (draft.IsLocked ? $"locked {draft.LockedTradeIrHashSha256![..8]}…" : "unlocked draft (not TradeIR yet)");
        }
    }

    /// <summary>
    /// Called only by the trusted host chart. Authored strategy code never receives this seam.
    /// </summary>
    public void SetStrategyDraft(StrategyDraftV1 draft)
    {
        StrategyDraftValidatorV1.RequireStructurallyValid(draft);
        PendingStrategyDraft = draft;
        EnterResearchWorkspace();
        Status = "Chart strategy draft received (stop/target). Refine in Brief when ready — this does not place orders.";
        Save();
    }

    /// <summary>
    /// Locks the pending chart draft to an exact TradeIR content hash when Builder has one active.
    /// </summary>
    public bool TryLockPendingStrategyDraft(string tradeIrHashSha256, out string message)
    {
        if (PendingStrategyDraft is not { } draft)
        {
            message = "No chart strategy draft is pending in Builder.";
            return false;
        }

        try
        {
            PendingStrategyDraft = StrategyDraftGestureApplierV1.Lock(draft, tradeIrHashSha256);
            EnterResearchWorkspace();
            message = $"Chart draft locked to TradeIR {tradeIrHashSha256[..8]}…";
            Status = message;
            Save();
            return true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return false;
        }
    }

    public bool TryLockPendingStrategyDraftToActiveTradeIr(out string message)
    {
        if (!TryResolveActiveTradeIr(out _, out var hash, out _, out _))
        {
            message = "No package-valid active TradeIR in Builder. Generate/confirm Typed Graph first, then Lock draft.";
            return false;
        }

        return TryLockPendingStrategyDraft(hash, out message);
    }

    [RelayCommand(CanExecute = nameof(CanLockPendingStrategyDraft))]
    private void LockPendingStrategyDraft()
    {
        if (!TryLockPendingStrategyDraftToActiveTradeIr(out var message))
        {
            Status = message;
            AiStatus = message;
            return;
        }

        AiStatus = Status;
        LockPendingStrategyDraftCommand.NotifyCanExecuteChanged();
    }

    private bool CanLockPendingStrategyDraft() =>
        PendingStrategyDraft is { IsLocked: false } && !IsGenerating;

    [RelayCommand]
    private void ClearStrategyDraft()
    {
        PendingStrategyDraft = null;
        Status = "Chart strategy draft cleared.";
        Save();
    }

    private void RestoreStrategyDraft(AuthoringSessionSnapshot session, ref string? restoreWarning)
    {
        PendingStrategyDraft = null;
        if (string.IsNullOrWhiteSpace(session.StrategyDraftJson)) return;

        try
        {
            PendingStrategyDraft = StrategyDraftCanonicalJsonV1.Deserialize(session.StrategyDraftJson);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Could not restore strategy draft for {Id}", session.StrategyId);
            restoreWarning = "The session was restored, but its chart strategy draft failed validation and was detached.";
        }
    }

    partial void OnPendingStrategyDraftChanged(StrategyDraftV1? value)
    {
        OnPropertyChanged(nameof(HasStrategyDraft));
        OnPropertyChanged(nameof(StrategyDraftSummaryText));
        OnPropertyChanged(nameof(CanBindResearchConditionToDraft));
        LockPendingStrategyDraftCommand.NotifyCanExecuteChanged();
        BindResearchConditionToDraftCommand.NotifyCanExecuteChanged();
    }

    private static string FormatDraftPrice(decimal? price) =>
        price is { } value ? value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) : "—";
}
