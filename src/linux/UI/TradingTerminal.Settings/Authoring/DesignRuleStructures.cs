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

    public string FormulaDescription => ToBinding().FormulaDescription;

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
        OnPropertyChanged(nameof(FormulaDescription));
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
/// Structured entry/exit condition. "Crosses above" is distinct from "is above".
/// </summary>
public sealed partial class DesignConditionRow : ObservableObject
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

/// <summary>Position sizing: target quantity plus optional min–max range.</summary>
public sealed partial class DesignSizingForm : ObservableObject
{
    public static IReadOnlyList<string> MethodOptions { get; } =
        ["Target quantity", "Percent of equity", "Fixed contracts"];

    public static IReadOnlyList<string> UnitOptions { get; } =
        ["shares", "contracts", "%"];

    [ObservableProperty] private string _method = "Target quantity";
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _unit = "shares";
    [ObservableProperty] private string _rangeMinText = "";
    [ObservableProperty] private string _rangeMaxText = "";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Unset;

    public bool HasQuantity =>
        !string.IsNullOrWhiteSpace(QuantityText) &&
        decimal.TryParse(QuantityText.Trim(), out var q) &&
        q > 0;

    public bool HasRange =>
        !string.IsNullOrWhiteSpace(RangeMinText) &&
        !string.IsNullOrWhiteSpace(RangeMaxText) &&
        decimal.TryParse(RangeMinText.Trim(), out var min) &&
        decimal.TryParse(RangeMaxText.Trim(), out var max) &&
        min >= 0 &&
        max >= min;

    public bool IsComplete => HasQuantity;

    public string SummaryText
    {
        get
        {
            if (!HasQuantity) return "";
            var core = Method.StartsWith("Percent", StringComparison.OrdinalIgnoreCase)
                ? $"{QuantityText.Trim()}{Unit.Trim()}"
                : $"target {QuantityText.Trim()} {Unit.Trim()}";
            if (HasRange)
                core += $" (range {RangeMinText.Trim()}–{RangeMaxText.Trim()})";
            return core;
        }
    }

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    partial void OnMethodChanged(string value) => NotifyShape();
    partial void OnQuantityTextChanged(string value) => NotifyShape();
    partial void OnUnitChanged(string value) => NotifyShape();
    partial void OnRangeMinTextChanged(string value) => NotifyShape();
    partial void OnRangeMaxTextChanged(string value) => NotifyShape();
    partial void OnProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(ProvenanceLabel));

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(HasQuantity));
        OnPropertyChanged(nameof(HasRange));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Clear()
    {
        Method = "Target quantity";
        QuantityText = "";
        Unit = "shares";
        RangeMinText = "";
        RangeMaxText = "";
        Provenance = DesignValueProvenance.Unset;
    }
}

/// <summary>Risk limits — max loss / daily stop, with optional stop distance range.</summary>
public sealed partial class DesignRiskForm : ObservableObject
{
    public static IReadOnlyList<string> UnitOptions { get; } =
        ["%", "USD", "currency units"];

    [ObservableProperty] private string _maxLossText = "";
    [ObservableProperty] private string _maxLossUnit = "%";
    [ObservableProperty] private string _dailyStopText = "";
    [ObservableProperty] private string _stopRangeMinText = "";
    [ObservableProperty] private string _stopRangeMaxText = "";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Unset;

    public bool HasMaxLoss =>
        !string.IsNullOrWhiteSpace(MaxLossText) &&
        decimal.TryParse(MaxLossText.Trim(), out var v) &&
        v > 0;

    public bool HasDailyStop =>
        !string.IsNullOrWhiteSpace(DailyStopText) &&
        decimal.TryParse(DailyStopText.Trim(), out var v) &&
        v > 0;

    public bool HasStopRange =>
        !string.IsNullOrWhiteSpace(StopRangeMinText) &&
        !string.IsNullOrWhiteSpace(StopRangeMaxText) &&
        decimal.TryParse(StopRangeMinText.Trim(), out var min) &&
        decimal.TryParse(StopRangeMaxText.Trim(), out var max) &&
        min >= 0 &&
        max >= min;

    public bool IsComplete => HasMaxLoss || HasDailyStop;

    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (HasMaxLoss)
                parts.Add($"max loss {MaxLossText.Trim()}{FormatUnit(MaxLossUnit)}");
            if (HasDailyStop)
                parts.Add($"daily stop {DailyStopText.Trim()}{FormatUnit(MaxLossUnit)}");
            if (HasStopRange)
                parts.Add($"stop range {StopRangeMinText.Trim()}–{StopRangeMaxText.Trim()}");
            return string.Join(" · ", parts);
        }
    }

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    private static string FormatUnit(string unit) =>
        unit.Trim() == "%" ? "%" : $" {unit.Trim()}";

    partial void OnMaxLossTextChanged(string value) => NotifyShape();
    partial void OnMaxLossUnitChanged(string value) => NotifyShape();
    partial void OnDailyStopTextChanged(string value) => NotifyShape();
    partial void OnStopRangeMinTextChanged(string value) => NotifyShape();
    partial void OnStopRangeMaxTextChanged(string value) => NotifyShape();
    partial void OnProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(ProvenanceLabel));

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(HasMaxLoss));
        OnPropertyChanged(nameof(HasDailyStop));
        OnPropertyChanged(nameof(HasStopRange));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Clear()
    {
        MaxLossText = "";
        MaxLossUnit = "%";
        DailyStopText = "";
        StopRangeMinText = "";
        StopRangeMaxText = "";
        Provenance = DesignValueProvenance.Unset;
    }
}

/// <summary>Execution policy — order type, TIF, optional price rule.</summary>
public sealed partial class DesignOrdersForm : ObservableObject
{
    public static IReadOnlyList<string> OrderTypeOptions { get; } =
        ["", "Market", "Limit"];

    public static IReadOnlyList<string> TimeInForceOptions { get; } =
        ["", "Day", "IOC", "GTC", "FOK"];

    public static IReadOnlyList<string> PriceRuleOptions { get; } =
        ["", "last", "mid", "bid", "ask"];

    [ObservableProperty] private string _orderType = "";
    [ObservableProperty] private string _timeInForce = "";
    [ObservableProperty] private string _priceRule = "";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Unset;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(OrderType) &&
        !string.IsNullOrWhiteSpace(TimeInForce);

    public string SummaryText
    {
        get
        {
            if (!IsComplete) return "";
            var text = $"{OrderType.Trim()} {TimeInForce.Trim()}".Trim();
            if (!string.IsNullOrWhiteSpace(PriceRule))
                text += $" · price {PriceRule.Trim()}";
            return text;
        }
    }

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    partial void OnOrderTypeChanged(string value) => NotifyShape();
    partial void OnTimeInForceChanged(string value) => NotifyShape();
    partial void OnPriceRuleChanged(string value) => NotifyShape();
    partial void OnProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(ProvenanceLabel));

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Clear()
    {
        OrderType = "";
        TimeInForce = "";
        PriceRule = "";
        Provenance = DesignValueProvenance.Unset;
    }
}
