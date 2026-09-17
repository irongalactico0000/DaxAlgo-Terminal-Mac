using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Who set a Design value. Distinguishes operator intent from Hyperion suggestions and Research reuse.
/// </summary>
public enum DesignValueProvenance
{
    Unset = 0,
    /// <summary>Operator typed or edited the control.</summary>
    Operator = 1,
    /// <summary>Accepted from a Hyperion proposal (was a suggestion until Accept).</summary>
    HyperionAccepted = 2,
    /// <summary>Reused from linked Research chart bindings — available, not an entry rule yet.</summary>
    ResearchAvailable = 3,
    /// <summary>Filled as a soft default (e.g. template) — not an explicit request.</summary>
    SuggestedDefault = 4,
}

public static class DesignValueProvenanceLabels
{
    public static string Label(DesignValueProvenance provenance) => provenance switch
    {
        DesignValueProvenance.Operator => "You set this",
        DesignValueProvenance.HyperionAccepted => "From Hyperion (accepted)",
        DesignValueProvenance.ResearchAvailable => "From Research (available — not an entry rule)",
        DesignValueProvenance.SuggestedDefault => "Suggested default",
        _ => "",
    };
}

/// <summary>Editable Design indicator row. Optional — strategies need not use chart indicators.</summary>
public sealed partial class DesignIndicatorRow : ObservableObject
{
    [ObservableProperty] private string _bindingId = "";
    [ObservableProperty] private string _kind = "ema";
    [ObservableProperty] private int _period = 20;
    [ObservableProperty] private string _input = "Close";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Operator;

    public string DisplayLabel =>
        Period > 0 ? $"{Kind.Trim()}({Period})" : Kind.Trim();

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    public string EditorSummary =>
        $"{DisplayLabel} · input {Input}" +
        (string.IsNullOrEmpty(ProvenanceLabel) ? "" : $" · {ProvenanceLabel}");

    partial void OnKindChanged(string value) => NotifyDisplay();
    partial void OnPeriodChanged(int value) => NotifyDisplay();
    partial void OnInputChanged(string value) => NotifyDisplay();
    partial void OnProvenanceChanged(DesignValueProvenance value) => NotifyDisplay();

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(ProvenanceLabel));
        OnPropertyChanged(nameof(EditorSummary));
    }

    public ResearchIndicatorBindingV1 ToBinding() =>
        new(string.IsNullOrWhiteSpace(BindingId) ? $"{Kind.Trim().ToLowerInvariant()}-{Period}" : BindingId,
            Kind.Trim(),
            Period);

    public static DesignIndicatorRow FromBinding(
        ResearchIndicatorBindingV1 binding,
        DesignValueProvenance provenance,
        string input = "Close") =>
        new()
        {
            BindingId = binding.BindingId,
            Kind = binding.Kind,
            Period = binding.Period,
            Input = input,
            Provenance = provenance,
        };

    public static DesignIndicatorRow Create(string kind, int period, DesignValueProvenance provenance) =>
        new()
        {
            BindingId = $"{kind.Trim().ToLowerInvariant()}-{period}",
            Kind = kind.Trim(),
            Period = period,
            Input = "Close",
            Provenance = provenance,
        };
}

/// <summary>
/// Structured entry condition. "Crosses above" is distinct from "is above".
/// </summary>
public sealed partial class DesignEntryConditionRow : ObservableObject
{
    public static IReadOnlyList<string> OperatorOptions { get; } =
    [
        "crosses above",
        "crosses below",
        "is above",
        "is below",
        "equals",
    ];

    [ObservableProperty] private string _leftOperand = "";
    [ObservableProperty] private string _operatorKey = "crosses above";
    [ObservableProperty] private string _rightOperand = "";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Unset;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(LeftOperand) &&
        !string.IsNullOrWhiteSpace(OperatorKey) &&
        !string.IsNullOrWhiteSpace(RightOperand);

    public string SummaryText =>
        IsComplete
            ? $"{LeftOperand.Trim()} {OperatorKey.Trim()} {RightOperand.Trim()}"
            : "";

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    partial void OnLeftOperandChanged(string value) => NotifyShape();
    partial void OnOperatorKeyChanged(string value) => NotifyShape();
    partial void OnRightOperandChanged(string value) => NotifyShape();
    partial void OnProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(ProvenanceLabel));

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Clear()
    {
        LeftOperand = "";
        OperatorKey = "crosses above";
        RightOperand = "";
        Provenance = DesignValueProvenance.Unset;
    }
}
