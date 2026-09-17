using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// Validate/Replay execution fidelity. L1 touch ± slippage is always applied.
    /// Optional: quantity-capped partials per touch and execution latency ms.
    /// Queue position and liquidity consumption remain unavailable.
    /// </summary>
    [ObservableProperty] private string _executionBookType = SupportedExecutionBookType;
    [ObservableProperty] private bool _executionEnableQueuePosition;
    [ObservableProperty] private bool _executionEnableLiquidityConsumption;
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

    /// <summary>Queue / liquidity walk are not applied yet.</summary>
    public bool ExecutionUnsupportedOptionsAvailable => false;

    /// <summary>Partials and latency are applied by L1TouchFillModel / SimulatedOrderBook when enabled.</summary>
    public bool ExecutionPartialsAndLatencyAvailable => true;

    public string ExecutionUnsupportedOptionsExplanation =>
        "Applied today: L1 touch ± slippage; optional capped partials (max " +
        AppliedPartialFillMaxPerTouch +
        " per touch) and latency ms. " +
        "Not claimed: L2/L3 books, queue position, liquidity walk — those are a later Nautilus-class " +
        "fill-fidelity target, not this Validate lane.";

    public string ExecutionPartialsTip =>
        $"When checked, each L1 touch fills at most {AppliedPartialFillMaxPerTouch} units so orders can PartiallyFilled → Filled/Cancelled.";

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
                : "off — full remaining qty per touch";
            return
                "Execution fidelity (Validate → Replay) — applied by engine:\n" +
                $"• Book / data: {applied.BookType}\n" +
                $"• Fill model: {applied.FillModel}\n" +
                $"• Queue position: off (not available)\n" +
                $"• Liquidity consumption: off (not available)\n" +
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
                "Queue position and liquidity consumption are not available. Turn them off.";
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
        var latency = ExecutionLatencyMs;
        applied = new AppliedExecutionFidelityV1(
            SupportedExecutionBookType,
            SupportedExecutionFillModel,
            LatencyMs: latency,
            PartialsEnabled: partials,
            MaxFillPerTouch: maxFill,
            DataModeToken:
                $"L1TouchFillModel|book=L1|queue=off|liq=off|partials={(partials ? $"max{maxFill}" : "off")}|latencyMs={latency}|applied");
        rejection = string.Empty;
        return true;
    }

    /// <summary>Normalize restored/legacy planned labels; keep applied partials/latency user choices.</summary>
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
            "The report is in this strategy’s saved results — reopen it from the list without typing paths.";
        Save();
        reason = string.Empty;
        return true;
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
        OnPropertyChanged(nameof(HasHistoricalValidationEvidence));
        OnPropertyChanged(nameof(HistoricalValidationStatusText));
        OnPropertyChanged(nameof(CanRunHistoricalValidation));
        NotifyAuthoringScreenStateChanged();
        NotifyWorkingFlowMapChanged();
    }

    partial void OnIsRegisteredChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExportOpenPackage));
        OnPropertyChanged(nameof(CanRunHistoricalValidation));
        NotifyWorkingFlowMapChanged();
    }

    private sealed record PaperBindingV1(string ValidationEvidenceHashSha256, string BookId, string AccountId);
}
