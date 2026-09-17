using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Core.Strategies.Specification;

namespace TradingTerminal.App.Authoring;

public sealed partial class StrategyAuthoringViewModel
{
    private const string ReviewEvidenceId = "review-evidence";
    private const string ReviewedResolutionProvenance =
        "Reviewed and recorded by the user in Strategy request review.";
    private bool _strategyIntentReviewStarted;
    private bool _isApplyingStrategyIntentReview;
    private ResearchCaseV1? _strategyIntentResearchCase;
    private StrategySpec? _strategyIntentClassification;
    private readonly Dictionary<StrategyIntentRequirementStashKey, StrategySemanticRequirementV1>
        _strategyIntentRequirementStash = [];
    private StrategyIntentRequirementContext? _strategyIntentRequirementContext;

    [ObservableProperty] private StrategyIntentDraftV1? _strategyIntentDraft;
    [ObservableProperty] private string? _strategyIntentDraftHash;
    [ObservableProperty] private ConfirmedStrategyIntentV1? _confirmedStrategyIntent;
    [ObservableProperty] private string? _confirmedStrategyIntentHash;
    [ObservableProperty] private StrategyIntentProfileOption? _selectedStrategyIntentProfile;
    [ObservableProperty] private StrategyIntentShapeOption? _selectedStrategyIntentShape;
    [ObservableProperty] private string _strategyResearchObjective = string.Empty;
    [ObservableProperty] private string _strategyResearchHypothesis = string.Empty;
    [ObservableProperty] private string _strategyResearchEvidence = string.Empty;
    [ObservableProperty] private string _strategyResearchPointInTimeRule = string.Empty;
    [ObservableProperty] private string _strategyResearchQualificationRule = string.Empty;
    [ObservableProperty] private string _strategyResearchFalsifier = string.Empty;
    [ObservableProperty] private string _strategyIntentStatusText =
        "Confirm strategy meaning first, then review the research case and implementation questions.";

    public ObservableCollection<StrategyIntentProfileOption> StrategyIntentProfiles { get; } =
    [
        StrategyIntentProfileOption.CreateSignalOnly(),
    ];

    public IReadOnlyList<StrategyIntentShapeOption> StrategyIntentShapes { get; } =
    [
        new(StrategyIntentKindV1.PositionTarget, "Position target",
            "Long, short, flat, resize, exit, or reverse one position or eligible instrument."),
        new(StrategyIntentKindV1.MultiLegTarget, "Coordinated multi-leg target",
            "Pairs, arbitrage, spreads, and option structures whose legs must be managed together."),
        new(StrategyIntentKindV1.PortfolioTarget, "Portfolio targets",
            "Target weights or exposures for a universe, followed by rebalance decisions."),
        new(StrategyIntentKindV1.QuoteSet, "Two-sided quotes",
            "Bid and ask quote intents with refresh, cancel/replace, and inventory control."),
        new(StrategyIntentKindV1.ExecutionSchedule, "Parent-order schedule",
            "TWAP, VWAP, POV, routing, slicing, pause, residual, and completion behavior."),
        new(StrategyIntentKindV1.SignalOnly, "Publish a signal only",
            "Publish value, confidence, and expiry without sizing, orders, or position exits."),
    ];

    public ObservableCollection<StrategyIntentRequirementRow> StrategyIntentRequirements { get; } = [];
    public ObservableCollection<StrategyResearchEvidenceRow> StrategyResearchEvidenceRows { get; } = [];
    public ObservableCollection<StrategyResearchFalsifierRow> StrategyResearchFalsifierRows { get; } = [];
    public ObservableCollection<StrategyResearchUnresolvedRow> StrategyResearchUnresolvedRows { get; } = [];
    public ObservableCollection<StrategyResearchResolvedRow> StrategyResearchResolvedRows { get; } = [];
    public ObservableCollection<StrategyIntentQuestionV1> StrategyIntentQuestions { get; } = [];
    public ObservableCollection<StrategyIntentIssueV1> StrategyIntentIssues { get; } = [];

    public bool HasStrategyIntentReview => _strategyIntentReviewStarted || StrategyIntentDraft is not null;
    public bool HasConfirmedStrategyIntent => ConfirmedStrategyIntent is not null;
    public string StrategyIntentFamilyText =>
        SelectedStrategyIntentProfile is null || SelectedStrategyIntentShape is null
            ? "Choose a strategy profile and decision shape."
            : FriendlyFamily(StrategyIntentCompletenessV1.ClassifyFamily(
                new StrategyIntentModelV1(
                    SelectedStrategyIntentShape.Kind,
                    SelectedStrategyIntentShape.ExtensionId),
                SelectedStrategyIntentProfile.Classification));

    public bool CanConfirmStrategyIntentReview =>
        StrategyIntentDraft is not null &&
        StrategyIntentDraftHash is not null &&
        ConfirmedStrategyIntent is null &&
        CurrentCandidate?.Status == StrategyCandidateStatusV1.Confirmed &&
        StrategyIntentQuestions.Count == 0 &&
        StrategyIntentIssues.Count == 0 &&
        !IsGenerating;

    /// <summary>
    /// The exact local gate used by downstream implementation commands. A successful review still
    /// grants no compile, backtest, paper-trading, or live-trading authority.
    /// </summary>
    public bool CanEnterFourLaneConformance =>
        ConfirmedStrategyIntent is not null &&
        StrategyIntentDraft is not null &&
        ConfirmedStrategyIntentHash is { Length: 64 } &&
        string.Equals(
            ConfirmedStrategyIntentHash,
            StrategyIntentCanonicalJsonV1.Hash(ConfirmedStrategyIntent),
            StringComparison.Ordinal) &&
        _strategyIntentResearchCase is not null &&
        _strategyIntentClassification is not null &&
        CurrentCandidate is not null &&
        StrategyIntentConfirmationV1.ValidateConfirmed(
            ConfirmedStrategyIntent,
            CurrentCandidate,
            _strategyIntentResearchCase,
            _strategyIntentClassification,
            StrategyIntentDraft,
            _strategyIntentExtensionRegistry).Count == 0;

    public bool CanGenerateStrategyImplementations => CanEnterFourLaneConformance;

    /// <summary>Adds a host-owned strategy classification choice without selecting it.</summary>
    public void AddStrategyIntentProfile(StrategyStarterBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (StrategyIntentProfiles.Any(profile =>
                string.Equals(profile.Id, brief.Id, StringComparison.Ordinal)))
            return;

        StrategyIntentProfiles.Add(StrategyIntentProfileOption.FromBrief(brief));
    }

