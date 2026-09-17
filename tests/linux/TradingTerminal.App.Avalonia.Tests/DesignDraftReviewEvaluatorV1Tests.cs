using FluentAssertions;
using TradingTerminal.App.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class DesignDraftReviewEvaluatorV1Tests
{
    [Fact]
    public void Evaluate_requires_entry_sizing_and_risk_limits()
    {
        var draft = EmptyDraft() with
        {
            Instrument = "ES",
            Timeframe = "5m",
            EvaluationTiming = "Completed bar",
        };

        var result = DesignDraftReviewEvaluatorV1.Evaluate(draft);

        result.HasRequired.Should().BeTrue();
        result.Items.Should().Contain(i => i.Id == "entry" && i.Severity == DesignReviewSeverityV1.Required);
        result.Items.Should().Contain(i => i.Id == "sizing" && i.Severity == DesignReviewSeverityV1.Required);
        result.Items.Should().Contain(i => i.Id == "risk" && i.Severity == DesignReviewSeverityV1.Required);
        result.CanContinueToBuild(warningsAccepted: true).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ready_draft_allows_continue_after_warnings_accepted()
    {
        var draft = EmptyDraft() with
        {
            Instrument = "ES",
            Timeframe = "5m",
            EvaluationTiming = "Completed bar",
            EntrySummary = "close crosses above SMA(20)",
            ExitSummary = "close crosses below SMA(20)",
            SizingSummary = "fixed 1 contracts",
            RiskLimits =
            [
                new DesignRiskLimitCanonicalV1(
                    "Daily loss", "2", "% of equity", "This strategy", "Stop new entries"),
            ],
            OrdersSummary = "Market · Day",
            LinkedFindingId = "Finding 1",
        };

        var result = DesignDraftReviewEvaluatorV1.Evaluate(draft);

        result.HasRequired.Should().BeFalse();
        result.Items.Should().OnlyContain(i => i.Severity == DesignReviewSeverityV1.Ready);
        result.CanContinueToBuild(warningsAccepted: false).Should().BeTrue();
        result.DraftHashSha256.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HashDraft_changes_when_risk_limit_changes()
    {
        var a = EmptyDraft() with
        {
            Instrument = "ES",
            Timeframe = "5m",
            EntrySummary = "entry",
            SizingSummary = "1",
            RiskLimits =
            [
                new DesignRiskLimitCanonicalV1(
                    "Daily loss", "2", "% of equity", "This strategy", "Stop new entries"),
            ],
        };
        var b = a with
        {
            RiskLimits =
            [
                new DesignRiskLimitCanonicalV1(
                    "Daily loss", "3", "% of equity", "This strategy", "Stop new entries"),
            ],
        };

        DesignDraftReviewEvaluatorV1.HashDraft(a)
            .Should().NotBe(DesignDraftReviewEvaluatorV1.HashDraft(b));
    }

    [Fact]
    public void Evaluate_exit_and_research_are_warnings_not_required()
    {
        var draft = EmptyDraft() with
        {
            Instrument = "ES",
            Timeframe = "5m",
            EntrySummary = "entry",
            SizingSummary = "1",
            RiskLimits =
            [
                new DesignRiskLimitCanonicalV1(
                    "Maximum loss", "1", "% of equity", "Per position", "Exit position"),
            ],
        };

        var result = DesignDraftReviewEvaluatorV1.Evaluate(draft);

        result.HasRequired.Should().BeFalse();
        result.HasWarnings.Should().BeTrue();
        result.Items.Should().Contain(i => i.Id == "exit" && i.Severity == DesignReviewSeverityV1.Warning);
        result.Items.Should().Contain(i => i.Id == "research" && i.Severity == DesignReviewSeverityV1.Warning);
        result.CanContinueToBuild(warningsAccepted: false).Should().BeFalse();
        result.CanContinueToBuild(warningsAccepted: true).Should().BeTrue();
    }

    private static DesignDraftCanonicalV1 EmptyDraft() =>
        new(
            Instrument: "",
            Timeframe: "",
            EvaluationTiming: "",
            Indicators: Array.Empty<string>(),
            EntrySummary: "",
            ExitSummary: "",
            SizingSummary: "",
            RiskLimits: Array.Empty<DesignRiskLimitCanonicalV1>(),
            OrdersSummary: "",
            LinkedFindingId: null,
            EntryNotes: null,
            ExitNotes: null,
            RiskNotes: null);
}
