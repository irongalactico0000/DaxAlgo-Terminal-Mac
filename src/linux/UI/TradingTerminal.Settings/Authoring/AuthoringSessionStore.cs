using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>One bubble as the user saw it. Kept separately from the model thread because they are not the
/// same thing: the thread also carries the compiler's auto-fix prompts, which the user never typed and
/// should not have to read.</summary>
public sealed record AuthoringChatEntry(
    string Role,
    string Text,
    DateTime TimestampLocal,
    // The agent-workspace transcript kinds (issue #29). All optional so pre-redesign session files
    // keep deserializing: a null Kind is a plain user/assistant/system bubble.
    string? Kind = null,
    string? State = null,
    string? Detail = null)
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

/// <summary>
/// A whole authoring session, as it stood when it was last touched: the chat the user reads, the thread
/// the MODEL reads (so a resumed conversation still remembers what it wrote), the files, the provider
/// setup and what it has cost so far.
/// </summary>
/// <param name="StrategyDraftJson">Optional chart-authored strategy draft (stop/target). Unlocked drafts are not TradeIR.</param>
/// <param name="ResearchConditionJson">Pending editable research condition (canon R08) — survives reopen without file re-upload.</param>
/// <param name="ResearchConditionSearchResultJson">Last condition search result, including LiveMeetsCondition.</param>
/// <param name="ResearchAnalysisReferencesJson">Named in-app research findings 1/2/… (canon R13; ReferenceId stays A/B).</param>
/// <param name="ResearchChartSelectionJson">Pending chart brush (U03) — survives reopen without re-selecting on the chart.</param>
/// <param name="ResearchIndicatorBindingsJson">Pending indicator bindings captured with the chart brush (U04).</param>
public sealed record AuthoringSessionSnapshot(
    string StrategyId,
    string DisplayName,
    IReadOnlyList<AuthoringChatEntry> Chat,
    IReadOnlyList<CodegenMessage> Thread,
    IReadOnlyList<StrategyFile> Files,
    string? ProviderId = null,
    string? Model = null,
    string? Effort = null,
    string? BuildEffort = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    bool Registered = false,
    string? CandidateJson = null,
    // Null means the snapshot predates the lane picker. The UX version also distinguishes values
    // written by the old ambiguous toggle from an explicit choice made with the redesigned control.
    bool? GenerateCandidateFirst = null,
    // The durable four-lane strategy request. It starts with the user's original brief and carries
    // forward later strategy refinements; navigation requests such as "go to backtest" never replace it.
    string? FourLaneStrategyBrief = null,
    // A submitted four-lane prompt that did not commit a replacement batch (stop, provider failure,
    // or process exit). It is restored into the composer and never treated as part of the old batch.
    string? PendingFourLanePrompt = null,
    string? ParallelCandidateBatchJson = null,
    string? SelectedParallelCandidateHash = null,
    string? EditorBaseParallelCandidateHash = null,
    string? ResearchCaseJson = null,
    string? StrategyClassificationJson = null,
    string? StrategyIntentDraftJson = null,
    string? ConfirmedStrategyIntentJson = null,
    StrategyAuthoringScreen ActiveScreen = StrategyAuthoringScreen.Design,
    bool HasDetachedImplementationSource = false,
    bool EditorOriginatedFromCombinedTradeIr = false,
    int AuthoringUxVersion = 0,
    DateTime UpdatedUtc = default,
    IReadOnlyList<AuthoringChartReferenceSnapshot>? ChartReferences = null,
    string? AuthoredUnitSpecificationJson = null,
    IReadOnlyList<AuthoredChartReferenceInspectionV1>? ChartReferenceInspections = null,
    ChartPatternSearchResultV1? ChartPatternSearchResult = null,
    IReadOnlyList<ChartPatternSelectionV1>? ChartPatternSelections = null,
    string? StrategyWorkspaceJson = null,
    string? ResearchDatasetJson = null,
    string? ResearchExperimentJson = null,
    string? StrategyDraftJson = null,
    string? ResearchConditionJson = null,
    string? ResearchConditionSearchResultJson = null,
    string? ResearchAnalysisReferencesJson = null,
    string? ResearchChartSelectionJson = null,
    string? ResearchIndicatorBindingsJson = null)
{
    public const int CurrentAuthoringUxVersion = 3;

    [JsonIgnore]
    public bool FourLaneGenerationEnabled =>
        AuthoringUxVersion < CurrentAuthoringUxVersion || GenerateCandidateFirst is not false;

    /// <summary>"2 hours ago" — what the session picker shows next to the name.</summary>
    public string Age
    {
        get
        {
            var elapsed = DateTime.UtcNow - UpdatedUtc;
            if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
            if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }

    public string Label => $"{DisplayName} ({StrategyId}) · {Age}";
}

/// <summary>Persistence seam used by the authoring view-model. The file-backed implementation remains
/// the product default; tests can supply an in-memory repository without reading or writing a user's
/// saved authoring sessions.</summary>
public interface IAuthoringSessionRepository
{
    IReadOnlyList<AuthoringSessionSnapshot> List();
    bool Save(AuthoringSessionSnapshot session);
    void Delete(string strategyId);
}

internal sealed class FileAuthoringSessionRepository : IAuthoringSessionRepository
{
    public static FileAuthoringSessionRepository Instance { get; } = new();

    private FileAuthoringSessionRepository()
    {
    }

    public IReadOnlyList<AuthoringSessionSnapshot> List() => AuthoringSessionStore.List();
    public bool Save(AuthoringSessionSnapshot session) => AuthoringSessionStore.Save(session);
    public void Delete(string strategyId) => AuthoringSessionStore.Delete(strategyId);
}

/// <summary>
/// Persists authoring sessions to <c>%LocalAppData%\DaxAlgo Terminal\authoring\</c>, one JSON file per
/// strategy id. A strategy is often several sittings' work — a brief, the model's questions, a few
/// rounds of fixes — and losing all of that to a restart (which is what happened before this existed)
/// makes the builder unusable for anything serious.
/// <para>
/// Nothing here is secret: the transcript, the code, and the provider/model choice. API keys live in the
/// DPAPI credential store and never come near this file.
/// </para>
/// </summary>
public static class AuthoringSessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "authoring");

    /// <summary>Writes the session. A failure is swallowed (and reported to the caller as false) — a
    /// read-only profile must not take the chat down with it.</summary>
    public static bool Save(AuthoringSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.StrategyId)) return false;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(
                PathFor(session.StrategyId),
                JsonSerializer.Serialize(session with { UpdatedUtc = DateTime.UtcNow }, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Every saved session, newest first. A corrupt file is skipped, not thrown — one bad file
    /// must not hide the rest of the user's work.</summary>
    public static IReadOnlyList<AuthoringSessionSnapshot> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var sessions = new List<AuthoringSessionSnapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (TryRead(file) is { } session) sessions.Add(session);
        }

        return [.. sessions.OrderByDescending(s => s.UpdatedUtc)];
    }

    public static AuthoringSessionSnapshot? Load(string strategyId) =>
        string.IsNullOrWhiteSpace(strategyId) ? null : TryRead(PathFor(strategyId));

    public static void Delete(string strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) return;

        try
        {
            File.Delete(PathFor(strategyId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do — the session simply stays in the list until the next start.
        }
    }

    private static AuthoringSessionSnapshot? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AuthoringSessionSnapshot>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A strategy id is user input and becomes a file name — anything that isn't a letter, digit,
    /// dot, dash or underscore is replaced, so an id can never escape the folder.</summary>
    private static string PathFor(string strategyId)
    {
        var safe = new string(strategyId
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
            .ToArray());
        return Path.Combine(Directory, $"{safe}.json");
    }
}
