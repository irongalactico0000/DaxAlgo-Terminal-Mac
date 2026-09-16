namespace TradingTerminal.App.Authoring;

/// <summary>
/// Strategy Builder workflow vocabulary:
/// Research → Design → Build → Validate → Run.
/// Brief is an editable project description, not the first stage.
/// </summary>
public sealed partial class StrategyAuthoringViewModel
{
    private bool _showWorkflowHelp;

    /// <summary>Expanded help only — never permanent top chrome.</summary>
    public bool ShowWorkingFlowMap => GenerateCandidateFirst && _showWorkflowHelp;

    public bool ShowWorkflowHelp
    {
        get => _showWorkflowHelp;
        set
        {
            if (_showWorkflowHelp == value) return;
            _showWorkflowHelp = value;
            OnPropertyChanged(nameof(ShowWorkflowHelp));
            OnPropertyChanged(nameof(ShowWorkingFlowMap));
            OnPropertyChanged(nameof(WorkflowHelpToggleText));
        }
    }

    public string WorkflowHelpToggleText => ShowWorkflowHelp ? "Hide help" : "Help";

    public string HowStrategyGetsInText =>
        "Research on the chart and save observations → Design makes rules precise → Build registers a version → " +
        "Validate on history → Run on Paper (or Live when authorized). Brief is the editable project description.";

    /// <summary>Same five stages as the toolbar pills (one vocabulary).</summary>
    public string WorkingFlowMapText
    {
        get
        {
            string Mark(string label, bool done) => done ? $"{label} ✓" : label;

            var research = Mark("1 Research",
                StrategyWorkspace.Bindings.ResearchCaseHashSha256 is not null ||
                StrategyWorkspace.Bindings.DatasetDefinitionHashSha256 is not null ||
                HasChartReferences);
            var design = Mark("2 Design",
                HasResearchDesignHandoff ||
                StrategyWorkspace.Bindings.ConfirmedIntentHashSha256 is not null);
            var build = Mark("3 Build",
                IsRegistered || StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null);
            var validate = Mark("4 Validate", HasHistoricalValidationEvidence);
            var run = Mark("5 Run", StrategyWorkspace.Bindings.PaperBindingHashSha256 is not null);
            return $"{research} → {design} → {build} → {validate} → {run}";
        }
    }

    public string WorkingFlowNextActionText
    {
        get
        {
            if (!GenerateCandidateFirst)
                return "Expert C#: Compile & Register, then Validate / Run when available.";

            if (IsGenerating)
                return "Wait for the current task to finish.";

            // While Research is selected, next-action must describe the research task — never skip
            // ahead to Build because a visualizer/spec freeze left AuthoredUnitSpecification set.
            if (IsResearchStage)
            {
                if (IsScanningResearchGallery)
                    return "Research: scanning local history for outcome events…";
                if (HasResearchOutcomeGalleryMatches && SelectedResearchGalleryCard is null)
                    return "Next: select an event in RESULTS to load it on the Research chart.";
                if (SelectedResearchGalleryCard is not null || HasResearchChartSelection)
                {
                    if (ResearchEventSampleCount == 0 && !HasResearchReferenceA && !HasResearchReferenceB)
                        return "Next: review indicators on the linked chart, label B/C/N or Save reference — then Use in Design.";
                    return "Next: Use in Design to turn this observation into explicit rules.";
                }
                if (HasResearchOutcomeGalleryResult && !HasResearchOutcomeGalleryMatches)
                    return "Next: no gallery hits — brush manually on the Research chart or try another scan.";
                return "Next: Load chart, add indicators, run a scan or brush an observation.";
            }

            if (IsChartDesignStage)
            {
                if (!HasCandidate && AuthoredUnitSpecification is null)
                    return "Next: turn Research observations into explicit entry, exit, sizing, and risk rules.";
                return "Next: confirm the design, then open Build to compile and register.";
            }

            if (!(IsRegistered || StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null))
            {
                if (AuthoredUnitSpecification is null && !HasCandidate)
                    return "Next: finish Research/Design, then open Build.";
                return "Next: open Build → compile and register.";
            }

            if (!HasHistoricalValidationEvidence)
                return "Next: open Validate → run historical validation.";

            if (StrategyWorkspace.Bindings.PaperBindingHashSha256 is null)
                return "Next: open Run → bind Paper book and start.";

            return "Ready: Run book bound for this revision.";
        }
    }

    public string WorkingFlowYouAreHereText =>
        $"You are here: {ActiveScreenTitle}.";

    public void ToggleWorkflowHelp() => ShowWorkflowHelp = !ShowWorkflowHelp;

    private void NotifyWorkingFlowMapChanged()
    {
        OnPropertyChanged(nameof(ShowWorkingFlowMap));
        OnPropertyChanged(nameof(ShowWorkflowHelp));
        OnPropertyChanged(nameof(WorkflowHelpToggleText));
        OnPropertyChanged(nameof(HowStrategyGetsInText));
        OnPropertyChanged(nameof(WorkingFlowMapText));
        OnPropertyChanged(nameof(WorkingFlowNextActionText));
        OnPropertyChanged(nameof(WorkingFlowYouAreHereText));
    }
}
