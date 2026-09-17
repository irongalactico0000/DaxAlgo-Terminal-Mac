using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// Workspace binding stages for one authored chart or strategy.
/// Builder UI rail is Design → Build → Validate → Paper(Run) only;
/// Brief and Research remain in the aggregate for hash bindings / optional Studio work.
/// </summary>
public enum StrategyWorkspaceStageV1
{
    Brief = 0,
    Research = 1,
    Design = 2,
    Build = 3,
    Validate = 4,
    Paper = 5,
}

public enum StrategyWorkspaceStageRequirementV1
{
    Required = 0,
    Optional = 1,
    Skipped = 2,
}

public enum StrategyWorkspaceStageStateV1
{
    Pending = 0,
    NeedsReview = 1,
    Completed = 2,
    Skipped = 3,
    Unavailable = 4,
}

/// <summary>
/// Declares what changed so downstream evidence is invalidated deliberately instead of being reused
/// merely because it still exists on disk.
/// </summary>
public enum StrategyWorkspaceChangeKindV1
{
    NavigationOnly = 0,
    BriefMeaning = 1,
    ResearchDefinition = 2,
    DatasetOrFeatureDefinition = 3,
    StrategyMeaning = 4,
    AppearanceOnly = 5,
    GeneratedSpecification = 6,
    BuildArtifact = 7,
    ValidationEvidence = 8,
    PaperBinding = 9,
}

/// <summary>
/// Hash bindings for the exact artifacts shared by Research, Design, Build, Validate, and Paper.
/// Null means the artifact has not been produced for this revision. Appearance and drawing semantics
/// are deliberately separate: hiding or recoloring a layer cannot alter a trading rule.
/// </summary>
public sealed record StrategyWorkspaceBindingsV1(
    string? BriefHashSha256 = null,
    string? ResearchCaseHashSha256 = null,
    string? DatasetDefinitionHashSha256 = null,
    string? FeatureSetHashSha256 = null,
    string? ConfirmedIntentHashSha256 = null,
    string? DrawingSemanticsHashSha256 = null,
    string? AppearanceHashSha256 = null,
    string? AuthoredUnitSpecificationHashSha256 = null,
    string? BuildArtifactHashSha256 = null,
    string? ValidationEvidenceHashSha256 = null,
    string? PaperBindingHashSha256 = null);

public sealed record StrategyWorkspaceStageSnapshotV1(
    StrategyWorkspaceStageV1 Stage,
    StrategyWorkspaceStageRequirementV1 Requirement,
    StrategyWorkspaceStageStateV1 State,
    string StatusText);

/// <summary>
/// One immutable, hash-chainable workspace revision. It is the aggregate identity that prevents a
/// backtest or Paper handoff from silently referring to a different brief, feature set, strategy, or
/// executable than the chart currently shows.
/// </summary>
public sealed record StrategyWorkspaceRevisionV1(
    string SchemaVersion,
    string WorkspaceId,
    long Revision,
    string? PreviousRevisionHashSha256,
    StrategyWorkspaceStageV1 ActiveStage,
    StrategyWorkspaceStageRequirementV1 ResearchRequirement,
    StrategyWorkspaceStageRequirementV1 DesignRequirement,
    StrategyWorkspaceBindingsV1 Bindings,
    IReadOnlyList<StrategyWorkspaceStageSnapshotV1> Stages,
    string RevisionReason)
{
    public const string CurrentSchemaVersion = "strategy-workspace/v1";

    public StrategyWorkspaceStageSnapshotV1 Stage(StrategyWorkspaceStageV1 stage) =>
        Stages.Single(item => item.Stage == stage);
}

public sealed record StrategyWorkspaceIssueV1(string Code, string Path, string Message);

