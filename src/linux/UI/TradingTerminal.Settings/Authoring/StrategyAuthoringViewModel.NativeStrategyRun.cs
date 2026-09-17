using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Infrastructure.StrategyAgent;

namespace TradingTerminal.App.Authoring;

public sealed partial class StrategyAuthoringViewModel
{
    private const int MaxNativeEventsPerStream = 200;

    internal static readonly JsonSerializerOptions NativeEvidenceJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IStrategyAgentClient? _strategyAgentClient;
    private CancellationTokenSource? _nativeStrategyRunCts;

    public ObservableCollection<NativeStrategyEvidencePanel> NativeStrategyEvidencePanels { get; } =
    [
        new("research", "Research", "Retained QueryEngine session events · non-executable"),
        new("vibequant", "VibeQuant / AKQuant", "transcend-0/VibeQuant → AKQuant native result"),
        new("csp", "Point72 CSP", "Point72 CSP native graph result · not a trading backtest"),
        new("comparison", "Compare", "Retained DaxAlgo comparison report · no inferred metrics"),
    ];

    public bool IsNativeStrategyAgentWired => _strategyAgentClient is not null;
    public bool ShowNativeStrategyRunPanel =>
        IsBuildScreen && GenerateCandidateFirst && IsNativeStrategyAgentWired;
    public bool ShowLegacyCandidateBoundary =>
        GenerateCandidateFirst && !ShowNativeStrategyRunPanel;
    public bool ShowLegacyBuildResultSpine =>
        IsBuildScreen && GenerateCandidateFirst && !ShowNativeStrategyRunPanel;

    [ObservableProperty]
    private string _nativeRunId = string.Empty;

    [ObservableProperty]
    private string? _loadedNativeRunId;

    [ObservableProperty]
    private string? _loadedNativeSessionId;

    [ObservableProperty]
    private string _nativeRunStatus = "not loaded";

    [ObservableProperty]
    private string _nativeEvidenceStatus = "not reported";

    [ObservableProperty]
    private string _nativeManifestSha256 = "not loaded";

    [ObservableProperty]
    private bool _nativeRunCancelRequested;

    [ObservableProperty]
    private bool _isNativeRunLoading;

    [ObservableProperty]
    private string _nativeRunLoadStatus =
        "Prefer Saved results for this strategy above. Advanced: enter a retained run ID only when the run is not listed yet.";

    [ObservableProperty]
    private string _nativeEventDisplayBoundary =
        $"The UI retains at most the latest {MaxNativeEventsPerStream:N0} events from each service stream.";

    [ObservableProperty]
    private bool _nativeResearchSessionUnavailable;

    [ObservableProperty]
    private string? _nativeRunFailureCode;

    [ObservableProperty]
    private string? _nativeRunFailureDetail;

    [ObservableProperty]
    private string _nativeArtifactPath = string.Empty;

    [ObservableProperty]
    private string _nativeArtifactContent = string.Empty;

    [ObservableProperty]
    private string _nativeArtifactStatus = "Enter an exact relative path reported by a native panel.";

    public string NativeRunIdentityText => LoadedNativeRunId is null
        ? "No retained run loaded"
        : $"Run {LoadedNativeRunId} · session {LoadedNativeSessionId}";

    public string NativeResearchAvailabilityText => NativeResearchSessionUnavailable
        ? "Research session transcript unavailable after service restart. Showing retained run-level research events only."
        : "Research evidence comes from the retained session event stream.";

    public bool HasNativeRunFailure => !string.IsNullOrWhiteSpace(NativeRunFailureDetail);
    public bool HasNativeArtifactContent => NativeArtifactContent.Length > 0;

    private bool CanLoadNativeRunAction() =>
        _strategyAgentClient is not null &&
        !IsNativeRunLoading &&
        !string.IsNullOrWhiteSpace(NativeRunId);

    private bool CanRefreshNativeRunAction() =>
        _strategyAgentClient is not null &&
        !IsNativeRunLoading &&
        !string.IsNullOrWhiteSpace(LoadedNativeRunId);

    private bool CanStartNativeRunAction() =>
        CanRefreshNativeRunAction() &&
        string.Equals(NativeRunStatus, "confirmed", StringComparison.Ordinal);

