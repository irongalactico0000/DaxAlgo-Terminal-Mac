using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.App.Authoring;

public sealed partial class StrategyAuthoringViewModel
{
    private IReadOnlyDictionary<string, object?>? _historicalValidationParameters;

    [ObservableProperty]
    private HistoricalValidationEvidenceV1? _historicalValidationEvidence;

    /// <summary>
    /// Simulated fills from the last accepted QuickBacktest run — in-memory only.
    /// Not part of evidence aggregates; not Nautilus fill ledger.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<ValidationChartFillV1> _lastValidationFills = Array.Empty<ValidationChartFillV1>();

    /// <summary>
    /// Design ENTRY evaluation for Validate condition triangles — same evaluator as Design preview.
    /// Not the compiled strategy kernel; not Nautilus.
    /// </summary>
    [ObservableProperty]
    private ResearchConditionSearchResultV1? _lastValidationDesignEntryResult;

    /// <summary>
    /// Validate/Replay execution fidelity. L1 touch ± slippage is always applied.
    /// Optional: quantity-capped partials per touch, opposite-L1-size cap (BidSize/AskSize proxy),
    /// and execution latency ms. Queue position and multi-level liquidity walk remain unavailable.
    /// </summary>
    [ObservableProperty] private string _executionBookType = SupportedExecutionBookType;
    [ObservableProperty] private bool _executionEnableQueuePosition;
    [ObservableProperty] private bool _executionEnableLiquidityConsumption;
    [ObservableProperty] private bool _executionEnableOppositeL1SizeCap;
    [ObservableProperty] private bool _executionEnablePartialFills;
    [ObservableProperty] private int _executionLatencyMs;
    [ObservableProperty] private string _executionFillModel = SupportedExecutionFillModel;

    /// <summary>When partial fills are on, each L1 touch fills at most this many units (demo default 4).</summary>
    public const long AppliedPartialFillMaxPerTouch = 4;

    public const string SupportedExecutionBookType = "L1 quotes (applied)";
    public const string SupportedExecutionFillModel = "L1 touch ± slippage (applied)";

    /// <summary>Only engine-applied book types — unsupported L2/L3 are not selectable.</summary>
    public IReadOnlyList<string> ExecutionBookTypeOptions { get; } = [SupportedExecutionBookType];

    /// <summary>Only engine-applied fill models — unsupported walks/queue fills are not selectable.</summary>
    public IReadOnlyList<string> ExecutionFillModelOptions { get; } = [SupportedExecutionFillModel];

    /// <summary>Queue / multi-level liquidity walk are not applied yet.</summary>
    public bool ExecutionUnsupportedOptionsAvailable => false;

    /// <summary>Partials, opposite-L1-size cap, and latency are applied by L1FillModel when enabled.</summary>
    public bool ExecutionPartialsAndLatencyAvailable => true;

    /// <summary>Opposite BidSize/AskSize fill cap is applied — not full Nautilus matching.</summary>
    public bool ExecutionOppositeL1SizeCapAvailable => true;

    public string ExecutionUnsupportedOptionsExplanation =>
        "Applied today: L1 touch ± slippage; optional capped partials (max " +
        AppliedPartialFillMaxPerTouch +
        " per touch); optional opposite-L1-size cap (BidSize/AskSize proxy); latency ms. " +
        "Not claimed: L2/L3 books, queue position, multi-level liquidity walk — those remain a later " +
        "Nautilus-class fill-fidelity target, not this Validate lane.";

    public string ExecutionPartialsTip =>
        $"When checked, each L1 touch fills at most {AppliedPartialFillMaxPerTouch} units so orders can PartiallyFilled → Filled/Cancelled.";

    public string ExecutionOppositeL1SizeCapTip =>
        "When checked, each fill is also capped by the opposite L1 size (AskSize for buys, BidSize for sells). " +
        "Zero opposite size → no fill. This is an L1 size proxy — not queue position or book walk.";