public static class StrategyWorkspaceCanonicalJsonV1
{
    public static string Serialize(StrategyWorkspaceRevisionV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Serialize(value);

    public static StrategyWorkspaceRevisionV1 Deserialize(string json)
    {
        var value = ExecutableStrategyDefinitionCanonicalJson.Deserialize<StrategyWorkspaceRevisionV1>(json);
        StrategyWorkspaceRevisionPolicyV1.RequireValid(value);
        return value;
    }

    public static string Hash(StrategyWorkspaceRevisionV1 value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);

    public static string HashArtifact(object value) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(value);
}

/// <summary>Creates, revises, validates, and invalidates the shared workspace aggregate.</summary>
public static class StrategyWorkspaceRevisionPolicyV1
{
    public static StrategyWorkspaceRevisionV1 Create(
        string workspaceId,
        StrategyWorkspaceBindingsV1? bindings = null,
        StrategyWorkspaceStageV1 activeStage = StrategyWorkspaceStageV1.Brief,
        StrategyWorkspaceStageRequirementV1 researchRequirement = StrategyWorkspaceStageRequirementV1.Optional,
        StrategyWorkspaceStageRequirementV1 designRequirement = StrategyWorkspaceStageRequirementV1.Optional,
        string revisionReason = "Workspace created")
    {
        var normalizedId = RequireText(workspaceId, nameof(workspaceId));
        var normalizedBindings = bindings ?? new StrategyWorkspaceBindingsV1();
        ValidateBindings(normalizedBindings);
        var result = new StrategyWorkspaceRevisionV1(
            StrategyWorkspaceRevisionV1.CurrentSchemaVersion,
            normalizedId,
            1,
            null,
            activeStage,
            researchRequirement,
            designRequirement,
            normalizedBindings,
            BuildStages(normalizedBindings, researchRequirement, designRequirement),
            RequireText(revisionReason, nameof(revisionReason)));
        RequireValid(result);
        return result;
    }

    public static StrategyWorkspaceRevisionV1 Revise(
        StrategyWorkspaceRevisionV1 current,
        StrategyWorkspaceChangeKindV1 changeKind,
        StrategyWorkspaceBindingsV1 requestedBindings,
        StrategyWorkspaceStageV1 activeStage,
        StrategyWorkspaceStageRequirementV1? researchRequirement = null,
        StrategyWorkspaceStageRequirementV1? designRequirement = null,
        string revisionReason = "Workspace updated")
    {
        RequireValid(current);
        ArgumentNullException.ThrowIfNull(requestedBindings);

        var nextResearchRequirement = researchRequirement ?? current.ResearchRequirement;
        var nextDesignRequirement = designRequirement ?? current.DesignRequirement;
        var nextBindings = InvalidateDownstream(requestedBindings, changeKind);
        ValidateBindings(nextBindings);

        if (current.ActiveStage == activeStage &&
            current.ResearchRequirement == nextResearchRequirement &&
            current.DesignRequirement == nextDesignRequirement &&
            current.Bindings == nextBindings)
            return current;

        var result = new StrategyWorkspaceRevisionV1(
            StrategyWorkspaceRevisionV1.CurrentSchemaVersion,
            current.WorkspaceId,
            checked(current.Revision + 1),
            StrategyWorkspaceCanonicalJsonV1.Hash(current),
            activeStage,
            nextResearchRequirement,
            nextDesignRequirement,
            nextBindings,
            BuildStages(nextBindings, nextResearchRequirement, nextDesignRequirement),
            RequireText(revisionReason, nameof(revisionReason)));
        RequireValid(result);
        return result;
    }