    private bool CanCancelNativeRunAction() =>
        CanRefreshNativeRunAction() &&
        string.Equals(NativeRunStatus, "running", StringComparison.Ordinal) &&
        !NativeRunCancelRequested;

    private bool CanLoadNativeArtifactAction() =>
        CanRefreshNativeRunAction() && !string.IsNullOrWhiteSpace(NativeArtifactPath);

    [RelayCommand(CanExecute = nameof(CanLoadNativeRunAction))]
    private Task LoadNativeRunAsync() => ExecuteNativeStrategyActionAsync(async cancellationToken =>
    {
        var runId = NativeRunId.Trim();
        var run = await _strategyAgentClient!
            .GetRunAsync(runId, cancellationToken)
            .ConfigureAwait(true);
        await ApplyNativeRunAsync(run, cancellationToken).ConfigureAwait(true);
        NativeRunLoadStatus = "Loaded the retained native status and bounded session/run event views.";
    });

    [RelayCommand(CanExecute = nameof(CanRefreshNativeRunAction))]
    private Task RefreshNativeRunAsync() => ExecuteNativeStrategyActionAsync(async cancellationToken =>
    {
        var run = await _strategyAgentClient!
            .GetRunAsync(LoadedNativeRunId!, cancellationToken)
            .ConfigureAwait(true);
        await ApplyNativeRunAsync(run, cancellationToken).ConfigureAwait(true);
        NativeRunLoadStatus = "Refreshed from the retained native run. This screen does not infer progress between refreshes.";
    });

    [RelayCommand(CanExecute = nameof(CanStartNativeRunAction))]
    private Task StartNativeRunAsync() => ExecuteNativeStrategyActionAsync(async cancellationToken =>
    {
        var run = await _strategyAgentClient!
            .StartAsync(LoadedNativeRunId!, cancellationToken)
            .ConfigureAwait(true);
        await ApplyNativeRunAsync(run, cancellationToken).ConfigureAwait(true);
        NativeRunLoadStatus = "The native service accepted the start request. Use Refresh to read later retained events and terminal results.";
    });

    [RelayCommand(CanExecute = nameof(CanCancelNativeRunAction))]
    private Task CancelNativeRunAsync() => ExecuteNativeStrategyActionAsync(async cancellationToken =>
    {
        var run = await _strategyAgentClient!
            .CancelAsync(LoadedNativeRunId!, cancellationToken)
            .ConfigureAwait(true);
        await ApplyNativeRunAsync(run, cancellationToken).ConfigureAwait(true);
        NativeRunLoadStatus = run.CancelRequested
            ? "Cancellation was requested. An already-running native callback may still report its real terminal result; use Refresh."
            : "The native service returned without a pending cancellation request.";
    });

    [RelayCommand(CanExecute = nameof(CanLoadNativeArtifactAction))]
    private Task LoadNativeArtifactAsync() => ExecuteNativeStrategyActionAsync(async cancellationToken =>
    {
        var artifact = await _strategyAgentClient!
            .GetArtifactAsync(LoadedNativeRunId!, NativeArtifactPath.Trim(), cancellationToken)
            .ConfigureAwait(true);
        NativeArtifactPath = artifact.RelativePath;
        NativeArtifactContent = artifact.Content;
        NativeArtifactStatus =
            $"{artifact.RelativePath} · SHA-256 {artifact.Sha256} · {artifact.SizeBytes} bytes · {artifact.Encoding}";
    });

    private async Task ExecuteNativeStrategyActionAsync(Func<CancellationToken, Task> action)
    {
        if (_strategyAgentClient is null || IsNativeRunLoading) return;

        NativeRunFailureCode = null;
        NativeRunFailureDetail = null;
        _nativeStrategyRunCts?.Dispose();
        var operationCts = new CancellationTokenSource();
        _nativeStrategyRunCts = operationCts;
        IsNativeRunLoading = true;

        try
        {
            await action(operationCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            NativeRunLoadStatus = "The UI request was cancelled before it completed.";
        }
        catch (StrategyAgentApiException ex)
        {
            NativeRunFailureCode = ex.Code;
            NativeRunFailureDetail = ex.Message;
            NativeRunLoadStatus = $"{ex.Code}: {ex.Message}";
            _logger.LogWarning(ex, "Native strategy-agent action failed at {Code}", ex.Code);
        }
        catch (Exception ex)
        {
            NativeRunFailureCode = ex.GetType().Name;
            NativeRunFailureDetail = ex.Message;
            NativeRunLoadStatus = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "Native strategy-agent UI action failed");
        }
        finally
        {
            if (ReferenceEquals(_nativeStrategyRunCts, operationCts))
                _nativeStrategyRunCts = null;
            operationCts.Dispose();
            IsNativeRunLoading = false;
        }
    }

