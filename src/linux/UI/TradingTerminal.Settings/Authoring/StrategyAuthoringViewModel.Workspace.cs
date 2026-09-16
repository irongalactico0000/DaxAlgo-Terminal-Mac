using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

public sealed partial class StrategyAuthoringViewModel
{
    [ObservableProperty]
    private StrategyWorkspaceRevisionV1 _strategyWorkspace =
        StrategyWorkspaceRevisionPolicyV1.Create("myStrategy");

    public string WorkspaceRevisionText =>
        $"revision {StrategyWorkspace.Revision} · {StrategyWorkspaceCanonicalJsonV1.Hash(StrategyWorkspace)[..12]}…";

    public string BriefStageState => StageStateText(StrategyWorkspaceStageV1.Brief);
    public string ResearchStageState => StageStateText(StrategyWorkspaceStageV1.Research);
    public string DesignStageState => StageStateText(StrategyWorkspaceStageV1.Design);
    public string BuildStageState => StageStateText(StrategyWorkspaceStageV1.Build);
    public string ValidateStageState => StageStateText(StrategyWorkspaceStageV1.Validate);
    public string PaperStageState => StageStateText(StrategyWorkspaceStageV1.Paper);

    private void ResetStrategyWorkspace()
    {
        HistoricalValidationEvidence = null;
        _historicalValidationParameters = null;
        var workspaceId = string.IsNullOrWhiteSpace(StrategyId) ? DefaultStrategyId : StrategyId.Trim();
        StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Create(
            workspaceId,
            activeStage: ToWorkspaceStage(ActiveScreen));
    }