    /// <summary>Selects the classification proposed by a starter card; the user may still change it.</summary>
    public void SelectStrategyIntentProfile(StrategyStarterBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        AddStrategyIntentProfile(brief);
        SelectedStrategyIntentProfile = StrategyIntentProfiles.First(profile =>
            string.Equals(profile.Id, brief.Id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Opens the local interview after semantic candidate confirmation. It calls no model and does
    /// not invent evidence, causal timing, falsification, sizing, order, or lifecycle decisions.
    /// </summary>
    public void BeginStrategyIntentReview()
    {
        if (CurrentCandidate?.Status != StrategyCandidateStatusV1.Confirmed)
        {
            StrategyIntentStatusText = "Confirm strategy meaning before opening the strategy request review.";
            NotifyStrategyIntentStateChanged();
            return;
        }

        _isApplyingStrategyIntentReview = true;
        try
        {
            _strategyIntentReviewStarted = true;
            ConfirmedStrategyIntent = null;
            ConfirmedStrategyIntentHash = null;
            StrategyResearchObjective = CurrentCandidate.RawIntent;
            StrategyResearchHypothesis = CurrentCandidate.Interpretation.Summary;
            StrategyResearchEvidence = string.Empty;
            StrategyResearchPointInTimeRule = string.Empty;
            StrategyResearchQualificationRule = string.Empty;
            StrategyResearchFalsifier = string.Empty;
            var statementIds = ConfirmedCandidateStatementIds(CurrentCandidate);
            StrategyResearchEvidenceRows.Clear();
            StrategyResearchEvidenceRows.Add(StrategyResearchEvidenceRow.FromEvidence(
                new ResearchEvidenceRequirementV1(
                    ReviewEvidenceId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    true,
                    statementIds),
                OnResearchCollectionEdited));
            StrategyResearchFalsifierRows.Clear();
            StrategyResearchFalsifierRows.Add(StrategyResearchFalsifierRow.FromFalsifier(
                new ResearchFalsifierV1(
                    "review-falsifier",
                    string.Empty,
                    true,
                    statementIds),
                OnResearchCollectionEdited));
            StrategyResearchUnresolvedRows.Clear();
            StrategyResearchResolvedRows.Clear();
            _strategyIntentRequirementStash.Clear();
            _strategyIntentRequirementContext = null;

            if (SelectedStrategyIntentProfile is not null)
                SelectedStrategyIntentShape = FindShape(SuggestShape(SelectedStrategyIntentProfile.Classification));
            else
                SelectedStrategyIntentShape ??= FindShape(StrategyIntentKindV1.PositionTarget);

            RebuildRequirementRows(preserveExistingAnswers: false);
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }

        RebuildStrategyIntentDraft(save: true);
        if (SelectedStrategyIntentProfile is null)
            StrategyIntentStatusText = "Choose the strategy profile that matches the confirmed idea. No implementation agent has run.";
        NotifyStrategyIntentStateChanged();
    }

    /// <summary>
    /// Loads a host-built or restored payload into the same editable review. This method performs no
    /// provider call; changing any displayed answer rebuilds and relocks the local draft.
    /// </summary>
    public void ReviewStrategyIntent(
        ResearchCaseV1 researchCase,
        StrategySpec classification,
        StrategyIntentDraftV1 draft)
    {
        ArgumentNullException.ThrowIfNull(researchCase);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(draft);
        InvalidateImplementationResultsForStrategyRequestChange();

        _isApplyingStrategyIntentReview = true;
        try
        {
            _strategyIntentReviewStarted = true;
            _strategyIntentResearchCase = researchCase;
            _strategyIntentClassification = classification;
            _strategyIntentRequirementStash.Clear();
            _strategyIntentRequirementContext = null;

            var classificationHash = StrategySpecCanonicalJsonV1.Hash(classification);
            var profile = StrategyIntentProfiles.FirstOrDefault(option =>
                string.Equals(
                    StrategySpecCanonicalJsonV1.Hash(option.Classification),
                    classificationHash,
                    StringComparison.Ordinal));
            if (profile is null)
            {
                profile = StrategyIntentProfileOption.FromRestoredClassification(classification, classificationHash);
                StrategyIntentProfiles.Add(profile);
            }

            SelectedStrategyIntentProfile = profile;
            SelectedStrategyIntentShape = FindShape(draft.IntentModel.Kind) ??
                                          new StrategyIntentShapeOption(
                                              draft.IntentModel.Kind,
                                              "Reviewed extension",
                                              "A restored governed extension; unavailable unless a host registry owns it.",
                                              draft.IntentModel.ExtensionId);
            StrategyResearchObjective = researchCase.Objective;
            StrategyResearchHypothesis = researchCase.Hypothesis;
            StrategyResearchEvidence = researchCase.EvidenceRequirements?.FirstOrDefault()?.Description ?? string.Empty;
            StrategyResearchPointInTimeRule = researchCase.EvidenceRequirements?.FirstOrDefault()?.PointInTimeRule ?? string.Empty;
            StrategyResearchQualificationRule = researchCase.EvidenceRequirements?.FirstOrDefault()?.QualificationRule ?? string.Empty;
            StrategyResearchFalsifier = researchCase.Falsifiers?.FirstOrDefault()?.Description ?? string.Empty;
            LoadResearchRows(researchCase);

            StrategyIntentDraft = draft;
            StrategyIntentDraftHash = StrategyIntentCanonicalJsonV1.Hash(draft);
            ConfirmedStrategyIntent = null;
            ConfirmedStrategyIntentHash = null;
            LoadRequirementRows(draft);
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }

        RefreshStrategyIntentAssessment();
        SetStrategyIntentReviewStatus();
        NotifyStrategyIntentStateChanged();
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmStrategyIntentReviewAction))]
    private void ConfirmStrategyIntentReview()
    {
        if (StrategyIntentDraft is null || StrategyIntentDraftHash is null ||
            CurrentCandidate is null || _strategyIntentResearchCase is null ||
            _strategyIntentClassification is null)
            return;

        var result = StrategyIntentConfirmationV1.Confirm(
            CurrentCandidate,
            _strategyIntentResearchCase,
            _strategyIntentClassification,
            StrategyIntentDraft,
            StrategyIntentDraftHash,
            _strategyIntentExtensionRegistry);
        ApplyStrategyIntentAssessment(result);
        if (!result.Success)
        {
            StrategyIntentStatusText = "Confirmation failed closed. Resolve the visible research and strategy questions.";
            NotifyStrategyIntentStateChanged();
            return;
        }

        ConfirmedStrategyIntent = result.Intent;
        ConfirmedStrategyIntentHash = StrategyIntentCanonicalJsonV1.Hash(result.Intent!);
        StrategyIntentStatusText =
            "Strategy request confirmed for implementation work only. No backtest result, paper approval, or live authority was granted.";
        Append(AuthoringMessage.Tool(
            "Ok",
            "Strategy request confirmed",
            "Implementation agents may now start; execution and approval remain locked."));
        NotifyStrategyIntentStateChanged();
        Save();
    }

    private bool CanConfirmStrategyIntentReviewAction() => CanConfirmStrategyIntentReview;

    private void RebuildStrategyIntentDraft(bool save)
    {
        if (_isApplyingStrategyIntentReview) return;

        InvalidateImplementationResultsForStrategyRequestChange();
        ConfirmedStrategyIntent = null;
        ConfirmedStrategyIntentHash = null;
        StrategyIntentQuestions.Clear();
        StrategyIntentIssues.Clear();

        if (!_strategyIntentReviewStarted || CurrentCandidate is null)
        {
            StrategyIntentDraft = null;
            StrategyIntentDraftHash = null;
            NotifyStrategyIntentStateChanged();
            return;
        }

        if (SelectedStrategyIntentProfile is null || SelectedStrategyIntentShape is null)
        {
            _strategyIntentResearchCase = null;
            _strategyIntentClassification = null;
            StrategyIntentDraft = null;
            StrategyIntentDraftHash = null;
            StrategyIntentStatusText = "Choose a strategy profile and decision shape before answering implementation questions.";
            NotifyStrategyIntentStateChanged();
            if (save) Save();
            return;
        }

        var statementIds = ConfirmedCandidateStatementIds(CurrentCandidate);
        var researchCase = new ResearchCaseV1(
            ResearchCaseV1.CurrentSchemaVersion,
            $"research/{CurrentCandidate.CandidateId}",
            CurrentCandidate.CandidateId,
            StrategyCandidateCanonicalJsonV1.Hash(CurrentCandidate),
            StrategyResearchObjective,
            StrategyResearchHypothesis,
            StrategyResearchEvidenceRows.Select(static row => row.ToEvidence()).ToArray(),
            StrategyResearchFalsifierRows.Select(static row => row.ToFalsifier()).ToArray(),
            StrategyResearchUnresolvedRows.Select(static row => row.ToUnresolvedItem()).ToArray(),
            StrategyResearchResolvedRows.Select(static row => row.ToResolvedItem()).ToArray());
        var classification = SelectedStrategyIntentProfile.Classification;
        var defaultEvidenceIds = DefaultRequirementEvidenceIds(
            researchCase.EvidenceRequirements.Select(static evidence => evidence.EvidenceId));
        var requirements = StrategyIntentRequirements
            .Select(row => row.ToRequirement(statementIds, defaultEvidenceIds))
            .ToArray();
        var draft = new StrategyIntentDraftV1(
            StrategyIntentDraftV1.CurrentSchemaVersion,
            $"intent/{CurrentCandidate.CandidateId}",
            CurrentCandidate.CandidateId,
            CurrentCandidate.Revision,
            StrategyCandidateCanonicalJsonV1.Hash(CurrentCandidate),
            ResearchCaseCanonicalJsonV1.Hash(researchCase),
            new StrategyClassificationBindingV1(
                classification.Id,
                StrategySpecCanonicalJsonV1.Hash(classification)),
            new StrategyIntentModelV1(
                SelectedStrategyIntentShape.Kind,
                SelectedStrategyIntentShape.ExtensionId),
            StrategyIntentCompletenessV1.CatalogVersion,
            requirements);

        _strategyIntentResearchCase = researchCase;
        _strategyIntentClassification = classification;
        StrategyIntentDraft = draft;
        StrategyIntentDraftHash = StrategyIntentCanonicalJsonV1.Hash(draft);
        RefreshStrategyIntentAssessment();
        SetStrategyIntentReviewStatus();
        NotifyStrategyIntentStateChanged();
        if (save) Save();
    }

    private void RefreshStrategyIntentAssessment()
    {
        StrategyIntentQuestions.Clear();
        StrategyIntentIssues.Clear();
        if (StrategyIntentDraft is null || StrategyIntentDraftHash is null ||
            CurrentCandidate is null || _strategyIntentResearchCase is null ||
            _strategyIntentClassification is null)
            return;

        ApplyStrategyIntentAssessment(StrategyIntentConfirmationV1.Confirm(
            CurrentCandidate,
            _strategyIntentResearchCase,
            _strategyIntentClassification,
            StrategyIntentDraft,
            StrategyIntentDraftHash,
            _strategyIntentExtensionRegistry));
    }

    private void ApplyStrategyIntentAssessment(StrategyIntentConfirmationResultV1 result)
    {
        StrategyIntentQuestions.Clear();
        foreach (var question in result.Questions) StrategyIntentQuestions.Add(question);
        StrategyIntentIssues.Clear();
        foreach (var issue in result.Issues) StrategyIntentIssues.Add(issue);
    }

    private void SetStrategyIntentReviewStatus()
    {
        if (StrategyIntentQuestions.Count > 0)
        {
            StrategyIntentStatusText =
                $"{StrategyIntentQuestions.Count} required strategy answer(s) remain. Material blanks cannot be confirmed.";
            return;
        }

        if (StrategyIntentIssues.Count > 0)
        {
            StrategyIntentStatusText =
                "The research case or strategy choices still need attention. Read the visible reasons below.";
            return;
        }

        StrategyIntentStatusText =
            "The strategy request is complete and ready for your confirmation. No implementation or backtest has run.";
    }

    private void RebuildRequirementRows(bool preserveExistingAnswers)
    {
        if (preserveExistingAnswers)
            CaptureRequirementRowsInStash();
        if (!preserveExistingAnswers)
        {
            _strategyIntentRequirementStash.Clear();
            _strategyIntentRequirementContext = null;
        }

        if (CurrentCandidate is null || SelectedStrategyIntentProfile is null || SelectedStrategyIntentShape is null)
        {
            StrategyIntentRequirements.Clear();
            _strategyIntentRequirementContext = null;
            return;
        }

        StrategyIntentRequirements.Clear();
        var targetContext = new StrategyIntentRequirementContext(
            StrategySpecCanonicalJsonV1.Hash(SelectedStrategyIntentProfile.Classification),
            SelectedStrategyIntentShape.Kind,
            SelectedStrategyIntentShape.ExtensionId);

        var emptyDraft = QuestionProjectionDraft(CurrentCandidate, SelectedStrategyIntentProfile.Classification,
            new StrategyIntentModelV1(
                SelectedStrategyIntentShape.Kind,
                SelectedStrategyIntentShape.ExtensionId),
            []);
        var questions = StrategyIntentCompletenessV1.Questions(
            emptyDraft,
            SelectedStrategyIntentProfile.Classification);
        foreach (var question in questions)
        {
            var targetKey = new StrategyIntentRequirementStashKey(
                targetContext.ClassificationHash,
                targetContext.IntentKind,
                targetContext.ExtensionId,
                question.RequirementId);
            _strategyIntentRequirementStash.TryGetValue(targetKey, out var prior);
            var mustBeNotApplicable = MustBeNotApplicable(question.RequirementId, SelectedStrategyIntentShape.Kind);
            var allowsNotApplicable = mustBeNotApplicable || AllowsNotApplicable(
                emptyDraft,
                SelectedStrategyIntentProfile.Classification,
                question);
            StrategyIntentRequirements.Add(prior is null
                ? StrategyIntentRequirementRow.FromQuestion(
                    question,
                    allowsNotApplicable,
                    mustBeNotApplicable,
                    null,
                    null,
                    OnStrategyIntentRequirementEdited)
                : StrategyIntentRequirementRow.FromRequirement(
                    prior,
                    question.Prompt,
                    allowsNotApplicable,
                    mustBeNotApplicable,
                    OnStrategyIntentRequirementEdited));
        }

        var activeRequirementIds = questions
            .Select(static question => question.RequirementId)
            .ToHashSet(StringComparer.Ordinal);
        var governedExtensions = _strategyIntentRequirementStash
            .Where(entry =>
                entry.Key.ClassificationHash == targetContext.ClassificationHash &&
                entry.Key.IntentKind == targetContext.IntentKind &&
                string.Equals(entry.Key.ExtensionId, targetContext.ExtensionId, StringComparison.Ordinal) &&
                !StrategyIntentCompletenessV1.IsKnownRequirementId(entry.Value.RequirementId))
            .Select(static entry => entry.Value)
            .ToDictionary(requirement => requirement.RequirementId, StringComparer.Ordinal);
        foreach (var prior in governedExtensions.Values.Where(requirement =>
                     !activeRequirementIds.Contains(requirement.RequirementId)))
        {
            StrategyIntentRequirements.Add(StrategyIntentRequirementRow.FromRequirement(
                prior,
                prior.Description,
                allowsNotApplicable: true,
                mustBeNotApplicable: false,
                changed: OnStrategyIntentRequirementEdited));
        }
        _strategyIntentRequirementContext = targetContext;
        ApplyPendingDesignOrdersIntentSeedsToRows();
    }

    private void CaptureRequirementRowsInStash()
    {
        if (CurrentCandidate is null) return;
        var statementIds = ConfirmedCandidateStatementIds(CurrentCandidate);
        var defaultEvidenceIds = DefaultRequirementEvidenceIds(
            StrategyResearchEvidenceRows.Select(static row => row.EvidenceId));
        foreach (var row in StrategyIntentRequirements)
        {
            var requirement = row.ToRequirement(statementIds, defaultEvidenceIds);
            if (_strategyIntentRequirementContext is { } context)
            {
                _strategyIntentRequirementStash[new StrategyIntentRequirementStashKey(
                    context.ClassificationHash,
                    context.IntentKind,
                    context.ExtensionId,
                    row.RequirementId)] = requirement;
            }
        }
    }

    private static IReadOnlyList<string> DefaultRequirementEvidenceIds(IEnumerable<string?> evidenceIds) =>
        evidenceIds
            .Where(static evidenceId => !string.IsNullOrWhiteSpace(evidenceId))
            .Select(static evidenceId => evidenceId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static evidenceId => evidenceId, StringComparer.Ordinal)
            .Take(1)
            .ToArray();

    private void LoadRequirementRows(StrategyIntentDraftV1 draft)
    {
        StrategyIntentRequirements.Clear();
        if (SelectedStrategyIntentProfile is null) return;

        var promptDraft = draft with { Requirements = [] };
        var prompts = StrategyIntentCompletenessV1.Questions(
            promptDraft,
            SelectedStrategyIntentProfile.Classification);
        var requirements = (draft.Requirements ?? [])
            .Where(static requirement => requirement is not null)
            .ToDictionary(requirement => requirement.RequirementId, StringComparer.Ordinal);
        foreach (var question in prompts)
        {
            requirements.TryGetValue(question.RequirementId, out var requirement);
            var mustBeNotApplicable = MustBeNotApplicable(question.RequirementId, draft.IntentModel.Kind);
            var allowsNotApplicable = mustBeNotApplicable || AllowsNotApplicable(
                promptDraft,
                SelectedStrategyIntentProfile.Classification,
                question);
            StrategyIntentRequirements.Add(requirement is null
                ? StrategyIntentRequirementRow.FromQuestion(
                    question,
                    allowsNotApplicable,
                    mustBeNotApplicable,
                    null,
                    null,
                    OnStrategyIntentRequirementEdited)
                : StrategyIntentRequirementRow.FromRequirement(
                    requirement,
                    question.Prompt,
                    allowsNotApplicable,
                    mustBeNotApplicable,
                    OnStrategyIntentRequirementEdited));
        }

        var promptIds = prompts.Select(question => question.RequirementId).ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in requirements.Values.Where(requirement =>
                     !promptIds.Contains(requirement.RequirementId)))
        {
            StrategyIntentRequirements.Add(StrategyIntentRequirementRow.FromRequirement(
                requirement,
                requirement.Description,
                allowsNotApplicable: true,
                mustBeNotApplicable: false,
                changed: OnStrategyIntentRequirementEdited));
        }
        _strategyIntentRequirementContext = new StrategyIntentRequirementContext(
            StrategySpecCanonicalJsonV1.Hash(SelectedStrategyIntentProfile.Classification),
            draft.IntentModel.Kind,
            draft.IntentModel.ExtensionId);
        CaptureRequirementRowsInStash();
    }