    private async Task ApplyNativeRunAsync(
        StrategyAgentRunStatus run,
        CancellationToken cancellationToken)
    {
        var runEventRead = await ReadAllEventsAsync(
            (after, limit, token) => _strategyAgentClient!.GetRunEventsAsync(
                run.RunId, after, limit, token),
            cancellationToken).ConfigureAwait(true);
        NativeEventReadResult researchEventRead;
        var researchSessionUnavailable = false;
        try
        {
            researchEventRead = await ReadAllEventsAsync(
                (after, limit, token) => _strategyAgentClient!.GetSessionEventsAsync(
                    run.SessionId, after, limit, token),
                cancellationToken).ConfigureAwait(true);
        }
        catch (StrategyAgentApiException ex) when (string.Equals(
            ex.Code,
            "research_session_not_found",
            StringComparison.Ordinal))
        {
            researchSessionUnavailable = true;
            researchEventRead = new NativeEventReadResult(
                runEventRead.Events
                    .Where(eventRow => string.Equals(
                        eventRow.Lane,
                        "research",
                        StringComparison.Ordinal))
                    .ToArray(),
                runEventRead.Truncated);
        }

        // Commit the new identity and evidence only after both required event reads have either
        // succeeded or used the explicit post-restart research fallback above. A failed read must
        // leave the previously inspected run and panels intact.
        LoadedNativeRunId = run.RunId;
        LoadedNativeSessionId = run.SessionId;
        NativeRunId = run.RunId;
        NativeRunStatus = run.Status;
        NativeEvidenceStatus = run.EvidenceStatus ?? "not reported";
        NativeManifestSha256 = run.ManifestSha256;
        NativeRunCancelRequested = run.CancelRequested;
        NativeResearchSessionUnavailable = researchSessionUnavailable;

        var researchEvents = researchEventRead.Events;
        var runEvents = runEventRead.Events;
        NativeEventDisplayBoundary = researchEventRead.Truncated || runEventRead.Truncated
            ? $"Showing only the latest {MaxNativeEventsPerStream:N0} events from each stream; older retained events are not rendered."
            : $"Showing all retained events currently returned, bounded to {MaxNativeEventsPerStream:N0} per stream.";

        var research = NativePanel("research");
        research.Apply(
            status: researchEvents.LastOrDefault()?.Status ?? "not reported",
            stage: researchEvents.LastOrDefault()?.Stage ?? "not reported",
            summary: researchSessionUnavailable
                ? $"Session endpoint unavailable · {researchEvents.Count} retained run-level research event(s)"
                : $"Session {run.SessionId} · {researchEvents.Count} retained session event(s)",
            exactFailure: researchEvents.LastOrDefault(eventRow =>
                string.Equals(eventRow.Status, "failed", StringComparison.Ordinal))?.Message,
            evidence: researchSessionUnavailable
                ? "Research session transcript unavailable after service restart. Only real lane=research events retained with this run are shown. Research has no executable or backtest status."
                : "Research is represented by retained session events only. It has no executable or backtest status.",
            researchEvents);

        ApplyNativeLane(run, runEvents, "vibequant");
        ApplyNativeLane(run, runEvents, "csp");

        var comparisonEvents = runEvents
            .Where(eventRow => string.Equals(eventRow.Lane, "comparison", StringComparison.Ordinal))
            .ToArray();
        var comparison = NativePanel("comparison");
        comparison.Apply(
            status: run.EvidenceStatus ?? comparisonEvents.LastOrDefault()?.Status ?? "not reported",
            stage: comparisonEvents.LastOrDefault()?.Stage ?? "not reported",
            summary: run.Comparison is null
                ? "No retained comparison report was returned."
                : $"{run.Comparison.RelativePath} · SHA-256 {run.Comparison.Sha256}",
            exactFailure: comparisonEvents.LastOrDefault(eventRow =>
                string.Equals(eventRow.Status, "failed", StringComparison.Ordinal))?.Message,
            evidence: run.Comparison is null
                ? "No report JSON is available for this run status."
                : FormatJson(run.Comparison.Report),
            comparisonEvents);

        OnPropertyChanged(nameof(NativeRunIdentityText));
        NotifyNativeRunCommands();
        UpsertNativeCompareResult(
            run.RunId,
            run.SessionId,
            run.Status,
            run.Comparison is null
                ? $"Run {run.RunId} · evidence {run.EvidenceStatus ?? "not reported"}"
                : $"{run.Comparison.RelativePath} · SHA-256 {run.Comparison.Sha256}");
        if (run.Comparison is not null)
            NativeArtifactPath = run.Comparison.RelativePath;
        Save();
    }

