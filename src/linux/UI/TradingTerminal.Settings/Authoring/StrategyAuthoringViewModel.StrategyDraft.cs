using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
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
    [ObservableProperty] private DesignValueProvenance _designInstrumentProvenance = DesignValueProvenance.Unset;
    [ObservableProperty] private DesignValueProvenance _designTimeframeProvenance = DesignValueProvenance.Unset;
    [ObservableProperty] private string _newDesignIndicatorKind = "ema";
    [ObservableProperty] private string _newDesignIndicatorPeriodText = "20";

    /// <summary>True while Accept applies a proposal — skips marking edits as Operator.</summary>
    private bool _applyingDesignProposal;

    /// <summary>True while syncing freeform summaries from structured form controls.</summary>
    private bool _syncingStructuredRuleText;

    public ObservableCollection<DesignIndicatorRow> DesignIndicators { get; } = [];

    public DesignConditionRow DesignEntryCondition { get; } = new();
    public DesignConditionRow DesignExitCondition { get; } = new();
    public DesignSizingForm DesignSizing { get; } = new();
    public DesignRiskForm DesignRisk { get; } = new();
    public DesignOrdersForm DesignOrders { get; } = new();

    public IReadOnlyList<string> DesignIndicatorKindOptions { get; } =
        ["ema", "sma", "rsi", "macd", "bb"];

    public IReadOnlyList<string> DesignConditionOperatorOptions =>
        DesignConditionRow.OperatorOptions;

    public IReadOnlyList<string> DesignSizingMethodOptions => DesignSizingForm.MethodOptions;
    public IReadOnlyList<string> DesignSizingUnitOptions => DesignSizingForm.UnitOptions;
    public IReadOnlyList<string> DesignRiskUnitOptions => DesignRiskForm.UnitOptions;
    public IReadOnlyList<string> DesignOrderTypeOptions => DesignOrdersForm.OrderTypeOptions;
    public IReadOnlyList<string> DesignTimeInForceOptions => DesignOrdersForm.TimeInForceOptions;
    public IReadOnlyList<string> DesignPriceRuleOptions => DesignOrdersForm.PriceRuleOptions;

    public IReadOnlyList<string> DesignTimeframeOptions { get; } =
        ["", "1m", "5m", "15m", "1h", "1D"];

    public IReadOnlyList<string> DesignEvaluationTimingOptions { get; } =
        ["", "Completed bar", "Forming bar", "Session open"];

    public bool HasDesignIndicators => DesignIndicators.Count > 0;

    public bool HasDesignRuleDraft =>
        !string.IsNullOrWhiteSpace(DesignInstrumentText) ||
        !string.IsNullOrWhiteSpace(DesignTimeframeText) ||
        !string.IsNullOrWhiteSpace(DesignEvaluationTimingText) ||
        !string.IsNullOrWhiteSpace(DesignEntryRuleText) ||
        !string.IsNullOrWhiteSpace(DesignExitRuleText) ||
        !string.IsNullOrWhiteSpace(DesignSizingRuleText) ||
        !string.IsNullOrWhiteSpace(DesignRiskRuleText) ||
        !string.IsNullOrWhiteSpace(DesignOrderRuleText) ||
        HasDesignIndicators ||
        DesignEntryCondition.IsComplete;

    public string DesignInstrumentProvenanceLabel =>
        DesignValueProvenanceLabels.Label(DesignInstrumentProvenance);

    public string DesignTimeframeProvenanceLabel =>
        DesignValueProvenanceLabels.Label(DesignTimeframeProvenance);

    public string DesignIndicatorsEmptyHint =>
        "Optional. Add indicators the rules reference, or reuse Research chart bindings. " +
        "Adding an indicator does not create an entry rule.";

    public bool HasPendingHyperionDesignProposal =>
        !string.IsNullOrWhiteSpace(PendingHyperionDesignProposalText);

    public string DesignRuleEditorHint =>
        HasPendingFindingDesignProposal
            ? "Review the finding → Design rules proposal below. Apply writes fields; Discard keeps your draft."
            : HasPendingHyperionDesignProposal
            ? "Review Hyperion’s proposal. Accept writes the same fields and structured indicators/conditions you can edit."
            : HasResearchDesignHandoff
            ? "Linked research is below — Apply finding (review first), add indicators/conditions, or Ask Hyperion. Chat and controls share one draft."
            : "Chat progressively specifies the strategy; Accept applies proposals into these controls. Edit the same rules here — do not retype what Hyperion already proposed.";

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

    public bool CanAddDesignIndicator => !IsGenerating;

    public bool CanImportResearchIndicatorsToDesign =>
        !IsGenerating && PendingResearchIndicatorBindings.Count > 0;

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
            if (IsDesignFieldUnresolved(DesignEntryRuleText) && !DesignEntryCondition.IsComplete)
                missing.Add("entry condition");
            if (IsDesignFieldUnresolved(DesignExitRuleText) && !DesignExitCondition.IsComplete)
                missing.Add("exit");
            if (IsDesignFieldUnresolved(DesignSizingRuleText) && !DesignSizing.IsComplete)
                missing.Add("sizing");
            if (IsDesignFieldUnresolved(DesignRiskRuleText) && !DesignRisk.IsComplete)
                missing.Add("risk");
            if (IsDesignFieldUnresolved(DesignOrderRuleText) && !DesignOrders.IsComplete)
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

            var indicators = DesignIndicators.Count == 0
                ? "INDICATORS: (none — optional)"
                : "INDICATORS: " + string.Join(", ", DesignIndicators.Select(static i => i.DisplayLabel));
            var condition = DesignEntryCondition.IsComplete
                ? $"CONDITION: {DesignEntryCondition.SummaryText}"
                : "CONDITION: (unresolved)";
            var exit = DesignExitCondition.IsComplete
                ? $"EXIT: {DesignExitCondition.SummaryText}"
                : Line("EXIT", DesignExitRuleText);
            var sizing = DesignSizing.IsComplete
                ? $"SIZING: {DesignSizing.SummaryText}"
                : Line("SIZING", DesignSizingRuleText);
            var risk = DesignRisk.IsComplete
                ? $"RISK: {DesignRisk.SummaryText}"
                : Line("RISK", DesignRiskRuleText);
            var orders = DesignOrders.IsComplete
                ? $"ORDERS: {DesignOrders.SummaryText}"
                : Line("ORDERS", DesignOrderRuleText);

            return string.Join('\n', new[]
            {
                "Working strategy draft (shared by Design, Hyperion Accept, and Build):",
                Line("INSTRUMENT", DesignInstrumentText) +
                    ProvenanceSuffix(DesignInstrumentProvenance),
                Line("TIMEFRAME", DesignTimeframeText) +
                    ProvenanceSuffix(DesignTimeframeProvenance),
                Line("EVALUATION", DesignEvaluationTimingText),
                indicators,
                condition,
                Line("ENTRY", DesignEntryRuleText),
                exit,
                sizing,
                risk,
                orders,
                "",
                DesignUnresolvedChecklistText,
                "Changing EMA 20 → EMA 30 here must be what Build uses for the next revision.",
            });
        }
    }

    private static string ProvenanceSuffix(DesignValueProvenance provenance)
    {
        var label = DesignValueProvenanceLabels.Label(provenance);
        return string.IsNullOrEmpty(label) ? "" : $" · {label}";
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
        OnPropertyChanged(nameof(HasDesignIndicators));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        OnPropertyChanged(nameof(CanReviewDesignRules));
        OnPropertyChanged(nameof(CanImportResearchIndicatorsToDesign));
        OnPropertyChanged(nameof(DesignRulesReviewText));
        OnPropertyChanged(nameof(DesignUnresolvedChecklistText));
        OnPropertyChanged(nameof(DesignInstrumentProvenanceLabel));
        OnPropertyChanged(nameof(DesignTimeframeProvenanceLabel));
        OnPropertyChanged(nameof(DesignRuleEditorHint));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        ReviewDesignRulesCommand.NotifyCanExecuteChanged();
        ImportResearchIndicatorsToDesignCommand.NotifyCanExecuteChanged();
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

    partial void OnDesignInstrumentTextChanged(string value)
    {
        if (!_applyingDesignProposal && !_restoring)
            DesignInstrumentProvenance = DesignValueProvenance.Operator;
        NotifyDesignDraftChanged();
    }

    partial void OnDesignTimeframeTextChanged(string value)
    {
        if (!_applyingDesignProposal && !_restoring)
            DesignTimeframeProvenance = DesignValueProvenance.Operator;
        NotifyDesignDraftChanged();
    }

    partial void OnDesignEvaluationTimingTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignEntryRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignExitRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignSizingRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignRiskRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignOrderRuleTextChanged(string value) => NotifyDesignDraftChanged();
    partial void OnDesignInstrumentProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(DesignInstrumentProvenanceLabel));
    partial void OnDesignTimeframeProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(DesignTimeframeProvenanceLabel));

    partial void OnPendingHyperionDesignProposalTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPendingHyperionDesignProposal));
        OnPropertyChanged(nameof(CanAcceptHyperionDesignProposal));
        OnPropertyChanged(nameof(DesignRuleEditorHint));
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

        var indicatorLine = DesignIndicators.Count == 0
            ? ""
            : "INDICATORS: " + string.Join(", ", DesignIndicators.Select(static i => i.DisplayLabel));
        var conditionLine = DesignEntryCondition.IsComplete
            ? $"CONDITION: {DesignEntryCondition.SummaryText}"
            : "";
        var exitLine = DesignExitCondition.IsComplete
            ? $"EXIT: {DesignExitCondition.SummaryText}"
            : Line("EXIT", DesignExitRuleText);
        var sizingLine = DesignSizing.IsComplete
            ? $"SIZING: {DesignSizing.SummaryText}"
            : Line("SIZING", DesignSizingRuleText);
        var riskLine = DesignRisk.IsComplete
            ? $"RISK: {DesignRisk.SummaryText}"
            : Line("RISK", DesignRiskRuleText);
        var ordersLine = DesignOrders.IsComplete
            ? $"ORDERS: {DesignOrders.SummaryText}"
            : Line("ORDERS", DesignOrderRuleText);

        var body = string.Join('\n', new[]
        {
            "Design rules draft (composer prompt — Hyperion may reinterpret on Send):",
            Line("INSTRUMENT", DesignInstrumentText),
            Line("TIMEFRAME", DesignTimeframeText),
            Line("EVALUATION", DesignEvaluationTimingText),
            indicatorLine,
            conditionLine,
            Line("ENTRY", DesignEntryRuleText),
            exitLine,
            sizingLine,
            riskLine,
            ordersLine,
            "",
            "Reply with the same keys. Prefer INDICATORS / CONDITION / EXIT conditions,",
            "SIZING: target 10 shares (range 5-15), RISK: max loss 1% · daily stop 2%, ORDERS: Market IOC.",
            "Distinguish requested values from suggested defaults. Operator must Accept before Design fields change.",
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
        ApplyHyperionProposalToDesignFields(
            PendingHyperionDesignProposalText,
            DesignValueProvenance.HyperionAccepted);
        PendingHyperionDesignProposalText = "";
        AwaitingHyperionDesignProposal = false;
        Status =
            "Accepted Hyperion proposal into Design fields and structured controls. " +
            "Edit indicators/conditions here — next chat reads this draft. Review before Build.";
        NotifyDesignDraftChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAcceptHyperionDesignProposal))]
    private void DiscardHyperionDesignProposal()
    {
        PendingHyperionDesignProposalText = "";
        AwaitingHyperionDesignProposal = false;
        Status = "Discarded Hyperion proposal. Design fields unchanged.";
    }

    [RelayCommand(CanExecute = nameof(CanAddDesignIndicator))]
    private void AddDesignIndicator()
    {
        if (!int.TryParse(NewDesignIndicatorPeriodText.Trim(), out var period) || period <= 0)
            period = 20;
        var kind = string.IsNullOrWhiteSpace(NewDesignIndicatorKind) ? "ema" : NewDesignIndicatorKind.Trim();
        var row = DesignIndicatorRow.Create(kind, period, DesignValueProvenance.Operator);
        if (DesignIndicators.Any(i =>
                string.Equals(i.Kind, row.Kind, StringComparison.OrdinalIgnoreCase) &&
                i.Period == row.Period))
        {
            Status = $"{row.DisplayLabel} is already on the Design draft.";
            return;
        }

        DesignIndicators.Add(row);
        Status = $"Added {row.DisplayLabel} to Design. It is available for conditions — not an entry rule until you set one.";
        NotifyDesignDraftChanged();
    }

    [RelayCommand]
    private void RemoveDesignIndicator(DesignIndicatorRow? row)
    {
        if (row is null) return;
        if (!DesignIndicators.Remove(row)) return;
        Status = $"Removed {row.DisplayLabel} from Design indicators.";
        NotifyDesignDraftChanged();
    }

    /// <summary>
    /// Copy Research chart bindings into Design as available indicators — does not invent an entry rule.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportResearchIndicatorsToDesign))]
    private void ImportResearchIndicatorsToDesign()
    {
        var added = ImportResearchIndicatorsAsAvailable();
        Status = added == 0
            ? "No new Research indicators to import (already on Design or none linked)."
            : $"Imported {added} Research indicator(s) into Design as available — entry rule unchanged.";
        NotifyDesignDraftChanged();
    }

    internal int ImportResearchIndicatorsAsAvailable()
    {
        var added = 0;
        foreach (var binding in PendingResearchIndicatorBindings)
        {
            if (DesignIndicators.Any(i =>
                    string.Equals(i.BindingId, binding.BindingId, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(i.Kind, binding.Kind, StringComparison.OrdinalIgnoreCase) &&
                     i.Period == binding.Period)))
            {
                continue;
            }

            DesignIndicators.Add(
                DesignIndicatorRow.FromBinding(binding, DesignValueProvenance.ResearchAvailable));
            added++;
        }

        return added;
    }

    /// <summary>Wire once from ctor so structured form edits refresh summary strings.</summary>
    internal void AttachDesignStructureChangeHandlers()
    {
        DesignIndicators.CollectionChanged -= OnDesignIndicatorsCollectionChanged;
        DesignIndicators.CollectionChanged += OnDesignIndicatorsCollectionChanged;
        DesignEntryCondition.PropertyChanged -= OnDesignEntryConditionPropertyChanged;
        DesignEntryCondition.PropertyChanged += OnDesignEntryConditionPropertyChanged;
        DesignExitCondition.PropertyChanged -= OnDesignExitConditionPropertyChanged;
        DesignExitCondition.PropertyChanged += OnDesignExitConditionPropertyChanged;
        DesignSizing.PropertyChanged -= OnDesignSizingPropertyChanged;
        DesignSizing.PropertyChanged += OnDesignSizingPropertyChanged;
        DesignRisk.PropertyChanged -= OnDesignRiskPropertyChanged;
        DesignRisk.PropertyChanged += OnDesignRiskPropertyChanged;
        DesignOrders.PropertyChanged -= OnDesignOrdersPropertyChanged;
        DesignOrders.PropertyChanged += OnDesignOrdersPropertyChanged;
    }

    private void OnDesignIndicatorsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyDesignDraftChanged();

    private void OnDesignEntryConditionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignConditionRow.LeftOperand) or
            nameof(DesignConditionRow.OperatorKey) or
            nameof(DesignConditionRow.RightOperand))
        {
            if (!_applyingDesignProposal && !_restoring && !_syncingStructuredRuleText)
                DesignEntryCondition.Provenance = DesignValueProvenance.Operator;
            SyncEntryRuleTextFromCondition();
        }

        NotifyDesignDraftChanged();
    }

    private void OnDesignExitConditionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignConditionRow.LeftOperand) or
            nameof(DesignConditionRow.OperatorKey) or
            nameof(DesignConditionRow.RightOperand))
        {
            if (!_applyingDesignProposal && !_restoring && !_syncingStructuredRuleText)
                DesignExitCondition.Provenance = DesignValueProvenance.Operator;
            SyncExitRuleTextFromCondition();
        }

        NotifyDesignDraftChanged();
    }

    private void OnDesignSizingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignSizingForm.QuantityText) or
            nameof(DesignSizingForm.Unit) or
            nameof(DesignSizingForm.Method) or
            nameof(DesignSizingForm.RangeMinText) or
            nameof(DesignSizingForm.RangeMaxText))
        {
            if (!_applyingDesignProposal && !_restoring && !_syncingStructuredRuleText)
                DesignSizing.Provenance = DesignValueProvenance.Operator;
            SyncSizingRuleTextFromForm();
        }

        NotifyDesignDraftChanged();
    }

    private void OnDesignRiskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignRiskForm.MaxLossText) or
            nameof(DesignRiskForm.MaxLossUnit) or
            nameof(DesignRiskForm.DailyStopText) or
            nameof(DesignRiskForm.StopRangeMinText) or
            nameof(DesignRiskForm.StopRangeMaxText))
        {
            if (!_applyingDesignProposal && !_restoring && !_syncingStructuredRuleText)
                DesignRisk.Provenance = DesignValueProvenance.Operator;
            SyncRiskRuleTextFromForm();
        }

        NotifyDesignDraftChanged();
    }

    private void OnDesignOrdersPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignOrdersForm.OrderType) or
            nameof(DesignOrdersForm.TimeInForce) or
            nameof(DesignOrdersForm.PriceRule))
        {
            if (!_applyingDesignProposal && !_restoring && !_syncingStructuredRuleText)
                DesignOrders.Provenance = DesignValueProvenance.Operator;
            SyncOrderRuleTextFromForm();
        }

        NotifyDesignDraftChanged();
    }

    private void SyncEntryRuleTextFromCondition()
    {
        if (!DesignEntryCondition.IsComplete) return;
        _syncingStructuredRuleText = true;
        try
        {
            DesignEntryRuleText = DesignEntryCondition.SummaryText;
        }
        finally
        {
            _syncingStructuredRuleText = false;
        }
    }

    private void SyncExitRuleTextFromCondition()
    {
        if (!DesignExitCondition.IsComplete) return;
        _syncingStructuredRuleText = true;
        try
        {
            DesignExitRuleText = DesignExitCondition.SummaryText;
        }
        finally
        {
            _syncingStructuredRuleText = false;
        }
    }

    private void SyncSizingRuleTextFromForm()
    {
        if (!DesignSizing.IsComplete) return;
        _syncingStructuredRuleText = true;
        try
        {
            DesignSizingRuleText = DesignSizing.SummaryText;
        }
        finally
        {
            _syncingStructuredRuleText = false;
        }
    }

    private void SyncRiskRuleTextFromForm()
    {
        if (!DesignRisk.IsComplete) return;
        _syncingStructuredRuleText = true;
        try
        {
            DesignRiskRuleText = DesignRisk.SummaryText;
        }
        finally
        {
            _syncingStructuredRuleText = false;
        }
    }

    private void SyncOrderRuleTextFromForm()
    {
        if (!DesignOrders.IsComplete) return;
        _syncingStructuredRuleText = true;
        try
        {
            DesignOrderRuleText = DesignOrders.SummaryText;
        }
        finally
        {
            _syncingStructuredRuleText = false;
        }
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
        ApplyHyperionProposalToDesignFields(
            PendingFindingDesignProposalText,
            DesignValueProvenance.HyperionAccepted);
        ImportResearchIndicatorsAsAvailable();
        PendingFindingDesignProposalText = "";
        Status =
            "Applied finding into Design fields. Research indicators are available (not auto-entry). " +
            "Unresolved sizing/exit/risk stay editable.";
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

        var indicatorLabels = PendingResearchIndicatorBindings.Count > 0
            ? string.Join(", ", PendingResearchIndicatorBindings.Select(static b => b.DisplayLabel))
            : "";
        var indicatorsLine = string.IsNullOrEmpty(indicatorLabels)
            ? null
            : $"INDICATORS: {indicatorLabels}";

        var entry =
            $"When {condition.SummaryText} (condition {condition.ConditionId} · ver {condition.VersionShort}" +
            (string.IsNullOrEmpty(indicatorLabels) ? "" : $"; indicators [{indicatorLabels}]") +
            ")";

        // Only propose fields the finding can fill. EXIT/SIZING/RISK/ORDERS stay empty
        // until the user (or Hyperion) writes real rules — do not apply "Unresolved" placeholders.
        // INDICATORS are listed for reuse — Apply imports them as available, not as entry rules alone.
        return string.Join('\n', new[]
        {
            $"INSTRUMENT: {instrument.Trim()}",
            $"TIMEFRAME: {timeframe}",
            "EVALUATION: Completed bar",
            indicatorsLine,
            $"ENTRY: {entry}",
            "UNRESOLVED: exit, sizing, risk, orders — define from research or Hyperion before Build",
        }.Where(static s => !string.IsNullOrWhiteSpace(s))!);
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

    private void ApplyHyperionProposalToDesignFields(
        string proposal,
        DesignValueProvenance provenance = DesignValueProvenance.HyperionAccepted)
    {
        _applyingDesignProposal = true;
        try
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

                if (line.StartsWith("INDICATORS", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyIndicatorsProposalLine(line, provenance);
                    appliedKey = true;
                }
                else if (line.StartsWith("CONDITION", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyConditionProposalLine(line, provenance);
                    appliedKey = true;
                }
                else if (line.StartsWith("INSTRUMENT", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignInstrumentText = v;
                        DesignInstrumentProvenance = provenance;
                    });
                    appliedKey = true;
                }
                else if (line.StartsWith("TIMEFRAME", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignTimeframeText = v;
                        DesignTimeframeProvenance = provenance;
                    });
                    appliedKey = true;
                }
                else if (line.StartsWith("EVALUATION", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v => DesignEvaluationTimingText = v);
                    appliedKey = true;
                }
                else if (line.StartsWith("ENTRY", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignEntryRuleText = v;
                        TryParseConditionFromFreeText(v, DesignEntryCondition, provenance);
                    });
                    appliedKey = true;
                }
                else if (line.StartsWith("EXIT", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignExitRuleText = v;
                        if (!TryParseConditionFromFreeText(v, DesignExitCondition, provenance))
                            DesignExitCondition.Clear();
                        SyncExitRuleTextFromCondition();
                    });
                    appliedKey = true;
                }
                else if (line.StartsWith("SIZING", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignSizingRuleText = v;
                        TryParseSizingFromFreeText(v, provenance);
                    });
                    appliedKey = true;
                }
                else if (line.StartsWith("RISK", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignRiskRuleText = v;
                        TryParseRiskFromFreeText(v, provenance);
                    });
                    appliedKey = true;
                }
                else if (line.StartsWith("ORDERS", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyKeyedLine(line, v =>
                    {
                        DesignOrderRuleText = v;
                        TryParseOrdersFromFreeText(v, provenance);
                    });
                    appliedKey = true;
                }
            }

            if (!appliedKey)
                DesignEntryRuleText = proposal.Trim();
        }
        finally
        {
            _applyingDesignProposal = false;
        }
    }

    private void ApplyIndicatorsProposalLine(string line, DesignValueProvenance provenance)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return;
        var value = line[(idx + 1)..].Trim();
        if (value.Length == 0 || IsDesignFieldUnresolved(value)) return;

        foreach (Match match in IndicatorTokenRegex().Matches(value))
        {
            var kind = match.Groups["kind"].Value;
            var periodText = match.Groups["period"].Value;
            if (!int.TryParse(periodText, out var period) || period <= 0)
                continue;
            UpsertDesignIndicator(kind, period, provenance);
        }
    }

    private void ApplyConditionProposalLine(string line, DesignValueProvenance provenance)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return;
        var value = line[(idx + 1)..].Trim();
        if (value.Length == 0 || IsDesignFieldUnresolved(value)) return;
        if (!TryParseConditionFromFreeText(value, DesignEntryCondition, provenance))
            DesignEntryRuleText = value;
    }

    private bool TryParseConditionFromFreeText(
        string text,
        DesignConditionRow target,
        DesignValueProvenance provenance)
    {
        var match = ConditionPhraseRegex().Match(text);
        if (!match.Success) return false;

        var leftKind = match.Groups["leftKind"].Value;
        var leftPeriod = int.Parse(match.Groups["leftPeriod"].Value);
        var op = match.Groups["op"].Value.Trim().ToLowerInvariant();
        var rightKind = match.Groups["rightKind"].Value;
        var rightPeriod = int.Parse(match.Groups["rightPeriod"].Value);

        UpsertDesignIndicator(leftKind, leftPeriod, provenance);
        UpsertDesignIndicator(rightKind, rightPeriod, provenance);

        target.LeftOperand = $"{leftKind}({leftPeriod})";
        target.OperatorKey = NormalizeConditionOperator(op);
        target.RightOperand = $"{rightKind}({rightPeriod})";
        target.Provenance = provenance;
        if (ReferenceEquals(target, DesignEntryCondition))
            SyncEntryRuleTextFromCondition();
        else if (ReferenceEquals(target, DesignExitCondition))
            SyncExitRuleTextFromCondition();
        return true;
    }

    private void TryParseSizingFromFreeText(string text, DesignValueProvenance provenance)
    {
        var qty = SizingQuantityRegex().Match(text);
        if (!qty.Success) return;

        DesignSizing.QuantityText = qty.Groups["qty"].Value;
        var unit = qty.Groups["unit"].Value.Trim().ToLowerInvariant();
        DesignSizing.Unit = unit switch
        {
            "%" or "percent" or "pct" => "%",
            "contract" or "contracts" => "contracts",
            _ => "shares",
        };
        DesignSizing.Method = DesignSizing.Unit == "%"
            ? "Percent of equity"
            : DesignSizing.Unit == "contracts"
                ? "Fixed contracts"
                : "Target quantity";

        var range = SizingRangeRegex().Match(text);
        if (range.Success)
        {
            DesignSizing.RangeMinText = range.Groups["min"].Value;
            DesignSizing.RangeMaxText = range.Groups["max"].Value;
        }

        DesignSizing.Provenance = provenance;
        SyncSizingRuleTextFromForm();
    }

    private void TryParseRiskFromFreeText(string text, DesignValueProvenance provenance)
    {
        var daily = RiskDailyStopRegex().Match(text);
        if (daily.Success)
        {
            DesignRisk.DailyStopText = daily.Groups["value"].Value;
            if (daily.Groups["unit"].Success && !string.IsNullOrWhiteSpace(daily.Groups["unit"].Value))
                DesignRisk.MaxLossUnit = NormalizeRiskUnit(daily.Groups["unit"].Value);
        }

        var maxLoss = RiskMaxLossRegex().Match(text);
        if (maxLoss.Success)
        {
            DesignRisk.MaxLossText = maxLoss.Groups["value"].Value;
            if (maxLoss.Groups["unit"].Success && !string.IsNullOrWhiteSpace(maxLoss.Groups["unit"].Value))
                DesignRisk.MaxLossUnit = NormalizeRiskUnit(maxLoss.Groups["unit"].Value);
        }
        else if (!DesignRisk.HasMaxLoss && text.Contains('%', StringComparison.Ordinal))
        {
            var pct = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*%");
            if (pct.Success)
            {
                DesignRisk.MaxLossText = pct.Groups[1].Value;
                DesignRisk.MaxLossUnit = "%";
            }
        }

        var stopRange = RiskStopRangeRegex().Match(text);
        if (stopRange.Success)
        {
            DesignRisk.StopRangeMinText = stopRange.Groups["min"].Value;
            DesignRisk.StopRangeMaxText = stopRange.Groups["max"].Value;
        }

        if (DesignRisk.IsComplete)
        {
            DesignRisk.Provenance = provenance;
            SyncRiskRuleTextFromForm();
        }
    }

    private void TryParseOrdersFromFreeText(string text, DesignValueProvenance provenance)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("limit", StringComparison.Ordinal))
            DesignOrders.OrderType = "Limit";
        else if (lower.Contains("market", StringComparison.Ordinal))
            DesignOrders.OrderType = "Market";

        if (lower.Contains("ioc", StringComparison.Ordinal))
            DesignOrders.TimeInForce = "IOC";
        else if (lower.Contains("fok", StringComparison.Ordinal))
            DesignOrders.TimeInForce = "FOK";
        else if (lower.Contains("gtc", StringComparison.Ordinal))
            DesignOrders.TimeInForce = "GTC";
        else if (lower.Contains("day", StringComparison.Ordinal))
            DesignOrders.TimeInForce = "Day";

        foreach (var rule in DesignOrdersForm.PriceRuleOptions)
        {
            if (string.IsNullOrEmpty(rule)) continue;
            if (lower.Contains(rule, StringComparison.Ordinal))
            {
                DesignOrders.PriceRule = rule;
                break;
            }
        }

        if (DesignOrders.IsComplete)
        {
            DesignOrders.Provenance = provenance;
            SyncOrderRuleTextFromForm();
        }
    }

    private static string NormalizeRiskUnit(string unit) =>
        unit.Trim().ToLowerInvariant() switch
        {
            "%" or "percent" or "pct" => "%",
            "usd" or "$" => "USD",
            _ => "currency units",
        };

    private void UpsertDesignIndicator(string kind, int period, DesignValueProvenance provenance)
    {
        var existing = DesignIndicators.FirstOrDefault(i =>
            string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase) && i.Period == period);
        if (existing is not null)
        {
            if (existing.Provenance == DesignValueProvenance.ResearchAvailable &&
                provenance == DesignValueProvenance.HyperionAccepted)
            {
                existing.Provenance = provenance;
            }

            return;
        }

        DesignIndicators.Add(DesignIndicatorRow.Create(kind, period, provenance));
    }

    private static string NormalizeConditionOperator(string op) => op switch
    {
        "crosses above" or "cross above" or "crosses over" => "crosses above",
        "crosses below" or "cross below" or "crosses under" => "crosses below",
        "is above" or "above" or ">" => "is above",
        "is below" or "below" or "<" => "is below",
        "equals" or "=" or "==" => "equals",
        _ => op,
    };

    [GeneratedRegex(
        @"(?<kind>[A-Za-z]+)[\s(]*(?<period>\d+)\)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndicatorTokenRegex();

    [GeneratedRegex(
        @"(?<leftKind>[A-Za-z]+)\s*\(?\s*(?<leftPeriod>\d+)\s*\)?\s+(?<op>crosses\s+above|crosses\s+below|is\s+above|is\s+below|equals|crosses\s+over|crosses\s+under|>|<|=)\s+(?<rightKind>[A-Za-z]+)\s*\(?\s*(?<rightPeriod>\d+)\s*\)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConditionPhraseRegex();

    [GeneratedRegex(
        @"(?:target\s+)?(?<qty>\d+(?:\.\d+)?)\s*(?<unit>shares?|contracts?|%|percent|pct)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizingQuantityRegex();

    [GeneratedRegex(
        @"range\s*(?:of\s*)?(?<min>\d+(?:\.\d+)?)\s*[-–to]+\s*(?<max>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizingRangeRegex();

    [GeneratedRegex(
        @"daily\s*stop\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>%|USD|\$|percent)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskDailyStopRegex();

    [GeneratedRegex(
        @"max(?:imum)?\s*loss\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>%|USD|\$|percent)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskMaxLossRegex();

    [GeneratedRegex(
        @"stop\s*range\s*(?<min>\d+(?:\.\d+)?)\s*[-–to]+\s*(?<max>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskStopRangeRegex();

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