    private static bool AllowsNotApplicable(
        StrategyIntentDraftV1 emptyDraft,
        StrategySpec classification,
        StrategyIntentQuestionV1 question)
    {
        var probe = new StrategySemanticRequirementV1(
            question.RequirementId,
            question.Stage,
            StrategySemanticDispositionV1.NotApplicable,
            question.Prompt,
            true,
            new StrategyRequirementProvenanceV1([], [], "Applicability probe."),
            DispositionRationale: "Applicability probe.");
        var projected = StrategyIntentCompletenessV1.Questions(
            emptyDraft with { Requirements = [probe] }, classification);
        return projected.All(candidate => candidate.RequirementId != question.RequirementId);
    }

    private static bool MustBeNotApplicable(string requirementId, StrategyIntentKindV1 kind) =>
        kind == StrategyIntentKindV1.SignalOnly && requirementId is
            "exposure.not_applicable" or "execution.not_applicable" or
            "lifecycle.fill_handling_not_applicable" or "finish.not_applicable";

    private static StrategyIntentDraftV1 QuestionProjectionDraft(
        StrategyCandidateV1 candidate,
        StrategySpec classification,
        StrategyIntentModelV1 intentModel,
        IReadOnlyList<StrategySemanticRequirementV1> requirements) => new(
        StrategyIntentDraftV1.CurrentSchemaVersion,
        "question-projection",
        candidate.CandidateId,
        candidate.Revision,
        StrategyCandidateCanonicalJsonV1.Hash(candidate),
        new string('0', 64),
        new StrategyClassificationBindingV1(classification.Id, StrategySpecCanonicalJsonV1.Hash(classification)),
        intentModel,
        StrategyIntentCompletenessV1.CatalogVersion,
        requirements);

