namespace TradingTerminal.App.Authoring;

/// <summary>
/// Strategy Builder workflow vocabulary:
/// Design → Build → Validate → Run (numbered stages).
/// Research Studio is optional chrome (not a numbered stage). Brief is project metadata, not a rail stage.
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
        "Optional: Research Studio → save a finding. Design makes entry/exit/sizing/risk/orders explicit → " +
        "Build registers a version → Validate on history → Run on Paper (or Live when authorized).";

    /// <summary>Builder rail vocabulary: Research optional · Design → Build → Validate → Run.</summary>
    public string WorkingFlowMapText
    {
        get
        {
            string Mark(string label, bool done) => done ? $"{label} ✓" : label;

            var research = Mark("Research (optional)",
                StrategyWorkspace.Bindings.ResearchCaseHashSha256 is not null ||
                StrategyWorkspace.Bindings.DatasetDefinitionHashSha256 is not null ||
                HasChartReferences ||
                HasResearchDesignHandoff);
            var design = Mark("1 Design",
                HasResearchDesignHandoff ||
                HasDesignRuleDraft ||
                StrategyWorkspace.Bindings.ConfirmedIntentHashSha256 is not null);
            var build = Mark("2 Build",
                IsRegistered || StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null);
            var validate = Mark("3 Validate", HasHistoricalValidationEvidence);
            var run = Mark("4 Run", StrategyWorkspace.Bindings.PaperBindingHashSha256 is not null);
            return $"{research} · {design} → {build} → {validate} → {run}";
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

            // Research Studio (or legacy Research stage) — never skip ahead because a prior spec exists.
            if (IsResearchStage || IsResearchStudioShell)
            {
                if (IsScanningResearchGallery)
                    return "Research: scanning local history for outcome events…";
                if (HasResearchOutcomeGalleryMatches && SelectedResearchGalleryCard is null)
                    return "Next: select an event in RESULTS to load it on the Research chart.";
                if (SelectedResearchGalleryCard is not null || HasResearchChartSelection)
                {
                    if (ResearchEventSampleCount == 0 && !HasResearchReferenceA && !HasResearchReferenceB)
                        return "Next: review indicators, optionally label B/C/N, or Save finding — then Use in Strategy Builder.";
                    return "Next: Use in Strategy Builder to attach this finding to Design rules.";
                }
                if (HasResearchOutcomeGalleryResult && !HasResearchOutcomeGalleryMatches)
                    return "Next: no gallery hits — brush manually on the Research chart or try another scan.";
                return "Next: Rank or load a chart, add indicators, save a finding when ready.";
            }

            if (IsChartDesignStage)
            {
                if (!HasCandidate && AuthoredUnitSpecification is null && !HasDesignRuleDraft)
                    return "Next: write entry, exit, sizing, risk, and order rules (Research Studio is optional).";
                return "Next: confirm the design, then open Build to compile and register.";
            }

            if (!(IsRegistered || StrategyWorkspace.Bindings.BuildArtifactHashSha256 is not null))
            {
                if (AuthoredUnitSpecification is null && !HasCandidate)
                    return "Next: finish Design rules, then open Build.";
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
