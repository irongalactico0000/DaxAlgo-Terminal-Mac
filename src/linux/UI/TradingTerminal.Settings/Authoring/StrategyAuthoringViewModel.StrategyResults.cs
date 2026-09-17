using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Session-local catalog of Build/Validate/Compare results for this strategy version.
/// Primary UX: pick a saved result — do not retype run IDs or report paths.
/// </summary>
public sealed partial class StrategyAuthoringViewModel
{
    public ObservableCollection<StrategyVersionResultItem> StrategyVersionResults { get; } = [];

    [ObservableProperty] private StrategyVersionResultItem? _selectedStrategyVersionResult;

    [ObservableProperty] private string? _boundNativeRunId;

    [ObservableProperty] private string? _boundNativeSessionId;

    /// <summary>True when Design is incomplete or review is not accepted for the current draft hash.</summary>
    public bool ShowBuildDesignBlockers =>
        IsBuildScreen &&
        GenerateCandidateFirst &&
        (!IsDesignReviewCurrent ||
         string.IsNullOrWhiteSpace(DesignInstrumentText) ||
         (!DesignEntryCondition.IsComplete && string.IsNullOrWhiteSpace(DesignEntryRuleText)));

    public string BuildDesignBlockerText
    {
        get
        {
            if (!IsDesignReviewCurrent)
            {
                return "Design review is not accepted for this exact draft. " +
                       "Return to Design → Review & continue → Continue to Build.";
            }

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(DesignInstrumentText))
                missing.Add("Entry instrument missing");
            if (!DesignEntryCondition.IsComplete && string.IsNullOrWhiteSpace(DesignEntryRuleText))
                missing.Add("Entry rule missing");
            if (IsDesignFieldUnresolved(DesignExitRuleText) && !DesignExitCondition.IsComplete)
                missing.Add("Exit unresolved");
            if (missing.Count == 0)
                return DesignUnresolvedChecklistText;
            return string.Join(". ", missing) + ". Return to Design before treating this Build as ready.";
        }
    }

    public bool HasStrategyVersionResults => StrategyVersionResults.Count > 0;

    public string StrategyVersionResultsEmptyText =>
        "No saved results for this strategy yet. After Build registers a version or Validate completes, results appear here automatically.";

    partial void OnSelectedStrategyVersionResultChanged(StrategyVersionResultItem? value)
    {
        if (value is null || _restoring) return;
        OpenStrategyVersionResult(value);
    }

    [RelayCommand]
    private void OpenStrategyVersionResult(StrategyVersionResultItem? item)
    {
        if (item is null) return;

        switch (item.Kind)
        {
            case StrategyVersionResultKind.HistoricalValidate:
                if (!string.IsNullOrWhiteSpace(item.EvidenceJson))
                {
                    try
                    {
                        HistoricalValidationEvidence =
                            HistoricalValidationEvidenceCanonicalJsonV1.Deserialize(item.EvidenceJson);
                        Status =
                            $"Reopened validation report · {item.Title} · {item.StatusLabel}. " +
                            "Tied to build " + (item.BuildArtifactHashSha256 is { Length: >= 12 } h
                                ? h[..12] + "…"
                                : "unknown") + ".";
                    }
                    catch (Exception ex)
                    {
                        Status = $"Could not reopen validation report: {ex.Message}";
                    }
                }

                if (OpenValidateScreenCommand.CanExecute(null))
                    OpenValidateScreenCommand.Execute(null);
                break;

            case StrategyVersionResultKind.NativeCompare:
                if (!string.IsNullOrWhiteSpace(item.NativeRunId))
                {
                    BoundNativeRunId = item.NativeRunId;
                    BoundNativeSessionId = item.NativeSessionId;
                    NativeRunId = item.NativeRunId;
                    NativeRunLoadStatus =
                        $"Selected saved compare run {item.NativeRunId}. Load run to refresh live status from the native service.";
                    if (LoadNativeRunCommand.CanExecute(null))
                        _ = LoadNativeRunCommand.ExecuteAsync(null);
                }

                if (OpenBuildScreenCommand.CanExecute(null))
                    OpenBuildScreenCommand.Execute(null);
                break;

            case StrategyVersionResultKind.ExecutionLifecycle:
                if (OpenValidateScreenCommand.CanExecute(null))
                    OpenValidateScreenCommand.Execute(null);
                else
                    ActiveScreen = StrategyAuthoringScreen.Validate;
                Status =
                    $"Reopened execution report · {item.Title} · {item.Summary}. " +
                    "L1FillModel only — queue/liquidity/Nautilus matching are not claimed.";
                break;
        }

        NotifyWorkingFlowMapChanged();
    }

    [RelayCommand(CanExecute = nameof(ShowBuildDesignBlockers))]
    private void ReturnToDesignFromBuild()
    {
        ActiveScreen = StrategyAuthoringScreen.Design;
        WorkbenchTab = 3;
        Status = BuildDesignBlockerText;
        NotifyAuthoringScreenStateChanged();
    }

    public void UpsertHistoricalValidationResult(HistoricalValidationEvidenceV1 evidence)
    {
        var hash = HistoricalValidationEvidenceCanonicalJsonV1.Hash(evidence);
        var title = $"{DisplayName} · historical validate";
        var summary =
            $"{evidence.FromUtc:u} → {evidence.ToUtc:u} · {evidence.TradeCount} trades · {evidence.DataMode} · " +
            $"build {evidence.Context.BuildArtifactHashSha256[..12]}…";
        UpsertResult(new StrategyVersionResultItem(
            StrategyVersionResultKind.HistoricalValidate,
            hash,
            title,
            "completed",
            summary,
            evidence.CompletedUtc,
            evidence.Context.BuildArtifactHashSha256,
            HistoricalValidationEvidenceCanonicalJsonV1.Serialize(evidence),
            null,
            null));
    }

    /// <summary>
    /// Attach a first-party L1 execution lifecycle report (target vs fills/cancel).
    /// Does not claim Nautilus queue or liquidity matching.
    /// </summary>
    public void UpsertExecutionLifecycleResult(
        string reportId,
        string summary,
        string evidenceJson,
        string? buildArtifactHashSha256 = null)
    {
        UpsertResult(new StrategyVersionResultItem(
            StrategyVersionResultKind.ExecutionLifecycle,
            "exec:" + reportId,
            $"{DisplayName} · L1 execution",
            "completed",
            summary,
            DateTime.UtcNow,
            buildArtifactHashSha256 ?? StrategyWorkspace.Bindings.BuildArtifactHashSha256,
            evidenceJson,
            null,
            null));
        Save();
    }

    internal void UpsertNativeCompareResult(string runId, string sessionId, string status, string summary)
    {
        BoundNativeRunId = runId;
        BoundNativeSessionId = sessionId;
        UpsertResult(new StrategyVersionResultItem(
            StrategyVersionResultKind.NativeCompare,
            "native:" + runId,
            $"{DisplayName} · compare run",
            status,
            summary,
            DateTime.UtcNow,
            StrategyWorkspace.Bindings.BuildArtifactHashSha256,
            null,
            runId,
            sessionId));
    }

    private void UpsertResult(StrategyVersionResultItem item)
    {
        var existing = StrategyVersionResults.FirstOrDefault(r =>
            string.Equals(r.Id, item.Id, StringComparison.Ordinal));
        if (existing is not null)
            StrategyVersionResults.Remove(existing);
        StrategyVersionResults.Insert(0, item);
        while (StrategyVersionResults.Count > 24)
            StrategyVersionResults.RemoveAt(StrategyVersionResults.Count - 1);
        OnPropertyChanged(nameof(HasStrategyVersionResults));
        ReturnToDesignFromBuildCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowBuildDesignBlockers));
        OnPropertyChanged(nameof(BuildDesignBlockerText));
    }

    private void ClearStrategyVersionResults()
    {
        StrategyVersionResults.Clear();
        SelectedStrategyVersionResult = null;
        BoundNativeRunId = null;
        BoundNativeSessionId = null;
        OnPropertyChanged(nameof(HasStrategyVersionResults));
    }

    private string? SerializeStrategyVersionResults()
    {
        if (StrategyVersionResults.Count == 0) return null;
        return JsonSerializer.Serialize(
            StrategyVersionResults.Select(StrategyVersionResultSnapshot.FromItem).ToArray(),
            NativeEvidenceJsonOptions);
    }

    private void RestoreStrategyVersionResults(AuthoringSessionSnapshot session)
    {
        StrategyVersionResults.Clear();
        if (!string.IsNullOrWhiteSpace(session.StrategyVersionResultsJson))
        {
            try
            {
                var rows = JsonSerializer.Deserialize<StrategyVersionResultSnapshot[]>(
                    session.StrategyVersionResultsJson);
                if (rows is not null)
                {
                    foreach (var row in rows)
                        StrategyVersionResults.Add(row.ToItem());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not restore strategy version results for {Id}", session.StrategyId);
            }
        }

        BoundNativeRunId = session.BoundNativeRunId;
        BoundNativeSessionId = session.BoundNativeSessionId;
        if (!string.IsNullOrWhiteSpace(BoundNativeRunId))
            NativeRunId = BoundNativeRunId;

        if (!string.IsNullOrWhiteSpace(session.HistoricalValidationEvidenceJson))
        {
            try
            {
                HistoricalValidationEvidence =
                    HistoricalValidationEvidenceCanonicalJsonV1.Deserialize(
                        session.HistoricalValidationEvidenceJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not restore historical validation evidence for {Id}", session.StrategyId);
            }
        }

        OnPropertyChanged(nameof(HasStrategyVersionResults));
        // Do not auto-open on restore — operator picks from the list.
    }

    private void NotifyBuildDesignBlockerStateChanged()
    {
        OnPropertyChanged(nameof(ShowBuildDesignBlockers));
        OnPropertyChanged(nameof(BuildDesignBlockerText));
        ReturnToDesignFromBuildCommand.NotifyCanExecuteChanged();
    }
}

public enum StrategyVersionResultKind
{
    HistoricalValidate,
    NativeCompare,
    /// <summary>First-party L1 execution fixture / Validate backend report — not Nautilus.</summary>
    ExecutionLifecycle,
}

public sealed class StrategyVersionResultItem
{
    public StrategyVersionResultItem(
        StrategyVersionResultKind kind,
        string id,
        string title,
        string statusLabel,
        string summary,
        DateTime completedUtc,
        string? buildArtifactHashSha256,
        string? evidenceJson,
        string? nativeRunId,
        string? nativeSessionId)
    {
        Kind = kind;
        Id = id;
        Title = title;
        StatusLabel = statusLabel;
        Summary = summary;
        CompletedUtc = completedUtc;
        BuildArtifactHashSha256 = buildArtifactHashSha256;
        EvidenceJson = evidenceJson;
        NativeRunId = nativeRunId;
        NativeSessionId = nativeSessionId;
    }

    public StrategyVersionResultKind Kind { get; }
    public string Id { get; }
    public string Title { get; }
    public string StatusLabel { get; }
    public string Summary { get; }
    public DateTime CompletedUtc { get; }
    public string? BuildArtifactHashSha256 { get; }
    public string? EvidenceJson { get; }
    public string? NativeRunId { get; }
    public string? NativeSessionId { get; }

    public string KindLabel => Kind switch
    {
        StrategyVersionResultKind.HistoricalValidate => "Validate",
        StrategyVersionResultKind.NativeCompare => "Research compare",
        StrategyVersionResultKind.ExecutionLifecycle => "Execution",
        _ => Kind.ToString(),
    };

    public string ListLabel =>
        $"{KindLabel} · {StatusLabel} · {CompletedUtc:u} · {Title}";
}

public sealed record StrategyVersionResultSnapshot(
    string Kind,
    string Id,
    string Title,
    string StatusLabel,
    string Summary,
    DateTime CompletedUtc,
    string? BuildArtifactHashSha256,
    string? EvidenceJson,
    string? NativeRunId,
    string? NativeSessionId)
{
    public static StrategyVersionResultSnapshot FromItem(StrategyVersionResultItem item) =>
        new(
            item.Kind.ToString(),
            item.Id,
            item.Title,
            item.StatusLabel,
            item.Summary,
            item.CompletedUtc,
            item.BuildArtifactHashSha256,
            item.EvidenceJson,
            item.NativeRunId,
            item.NativeSessionId);

    public StrategyVersionResultItem ToItem()
    {
        var kind = Enum.TryParse<StrategyVersionResultKind>(Kind, out var parsed)
            ? parsed
            : StrategyVersionResultKind.HistoricalValidate;
        return new StrategyVersionResultItem(
            kind, Id, Title, StatusLabel, Summary, CompletedUtc,
            BuildArtifactHashSha256, EvidenceJson, NativeRunId, NativeSessionId);
    }
}