    private void OnStrategyIntentRequirementEdited()
    {
        if (_isApplyingStrategyIntentReview) return;
        RebuildStrategyIntentDraft(save: true);
    }

    private void OnResearchFieldEdited()
    {
        if (_isApplyingStrategyIntentReview || !_strategyIntentReviewStarted) return;
        _isApplyingStrategyIntentReview = true;
        try
        {
            if (StrategyResearchEvidenceRows.FirstOrDefault() is { } evidence)
            {
                evidence.Description = StrategyResearchEvidence;
                evidence.PointInTimeRule = StrategyResearchPointInTimeRule;
                evidence.QualificationRule = StrategyResearchQualificationRule;
            }
            if (StrategyResearchFalsifierRows.FirstOrDefault() is { } falsifier)
                falsifier.Description = StrategyResearchFalsifier;
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }
        RebuildStrategyIntentDraft(save: true);
    }

    private void OnResearchCollectionEdited()
    {
        if (_isApplyingStrategyIntentReview || !_strategyIntentReviewStarted) return;
        _isApplyingStrategyIntentReview = true;
        try
        {
            var evidence = StrategyResearchEvidenceRows.FirstOrDefault();
            StrategyResearchEvidence = evidence?.Description ?? string.Empty;
            StrategyResearchPointInTimeRule = evidence?.PointInTimeRule ?? string.Empty;
            StrategyResearchQualificationRule = evidence?.QualificationRule ?? string.Empty;
            StrategyResearchFalsifier = StrategyResearchFalsifierRows.FirstOrDefault()?.Description ?? string.Empty;
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }
        RebuildStrategyIntentDraft(save: true);
    }

    private void ResolveResearchUnresolvedRow(StrategyResearchUnresolvedRow row)
    {
        if (!row.CanResolve) return;
        var resolved = StrategyResearchResolvedRow.FromResolvedItem(
            row.ToResolvedItem(ReviewedResolutionProvenance));
        if (!StrategyResearchUnresolvedRows.Remove(row)) return;
        StrategyResearchResolvedRows.Add(resolved);
        OnResearchCollectionEdited();
    }