    private void RestoreStrategyWorkspace(AuthoringSessionSnapshot session, ref string? restoreWarning)
    {
        HistoricalValidationEvidence = null;
        _historicalValidationParameters = null;
        StrategyWorkspaceRevisionV1? restored = null;
        if (!string.IsNullOrWhiteSpace(session.StrategyWorkspaceJson))
        {
            try
            {
                restored = StrategyWorkspaceCanonicalJsonV1.Deserialize(session.StrategyWorkspaceJson);
                if (!string.Equals(restored.WorkspaceId, session.StrategyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The saved workspace belongs to another authoring session.");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not restore strategy workspace for {Id}", session.StrategyId);
                restoreWarning = "The authoring session was restored, but its workspace revision was invalid and was rebuilt from the retained artifacts.";
            }
        }

        StrategyWorkspace = restored ?? StrategyWorkspaceRevisionPolicyV1.Create(
            session.StrategyId,
            activeStage: ToWorkspaceStage(ActiveScreen),
            revisionReason: restored is null ? "Migrated legacy authoring session" : "Workspace restored");

        // A historical Boolean registration flag and workspace hash are not sufficient executable
        // or backtest receipts after restart. Rebuild from retained source/specification and clear
        // authority that cannot be independently re-established by this session model yet.
        var bindings = BuildCurrentWorkspaceBindings(StrategyWorkspace.Bindings) with
        {
            BuildArtifactHashSha256 = null,
            ValidationEvidenceHashSha256 = null,
            PaperBindingHashSha256 = null,
        };
        StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Revise(
            StrategyWorkspace,
            StrategyWorkspaceChangeKindV1.NavigationOnly,
            bindings,
            ToWorkspaceStage(ActiveScreen),
            revisionReason: "Reconciled retained authoring artifacts after restore");
    }

    private void SynchronizeStrategyWorkspace()
    {
        var workspaceId = string.IsNullOrWhiteSpace(StrategyId) ? DefaultStrategyId : StrategyId.Trim();
        var current = StrategyWorkspace;
        if (!string.Equals(current.WorkspaceId, workspaceId, StringComparison.Ordinal))
        {
            StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Create(
                workspaceId,
                BuildCurrentWorkspaceBindings(new StrategyWorkspaceBindingsV1()),
                ToWorkspaceStage(ActiveScreen),
                revisionReason: "Strategy identity changed");
            return;
        }

        StrategyWorkspace = StrategyWorkspaceRevisionPolicyV1.Revise(
            current,
            StrategyWorkspaceChangeKindV1.NavigationOnly,
            BuildCurrentWorkspaceBindings(current.Bindings),
            ToWorkspaceStage(ActiveScreen),
            revisionReason: "Synchronized authoring artifacts");
    }

    private StrategyWorkspaceBindingsV1 BuildCurrentWorkspaceBindings(StrategyWorkspaceBindingsV1 retained)
    {
        var brief = string.IsNullOrWhiteSpace(_fourLaneStrategyBrief)
            ? null
            : _fourLaneStrategyBrief.Trim();
        var briefHash = CandidateContentHash is { Length: 64 } candidateHash
            ? candidateHash
            : brief is null
                ? null
                : StrategyWorkspaceCanonicalJsonV1.HashArtifact(new StrategyWorkspaceBriefBinding(
                    brief,
                    ChartReferences
                        .Select(reference => reference.Reference.ContentHashSha256)
                        .Order(StringComparer.Ordinal)
                        .ToArray()));
        var researchHash = _strategyIntentResearchCase is null
            ? null
            : ResearchCaseCanonicalJsonV1.Hash(_strategyIntentResearchCase);
        var datasetHash = ResearchDatasetDefinition is null
            ? null
            : ResearchDatasetCanonicalJsonV1.Hash(ResearchDatasetDefinition);
        var featureSetHash = ResearchExperimentEvidence is not null &&
                             string.Equals(
                                 ResearchExperimentEvidence.DatasetHashSha256,
                                 datasetHash,
                                 StringComparison.Ordinal)
            ? ResearchExperimentCanonicalJsonV1.Hash(ResearchExperimentEvidence)
            : null;
        var intentHash = ConfirmedStrategyIntent is null
            ? null
            : StrategyIntentCanonicalJsonV1.Hash(ConfirmedStrategyIntent);
        var drawingHash = AuthoredUnitSpecification is { Kind: AuthoredUnitKindV1.Strategy }
            ? StrategyWorkspaceCanonicalJsonV1.HashArtifact(AuthoredUnitSpecification.Drawing)
            : null;
        var specificationHash = AuthoredUnitSpecification is { Kind: AuthoredUnitKindV1.Strategy }
            ? AuthoredUnitSpecificationCanonicalJsonV1.Hash(AuthoredUnitSpecification)
            : null;
        var buildHash = IsRegistered && specificationHash is not null
            ? StrategyWorkspaceCanonicalJsonV1.HashArtifact(new StrategyWorkspaceBuildBinding(
                specificationHash,
                Files
                    .OrderBy(file => file.Name, StringComparer.Ordinal)
                    .Select(file => new StrategyWorkspaceSourceBinding(
                        file.Name,
                        StrategyWorkspaceCanonicalJsonV1.HashArtifact(file.Content)))
                    .ToArray()))
            : null;
        var validationHash = HistoricalValidationEvidence is { } validation &&
                             string.Equals(validation.Context.WorkspaceId, StrategyWorkspace.WorkspaceId, StringComparison.Ordinal) &&
                             string.Equals(validation.Context.AuthoredUnitSpecificationHashSha256, specificationHash, StringComparison.Ordinal) &&
                             string.Equals(validation.Context.BuildArtifactHashSha256, buildHash, StringComparison.Ordinal) &&
                             string.Equals(validation.Context.FeatureSetHashSha256, featureSetHash, StringComparison.Ordinal)
            ? HistoricalValidationEvidenceCanonicalJsonV1.Hash(validation)
            : null;

        return retained with
        {
            BriefHashSha256 = briefHash,
            ResearchCaseHashSha256 = researchHash,
            DatasetDefinitionHashSha256 = datasetHash,
            FeatureSetHashSha256 = featureSetHash,
            ConfirmedIntentHashSha256 = intentHash,
            DrawingSemanticsHashSha256 = drawingHash,
            AuthoredUnitSpecificationHashSha256 = specificationHash,
            BuildArtifactHashSha256 = buildHash,
            ValidationEvidenceHashSha256 = validationHash,
            PaperBindingHashSha256 = validationHash is null ? null : retained.PaperBindingHashSha256,
        };
    }

    private string StageStateText(StrategyWorkspaceStageV1 stage)
    {
        var snapshot = StrategyWorkspace.Stage(stage);
        // Research/Design may be non-gating in the revision policy, but they are first-class
        // workflow stages — never label them "optional" in the stage pills.
        return snapshot.Requirement switch
        {
            StrategyWorkspaceStageRequirementV1.Skipped => "SKIPPED",
            _ => snapshot.State switch
            {
                StrategyWorkspaceStageStateV1.NeedsReview => "REVIEW",
                StrategyWorkspaceStageStateV1.Completed => "READY",
                StrategyWorkspaceStageStateV1.Unavailable => "LOCKED",
                _ => "PENDING",
            },
        };
    }

    partial void OnStrategyWorkspaceChanged(StrategyWorkspaceRevisionV1 value)
    {
        OnPropertyChanged(nameof(WorkspaceRevisionText));
        OnPropertyChanged(nameof(BriefStageState));
        OnPropertyChanged(nameof(ResearchStageState));
        OnPropertyChanged(nameof(DesignStageState));
        OnPropertyChanged(nameof(BuildStageState));
        OnPropertyChanged(nameof(ValidateStageState));
        OnPropertyChanged(nameof(PaperStageState));
        OnPropertyChanged(nameof(HasHistoricalValidationEvidence));
        OnPropertyChanged(nameof(HistoricalValidationStatusText));
        OnPropertyChanged(nameof(CanRunHistoricalValidation));
        NotifyAuthoringScreenStateChanged();
        NotifyWorkingFlowMapChanged();
    }

    private sealed record StrategyWorkspaceBriefBinding(
        string Brief,
        IReadOnlyList<string> ChartReferenceHashes);

    private sealed record StrategyWorkspaceBuildBinding(
        string SpecificationHashSha256,
        IReadOnlyList<StrategyWorkspaceSourceBinding> Sources);

    private sealed record StrategyWorkspaceSourceBinding(string Name, string ContentHashSha256);
}