    public string ExecutionLatencyTip =>
        "Milliseconds after submit before the order may fill on L1 touches (sim clock). 0 = immediate.";
    public bool HasHistoricalValidationEvidence => HistoricalValidationEvidence is { } evidence &&
        string.Equals(
            StrategyWorkspace.Bindings.ValidationEvidenceHashSha256,
            HistoricalValidationEvidenceCanonicalJsonV1.Hash(evidence),
            StringComparison.Ordinal);
    public bool CanRunHistoricalValidation =>
        IsRegistered &&
        AuthoredUnitSpecification is not null &&
        StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null &&
        !IsGenerating &&
        TryGetAppliedExecutionFidelity(out _, out _);

    /// <summary>
    /// Attach the fixed L1 +50→+25 partial-fill/cancel demo to Saved results.
    /// Pedagogical fixture — not extracted from the historical run; not Nautilus.
    /// </summary>
    public bool CanAttachL1ExecutionLifecycleDemo =>
        !IsGenerating && TryGetAppliedExecutionFidelity(out _, out _);

    /// <summary>
    /// Lane 3 · export registered authored unit as installable <c>.daxalgostrategy</c>.
    /// Same gate family as Historical BT (registered + spec + C#), without requiring validation evidence.
    /// </summary>
    public bool CanExportOpenPackage =>
        IsRegistered &&
        AuthoredUnitSpecification is not null &&
        Files.Any(static file =>
            file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(file.Content)) &&
        !IsGenerating;
    public string HistoricalValidationStatusText => !HasHistoricalValidationEvidence
        ? "Next: Run historical validation for this exact compiled revision. Optional: Export open package… (Lane 3 · install on another Mac)."
        : $"Validated {HistoricalValidationEvidence!.FromUtc:u} → {HistoricalValidationEvidence.ToUtc:u} · " +
          $"{HistoricalValidationEvidence.TradeCount} trades · {HistoricalValidationEvidence.DataMode}. " +
          "Next: Paper → Bind selected book → Harness.";

    /// <summary>
    /// Honest chart-layer note: Design ENTRY triangles (Validate eval) vs fill circles (last-run trades).
    /// </summary>
    public string ValidationChartLayersStatusText
    {
        get
        {
            var entryHits = LastValidationDesignEntryResult?.HitCount ?? 0;
            var researchHits = ResearchConditionSearchResult?.HitCount ?? 0;
            var fillCount = LastValidationFills.Count;
            string conditionPart;
            if (entryHits > 0)
            {
                conditionPart =
                    $"{entryHits} Design ENTRY hit(s) evaluated in Validate " +
                    $"({LastValidationDesignEntryResult!.ConditionSummary}) — triangles; not compiled-kernel ENTRY";
            }
            else if (CanEvaluateValidateDesignEntry)
            {
                conditionPart =
                    "Design ENTRY is ready to evaluate in Validate (same ema/sma rules as Design preview)";
            }
            else if (researchHits > 0)
            {
                conditionPart =
                    $"{researchHits} research condition hit(s) available as fallback triangles " +
                    "(not Design ENTRY Validate eval yet)";
            }
            else
            {
                conditionPart =
                    "no Design ENTRY hits yet — set ema(n)/sma(n) ENTRY in Design, keep a Research chart setup, then Evaluate";
            }

            var fillPart = fillCount > 0
                ? $"{fillCount} simulated fill(s) from last accepted historical run (circles at price)"
                : "no simulated fills stashed — run historical validation and Accept to capture trades";
            return $"Chart layers · {conditionPart} · {fillPart}.";
        }
    }

    public bool CanEvaluateValidateDesignEntry =>
        !IsGenerating &&
        _researchConditionSearch is not null &&
        DesignEntryCondition.IsComplete &&
        DesignConditionChartPreviewEvaluatorV1.TryParseSeriesOperand(
            DesignEntryCondition.LeftOperand, out _, out _) &&
        TryGetValidationChartSelection(out _, out _, out _);

    public bool CanShowValidationConditionMarkersOnChart =>
        !IsGenerating &&
        (LastValidationDesignEntryResult is { Hits.Count: > 0 } ||
         ResearchConditionSearchResult is { Hits.Count: > 0 } ||
         CanEvaluateValidateDesignEntry);

    public bool CanShowValidationFillMarkersOnChart =>
        !IsGenerating && LastValidationFills.Count > 0;