    private void LoadResearchRows(ResearchCaseV1 researchCase)
    {
        StrategyResearchEvidenceRows.Clear();
        foreach (var evidence in researchCase.EvidenceRequirements ?? [])
        {
            if (evidence is not null)
                StrategyResearchEvidenceRows.Add(
                    StrategyResearchEvidenceRow.FromEvidence(evidence, OnResearchCollectionEdited));
        }

        StrategyResearchFalsifierRows.Clear();
        foreach (var falsifier in researchCase.Falsifiers ?? [])
        {
            if (falsifier is not null)
                StrategyResearchFalsifierRows.Add(
                    StrategyResearchFalsifierRow.FromFalsifier(falsifier, OnResearchCollectionEdited));
        }

        StrategyResearchUnresolvedRows.Clear();
        foreach (var item in researchCase.UnresolvedItems ?? [])
        {
            if (item is not null)
                StrategyResearchUnresolvedRows.Add(
                    StrategyResearchUnresolvedRow.FromUnresolvedItem(
                        item,
                        OnResearchCollectionEdited,
                        ResolveResearchUnresolvedRow));
        }

        StrategyResearchResolvedRows.Clear();
        foreach (var item in researchCase.ResolvedItems ?? [])
        {
            if (item is not null)
                StrategyResearchResolvedRows.Add(StrategyResearchResolvedRow.FromResolvedItem(item));
        }
    }

    partial void OnSelectedStrategyIntentProfileChanged(StrategyIntentProfileOption? value)
    {
        OnPropertyChanged(nameof(StrategyIntentFamilyText));
        if (_isApplyingStrategyIntentReview || !_strategyIntentReviewStarted) return;

        _isApplyingStrategyIntentReview = true;
        try
        {
            if (value is not null)
                SelectedStrategyIntentShape = FindShape(SuggestShape(value.Classification));
            RebuildRequirementRows(preserveExistingAnswers: true);
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }
        RebuildStrategyIntentDraft(save: true);
    }

    partial void OnSelectedStrategyIntentShapeChanged(StrategyIntentShapeOption? value)
    {
        OnPropertyChanged(nameof(StrategyIntentFamilyText));
        if (_isApplyingStrategyIntentReview || !_strategyIntentReviewStarted) return;

        _isApplyingStrategyIntentReview = true;
        try
        {
            RebuildRequirementRows(preserveExistingAnswers: true);
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }
        RebuildStrategyIntentDraft(save: true);
    }

    partial void OnStrategyResearchObjectiveChanged(string value) => OnResearchFieldEdited();
    partial void OnStrategyResearchHypothesisChanged(string value) => OnResearchFieldEdited();
    partial void OnStrategyResearchEvidenceChanged(string value) => OnResearchFieldEdited();
    partial void OnStrategyResearchPointInTimeRuleChanged(string value) => OnResearchFieldEdited();
    partial void OnStrategyResearchQualificationRuleChanged(string value) => OnResearchFieldEdited();
    partial void OnStrategyResearchFalsifierChanged(string value) => OnResearchFieldEdited();