    public static StrategyWorkspaceBindingsV1 InvalidateDownstream(
        StrategyWorkspaceBindingsV1 bindings,
        StrategyWorkspaceChangeKindV1 changeKind) => changeKind switch
    {
        StrategyWorkspaceChangeKindV1.NavigationOnly => bindings,
        StrategyWorkspaceChangeKindV1.BriefMeaning => bindings with
        {
            ResearchCaseHashSha256 = null,
            DatasetDefinitionHashSha256 = null,
            FeatureSetHashSha256 = null,
            ConfirmedIntentHashSha256 = null,
            DrawingSemanticsHashSha256 = null,
            AppearanceHashSha256 = null,
            AuthoredUnitSpecificationHashSha256 = null,
            BuildArtifactHashSha256 = null,
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.ResearchDefinition => bindings with
        {
            DatasetDefinitionHashSha256 = null,
            FeatureSetHashSha256 = null,
            ConfirmedIntentHashSha256 = null,
            DrawingSemanticsHashSha256 = null,
            AuthoredUnitSpecificationHashSha256 = null,
            BuildArtifactHashSha256 = null,
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.DatasetOrFeatureDefinition => bindings with
        {
            ConfirmedIntentHashSha256 = null,
            DrawingSemanticsHashSha256 = null,
            AuthoredUnitSpecificationHashSha256 = null,
            BuildArtifactHashSha256 = null,
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.StrategyMeaning => bindings with
        {
            AuthoredUnitSpecificationHashSha256 = null,
            BuildArtifactHashSha256 = null,
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.AppearanceOnly => bindings with
        {
            AuthoredUnitSpecificationHashSha256 = null,
            BuildArtifactHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.GeneratedSpecification => bindings with
        {
            BuildArtifactHashSha256 = null,
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.BuildArtifact => bindings with
        {
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.ValidationEvidence => bindings with
        {
            PaperBindingHashSha256 = null,
        },
        StrategyWorkspaceChangeKindV1.PaperBinding => bindings,
        _ => throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null),
    };

    public static IReadOnlyList<StrategyWorkspaceIssueV1> Validate(StrategyWorkspaceRevisionV1? value)
    {
        var issues = new List<StrategyWorkspaceIssueV1>();
        if (value is null)
        {
            issues.Add(new("WORKSPACE_REQUIRED", "workspace", "A strategy workspace revision is required."));
            return issues;
        }

        if (value.SchemaVersion != StrategyWorkspaceRevisionV1.CurrentSchemaVersion)
            issues.Add(new("WORKSPACE_SCHEMA_UNSUPPORTED", "schemaVersion", "The workspace schema version is unsupported."));
        if (string.IsNullOrWhiteSpace(value.WorkspaceId))
            issues.Add(new("WORKSPACE_ID_REQUIRED", "workspaceId", "A workspace id is required."));
        if (value.Revision < 1)
            issues.Add(new("WORKSPACE_REVISION_INVALID", "revision", "The workspace revision must be positive."));
        if (value.Revision == 1 && value.PreviousRevisionHashSha256 is not null)
            issues.Add(new("WORKSPACE_GENESIS_HAS_PREVIOUS", "previousRevisionHashSha256", "The first revision cannot reference a previous revision."));
        if (value.Revision > 1 && !IsSha256(value.PreviousRevisionHashSha256))
            issues.Add(new("WORKSPACE_PREVIOUS_HASH_REQUIRED", "previousRevisionHashSha256", "A later revision must bind the exact previous revision hash."));
        if (string.IsNullOrWhiteSpace(value.RevisionReason))
            issues.Add(new("WORKSPACE_REASON_REQUIRED", "revisionReason", "A revision reason is required."));
        if (value.Bindings is null)
            issues.Add(new("WORKSPACE_BINDINGS_REQUIRED", "bindings", "Workspace artifact bindings are required."));
        else
            AddBindingIssues(value.Bindings, issues);

        var expectedStages = Enum.GetValues<StrategyWorkspaceStageV1>();
        if (value.Stages is null || value.Stages.Count != expectedStages.Length ||
            expectedStages.Any(stage => value.Stages.Count(item => item.Stage == stage) != 1))
            issues.Add(new("WORKSPACE_STAGES_INVALID", "stages", "Every workspace stage must appear exactly once."));
        else if (value.Bindings is not null)
        {
            var derived = BuildStages(value.Bindings, value.ResearchRequirement, value.DesignRequirement);
            if (!derived.SequenceEqual(value.Stages))
                issues.Add(new("WORKSPACE_STAGE_STATE_MISMATCH", "stages", "Stage state does not match the bound artifacts."));
        }
        return issues;
    }

    public static void RequireValid(StrategyWorkspaceRevisionV1 value)
    {
        var issues = Validate(value);
        if (issues.Count > 0)
            throw new ArgumentException(string.Join(" ", issues.Select(issue => $"{issue.Code}: {issue.Message}")), nameof(value));
    }

    private static IReadOnlyList<StrategyWorkspaceStageSnapshotV1> BuildStages(
        StrategyWorkspaceBindingsV1 bindings,
        StrategyWorkspaceStageRequirementV1 researchRequirement,
        StrategyWorkspaceStageRequirementV1 designRequirement) =>
    [
        Stage(StrategyWorkspaceStageV1.Brief, StrategyWorkspaceStageRequirementV1.Required,
            bindings.BriefHashSha256 is null ? StrategyWorkspaceStageStateV1.NeedsReview : StrategyWorkspaceStageStateV1.Completed,
            bindings.BriefHashSha256 is null ? "Interpret and confirm the request." : "The interpreted brief is hash-bound."),
        OptionalStage(StrategyWorkspaceStageV1.Research, researchRequirement,
            bindings.ResearchCaseHashSha256 ?? bindings.DatasetDefinitionHashSha256,
            "Investigate on the chart and save observations before specifying rules.",
            "Research evidence is hash-bound."),
        OptionalStage(StrategyWorkspaceStageV1.Design, designRequirement,
            bindings.ConfirmedIntentHashSha256 ??
            (bindings.DrawingSemanticsHashSha256 is not null && bindings.AuthoredUnitSpecificationHashSha256 is not null
                ? bindings.DrawingSemanticsHashSha256
                : null),
            "Promote a research observation into explicit entry, exit, sizing, and risk rules.",
            "Chart semantics and confirmed design are hash-bound."),
        Stage(StrategyWorkspaceStageV1.Build, StrategyWorkspaceStageRequirementV1.Required,
            bindings.BuildArtifactHashSha256 is not null
                ? StrategyWorkspaceStageStateV1.Completed
                : bindings.AuthoredUnitSpecificationHashSha256 is not null
                    ? StrategyWorkspaceStageStateV1.NeedsReview
                    : StrategyWorkspaceStageStateV1.Pending,
            bindings.BuildArtifactHashSha256 is not null
                ? "The compiled artifact is bound to this workspace."
                : bindings.AuthoredUnitSpecificationHashSha256 is not null
                    ? "The typed specification is ready to generate and compile."
                    : "Confirm strategy meaning before generating code."),
        Stage(StrategyWorkspaceStageV1.Validate, StrategyWorkspaceStageRequirementV1.Required,
            bindings.ValidationEvidenceHashSha256 is not null
                ? StrategyWorkspaceStageStateV1.Completed
                : bindings.BuildArtifactHashSha256 is null
                    ? StrategyWorkspaceStageStateV1.Unavailable
                    : StrategyWorkspaceStageStateV1.Pending,
            bindings.ValidationEvidenceHashSha256 is not null
                ? "Backtest evidence is bound to this workspace."
                : bindings.BuildArtifactHashSha256 is null
                    ? "Compile an exact artifact before validation."
                    : "Run historical preview and backtest."),
        Stage(StrategyWorkspaceStageV1.Paper, StrategyWorkspaceStageRequirementV1.Required,
            bindings.PaperBindingHashSha256 is not null
                ? StrategyWorkspaceStageStateV1.Completed
                : bindings.ValidationEvidenceHashSha256 is null
                    ? StrategyWorkspaceStageStateV1.Unavailable
                    : StrategyWorkspaceStageStateV1.Pending,
            bindings.PaperBindingHashSha256 is not null
                ? "A Paper book is bound to the validated revision."
                : bindings.ValidationEvidenceHashSha256 is null
                    ? "Validated evidence is required before Paper."
                    : "Select a Paper book and approve the handoff."),
    ];

    private static StrategyWorkspaceStageSnapshotV1 OptionalStage(
        StrategyWorkspaceStageV1 stage,
        StrategyWorkspaceStageRequirementV1 requirement,
        string? artifactHash,
        string optionalText,
        string completedText) => requirement == StrategyWorkspaceStageRequirementV1.Skipped
        ? Stage(stage, requirement, StrategyWorkspaceStageStateV1.Skipped, optionalText)
        : artifactHash is not null
            ? Stage(stage, requirement, StrategyWorkspaceStageStateV1.Completed, completedText)
            : Stage(stage, requirement,
                requirement == StrategyWorkspaceStageRequirementV1.Required
                    ? StrategyWorkspaceStageStateV1.NeedsReview
                    : StrategyWorkspaceStageStateV1.Pending,
                optionalText);

    private static StrategyWorkspaceStageSnapshotV1 Stage(
        StrategyWorkspaceStageV1 stage,
        StrategyWorkspaceStageRequirementV1 requirement,
        StrategyWorkspaceStageStateV1 state,
        string text) => new(stage, requirement, state, text);

    private static void ValidateBindings(StrategyWorkspaceBindingsV1 bindings)
    {
        var issues = new List<StrategyWorkspaceIssueV1>();
        AddBindingIssues(bindings, issues);
        if (issues.Count > 0)
            throw new ArgumentException(string.Join(" ", issues.Select(issue => $"{issue.Code}: {issue.Message}")), nameof(bindings));
    }

    private static void AddBindingIssues(
        StrategyWorkspaceBindingsV1 bindings,
        ICollection<StrategyWorkspaceIssueV1> issues)
    {
        foreach (var (path, value) in new (string Path, string? Value)[]
        {
            ("briefHashSha256", bindings.BriefHashSha256),
            ("researchCaseHashSha256", bindings.ResearchCaseHashSha256),
            ("datasetDefinitionHashSha256", bindings.DatasetDefinitionHashSha256),
            ("featureSetHashSha256", bindings.FeatureSetHashSha256),
            ("confirmedIntentHashSha256", bindings.ConfirmedIntentHashSha256),
            ("drawingSemanticsHashSha256", bindings.DrawingSemanticsHashSha256),
            ("appearanceHashSha256", bindings.AppearanceHashSha256),
            ("authoredUnitSpecificationHashSha256", bindings.AuthoredUnitSpecificationHashSha256),
            ("buildArtifactHashSha256", bindings.BuildArtifactHashSha256),
            ("validationEvidenceHashSha256", bindings.ValidationEvidenceHashSha256),
            ("paperBindingHashSha256", bindings.PaperBindingHashSha256),
        })
        {
            if (value is not null && !IsSha256(value))
                issues.Add(new("WORKSPACE_HASH_INVALID", $"bindings.{path}", "Artifact hashes must be lowercase SHA-256 values."));
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}