    public bool CanShowValidationChartLayersOnChart =>
        CanShowValidationConditionMarkersOnChart || CanShowValidationFillMarkersOnChart;

    /// <summary>
    /// Honest checklist: applied engine settings only — not requested-but-ignored intent.
    /// </summary>
    public string ExecutionFidelityChecklistText
    {
        get
        {
            if (!TryGetAppliedExecutionFidelity(out var applied, out var rejection))
            {
                return
                    "Execution fidelity (Validate → Replay):\n" +
                    $"• Blocked: {rejection}\n" +
                    ExecutionUnsupportedOptionsExplanation;
            }

            var partials = applied.PartialsEnabled
                ? $"on (max {AppliedPartialFillMaxPerTouch} per L1 touch)"
                : "off — full remaining qty per touch (unless opposite-size cap)";
            var opposite = applied.OppositeL1SizeCapEnabled
                ? "on — BidSize/AskSize proxy (not queue walk)"
                : "off";
            return
                "Execution fidelity (Validate → Replay) — applied by engine:\n" +
                $"• Book / data: {applied.BookType}\n" +
                $"• Fill model: {applied.FillModel}\n" +
                $"• Queue position: off (not available)\n" +
                $"• Multi-level liquidity walk: off (not available)\n" +
                $"• Opposite L1 size cap: {opposite}\n" +
                $"• Partial fills / lifecycle: {partials}\n" +
                $"• Execution latency: {applied.LatencyMs} ms\n" +
                "• Order books in Research: live L2 windows are separate; not synchronized to this replay clock\n" +
                $"Applied report token: {applied.DataModeToken}";
        }
    }

    /// <summary>Token recorded on validation evidence DataMode when the run uses applied L1 settings.</summary>
    public string ExecutionFidelityDataModeText =>
        TryGetAppliedExecutionFidelity(out var applied, out _)
            ? applied.DataModeToken
            : "execution-fidelity-rejected";

    public readonly record struct AppliedExecutionFidelityV1(
        string BookType,
        string FillModel,
        int LatencyMs,
        bool PartialsEnabled,
        long MaxFillPerTouch,
        bool OppositeL1SizeCapEnabled,
        string DataModeToken);

    /// <summary>
    /// Returns the settings the engine will actually use, or rejects unsupported combinations.
    /// </summary>
    public bool TryGetAppliedExecutionFidelity(
        out AppliedExecutionFidelityV1 applied,
        out string rejection)
    {
        var bookOk = string.Equals(ExecutionBookType, SupportedExecutionBookType, StringComparison.Ordinal);
        var fillOk = string.Equals(ExecutionFillModel, SupportedExecutionFillModel, StringComparison.Ordinal);
        if (!bookOk || !fillOk)
        {
            applied = default;
            rejection =
                "Execution book/fill selection is not an applied engine option. " +
                $"Use {SupportedExecutionBookType} / {SupportedExecutionFillModel}.";
            return false;
        }

        if (ExecutionEnableQueuePosition || ExecutionEnableLiquidityConsumption)
        {
            applied = default;
            rejection =
                "Queue position and multi-level liquidity consumption are not available. Turn them off.";
            return false;
        }

        if (ExecutionLatencyMs < 0)
        {
            applied = default;
            rejection = "Execution latency must be ≥ 0 ms.";
            return false;
        }

        var partials = ExecutionEnablePartialFills;
        var maxFill = partials ? AppliedPartialFillMaxPerTouch : 0L;
        var opposite = ExecutionEnableOppositeL1SizeCap;
        var latency = ExecutionLatencyMs;
        applied = new AppliedExecutionFidelityV1(
            SupportedExecutionBookType,
            SupportedExecutionFillModel,
            LatencyMs: latency,
            PartialsEnabled: partials,
            MaxFillPerTouch: maxFill,
            OppositeL1SizeCapEnabled: opposite,
            DataModeToken:
                $"L1TouchFillModel|book=L1|queue=off|liq=off|oppositeSize={(opposite ? "on" : "off")}|" +
                $"partials={(partials ? $"max{maxFill}" : "off")}|latencyMs={latency}|applied");
        rejection = string.Empty;
        return true;
    }