    private static StrategyIntentKindV1 SuggestShape(StrategySpec classification)
    {
        if (classification.Objective == StrategyObjectiveKind.LiquidityProvision ||
            classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.ContinuousQuoting))
            return StrategyIntentKindV1.QuoteSet;
        if (classification.Objective == StrategyObjectiveKind.Execution)
            return StrategyIntentKindV1.ExecutionSchedule;
        if (classification.Context.Topology is MarketTopologyKind.Pair or
                MarketTopologyKind.UnderlyingAndDerivative or MarketTopologyKind.MultiLeg ||
            classification.Context.Exposure is ExposureGeometryKind.Spread or ExposureGeometryKind.Arbitrage ||
            classification.Execution.Policies.Contains(StrategyExecutionPolicyKind.CoordinatedLegs))
            return StrategyIntentKindV1.MultiLegTarget;
        if (classification.Context.Topology == MarketTopologyKind.Basket)
            return classification.Objective is StrategyObjectiveKind.Allocation or
                       StrategyObjectiveKind.BenchmarkTracking ||
                   classification.Context.Exposure == ExposureGeometryKind.CrossSectionalLongShort
                ? StrategyIntentKindV1.PortfolioTarget
                : StrategyIntentKindV1.MultiLegTarget;
        if (classification.Objective is StrategyObjectiveKind.Allocation or StrategyObjectiveKind.BenchmarkTracking ||
            classification.Context.Topology == MarketTopologyKind.CrossSection ||
            classification.Context.Exposure == ExposureGeometryKind.CrossSectionalLongShort)
            return StrategyIntentKindV1.PortfolioTarget;
        if (classification.Portfolio.Construction == PortfolioConstructionKind.NotApplicable &&
            classification.Execution.Policies is [StrategyExecutionPolicyKind.NotApplicable] &&
            classification.Risk.Rules is [StrategyRiskExitKind.NotApplicable])
            return StrategyIntentKindV1.SignalOnly;
        return StrategyIntentKindV1.PositionTarget;
    }

    private StrategyIntentShapeOption? FindShape(StrategyIntentKindV1 kind) =>
        StrategyIntentShapes.FirstOrDefault(shape => shape.Kind == kind);

    private static IReadOnlyList<string> ConfirmedCandidateStatementIds(StrategyCandidateV1 candidate)
    {
        var ids = new List<string>();
        foreach (var group in candidate.Groups ?? [])
        {
            if (group is null) continue;
            foreach (var flattened in FlattenCandidateGroup(group))
            foreach (var statement in flattened.Statements ?? [])
            {
                if (statement is not null && statement.State is
                        StrategyCandidateStatementStateV1.Confirmed or StrategyCandidateStatementStateV1.Resolved)
                    ids.Add(statement.StatementId);
            }
        }
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<StrategyCandidateGroupV1> FlattenCandidateGroup(StrategyCandidateGroupV1 group)
    {
        yield return group;
        foreach (var child in group.Children ?? [])
        {
            if (child is null) continue;
            foreach (var nested in FlattenCandidateGroup(child)) yield return nested;
        }
    }

    private void RestoreStrategyIntentReview(AuthoringSessionSnapshot session)
    {
        if (CurrentCandidate is null ||
            string.IsNullOrWhiteSpace(session.ResearchCaseJson) ||
            string.IsNullOrWhiteSpace(session.StrategyClassificationJson) ||
            string.IsNullOrWhiteSpace(session.StrategyIntentDraftJson))
            return;

        try
        {
            var researchCase = ResearchCaseCanonicalJsonV1.Deserialize(session.ResearchCaseJson);
            var classification = StrategySpecCanonicalJsonV1.Deserialize(session.StrategyClassificationJson);
            var draft = StrategyIntentCanonicalJsonV1.DeserializeDraft(session.StrategyIntentDraftJson);
            ReviewStrategyIntent(researchCase, classification, draft);

            if (string.IsNullOrWhiteSpace(session.ConfirmedStrategyIntentJson)) return;
            var confirmed = StrategyIntentCanonicalJsonV1.DeserializeConfirmed(session.ConfirmedStrategyIntentJson);
            var issues = StrategyIntentConfirmationV1.ValidateConfirmed(
                confirmed,
                CurrentCandidate,
                researchCase,
                classification,
                draft,
                _strategyIntentExtensionRegistry);
            if (issues.Count > 0)
            {
                foreach (var issue in issues) StrategyIntentIssues.Add(issue);
                StrategyIntentStatusText = "The saved confirmation no longer matches this strategy request and was relocked.";
                NotifyStrategyIntentStateChanged();
                return;
            }

            ConfirmedStrategyIntent = confirmed;
            ConfirmedStrategyIntentHash = StrategyIntentCanonicalJsonV1.Hash(confirmed);
            StrategyIntentStatusText =
                "Restored and revalidated the confirmed strategy request. No backtest, paper approval, or live authority exists.";
            NotifyStrategyIntentStateChanged();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not restore strategy-intent review for {Id}", session.StrategyId);
            ClearStrategyIntentReview();
            StrategyIntentStatusText = "The saved strategy request was invalid and discarded; candidate and chat were preserved.";
        }
    }

    private void InvalidateStrategyIntentIfCandidateChanged(StrategyCandidateV1? candidate)
    {
        if (StrategyIntentDraft is null) return;
        if (candidate is not null &&
            string.Equals(
                StrategyIntentDraft.CandidateContentHashSha256,
                StrategyCandidateCanonicalJsonV1.Hash(candidate),
                StringComparison.Ordinal))
            return;

        InvalidateImplementationResultsForStrategyRequestChange();
        ClearStrategyIntentReview();
        StrategyIntentStatusText = "Strategy meaning changed. The prior request review and confirmation were invalidated.";
        Save();
    }

    private void InvalidateImplementationResultsForStrategyRequestChange()
    {
        if (_restoring) return;

        var editorWasBoundToPriorImplementation =
            _editorBaseGeneratedCandidateHash is not null ||
            _loadedCombinedTradeIrCandidateHash is not null ||
            EditorOriginatedFromCombinedTradeIr ||
            ChosenGeneratedCandidateHash is not null;
        var hadImplementationState =
            _parallelCandidateBatch is not null ||
            !string.IsNullOrWhiteSpace(_fourLaneStrategyBrief) ||
            !string.IsNullOrWhiteSpace(_pendingFourLanePrompt) ||
            _editorBaseGeneratedCandidateHash is not null ||
            EditorOriginatedFromCombinedTradeIr ||
            CombinedTradeIrSynthesis is not null ||
            TradeIrBacktestResult is not null;
        if (!hadImplementationState) return;

        _fourLaneStrategyBrief = null;
        SetPendingFourLanePrompt(null);
        ClearParallelCandidates();
        InvalidateDerivedArtifactState(markUnregistered: true);
        if (editorWasBoundToPriorImplementation)
        {
            HasDetachedImplementationSource = true;
            WorkbenchTab = 3;
        }
        AiStatus = "The strategy request changed. Prior implementation results were detached; generate a new bound run after confirmation.";
    }

    private void ClearStrategyIntentReview()
    {
        _isApplyingStrategyIntentReview = true;
        try
        {
            _strategyIntentReviewStarted = false;
            _strategyIntentResearchCase = null;
            _strategyIntentClassification = null;
            StrategyIntentDraft = null;
            StrategyIntentDraftHash = null;
            ConfirmedStrategyIntent = null;
            ConfirmedStrategyIntentHash = null;
            StrategyResearchObjective = string.Empty;
            StrategyResearchHypothesis = string.Empty;
            StrategyResearchEvidence = string.Empty;
            StrategyResearchPointInTimeRule = string.Empty;
            StrategyResearchQualificationRule = string.Empty;
            StrategyResearchFalsifier = string.Empty;
            StrategyResearchEvidenceRows.Clear();
            StrategyResearchFalsifierRows.Clear();
            StrategyResearchUnresolvedRows.Clear();
            StrategyResearchResolvedRows.Clear();
            _strategyIntentRequirementStash.Clear();
            _strategyIntentRequirementContext = null;
            StrategyIntentRequirements.Clear();
            StrategyIntentQuestions.Clear();
            StrategyIntentIssues.Clear();
        }
        finally
        {
            _isApplyingStrategyIntentReview = false;
        }
        NotifyStrategyIntentStateChanged();
    }

    private void NotifyStrategyIntentStateChanged()
    {
        OnPropertyChanged(nameof(HasStrategyIntentReview));
        OnPropertyChanged(nameof(ShowDesignInspector));
        OnPropertyChanged(nameof(ShowWorkbenchPanel));
        OnPropertyChanged(nameof(DesignInspectorWidth));
        OnPropertyChanged(nameof(ShowDesignRequestHeader));
        OnPropertyChanged(nameof(HasConfirmedStrategyIntent));
        OnPropertyChanged(nameof(CanConfirmStrategyIntentReview));
        OnPropertyChanged(nameof(CanEnterFourLaneConformance));
        OnPropertyChanged(nameof(CanGenerateStrategyImplementations));
        OnPropertyChanged(nameof(CanonicalPaperStrategyIntentSupported));
        OnPropertyChanged(nameof(CanonicalPaperStrategyAvailabilityText));
        OnPropertyChanged(nameof(CanGenerateCanonicalPaperStrategy));
        OnPropertyChanged(nameof(CanGenerateFourCandidates));
        OnPropertyChanged(nameof(CanChooseGeneratedCandidate));
        OnPropertyChanged(nameof(CanRevalidateGeneratedCandidate));
        OnPropertyChanged(nameof(StrategyIntentFamilyText));
        ConfirmStrategyIntentReviewCommand.NotifyCanExecuteChanged();
        GenerateCanonicalPaperStrategyCommand.NotifyCanExecuteChanged();
        GenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        RegenerateFourCandidatesCommand.NotifyCanExecuteChanged();
        ChooseGeneratedCandidateCommand.NotifyCanExecuteChanged();
        RevalidateGeneratedCandidateCommand.NotifyCanExecuteChanged();
        NotifyTradeIrSynthesisStateChanged();
        NotifyTradeIrBacktestStateChanged();
        RefreshAuthoringScreenGate();
    }

    partial void OnStrategyIntentDraftChanged(StrategyIntentDraftV1? value) => NotifyStrategyIntentStateChanged();
    partial void OnStrategyIntentDraftHashChanged(string? value) => NotifyStrategyIntentStateChanged();
    partial void OnConfirmedStrategyIntentChanged(ConfirmedStrategyIntentV1? value) => NotifyStrategyIntentStateChanged();
    partial void OnConfirmedStrategyIntentHashChanged(string? value) => NotifyStrategyIntentStateChanged();

    private static string FriendlyFamily(StrategyIntentFamilyV1 family) => family switch
    {
        StrategyIntentFamilyV1.Directional => "Directional / long-short interview",
        StrategyIntentFamilyV1.PairsOrArbitrage => "Pairs / arbitrage interview",
        StrategyIntentFamilyV1.PortfolioOrRebalance => "Portfolio / rebalance interview",
        StrategyIntentFamilyV1.MarketMaking => "Market-making interview",
        StrategyIntentFamilyV1.ExecutionAlgorithm => "Execution-algorithm interview",
        StrategyIntentFamilyV1.SignalPublication => "Signal-publication interview",
        StrategyIntentFamilyV1.Hedging => "Hedging interview",
        StrategyIntentFamilyV1.OptionsOrVolatility => "Options / volatility interview",
        _ => "Governed extension interview",
    };

    private sealed record StrategyIntentRequirementContext(
        string ClassificationHash,
        StrategyIntentKindV1 IntentKind,
        string? ExtensionId);

    private sealed record StrategyIntentRequirementStashKey(
        string ClassificationHash,
        StrategyIntentKindV1 IntentKind,
        string? ExtensionId,
        string RequirementId);
}