    private void ApplyNativeLane(
        StrategyAgentRunStatus run,
        IReadOnlyList<StrategyAgentEvent> runEvents,
        string lane)
    {
        var laneEvents = runEvents
            .Where(eventRow => string.Equals(eventRow.Lane, lane, StringComparison.Ordinal))
            .ToArray();
        var result = run.Results.FirstOrDefault(pair =>
            string.Equals(pair.Key, lane, StringComparison.Ordinal)).Value;
        var panel = NativePanel(lane);

        if (result is null)
        {
            panel.Apply(
                status: run.LaneStates.FirstOrDefault(pair =>
                    string.Equals(pair.Key, lane, StringComparison.Ordinal)).Value ?? "not reported",
                stage: laneEvents.LastOrDefault()?.Stage ?? "not reported",
                summary: "No retained terminal lane result is available yet.",
                exactFailure: laneEvents.LastOrDefault(eventRow =>
                    string.Equals(eventRow.Status, "failed", StringComparison.Ordinal))?.Message,
                evidence: "Refresh after the native service retains a lane result.",
                laneEvents);
            return;
        }

        panel.Apply(
            status: result.Status,
            stage: result.NativeStage,
            summary: $"{result.Framework} {result.FrameworkVersion} · manifest {result.ManifestSha256}",
            exactFailure: result.Error,
            evidence: BuildNativeLaneEvidence(result),
            laneEvents);
    }

    private NativeStrategyEvidencePanel NativePanel(string key) =>
        NativeStrategyEvidencePanels.Single(panel =>
            string.Equals(panel.Key, key, StringComparison.Ordinal));