    /// <summary>Normalize restored/legacy planned labels; keep applied partials/latency/opposite-size user choices.</summary>
    internal void CoerceLegacyExecutionFidelitySettings()
    {
        if (!string.Equals(ExecutionBookType, SupportedExecutionBookType, StringComparison.Ordinal))
            ExecutionBookType = SupportedExecutionBookType;
        if (!string.Equals(ExecutionFillModel, SupportedExecutionFillModel, StringComparison.Ordinal))
            ExecutionFillModel = SupportedExecutionFillModel;
        if (ExecutionEnableQueuePosition)
            ExecutionEnableQueuePosition = false;
        if (ExecutionEnableLiquidityConsumption)
            ExecutionEnableLiquidityConsumption = false;
        if (ExecutionLatencyMs < 0)
            ExecutionLatencyMs = 0;
    }

    partial void OnExecutionBookTypeChanged(string value) => NotifyExecutionFidelityChanged();
    partial void OnExecutionEnableQueuePositionChanged(bool value) => NotifyExecutionFidelityChanged();
    partial void OnExecutionEnableLiquidityConsumptionChanged(bool value) => NotifyExecutionFidelityChanged();
    partial void OnExecutionEnableOppositeL1SizeCapChanged(bool value) => NotifyExecutionFidelityChanged();
    partial void OnExecutionEnablePartialFillsChanged(bool value) => NotifyExecutionFidelityChanged();
    partial void OnExecutionLatencyMsChanged(int value) => NotifyExecutionFidelityChanged();
    partial void OnExecutionFillModelChanged(string value) => NotifyExecutionFidelityChanged();