public sealed record StrategyIntentProfileOption(
    string Id,
    string Title,
    string Summary,
    StrategySpec Classification)
{
    public static StrategyIntentProfileOption FromBrief(StrategyStarterBrief brief) =>
        new(brief.Id, brief.Title, brief.Summary, brief.Classification);

    public static StrategyIntentProfileOption FromRestoredClassification(
        StrategySpec classification,
        string classificationHash) =>
        new($"restored/{classification.Id}/{classificationHash[..8]}",
            classification.Name,
            "Restored strategy classification",
            classification);

    public static StrategyIntentProfileOption CreateSignalOnly()
    {
        var classification = new StrategySpec(
            "signal-publication",
            "Signal publication (no orders)",
            StrategyObjectiveKind.ReturnSeeking,
            new StrategyContextSpec(
                [AssetClass.Equity],
                MarketTopologyKind.SingleInstrument,
                ExposureGeometryKind.LongOnly,
                [StrategyInformationKind.Bar],
                new StrategyTimeSemantics(StrategyHorizonKind.Intraday, TimeSpan.FromMinutes(1))),
            new StrategySignalSpec(
                [ReturnHypothesisKind.Momentum],
                [StrategyTriggerKind.Bar],
                [SignalModelKind.DeterministicRule]),
            new StrategyPortfolioSpec(PortfolioConstructionKind.NotApplicable),
            new StrategyRiskSpec([StrategyRiskExitKind.NotApplicable]),
            new StrategyExecutionSpec([StrategyExecutionPolicyKind.NotApplicable]),
            new StrategyStateSpec([StrategyStateKind.Stateless], StrategyAdaptationKind.Fixed),
            []);
        return new StrategyIntentProfileOption(
            "signal-publication",
            "Signal publication (no orders)",
            "Publish a point-in-time signal without position sizing, orders, or exits.",
            classification);
    }

    public override string ToString() => Title;
}

public sealed record StrategyIntentShapeOption(
    StrategyIntentKindV1 Kind,
    string Title,
    string Summary,
    string? ExtensionId = null)
{
    public override string ToString() => Title;
}

public sealed record StrategyIntentApplicabilityOption(
    StrategySemanticDispositionV1 Disposition,
    string Label)
{
    public override string ToString() => Label;
}

public sealed partial class StrategyResearchEvidenceRow : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty] private string _description;
    [ObservableProperty] private string _pointInTimeRule;
    [ObservableProperty] private string _qualificationRule;

    private StrategyResearchEvidenceRow(ResearchEvidenceRequirementV1 evidence, Action changed)
    {
        EvidenceId = evidence.EvidenceId;
        IsMaterial = evidence.IsMaterial;
        CandidateStatementIds = evidence.CandidateStatementIds?.ToArray() ?? [];
        _description = evidence.Description;
        _pointInTimeRule = evidence.PointInTimeRule;
        _qualificationRule = evidence.QualificationRule;
        _changed = changed;
    }

    public string EvidenceId { get; }
    public bool IsMaterial { get; }
    public string MaterialityLabel => IsMaterial ? "Material evidence" : "Supporting evidence";
    public IReadOnlyList<string> CandidateStatementIds { get; }

    public static StrategyResearchEvidenceRow FromEvidence(
        ResearchEvidenceRequirementV1 evidence,
        Action changed) => new(evidence, changed);

    public ResearchEvidenceRequirementV1 ToEvidence() => new(
        EvidenceId,
        Description,
        PointInTimeRule,
        QualificationRule,
        IsMaterial,
        CandidateStatementIds);

    partial void OnDescriptionChanged(string value) => _changed();
    partial void OnPointInTimeRuleChanged(string value) => _changed();
    partial void OnQualificationRuleChanged(string value) => _changed();
}

public sealed partial class StrategyResearchFalsifierRow : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty] private string _description;

    private StrategyResearchFalsifierRow(ResearchFalsifierV1 falsifier, Action changed)
    {
        FalsifierId = falsifier.FalsifierId;
        IsMaterial = falsifier.IsMaterial;
        CandidateStatementIds = falsifier.CandidateStatementIds?.ToArray() ?? [];
        _description = falsifier.Description;
        _changed = changed;
    }

    public string FalsifierId { get; }
    public bool IsMaterial { get; }
    public string MaterialityLabel => IsMaterial ? "Material falsifier" : "Supporting falsifier";
    public IReadOnlyList<string> CandidateStatementIds { get; }

    public static StrategyResearchFalsifierRow FromFalsifier(
        ResearchFalsifierV1 falsifier,
        Action changed) => new(falsifier, changed);

    public ResearchFalsifierV1 ToFalsifier() => new(
        FalsifierId,
        Description,
        IsMaterial,
        CandidateStatementIds);

    partial void OnDescriptionChanged(string value) => _changed();
}

public sealed partial class StrategyResearchUnresolvedRow : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<StrategyResearchUnresolvedRow> _resolve;

    [ObservableProperty] private string _description;
    [ObservableProperty] private string _resolution = string.Empty;

    private StrategyResearchUnresolvedRow(
        ResearchUnresolvedItemV1 item,
        Action changed,
        Action<StrategyResearchUnresolvedRow> resolve)
    {
        ItemId = item.ItemId;
        IsMaterial = item.IsMaterial;
        CandidateStatementIds = item.CandidateStatementIds?.ToArray() ?? [];
        _description = item.Description;
        _changed = changed;
        _resolve = resolve;
    }

    public string ItemId { get; }
    public bool IsMaterial { get; }
    public string MaterialityLabel => IsMaterial ? "Material unresolved item — launch remains locked" : "Non-material open item";
    public IReadOnlyList<string> CandidateStatementIds { get; }
    public bool CanResolve => !string.IsNullOrWhiteSpace(Resolution);

    public static StrategyResearchUnresolvedRow FromUnresolvedItem(
        ResearchUnresolvedItemV1 item,
        Action changed,
        Action<StrategyResearchUnresolvedRow> resolve) => new(item, changed, resolve);

    public ResearchUnresolvedItemV1 ToUnresolvedItem() => new(
        ItemId,
        Description,
        IsMaterial,
        CandidateStatementIds);

    public ResearchResolvedItemV1 ToResolvedItem(string resolutionProvenance) => new(
        ItemId,
        Description,
        Resolution.Trim(),
        IsMaterial,
        CandidateStatementIds,
        resolutionProvenance);

    partial void OnDescriptionChanged(string value) => _changed();

    partial void OnResolutionChanged(string value)
    {
        OnPropertyChanged(nameof(CanResolve));
        ResolveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private void Resolve() => _resolve(this);
}

public sealed record StrategyResearchResolvedRow(
    string ItemId,
    string OriginalDescription,
    string Resolution,
    bool IsMaterial,
    IReadOnlyList<string> CandidateStatementIds,
    string ResolutionProvenance)
{
    public string MaterialityLabel => IsMaterial ? "Material research choice resolved" : "Supporting research choice resolved";

    public static StrategyResearchResolvedRow FromResolvedItem(ResearchResolvedItemV1 item) => new(
        item.ItemId,
        item.OriginalDescription,
        item.Resolution,
        item.IsMaterial,
        item.CandidateStatementIds?.ToArray() ?? [],
        item.ResolutionProvenance);

    public ResearchResolvedItemV1 ToResolvedItem() => new(
        ItemId,
        OriginalDescription,
        Resolution,
        IsMaterial,
        CandidateStatementIds,
        ResolutionProvenance);
}

