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

    [ObservableProperty] private string _designEntryRuleText = "";
    [ObservableProperty] private string _designExitRuleText = "";
    [ObservableProperty] private string _designSizingRuleText = "";
    [ObservableProperty] private string _designRiskRuleText = "";
    [ObservableProperty] private string _designOrderRuleText = "";

    public bool HasDesignRuleDraft =>
        !string.IsNullOrWhiteSpace(DesignEntryRuleText) ||
        !string.IsNullOrWhiteSpace(DesignExitRuleText) ||
        !string.IsNullOrWhiteSpace(DesignSizingRuleText) ||
        !string.IsNullOrWhiteSpace(DesignRiskRuleText) ||
        !string.IsNullOrWhiteSpace(DesignOrderRuleText);

    public string DesignRuleEditorHint =>
        HasResearchDesignHandoff
            ? "Linked Research finding is in the composer — turn it into explicit rules below."
            : "Start from rules here, or use Research Studio in the top chrome for chart evidence. Templates remain optional in the left pane.";

    public bool CanPromoteDesignRulesToRequest => HasDesignRuleDraft && !IsGenerating;

    partial void OnDesignEntryRuleTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDesignRuleDraft));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnDesignExitRuleTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDesignRuleDraft));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnDesignSizingRuleTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDesignRuleDraft));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnDesignRiskRuleTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDesignRuleDraft));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnDesignOrderRuleTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDesignRuleDraft));
        OnPropertyChanged(nameof(CanPromoteDesignRulesToRequest));
        PromoteDesignRulesToRequestCommand.NotifyCanExecuteChanged();
        NotifyWorkingFlowMapChanged();
    }

    /// <summary>
    /// Honest Design → request handoff: fills the composer with explicit rules.
    /// Does not synthesize TradeIR or skip Build — user still confirms and builds.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPromoteDesignRulesToRequest))]
    private void PromoteDesignRulesToRequest()
    {
        if (!HasDesignRuleDraft) return;
        static string Line(string key, string value) =>
            string.IsNullOrWhiteSpace(value) ? "" : $"{key}: {value.Trim()}";

        var body = string.Join('\n', new[]
        {
            "Design rules draft (explicit — not compiled TradeIR yet):",
            Line("ENTRY", DesignEntryRuleText),
            Line("EXIT", DesignExitRuleText),
            Line("SIZING", DesignSizingRuleText),
            Line("RISK", DesignRiskRuleText),
            Line("ORDERS", DesignOrderRuleText),
            "",
            "Next: confirm meaning in Request, then Build to generate/compile. Validate uses that revision hash.",
        }.Where(static s => s.Length == 0 || !string.IsNullOrWhiteSpace(s)));

        Composer = body;
        Status =
            "Design rules copied into the request composer. Confirm the request, then Build — " +
            "this does not create TradeIR by itself.";
        NotifyWorkingFlowMapChanged();
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
