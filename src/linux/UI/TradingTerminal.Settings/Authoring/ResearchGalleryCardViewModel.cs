using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>One gallery hit shown as a selectable box in the Research tab.</summary>
public sealed partial class ResearchGalleryCardViewModel : ObservableObject
{
    public ResearchGalleryCardViewModel(ResearchOutcomeGalleryMatchV1 match) =>
        Match = match ?? throw new ArgumentNullException(nameof(match));

    public ResearchOutcomeGalleryMatchV1 Match { get; }

    [ObservableProperty] private bool _isSelected;

    public string Symbol => Match.CanonicalSymbol;
    public string ReturnText => Match.OutcomeReturn.ToString("P1");
    public string WhenText => Match.OutcomeFromUtc.ToString("yyyy-MM-dd HH:mm");
    public string TimeframeText => Match.Timeframe.ToDisplayString();
    public string Hint => Match.LabelHint;
    public string ScoreText => string.IsNullOrWhiteSpace(Match.IndicatorScoreSummary)
        ? "scores —"
        : Match.IndicatorScoreSummary!;

    /// <summary>Per-event bar provenance — not the app-wide simulated banner.</summary>
    public string DataSourceText => Match.Source switch
    {
        BrokerKind.Simulated => "Data: Simulated local history",
        _ => $"Data: {Match.Source} bars",
    };
}