public sealed partial class StrategyIntentRequirementRow : ObservableObject
{
    private static readonly StrategyIntentApplicabilityOption Applicable =
        new(StrategySemanticDispositionV1.Applicable, "Required answer");
    private static readonly StrategyIntentApplicabilityOption NotApplicable =
        new(StrategySemanticDispositionV1.NotApplicable, "Not relevant here");
    private static readonly StrategyIntentApplicabilityOption Unresolved =
        new(StrategySemanticDispositionV1.Unresolved, "Still unanswered");
    private static readonly StrategyIntentApplicabilityOption Unsupported =
        new(StrategySemanticDispositionV1.Unsupported, "Cannot represent yet");

    private readonly Action? _changed;
    private bool _updating;

    [ObservableProperty] private string _answer;
    [ObservableProperty] private StrategyIntentApplicabilityOption _selectedApplicability;

    private StrategyIntentRequirementRow(
        string requirementId,
        StrategySemanticStageV1 stage,
        string question,
        bool allowsNotApplicable,
        bool mustBeNotApplicable,
        string? answer,
        StrategySemanticDispositionV1 disposition,
        StrategySemanticRequirementV1? source,
        Action? changed)
    {
        RequirementId = requirementId;
        Stage = stage;
        Description = source?.Description ?? question;
        IsMaterial = source?.IsMaterial ?? true;
        Provenance = source?.Provenance;
        ValueTypeId = source?.Value?.TypeId ?? StrategyIntentValueTypesV1.SemanticClause;
        ValueUnit = source?.Value?.Unit;
        StageLabel = stage switch
        {
            StrategySemanticStageV1.ObserveOrTrigger => "Observe / trigger",
            StrategySemanticStageV1.QualifyEvidence => "Qualify evidence",
            StrategySemanticStageV1.DecideIntent => "Decide intent",
            StrategySemanticStageV1.SizeOrExposure => "Size / exposure",
            StrategySemanticStageV1.Execution => "Execution",
            StrategySemanticStageV1.ManageLifecycle => "Manage lifecycle",
            StrategySemanticStageV1.FinishOrUnwind => "Finish / unwind",
            _ => stage.ToString(),
        };
        Question = question;
        MustBeNotApplicable = mustBeNotApplicable;
        CanChangeApplicability = !mustBeNotApplicable;
        ApplicabilityOptions = mustBeNotApplicable
            ? [NotApplicable]
            : allowsNotApplicable
                ? [Applicable, NotApplicable, Unresolved, Unsupported]
                : [Applicable, Unresolved, Unsupported];

        var selected = ApplicabilityOptions.FirstOrDefault(option => option.Disposition == disposition) ?? Unresolved;
        _selectedApplicability = mustBeNotApplicable ? NotApplicable : selected;
        _answer = mustBeNotApplicable && string.IsNullOrWhiteSpace(answer)
            ? "This signal-only request does not own sizing, orders, fill handling, or position unwind."
            : answer ?? string.Empty;
        _changed = changed;
    }

    public string RequirementId { get; }
    public StrategySemanticStageV1 Stage { get; }
    public string StageLabel { get; }
    public string Question { get; }
    public string Description { get; }
    public bool IsMaterial { get; }
    public string MaterialityLabel => IsMaterial ? "Material requirement" : "Supporting requirement";
    public StrategyRequirementProvenanceV1? Provenance { get; }
    public string ValueTypeId { get; }
    public string? ValueUnit { get; }
    public bool MustBeNotApplicable { get; }
    public bool CanChangeApplicability { get; }
    public IReadOnlyList<StrategyIntentApplicabilityOption> ApplicabilityOptions { get; }
    public string AnswerWatermark => SelectedApplicability.Disposition switch
    {
        StrategySemanticDispositionV1.NotApplicable => "Explain why this does not apply",
        StrategySemanticDispositionV1.Unsupported => "Name the missing capability",
        StrategySemanticDispositionV1.Unresolved => "Enter the decision when known",
        _ => "Enter the exact rule, threshold, formula, or behavior",
    };

    public static StrategyIntentRequirementRow FromQuestion(
        StrategyIntentQuestionV1 question,
        bool allowsNotApplicable,
        bool mustBeNotApplicable,
        string? priorAnswer,
        StrategySemanticDispositionV1? priorDisposition,
        Action changed) =>
        new(
            question.RequirementId,
            question.Stage,
            question.Prompt,
            allowsNotApplicable,
            mustBeNotApplicable,
            priorAnswer,
            priorDisposition ?? (mustBeNotApplicable
                ? StrategySemanticDispositionV1.NotApplicable
                : StrategySemanticDispositionV1.Unresolved),
            null,
            changed);

    public static StrategyIntentRequirementRow FromRequirement(
        StrategySemanticRequirementV1 requirement,
        string question,
        bool allowsNotApplicable,
        bool mustBeNotApplicable,
        Action changed) =>
        new(
            requirement.RequirementId,
            requirement.Stage,
            question,
            allowsNotApplicable,
            mustBeNotApplicable,
            requirement.Disposition == StrategySemanticDispositionV1.Applicable
                ? requirement.Value?.CanonicalValue
                : requirement.DispositionRationale,
            requirement.Disposition,
            requirement,
            changed);

    public StrategySemanticRequirementV1 ToRequirement(
        IReadOnlyList<string> candidateStatementIds,
        IReadOnlyList<string> researchEvidenceIds)
    {
        var disposition = SelectedApplicability.Disposition;
        var value = disposition == StrategySemanticDispositionV1.Applicable
            ? new StrategyCandidateValueV1(ValueTypeId, Answer.Trim(), ValueUnit)
            : null;
        var rationale = disposition switch
        {
            StrategySemanticDispositionV1.Applicable => null,
            StrategySemanticDispositionV1.Unresolved when string.IsNullOrWhiteSpace(Answer) => "Awaiting a reviewed answer.",
            _ => Answer.Trim(),
        };
        return new StrategySemanticRequirementV1(
            RequirementId,
            Stage,
            disposition,
            Description,
            IsMaterial,
            Provenance ?? new StrategyRequirementProvenanceV1(
                candidateStatementIds.ToArray(),
                researchEvidenceIds.ToArray(),
                "Entered and reviewed by the user in Strategy request review."),
            value,
            rationale);
    }

    partial void OnAnswerChanged(string value)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            if (!MustBeNotApplicable && !string.IsNullOrWhiteSpace(value) &&
                SelectedApplicability.Disposition == StrategySemanticDispositionV1.Unresolved)
                SelectedApplicability = Applicable;
            else if (!MustBeNotApplicable && string.IsNullOrWhiteSpace(value) &&
                     SelectedApplicability.Disposition == StrategySemanticDispositionV1.Applicable)
                SelectedApplicability = Unresolved;
        }
        finally
        {
            _updating = false;
        }
        _changed?.Invoke();
    }

    partial void OnSelectedApplicabilityChanged(StrategyIntentApplicabilityOption value)
    {
        OnPropertyChanged(nameof(AnswerWatermark));
        if (!_updating) _changed?.Invoke();
    }
}
