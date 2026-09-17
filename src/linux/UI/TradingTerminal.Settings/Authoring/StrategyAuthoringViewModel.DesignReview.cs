using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Design → Build review gate (not a numbered stage) + structured risk limits.
/// Review binds to a content hash of the Design draft; edits invalidate acceptance.
/// </summary>
public sealed partial class StrategyAuthoringViewModel
{
    public ObservableCollection<DesignRiskLimitRow> DesignRiskLimits { get; } = [];

    public ObservableCollection<DesignReviewItemV1> DesignReviewItems { get; } = [];

    [ObservableProperty] private bool _showDesignReviewPanel;
    [ObservableProperty] private bool _designReviewWarningsAccepted;
    [ObservableProperty] private string? _acceptedDesignReviewHashSha256;
    [ObservableProperty] private string _designReviewDraftHashSha256 = "";
    [ObservableProperty] private string _designFocusedSection = "";
    [ObservableProperty] private string _newRiskLimitType = "Daily loss";
    [ObservableProperty] private string _newRiskLimitValueText = "";
    [ObservableProperty] private string _newRiskLimitUnit = "% of equity";
    [ObservableProperty] private string _newRiskLimitScope = "This strategy";
    [ObservableProperty] private string _newRiskLimitAction = "Stop new entries";

    public IReadOnlyList<string> DesignRiskLimitTypeOptions => DesignRiskLimitRow.TypeOptions;
    public IReadOnlyList<string> DesignRiskLimitUnitOptions => DesignRiskLimitRow.UnitOptions;
    public IReadOnlyList<string> DesignRiskLimitScopeOptions => DesignRiskLimitRow.ScopeOptions;
    public IReadOnlyList<string> DesignRiskLimitActionOptions => DesignRiskLimitRow.ActionOptions;

    public bool HasDesignRiskLimits => DesignRiskLimits.Count > 0;

    public bool DesignReviewHasRequired =>
        DesignReviewItems.Any(static i => i.Severity == DesignReviewSeverityV1.Required);

    public bool DesignReviewHasWarnings =>
        DesignReviewItems.Any(static i => i.Severity == DesignReviewSeverityV1.Warning);

    public bool IsDesignReviewCurrent =>
        !string.IsNullOrWhiteSpace(AcceptedDesignReviewHashSha256) &&
        string.Equals(AcceptedDesignReviewHashSha256, DesignReviewDraftHashSha256, StringComparison.Ordinal);

    public bool CanContinueDesignToBuild =>
        !IsGenerating &&
        ShowDesignReviewPanel &&
        !string.IsNullOrWhiteSpace(DesignReviewDraftHashSha256) &&
        !DesignReviewHasRequired &&
        (!DesignReviewHasWarnings || DesignReviewWarningsAccepted);

    public string DesignReviewStatusText
    {
        get
        {
            if (!ShowDesignReviewPanel)
                return "Review & continue opens a checklist inside Design (not a separate stage).";
            if (string.IsNullOrWhiteSpace(DesignReviewDraftHashSha256))
                return "No draft hash yet.";
            var shortHash = DesignReviewDraftHashSha256.Length >= 12
                ? DesignReviewDraftHashSha256[..12] + "…"
                : DesignReviewDraftHashSha256;
            if (DesignReviewHasRequired)
                return $"Draft {shortHash} · required items block Continue to Build.";
            if (DesignReviewHasWarnings && !DesignReviewWarningsAccepted)
                return $"Draft {shortHash} · accept warnings to Continue to Build.";
            if (IsDesignReviewCurrent)
                return $"Draft {shortHash} · review accepted for this exact content.";
            return $"Draft {shortHash} · ready to Continue to Build.";
        }
    }

    public string DesignReviewEvidenceCaption =>
        string.IsNullOrWhiteSpace(DesignReviewDraftHashSha256)
            ? ""
            : $"Bound to draft hash {DesignReviewDraftHashSha256[..Math.Min(16, DesignReviewDraftHashSha256.Length)]}…";

    private bool _designRiskLimitsWired;