    private static string BuildNativeLaneEvidence(StrategyAgentLaneResult result)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.SourceRelativePath))
            text.AppendLine($"Source: {result.SourceRelativePath}");

        if (result.ArtifactRelativePaths.Count > 0)
        {
            text.AppendLine("Artifacts:");
            foreach (var path in result.ArtifactRelativePaths)
            {
                result.ArtifactSha256.TryGetValue(path, out var sha256);
                text.Append("- ").Append(path);
                if (!string.IsNullOrWhiteSpace(sha256))
                    text.Append(" · SHA-256 ").Append(sha256);
                text.AppendLine();
            }
        }

        text.AppendLine("Observations:");
        text.Append(FormatJson(result.Observations));
        return text.ToString();
    }

    private static async Task<NativeEventReadResult> ReadAllEventsAsync(
        Func<long, int, CancellationToken, Task<StrategyAgentEventPage>> readPage,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var events = new List<StrategyAgentEvent>();
        var truncated = false;
        long afterSequence = 0;

        while (true)
        {
            var page = await readPage(afterSequence, pageSize, cancellationToken)
                .ConfigureAwait(true);
            events.AddRange(page.Events);
            if (events.Count > MaxNativeEventsPerStream)
            {
                events.RemoveRange(0, events.Count - MaxNativeEventsPerStream);
                truncated = true;
            }
            if (!page.HasMore) return new NativeEventReadResult(events, truncated);
            if (page.NextAfterSeq <= afterSequence)
                throw new InvalidOperationException("Strategy-agent event paging did not advance.");
            afterSequence = page.NextAfterSeq;
        }
    }

    private static string FormatJson(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "null"
            : JsonSerializer.Serialize(element, NativeEvidenceJsonOptions);

    partial void OnNativeRunIdChanged(string value) =>
        LoadNativeRunCommand.NotifyCanExecuteChanged();

    partial void OnLoadedNativeRunIdChanged(string? value)
    {
        OnPropertyChanged(nameof(NativeRunIdentityText));
        NotifyNativeRunCommands();
    }

    partial void OnLoadedNativeSessionIdChanged(string? value) =>
        OnPropertyChanged(nameof(NativeRunIdentityText));

    partial void OnNativeRunStatusChanged(string value) => NotifyNativeRunCommands();
    partial void OnNativeRunCancelRequestedChanged(bool value) => NotifyNativeRunCommands();

    partial void OnNativeResearchSessionUnavailableChanged(bool value) =>
        OnPropertyChanged(nameof(NativeResearchAvailabilityText));

    partial void OnIsNativeRunLoadingChanged(bool value) => NotifyNativeRunCommands();

    partial void OnNativeRunFailureDetailChanged(string? value) =>
        OnPropertyChanged(nameof(HasNativeRunFailure));

    partial void OnNativeArtifactPathChanged(string value) =>
        LoadNativeArtifactCommand.NotifyCanExecuteChanged();

    partial void OnNativeArtifactContentChanged(string value) =>
        OnPropertyChanged(nameof(HasNativeArtifactContent));

    private void NotifyNativeRunCommands()
    {
        LoadNativeRunCommand.NotifyCanExecuteChanged();
        RefreshNativeRunCommand.NotifyCanExecuteChanged();
        StartNativeRunCommand.NotifyCanExecuteChanged();
        CancelNativeRunCommand.NotifyCanExecuteChanged();
        LoadNativeArtifactCommand.NotifyCanExecuteChanged();
    }

    private void DisposeNativeStrategyRun()
    {
        _nativeStrategyRunCts?.Cancel();
        _nativeStrategyRunCts?.Dispose();
        _nativeStrategyRunCts = null;
    }

    private sealed record NativeEventReadResult(
        IReadOnlyList<StrategyAgentEvent> Events,
        bool Truncated);
}

public sealed class NativeStrategyEvidencePanel : ObservableObject
{
    private string _status = "not loaded";
    private string _stage = "not loaded";
    private string _summary = "No retained evidence loaded.";
    private string? _exactFailure;
    private string _evidence = "No retained evidence loaded.";

    public NativeStrategyEvidencePanel(string key, string title, string authority)
    {
        Key = key;
        Title = title;
        Authority = authority;
    }

    public string Key { get; }
    public string Title { get; }
    public string Authority { get; }
    public ObservableCollection<NativeStrategyEventRow> Events { get; } = [];

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string? ExactFailure
    {
        get => _exactFailure;
        private set
        {
            if (SetProperty(ref _exactFailure, value))
                OnPropertyChanged(nameof(HasExactFailure));
        }
    }

    public bool HasExactFailure => !string.IsNullOrWhiteSpace(ExactFailure);

    public string Evidence
    {
        get => _evidence;
        private set => SetProperty(ref _evidence, value);
    }

    internal void Apply(
        string status,
        string stage,
        string summary,
        string? exactFailure,
        string evidence,
        IReadOnlyList<StrategyAgentEvent> events)
    {
        Status = status;
        Stage = stage;
        Summary = summary;
        ExactFailure = exactFailure;
        Evidence = evidence;
        Events.Clear();
        foreach (var eventRow in events)
        {
            Events.Add(new NativeStrategyEventRow(
                eventRow.Sequence,
                eventRow.OccurredAtUtc,
                eventRow.Stage,
                eventRow.Status,
                eventRow.Message,
                eventRow.Details.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "null"
                    : JsonSerializer.Serialize(
                        eventRow.Details,
                        StrategyAuthoringViewModel.NativeEvidenceJsonOptions)));
        }
    }
}

public sealed record NativeStrategyEventRow(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Stage,
    string Status,
    string Message,
    string Details)
{
    public string TimestampText => OccurredAtUtc.ToString("u");
    public string StageStatusText => $"#{Sequence} · {Stage} · {Status}";
}