    private void NotifyExecutionFidelityChanged()
    {
        OnPropertyChanged(nameof(ExecutionFidelityChecklistText));
        OnPropertyChanged(nameof(ExecutionFidelityDataModeText));
        OnPropertyChanged(nameof(CanRunHistoricalValidation));
        OnPropertyChanged(nameof(CanAttachL1ExecutionLifecycleDemo));
        AttachL1ExecutionLifecycleDemoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAttachL1ExecutionLifecycleDemo))]
    private void AttachL1ExecutionLifecycleDemo()
    {
        if (!CanAttachL1ExecutionLifecycleDemo) return;

        var report = L1ExecutionLifecycleFixtureV1.RunTarget50PartialFillCancel();
        UpsertExecutionLifecycleResult(
            L1ExecutionLifecycleFixtureV1.ReportId,
            L1ExecutionLifecycleFixtureV1.Summary(report),
            L1ExecutionLifecycleFixtureV1.Serialize(report));
        if (OpenValidateScreenCommand.CanExecute(null))
            OpenValidateScreenCommand.Execute(null);
        else
            ActiveScreen = StrategyAuthoringScreen.Validate;
        Status =
            $"Attached L1 lifecycle demo · {L1ExecutionLifecycleFixtureV1.Summary(report)}. " +
            "Fixed fixture — not from your historical run; queue/liquidity/Nautilus not claimed.";
        NotifyWorkingFlowMapChanged();
    }

    /// <summary>
    /// Actionable blocker for Charts shell ⑤ / Validate. Lock draft ≠ historical-ready.
    /// </summary>
    public string DescribeHistoricalValidationBlocker()
    {
        if (IsGenerating)
            return "Wait for generation to finish before historical validation.";
        if (IsRegistered &&
            AuthoredUnitSpecification is not null &&
            StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null &&
            StrategyWorkspace.Bindings.AuthoredUnitSpecificationHashSha256 is not null)
        {
            var registration = _strategyKernelRegistry?.Find(AuthoredUnitSpecification.UnitId);
            if (registration is null)
                return "The compiled unit is not in the strategy registry. Confirm Register again, then retry Historical BT.";
            return string.Empty;
        }

        if (!CanCompileCurrentSource && AuthoredUnitSpecification is null)
            return "Historical BT needs a compiled authored unit. In Builder: generate/lower to C# → Compile → Register, then retry Historical BT. (TradeIR smoke ≠ historical.)";
        if (CanCompileCurrentSource && !IsRegistered)
            return "Compile and Confirm Register in Builder (Build/Review), then retry Historical BT from Charts.";
        if (IsRegistered && StrategyWorkspace.Bindings.BuildArtifactHashSha256 is null)
            return "Registration is incomplete (missing build artifact hash). Re-compile and Register, then retry.";

        return "Compile, review, and register this exact authored strategy before historical validation.";
    }

    public bool TryCreateHistoricalValidationContext(
        out HistoricalValidationContextV1? context,
        out string reason)
    {
        SynchronizeStrategyWorkspace();
        if (!TryGetAppliedExecutionFidelity(out _, out var executionRejection))
        {
            context = null;
            reason = executionRejection;
            return false;
        }

        var specificationHash = StrategyWorkspace.Bindings.AuthoredUnitSpecificationHashSha256;
        var buildHash = StrategyWorkspace.Bindings.BuildArtifactHashSha256;
        if (!IsRegistered || AuthoredUnitSpecification is null || specificationHash is null || buildHash is null)
        {
            context = null;
            reason = DescribeHistoricalValidationBlocker();
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Compile, review, and register this exact authored strategy before historical validation.";
            return false;
        }

        var registration = _strategyKernelRegistry?.Find(AuthoredUnitSpecification.UnitId);
        if (registration is null || !string.Equals(
                AuthoredUnitSpecificationCanonicalJsonV1.Hash(registration.AuthoredSpecification),
                specificationHash,
                StringComparison.Ordinal))
        {
            context = null;
            reason = "The exact compiled strategy is no longer present in the canonical strategy registry.";
            return false;
        }

        context = new HistoricalValidationContextV1(
            StrategyWorkspace.WorkspaceId,
            specificationHash,
            buildHash,
            StrategyWorkspace.Bindings.FeatureSetHashSha256);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Opens the Builder screen that unblocks historical validation (Build or Validate).
    /// </summary>
    public void FocusHistoricalValidationPrep()
    {
        if (OpenValidateScreenCommand.CanExecute(null))
            OpenValidateScreenCommand.Execute(null);
        else if (OpenBuildScreenCommand.CanExecute(null))
            OpenBuildScreenCommand.Execute(null);
        else if (OpenBriefScreenCommand.CanExecute(null))
            OpenBriefScreenCommand.Execute(null);
    }

    /// <summary>
    /// Charts shell ⑤ assist: open Build, auto-Compile when source is ready, leave Register
    /// for the user. Never calls <see cref="ConfirmRegisterCommand"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when review overlay is open and waiting for Register;
    /// <c>false</c> when compile could not run or registration is still incomplete.
    /// </returns>
    public bool PrepareHistoricalValidationAssist()
    {
        if (CanRunHistoricalValidation &&
            TryCreateHistoricalValidationContext(out _, out _))
            return false;

        // Prefer Build when Compile/Register is still required; Validate is only useful after register.
        if (!IsRegistered || StrategyWorkspace.Bindings.BuildArtifactHashSha256 is null)
        {
            if (OpenBuildScreenCommand.CanExecute(null))
                OpenBuildScreenCommand.Execute(null);
            else
                FocusHistoricalValidationPrep();
        }
        else
        {
            FocusHistoricalValidationPrep();
        }

        if (CanCompileCurrentSource &&
            CompileCommand.CanExecute(null) &&
            !ReviewOpen)
        {
            CompileCommand.Execute(null);
            if (ReviewOpen)
            {
                Status =
                    "Compiled for Historical BT — review the code, then press Register. Retry Charts ⑤ after Register.";
                return true;
            }
        }

        if (ReviewOpen)
        {
            Status =
                "Review the compiled code, then press Register. Retry Charts ⑤ after Register.";
            return true;
        }

        if (string.IsNullOrWhiteSpace(Status))
            Status = DescribeHistoricalValidationBlocker();
        return false;
    }

    public bool AcceptHistoricalValidationEvidence(
        HistoricalValidationEvidenceV1 evidence,
        IReadOnlyDictionary<string, object?> testedParameters,
        out string reason) =>
        AcceptHistoricalValidationEvidence(evidence, testedParameters, trades: null, out reason);

    public bool AcceptHistoricalValidationEvidence(
        HistoricalValidationEvidenceV1 evidence,
        IReadOnlyDictionary<string, object?> testedParameters,
        IEnumerable<TradingTerminal.Core.Backtest.Trade>? trades,
        out string reason)
    {
        try
        {
            HistoricalValidationEvidenceValidatorV1.RequireValid(evidence);
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }

        if (!TryCreateHistoricalValidationContext(out var current, out reason) || current != evidence.Context)
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "The workspace changed while historical validation was running. The stale result was discarded."
                : reason;
            return false;
        }

        HistoricalValidationEvidence = evidence;
        _historicalValidationParameters = testedParameters.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        LastValidationFills = ValidationChartFillMapperV1.FromTrades(trades);
        NotifyValidationChartLayersChanged();
        var evidenceHash = HistoricalValidationEvidenceCanonicalJsonV1.Hash(evidence);
        StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Revise(
            StrategyWorkspace,
            StrategyWorkspaceChangeKindV1.ValidationEvidence,
            StrategyWorkspace.Bindings with { ValidationEvidenceHashSha256 = evidenceHash },
            StrategyWorkspaceStageV1.Validate,
            revisionReason: "Exact historical validation completed");
        UpsertHistoricalValidationResult(evidence);
        Status =
            "Historical validation is bound to this exact compiled revision. " +
            "The report is in this strategy’s saved results — reopen it from the list without typing paths." +
            (LastValidationFills.Count > 0
                ? $" · {LastValidationFills.Count} fill marker(s) ready for chart (separate from condition layer)."
                : string.Empty);
        Save();
        reason = string.Empty;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanEvaluateValidateDesignEntry))]
    private async Task EvaluateValidateDesignEntryOnChartAsync()
    {
        var result = await RunValidateDesignEntryEvaluationAsync().ConfigureAwait(true);
        if (result is null)
            return;

        PublishValidationConditionHits(result, fillHits: null);
        Status =
            result.HitCount == 0
                ? $"Validate Design ENTRY: no hits · {result.ConditionSummary} · {result.Note}"
                : $"Validate Design ENTRY: {result.HitCount} triangle(s) · {result.ConditionSummary}. " +
                  "Same evaluator as Design preview — not compiled-kernel ENTRY; fills stay a separate layer.";
    }

    [RelayCommand(CanExecute = nameof(CanShowValidationConditionMarkersOnChart))]
    private async Task ShowValidationConditionMarkersOnChartAsync()
    {
        var result = await ResolveValidationConditionResultAsync().ConfigureAwait(true);
        if (result is null || result.Hits.Count == 0)
        {
            Status =
                CanEvaluateValidateDesignEntry
                    ? "Validate Design ENTRY evaluated with no hits on the linked chart bars."
                    : "No condition hits to show — evaluate Design ENTRY or run a research condition search first.";
            return;
        }

        PublishValidationConditionHits(result, fillHits: null);
        var source = ReferenceEquals(result, LastValidationDesignEntryResult)
            ? "Validate Design ENTRY eval"
            : "research condition search (fallback)";
        Status =
            $"Condition layer: {result.HitCount} triangle(s) · {source}. " +
            "Separate from fill circles; not compiled-kernel ENTRY.";
    }

    [RelayCommand(CanExecute = nameof(CanShowValidationFillMarkersOnChart))]
    private void ShowValidationFillMarkersOnChart()
    {
        if (LastValidationFills.Count == 0)
            return;

        var symbol = LastValidationDesignEntryResult?.Symbol
            ?? ResearchConditionSearchResult?.Symbol
            ?? PendingResearchChartSelection?.CanonicalSymbol
            ?? ResearchChartInstrumentText;
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                OverlaysForDesignPreview().ToArray(),
                preferredSymbol: symbol,
                fillHits: LastValidationFills));
        Status =
            $"Fill layer: {LastValidationFills.Count} circle marker(s) at simulated prices. " +
            "Separate from condition triangles; sourced from last accepted QuickBacktest trades.";
    }

    [RelayCommand(CanExecute = nameof(CanShowValidationChartLayersOnChart))]
    private async Task ShowValidationChartLayersOnChartAsync()
    {
        var conditionResult = await ResolveValidationConditionResultAsync().ConfigureAwait(true);
        var conditionHits = conditionResult is { Hits.Count: > 0 } ? conditionResult.Hits : null;
        var fillHits = LastValidationFills.Count > 0 ? LastValidationFills : null;
        if (conditionHits is null && fillHits is null)
            return;

        var symbol = conditionResult?.Symbol
            ?? PendingResearchChartSelection?.CanonicalSymbol
            ?? ResearchChartInstrumentText;
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                OverlaysForDesignPreview().ToArray(),
                preferredSymbol: symbol,
                conditionHits: conditionHits,
                fillHits: fillHits));
        Status =
            $"Both layers: {(conditionHits?.Count ?? 0)} Design ENTRY/research triangle(s) + " +
            $"{(fillHits?.Count ?? 0)} fill circle(s). ENTRY eval ≠ compiled kernel; fills = last-run trades.";
    }

    private async Task<ResearchConditionSearchResultV1?> ResolveValidationConditionResultAsync()
    {
        if (CanEvaluateValidateDesignEntry)
        {
            var evaluated = await RunValidateDesignEntryEvaluationAsync().ConfigureAwait(true);
            if (evaluated is not null)
                return evaluated;
        }

        if (LastValidationDesignEntryResult is { Hits.Count: > 0 } entry)
            return entry;
        if (ResearchConditionSearchResult is { Hits.Count: > 0 } research)
            return research;
        return null;
    }

    private async Task<ResearchConditionSearchResultV1?> RunValidateDesignEntryEvaluationAsync()
    {
        if (_researchConditionSearch is null || !DesignEntryCondition.IsComplete)
            return null;
        if (!TryGetValidationChartSelection(out var instrumentId, out var symbol, out var timeframe))
        {
            Status =
                "Validate Design ENTRY needs a linked Research chart selection (instrument + timeframe). " +
                "Keep a setup on Research, then return to Validate.";
            return null;
        }

        try
        {
            var result = await _researchConditionSearch.SearchDesignOperandsLocalAsync(
                    DesignEntryCondition.LeftOperand,
                    DesignEntryCondition.OperatorKey,
                    DesignEntryCondition.RightOperand,
                    instrumentId,
                    symbol,
                    timeframe)
                .ConfigureAwait(true);
            LastValidationDesignEntryResult = result;
            NotifyValidationChartLayersChanged();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Validate Design ENTRY evaluation failed");
            Status = $"Validate Design ENTRY failed: {ex.Message}";
            return null;
        }
    }

    private bool TryGetValidationChartSelection(
        out TradingTerminal.Core.Domain.InstrumentId instrumentId,
        out string symbol,
        out TradingTerminal.Core.Domain.BarSize timeframe)
    {
        ResearchChartSelectionV1? selection = PendingResearchChartSelection
            ?? ResearchDatasetDefinition?.Samples.LastOrDefault()?.Selection;
        if (selection is null || selection.InstrumentId.IsNone)
        {
            instrumentId = default;
            symbol = string.Empty;
            timeframe = default;
            return false;
        }

        instrumentId = selection.InstrumentId;
        symbol = selection.CanonicalSymbol;
        timeframe = selection.Timeframe;
        return true;
    }

    private void PublishValidationConditionHits(
        ResearchConditionSearchResultV1 result,
        IReadOnlyList<ValidationChartFillV1>? fillHits)
    {
        HostChartOverlayPreviewRequested?.Invoke(
            this,
            new HostChartOverlayPreviewRequestedEventArgs(
                OverlaysForDesignPreview().ToArray(),
                preferredSymbol: result.Symbol,
                conditionHits: result.Hits,
                fillHits: fillHits));
    }

    private void NotifyValidationChartLayersChanged()
    {
        OnPropertyChanged(nameof(ValidationChartLayersStatusText));
        OnPropertyChanged(nameof(CanEvaluateValidateDesignEntry));
        OnPropertyChanged(nameof(CanShowValidationConditionMarkersOnChart));
        OnPropertyChanged(nameof(CanShowValidationFillMarkersOnChart));
        OnPropertyChanged(nameof(CanShowValidationChartLayersOnChart));
        EvaluateValidateDesignEntryOnChartCommand.NotifyCanExecuteChanged();
        ShowValidationConditionMarkersOnChartCommand.NotifyCanExecuteChanged();
        ShowValidationFillMarkersOnChartCommand.NotifyCanExecuteChanged();
        ShowValidationChartLayersOnChartCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyDictionary<string, object?>? ValidatedPaperParameters =>
        HasHistoricalValidationEvidence ? _historicalValidationParameters : null;

    public bool BindValidatedPaperBook(string bookId, string accountId, out string reason)
    {
        if (HistoricalValidationEvidence is not { } evidence ||
            StrategyWorkspace.Bindings.ValidationEvidenceHashSha256 is not { } validationHash ||
            !string.Equals(validationHash, HistoricalValidationEvidenceCanonicalJsonV1.Hash(evidence), StringComparison.Ordinal))
        {
            reason = "Run exact historical validation for the current revision before selecting a Paper book.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(accountId))
        {
            reason = "A selected Paper book and account are required.";
            return false;
        }

        var paperHash = StrategyWorkspaceCanonicalJsonV1.HashArtifact(new PaperBindingV1(
            validationHash,
            bookId.Trim(),
            accountId.Trim()));
        StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Revise(
            StrategyWorkspace,
            StrategyWorkspaceChangeKindV1.PaperBinding,
            StrategyWorkspace.Bindings with { PaperBindingHashSha256 = paperHash },
            StrategyWorkspaceStageV1.Paper,
            revisionReason: "Validated strategy approved for selected Paper book");
        Status = "The validated revision is bound to the selected Paper book. Next: Harness opens to run Paper. Real-money routing remains unavailable.";
        Save();
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Snapshot for Lane 3 open-package export. Caller writes via shell exporter + file picker.
    /// </summary>
    public bool TryGetOpenPackageExport(
        out AuthoredUnitSpecificationV1 specification,
        out IReadOnlyList<StrategyFile> sources,
        out string reason)
    {
        specification = null!;
        sources = Array.Empty<StrategyFile>();
        if (!IsRegistered)
        {
            reason = "Compile and Confirm Register before exporting an open package.";
            return false;
        }

        if (AuthoredUnitSpecification is not { } spec)
        {
            reason = "An authored unit specification is required to export an open package.";
            return false;
        }

        if (IsGenerating)
        {
            reason = "Wait for generation to finish before exporting.";
            return false;
        }

        var cs = Files
            .Where(static file =>
                file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(file.Content))
            .Select(static file => new StrategyFile(file.Name, file.Content))
            .ToArray();
        if (cs.Length == 0)
        {
            reason = "Export needs at least one C# source file in the Builder workspace.";
            return false;
        }

        specification = spec;
        sources = cs;
        reason = string.Empty;
        return true;
    }

    partial void OnHistoricalValidationEvidenceChanged(HistoricalValidationEvidenceV1? value)
    {
        if (value is null)
            LastValidationFills = Array.Empty<ValidationChartFillV1>();
        OnPropertyChanged(nameof(HasHistoricalValidationEvidence));
        OnPropertyChanged(nameof(HistoricalValidationStatusText));
        OnPropertyChanged(nameof(CanRunHistoricalValidation));
        NotifyValidationChartLayersChanged();
        NotifyAuthoringScreenStateChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnLastValidationFillsChanged(IReadOnlyList<ValidationChartFillV1> value) =>
        NotifyValidationChartLayersChanged();

    partial void OnLastValidationDesignEntryResultChanged(ResearchConditionSearchResultV1? value) =>
        NotifyValidationChartLayersChanged();

    partial void OnIsRegisteredChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExportOpenPackage));
        OnPropertyChanged(nameof(CanRunHistoricalValidation));
        NotifyWorkingFlowMapChanged();
    }

    private sealed record PaperBindingV1(string ValidationEvidenceHashSha256, string BookId, string AccountId);
}