    private void EnsureDesignRiskLimitsWired()
    {
        if (_designRiskLimitsWired) return;
        _designRiskLimitsWired = true;
        DesignRiskLimits.CollectionChanged += OnDesignRiskLimitsChanged;
    }

    private void OnDesignRiskLimitsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (DesignRiskLimitRow row in e.OldItems)
                row.PropertyChanged -= OnDesignRiskLimitRowPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (DesignRiskLimitRow row in e.NewItems)
                row.PropertyChanged += OnDesignRiskLimitRowPropertyChanged;
        }

        OnPropertyChanged(nameof(HasDesignRiskLimits));
        NotifyDesignDraftChanged();
    }

    private void OnDesignRiskLimitRowPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        NotifyDesignDraftChanged();

    public DesignDraftCanonicalV1 CaptureDesignDraftCanonical()
    {
        var linked = HasResearchDesignHandoff
            ? (_researchAnalysisReferences.Values.FirstOrDefault()?.SummaryText
               ?? _researchAnalysisReferences.Keys.Select(ResearchFindingDisplayLabel).FirstOrDefault()
               ?? "linked finding")
            : null;

        return new DesignDraftCanonicalV1(
            Instrument: DesignInstrumentText?.Trim() ?? "",
            Timeframe: DesignTimeframeText?.Trim() ?? "",
            EvaluationTiming: DesignEvaluationTimingText?.Trim() ?? "",
            Indicators: DesignIndicators.Select(static i => i.CanonicalToken).ToArray(),
            EntrySummary: DesignEntryCondition.IsComplete
                ? DesignEntryCondition.SummaryText
                : NonUnresolved(DesignEntryRuleText),
            ExitSummary: DesignExitCondition.IsComplete
                ? DesignExitCondition.SummaryText
                : NonUnresolved(DesignExitRuleText),
            SizingSummary: DesignSizing.IsComplete
                ? DesignSizing.SummaryText
                : NonUnresolved(DesignSizingRuleText),
            RiskLimits: DesignRiskLimits.Select(static r => r.ToCanonical()).ToArray(),
            OrdersSummary: DesignOrders.IsComplete
                ? DesignOrders.SummaryText
                : NonUnresolved(DesignOrderRuleText),
            LinkedFindingId: linked,
            EntryNotes: NullIfBlank(DesignEntryNotesText),
            ExitNotes: NullIfBlank(DesignExitNotesText),
            RiskNotes: NullIfBlank(DesignRiskRuleText));
    }

    public DesignReviewResultV1 EvaluateDesignDraftReview()
    {
        var result = DesignDraftReviewEvaluatorV1.Evaluate(CaptureDesignDraftCanonical());
        DesignReviewDraftHashSha256 = result.DraftHashSha256;
        DesignReviewItems.Clear();
        foreach (var item in result.Items)
            DesignReviewItems.Add(item);
        OnPropertyChanged(nameof(DesignReviewHasRequired));
        OnPropertyChanged(nameof(DesignReviewHasWarnings));
        OnPropertyChanged(nameof(CanContinueDesignToBuild));
        OnPropertyChanged(nameof(DesignReviewStatusText));
        OnPropertyChanged(nameof(DesignReviewEvidenceCaption));
        OnPropertyChanged(nameof(IsDesignReviewCurrent));
        ContinueDesignToBuildCommand.NotifyCanExecuteChanged();
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanReviewDesignRules))]
    private void ReviewDesignRules()
    {
        if (!HasDesignRuleDraft) return;
        EnsureDesignRiskLimitsWired();
        ShowDesignReviewPanel = true;
        DesignReviewWarningsAccepted = false;
        EvaluateDesignDraftReview();
        Status =
            "Review & continue — fix required items or accept warnings, then Continue to Build. " +
            "Editing the draft invalidates this review.";
        NotifyWorkingFlowMapChanged();
    }

    [RelayCommand]
    private void FocusDesignReviewItem(DesignReviewItemV1? item)
    {
        if (item is null) return;
        DesignFocusedSection = item.FocusField;
        ShowDesignReviewPanel = true;
        Status = $"Go to {item.Title}: {item.Detail}";
    }

    [RelayCommand]
    private void GoToFirstMissingDesignReviewItem()
    {
        var first = DesignReviewItems.FirstOrDefault(static i =>
            i.Severity is DesignReviewSeverityV1.Required or DesignReviewSeverityV1.Warning);
        if (first is null)
        {
            Status = "No missing Design items — Continue to Build when ready.";
            return;
        }

        FocusDesignReviewItem(first);
    }

    [RelayCommand(CanExecute = nameof(CanContinueDesignToBuild))]
    private void ContinueDesignToBuild()
    {
        if (!CanContinueDesignToBuild) return;
        AcceptedDesignReviewHashSha256 = DesignReviewDraftHashSha256;
        OnPropertyChanged(nameof(IsDesignReviewCurrent));

        // Carry the exact Design draft (incl. ORDERS side + 시장가/지정가) into Build brief/composer.
        SeedBuildBriefFromDesignDraft();

        Status =
            "Design review accepted for this exact draft hash. Build brief includes ORDERS/side. " +
            "Edit Design again and you must Review & continue once more.";
        ShowDesignReviewPanel = false;
        if (OpenBuildScreenCommand.CanExecute(null))
            OpenBuildScreenCommand.Execute(null);
        else
            ActiveScreen = StrategyAuthoringScreen.Build;
        NotifyWorkingFlowMapChanged();
        NotifyBuildDesignBlockerStateChanged();
    }

    /// <summary>
    /// Puts the live Design draft into the four-lane brief and Composer so Build generation
    /// sees Side · Market/Limit · TIF without retyping.
    /// </summary>
    private void SeedBuildBriefFromDesignDraft()
    {
        var draft = BuildDesignDraftContextBlock();
        if (string.IsNullOrWhiteSpace(draft))
            return;

        _fourLaneStrategyBrief = string.IsNullOrWhiteSpace(_fourLaneStrategyBrief)
            ? draft
            : CombineFourLaneStrategyBrief(_fourLaneStrategyBrief, draft);

        Composer = AttachDesignDraftContextToChatPrompt(
            "Build a runnable Paper strategy from this Design draft. " +
            "Preserve ORDERS (side, 시장가/지정가, TIF) and RISK limits exactly.");
    }

    [RelayCommand]
    private void AddDesignRiskLimit()
    {
        EnsureDesignRiskLimitsWired();
        if (!decimal.TryParse(NewRiskLimitValueText.Trim(), out var value) || value <= 0)
        {
            Status = "Risk limit value must be a number > 0.";
            return;
        }

        var row = new DesignRiskLimitRow
        {
            Type = string.IsNullOrWhiteSpace(NewRiskLimitType) ? "Daily loss" : NewRiskLimitType.Trim(),
            ValueText = NewRiskLimitValueText.Trim(),
            Unit = NewRiskLimitUnit,
            Scope = NewRiskLimitScope,
            Action = NewRiskLimitAction,
            Provenance = DesignValueProvenance.Operator,
        };
        DesignRiskLimits.Add(row);
        NewRiskLimitValueText = "";
        DesignFocusedSection = "risk";
        Status = $"Added risk limit: {row.SummaryText}";
    }

    [RelayCommand]
    private void RemoveDesignRiskLimit(DesignRiskLimitRow? row)
    {
        if (row is null) return;
        if (!DesignRiskLimits.Remove(row)) return;
        Status = "Removed risk limit.";
    }

    private void InvalidateDesignReviewAcceptance()
    {
        if (_restoring) return;

        if (!string.IsNullOrWhiteSpace(AcceptedDesignReviewHashSha256))
        {
            AcceptedDesignReviewHashSha256 = null;
            OnPropertyChanged(nameof(IsDesignReviewCurrent));
        }

        DesignReviewWarningsAccepted = false;
        if (ShowDesignReviewPanel)
            EvaluateDesignDraftReview();
        else
        {
            // Keep hash current for gating even when panel is closed.
            DesignReviewDraftHashSha256 = DesignDraftReviewEvaluatorV1.HashDraft(CaptureDesignDraftCanonical());
            OnPropertyChanged(nameof(DesignReviewStatusText));
            OnPropertyChanged(nameof(CanContinueDesignToBuild));
            OnPropertyChanged(nameof(IsDesignReviewCurrent));
            ContinueDesignToBuildCommand.NotifyCanExecuteChanged();
        }
    }

    private void MigrateLegacyRiskFormIntoLimitsIfNeeded()
    {
        if (DesignRiskLimits.Count > 0) return;
        if (DesignRisk.HasMaxLoss)
        {
            DesignRiskLimits.Add(new DesignRiskLimitRow
            {
                Type = "Maximum loss",
                ValueText = DesignRisk.MaxLossText.Trim(),
                Unit = DesignRisk.MaxLossUnit.Contains('%', StringComparison.Ordinal)
                    ? "% of equity"
                    : "account currency",
                Scope = "This strategy",
                Action = "Exit position",
                Provenance = DesignRisk.Provenance == DesignValueProvenance.Unset
                    ? DesignValueProvenance.Operator
                    : DesignRisk.Provenance,
            });
        }

        if (DesignRisk.HasDailyStop)
        {
            DesignRiskLimits.Add(new DesignRiskLimitRow
            {
                Type = "Daily loss",
                ValueText = DesignRisk.DailyStopText.Trim(),
                Unit = DesignRisk.MaxLossUnit.Contains('%', StringComparison.Ordinal)
                    ? "% of equity"
                    : "account currency",
                Scope = "This strategy",
                Action = "Stop new entries",
                Provenance = DesignRisk.Provenance == DesignValueProvenance.Unset
                    ? DesignValueProvenance.Operator
                    : DesignRisk.Provenance,
            });
        }
    }

    internal string? SerializeDesignRiskLimitsForSession()
    {
        if (DesignRiskLimits.Count == 0) return null;
        var rows = DesignRiskLimits.Select(static r => r.ToSession()).ToArray();
        return JsonSerializer.Serialize(rows);
    }

    internal void RestoreDesignRiskLimitsFromSession(string? json)
    {
        EnsureDesignRiskLimitsWired();
        foreach (var row in DesignRiskLimits.ToArray())
            DesignRiskLimits.Remove(row);

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var sessions = JsonSerializer.Deserialize<DesignRiskLimitSessionV1[]>(json);
                if (sessions is { Length: > 0 })
                {
                    foreach (var session in sessions)
                        DesignRiskLimits.Add(DesignRiskLimitRow.FromSession(session));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not restore Design risk limits");
            }
        }

        MigrateLegacyRiskFormIntoLimitsIfNeeded();
    }

    partial void OnShowDesignReviewPanelChanged(bool value)
    {
        OnPropertyChanged(nameof(CanContinueDesignToBuild));
        OnPropertyChanged(nameof(DesignReviewStatusText));
        ContinueDesignToBuildCommand.NotifyCanExecuteChanged();
    }

    partial void OnDesignReviewDraftHashSha256Changed(string value)
    {
        OnPropertyChanged(nameof(IsDesignReviewCurrent));
        OnPropertyChanged(nameof(DesignReviewStatusText));
        OnPropertyChanged(nameof(DesignReviewEvidenceCaption));
        OnPropertyChanged(nameof(CanContinueDesignToBuild));
        ContinueDesignToBuildCommand.NotifyCanExecuteChanged();
    }

    partial void OnDesignReviewWarningsAcceptedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanContinueDesignToBuild));
        OnPropertyChanged(nameof(DesignReviewStatusText));
        ContinueDesignToBuildCommand.NotifyCanExecuteChanged();
    }

    partial void OnAcceptedDesignReviewHashSha256Changed(string? value)
    {
        OnPropertyChanged(nameof(IsDesignReviewCurrent));
        NotifyBuildDesignBlockerStateChanged();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NonUnresolved(string? value) =>
        IsDesignFieldUnresolved(value) ? "" : value!.Trim();
}
