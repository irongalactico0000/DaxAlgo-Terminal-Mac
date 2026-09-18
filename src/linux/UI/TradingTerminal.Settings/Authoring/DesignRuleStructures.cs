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
    public static IReadOnlyList<string> InputOptions { get; } =
        ["Close", "Open", "High", "Low", "HL2", "HLC3"];

    [ObservableProperty] private string _bindingId = "";
    [ObservableProperty] private string _kind = "ema";
    [ObservableProperty] private int _period = 20;
    [ObservableProperty] private string _input = "Close";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Operator;

    /// <summary>Period before the latest edit — used to retune Design ENTRY operands.</summary>
    public int PreviousPeriod { get; private set; } = 20;

    /// <summary>Text box binding for period — rejects non-positive values.</summary>
    public string PeriodText
    {
        get => Period.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (!int.TryParse(value?.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var period) ||
                period <= 0)
                return;
            Period = period;
        }
    }

    public string DisplayLabel =>
        Period > 0 ? $"{Kind.Trim()}({Period})" : Kind.Trim();

    /// <summary>Canonical token for review hash / draft context (includes input).</summary>
    public string CanonicalToken =>
        $"{DisplayLabel}|input {(string.IsNullOrWhiteSpace(Input) ? "Close" : Input.Trim())}";

    public string FormulaDescription => ToBinding().FormulaDescription;

    public string SettingsSummary =>
        $"input {(string.IsNullOrWhiteSpace(Input) ? "Close" : Input.Trim())} · {ToBinding().SettingsSummary}";

    public string VersionShort => ToBinding().VersionShort;

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    public string EditorSummary =>
        $"{DisplayLabel} · input {Input}" +
        (string.IsNullOrEmpty(ProvenanceLabel) ? "" : $" · {ProvenanceLabel}");

    /// <summary>Formula + settings + version — what Hyperion Accept must leave on the row, not just ema(20).</summary>
    public string FormulaDetailText =>
        $"{FormulaDescription} · {SettingsSummary} · ver {VersionShort}";

    partial void OnKindChanged(string value) => NotifyDisplay();
    partial void OnPeriodChanged(int value)
    {
        // CommunityToolkit raises Changed after the field is assigned; stash prior via Changing.
        NotifyDisplay();
    }

    partial void OnPeriodChanging(int value)
    {
        PreviousPeriod = Period;
    }

    partial void OnInputChanged(string value) => NotifyDisplay();
    partial void OnProvenanceChanged(DesignValueProvenance value) => NotifyDisplay();
    partial void OnBindingIdChanged(string value) => NotifyDisplay();

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(CanonicalToken));
        OnPropertyChanged(nameof(PeriodText));
        OnPropertyChanged(nameof(FormulaDescription));
        OnPropertyChanged(nameof(SettingsSummary));
        OnPropertyChanged(nameof(VersionShort));
        OnPropertyChanged(nameof(ProvenanceLabel));
        OnPropertyChanged(nameof(EditorSummary));
        OnPropertyChanged(nameof(FormulaDetailText));
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

    public DesignIndicatorSessionV1 ToSession() =>
        new(BindingId, Kind, Period, Input, Provenance.ToString());

    public static DesignIndicatorRow FromSession(DesignIndicatorSessionV1 session)
    {
        var provenance = Enum.TryParse<DesignValueProvenance>(session.Provenance, ignoreCase: true, out var parsed)
            ? parsed
            : DesignValueProvenance.Operator;
        return new DesignIndicatorRow
        {
            BindingId = session.BindingId,
            Kind = session.Kind,
            Period = session.Period,
            Input = string.IsNullOrWhiteSpace(session.Input) ? "Close" : session.Input,
            Provenance = provenance,
        };
    }
}

/// <summary>Persisted Design indicator row (formula identity + provenance) for session restore.</summary>
public sealed record DesignIndicatorSessionV1(
    string BindingId,
    string Kind,
    int Period,
    string Input,
    string Provenance);

/// <summary>
/// Structured entry/exit condition. "Crosses above" is distinct from "is above".
/// Operand kinds drive the editors; <see cref="LeftOperand"/> / <see cref="RightOperand"/> stay the
/// canonical tokens used by preview/eval and summaries.
/// Constant uses a parameter pick (Price / Number) plus a typed numeric value — not freeform text.
/// </summary>
public sealed partial class DesignConditionRow : ObservableObject
{
    public const string KindPrice = "Price";
    public const string KindIndicator = "Saved indicator";
    /// <summary>Named Research composite (finding condition / indicator combo) — not a vague signal.</summary>
    public const string KindSavedCondition = "Saved condition";
    /// <summary>Legacy alias for <see cref="KindSavedCondition"/>.</summary>
    public const string KindSignal = KindSavedCondition;
    public const string KindConstant = "Constant";
    public const string KindExpression = "Advanced expression";

    /// <summary>Absolute market price level (e.g. 5200).</summary>
    public const string ConstantParameterPrice = "Price";
    /// <summary>Unitless threshold (e.g. RSI 70, ratio 0.5).</summary>
    public const string ConstantParameterNumber = "Number";

    public static IReadOnlyList<string> OperatorOptions { get; } =
    [
        "crosses above",
        "crosses below",
        "is above",
        "is below",
        "equals",
    ];

    public static IReadOnlyList<string> OperandKindOptions { get; } =
    [
        KindSavedCondition,
        KindIndicator,
        KindPrice,
        KindConstant,
        KindExpression,
    ];

    public static IReadOnlyList<string> ConstantParameterOptions { get; } =
    [
        ConstantParameterPrice,
        ConstantParameterNumber,
    ];

    [ObservableProperty] private string _leftKind = KindSavedCondition;
    [ObservableProperty] private string _rightKind = KindIndicator;
    [ObservableProperty] private string _leftIndicatorLabel = "";
    [ObservableProperty] private string _rightIndicatorLabel = "";
    [ObservableProperty] private string _leftConstantParameter = ConstantParameterNumber;
    [ObservableProperty] private string _rightConstantParameter = ConstantParameterNumber;
    [ObservableProperty] private string _leftConstantText = "";
    [ObservableProperty] private string _rightConstantText = "";
    [ObservableProperty] private string _leftExpressionText = "";
    [ObservableProperty] private string _rightExpressionText = "";
    [ObservableProperty] private string _leftSignalId = "";
    [ObservableProperty] private string _rightSignalId = "";
    [ObservableProperty] private string _leftOperand = "";
    [ObservableProperty] private string _operatorKey = "crosses above";
    [ObservableProperty] private string _rightOperand = "";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Unset;

    private bool _rebuildingOperands;

    public bool IsComplete =>
        UsesSavedConditionAlone
            ? !string.IsNullOrWhiteSpace(LeftOperand)
            : IsOperandReady(LeftKind, LeftOperand, LeftConstantText) &&
              !string.IsNullOrWhiteSpace(OperatorKey) &&
              IsOperandReady(RightKind, RightOperand, RightConstantText);

    /// <summary>Primary Design path: one Research composite — no right operand required.</summary>
    public bool UsesSavedConditionAlone =>
        string.Equals(LeftKind, KindSavedCondition, StringComparison.Ordinal);

    public string SummaryText =>
        !IsComplete
            ? ""
            : UsesSavedConditionAlone
                ? LeftOperand.Trim()
                : $"{LeftOperand.Trim()} {OperatorKey.Trim()} {RightOperand.Trim()}";

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    public bool ShowLeftIndicatorPicker =>
        string.Equals(LeftKind, KindIndicator, StringComparison.Ordinal);
    public bool ShowLeftConstant =>
        string.Equals(LeftKind, KindConstant, StringComparison.Ordinal);
    public bool ShowLeftExpression =>
        string.Equals(LeftKind, KindExpression, StringComparison.Ordinal);
    public bool ShowLeftSignalPicker =>
        string.Equals(LeftKind, KindSignal, StringComparison.Ordinal);
    public bool ShowLeftPriceHint =>
        string.Equals(LeftKind, KindPrice, StringComparison.Ordinal);
    public bool ShowRightOperandEditors => !UsesSavedConditionAlone;

    public bool ShowRightIndicatorPicker =>
        string.Equals(RightKind, KindIndicator, StringComparison.Ordinal);
    public bool ShowRightConstant =>
        string.Equals(RightKind, KindConstant, StringComparison.Ordinal);
    public bool ShowRightExpression =>
        string.Equals(RightKind, KindExpression, StringComparison.Ordinal);
    public bool ShowRightSignalPicker =>
        string.Equals(RightKind, KindSignal, StringComparison.Ordinal);
    public bool ShowRightPriceHint =>
        string.Equals(RightKind, KindPrice, StringComparison.Ordinal);

    public string LeftConstantHint => ConstantParameterHint(LeftConstantParameter);
    public string RightConstantHint => ConstantParameterHint(RightConstantParameter);
    public string LeftConstantWatermark => ConstantParameterWatermark(LeftConstantParameter);
    public string RightConstantWatermark => ConstantParameterWatermark(RightConstantParameter);

    public string LeftSignalEmptyHint =>
        "No Research composites yet — save a finding with a condition in Research Studio (indicator combo / rule), then pick it here.";
    public string RightSignalEmptyHint => LeftSignalEmptyHint;

    partial void OnLeftKindChanged(string value)
    {
        if (!_rebuildingOperands)
        {
            // Leaving Saved condition clears the primary picker so it cannot stay visually selected
            // while the operand builder is editing a different kind.
            if (!string.Equals(value, KindSavedCondition, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(LeftSignalId))
            {
                LeftSignalId = "";
            }

            RebuildLeftOperandFromKind();
        }

        NotifyOperandUi();
    }

    partial void OnRightKindChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildRightOperandFromKind();
        NotifyOperandUi();
    }

    partial void OnLeftIndicatorLabelChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildLeftOperandFromKind();
    }

    partial void OnRightIndicatorLabelChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildRightOperandFromKind();
    }

    partial void OnLeftConstantParameterChanged(string value)
    {
        OnPropertyChanged(nameof(LeftConstantHint));
        OnPropertyChanged(nameof(LeftConstantWatermark));
        if (!_rebuildingOperands)
            RebuildLeftOperandFromKind();
    }

    partial void OnRightConstantParameterChanged(string value)
    {
        OnPropertyChanged(nameof(RightConstantHint));
        OnPropertyChanged(nameof(RightConstantWatermark));
        if (!_rebuildingOperands)
            RebuildRightOperandFromKind();
    }

    partial void OnLeftConstantTextChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildLeftOperandFromKind();
    }

    partial void OnRightConstantTextChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildRightOperandFromKind();
    }

    partial void OnLeftExpressionTextChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildLeftOperandFromKind();
    }

    partial void OnRightExpressionTextChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildRightOperandFromKind();
    }

    partial void OnLeftSignalIdChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildLeftOperandFromKind();
    }

    partial void OnRightSignalIdChanged(string value)
    {
        if (!_rebuildingOperands)
            RebuildRightOperandFromKind();
    }

    partial void OnLeftOperandChanged(string value)
    {
        if (!_rebuildingOperands)
            InferKindFromOperand(isLeft: true, value);
        NotifyShape();
    }

    partial void OnOperatorKeyChanged(string value) => NotifyShape();

    partial void OnRightOperandChanged(string value)
    {
        if (!_rebuildingOperands)
            InferKindFromOperand(isLeft: false, value);
        NotifyShape();
    }

    partial void OnProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(ProvenanceLabel));

    private void RebuildLeftOperandFromKind()
    {
        _rebuildingOperands = true;
        try
        {
            LeftOperand = TokenFromKind(LeftKind, LeftIndicatorLabel, LeftConstantText, LeftExpressionText, LeftSignalId);
        }
        finally
        {
            _rebuildingOperands = false;
        }

        NotifyShape();
    }

    private void RebuildRightOperandFromKind()
    {
        _rebuildingOperands = true;
        try
        {
            RightOperand = TokenFromKind(RightKind, RightIndicatorLabel, RightConstantText, RightExpressionText, RightSignalId);
        }
        finally
        {
            _rebuildingOperands = false;
        }

        NotifyShape();
    }

    private static string TokenFromKind(
        string kind,
        string indicatorLabel,
        string constantText,
        string expressionText,
        string signalId) =>
        kind switch
        {
            KindPrice => "close",
            KindIndicator => indicatorLabel.Trim(),
            KindConstant => TryNormalizeConstantNumber(constantText, out var normalized) ? normalized : "",
            KindExpression => expressionText.Trim(),
            KindSavedCondition => string.IsNullOrWhiteSpace(signalId) ? "" : signalId.Trim(),
            _ => expressionText.Trim(),
        };

    private void InferKindFromOperand(bool isLeft, string operand)
    {
        var token = operand?.Trim() ?? "";
        if (token.Length == 0)
            return;

        string kind;
        if (string.Equals(token, "close", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "price", StringComparison.OrdinalIgnoreCase))
            kind = KindPrice;
        else if (TryNormalizeConstantNumber(token, out _))
            kind = KindConstant;
        else if (DesignConditionChartPreviewEvaluatorV1.TryParseSeriesOperand(token, out _, out _))
            kind = KindIndicator;
        else
            kind = KindExpression;

        _rebuildingOperands = true;
        try
        {
            if (isLeft)
            {
                LeftKind = kind;
                switch (kind)
                {
                    case KindIndicator:
                        LeftIndicatorLabel = token;
                        break;
                    case KindConstant:
                        if (TryNormalizeConstantNumber(token, out var leftNum))
                            LeftConstantText = leftNum;
                        // Keep existing parameter (Price vs Number); default Number on blank.
                        if (string.IsNullOrWhiteSpace(LeftConstantParameter))
                            LeftConstantParameter = ConstantParameterNumber;
                        break;
                    case KindExpression:
                        LeftExpressionText = token;
                        break;
                }
            }
            else
            {
                RightKind = kind;
                switch (kind)
                {
                    case KindIndicator:
                        RightIndicatorLabel = token;
                        break;
                    case KindConstant:
                        if (TryNormalizeConstantNumber(token, out var rightNum))
                            RightConstantText = rightNum;
                        if (string.IsNullOrWhiteSpace(RightConstantParameter))
                            RightConstantParameter = ConstantParameterNumber;
                        break;
                    case KindExpression:
                        RightExpressionText = token;
                        break;
                }
            }
        }
        finally
        {
            _rebuildingOperands = false;
        }

        NotifyOperandUi();
    }

    private void NotifyOperandUi()
    {
        OnPropertyChanged(nameof(ShowLeftIndicatorPicker));
        OnPropertyChanged(nameof(ShowLeftConstant));
        OnPropertyChanged(nameof(ShowLeftExpression));
        OnPropertyChanged(nameof(ShowLeftSignalPicker));
        OnPropertyChanged(nameof(ShowLeftPriceHint));
        OnPropertyChanged(nameof(LeftConstantHint));
        OnPropertyChanged(nameof(LeftConstantWatermark));
        OnPropertyChanged(nameof(ShowRightIndicatorPicker));
        OnPropertyChanged(nameof(ShowRightConstant));
        OnPropertyChanged(nameof(ShowRightExpression));
        OnPropertyChanged(nameof(ShowRightSignalPicker));
        OnPropertyChanged(nameof(ShowRightPriceHint));
        OnPropertyChanged(nameof(ShowRightOperandEditors));
        OnPropertyChanged(nameof(UsesSavedConditionAlone));
        OnPropertyChanged(nameof(RightConstantHint));
        OnPropertyChanged(nameof(RightConstantWatermark));
        NotifyShape();
    }

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
    }

    private static bool IsOperandReady(string kind, string operand, string constantText)
    {
        if (string.Equals(kind, KindConstant, StringComparison.Ordinal))
            return TryNormalizeConstantNumber(constantText, out _);
        return !string.IsNullOrWhiteSpace(operand);
    }

    /// <summary>Accepts a real number; stores invariant form for the rule token.</summary>
    public static bool TryNormalizeConstantNumber(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!double.TryParse(
                text.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ||
            double.IsNaN(value) ||
            double.IsInfinity(value))
            return false;
        normalized = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static string ConstantParameterHint(string parameter) =>
        string.Equals(parameter, ConstantParameterPrice, StringComparison.Ordinal)
            ? "Absolute market price (e.g. close crosses above 5200)."
            : "Unitless threshold (e.g. RSI is above 70, or ratio 0.5).";

    private static string ConstantParameterWatermark(string parameter) =>
        string.Equals(parameter, ConstantParameterPrice, StringComparison.Ordinal)
            ? "price e.g. 5200"
            : "number e.g. 70";

    public void Clear()
    {
        _rebuildingOperands = true;
        try
        {
            LeftKind = KindIndicator;
            RightKind = KindIndicator;
            LeftIndicatorLabel = "";
            RightIndicatorLabel = "";
            LeftConstantParameter = ConstantParameterNumber;
            RightConstantParameter = ConstantParameterNumber;
            LeftConstantText = "";
            RightConstantText = "";
            LeftExpressionText = "";
            RightExpressionText = "";
            LeftSignalId = "";
            RightSignalId = "";
            LeftOperand = "";
            OperatorKey = "crosses above";
            RightOperand = "";
            Provenance = DesignValueProvenance.Unset;
        }
        finally
        {
            _rebuildingOperands = false;
        }

        NotifyOperandUi();
        NotifyShape();
    }

    /// <summary>Apply a parsed free-text condition while keeping kind editors in sync.</summary>
    public void SetFromTokens(string left, string op, string right, DesignValueProvenance provenance)
    {
        OperatorKey = op;
        _rebuildingOperands = false;
        LeftOperand = left;
        RightOperand = right;
        Provenance = provenance;
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

/// <summary>Risk limits — max loss / daily stop, with optional stop distance range (legacy).</summary>
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

/// <summary>One structured risk limit (type · value · unit · scope · action).</summary>
public sealed partial class DesignRiskLimitRow : ObservableObject
{
    public static IReadOnlyList<string> TypeOptions { get; } =
    [
        "Maximum loss",
        "Daily loss",
        "Maximum position size",
        "Maximum exposure",
        "Maximum open positions",
        "Drawdown limit",
        "Stop-loss",
        "Trading-hours restriction",
    ];

    public static IReadOnlyList<string> UnitOptions { get; } =
        ["% of equity", "account currency", "shares", "contracts", "%"];

    public static IReadOnlyList<string> ScopeOptions { get; } =
        ["This strategy", "Per position", "Account", "Session"];

    public static IReadOnlyList<string> ActionOptions { get; } =
    [
        "Stop new entries",
        "Exit position",
        "Flatten all",
        "Alert only",
    ];

    [ObservableProperty] private string _type = "Daily loss";
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string _unit = "% of equity";
    [ObservableProperty] private string _scope = "This strategy";
    [ObservableProperty] private string _action = "Stop new entries";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Operator;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Type) &&
        !string.IsNullOrWhiteSpace(ValueText) &&
        decimal.TryParse(ValueText.Trim(), out var v) &&
        v > 0 &&
        !string.IsNullOrWhiteSpace(Unit) &&
        !string.IsNullOrWhiteSpace(Scope) &&
        !string.IsNullOrWhiteSpace(Action);

    public string SummaryText =>
        IsComplete
            ? $"{Type.Trim()} {ValueText.Trim()} {Unit.Trim()} · {Scope.Trim()} · {Action.Trim()}"
            : "(incomplete risk limit)";

    public string EditorSummary => SummaryText;

    public DesignRiskLimitCanonicalV1 ToCanonical() =>
        new(Type.Trim(), ValueText.Trim(), Unit.Trim(), Scope.Trim(), Action.Trim());

    public DesignRiskLimitSessionV1 ToSession() =>
        new(Type, ValueText, Unit, Scope, Action, Provenance.ToString());

    public static DesignRiskLimitRow FromSession(DesignRiskLimitSessionV1 session)
    {
        var provenance = Enum.TryParse<DesignValueProvenance>(session.Provenance, true, out var p)
            ? p
            : DesignValueProvenance.Operator;
        return new DesignRiskLimitRow
        {
            Type = session.Type,
            ValueText = session.ValueText,
            Unit = session.Unit,
            Scope = session.Scope,
            Action = session.Action,
            Provenance = provenance,
        };
    }

    public static DesignRiskLimitRow CreateDefault() => new();

    partial void OnTypeChanged(string value) => NotifyShape();
    partial void OnValueTextChanged(string value) => NotifyShape();
    partial void OnUnitChanged(string value) => NotifyShape();
    partial void OnScopeChanged(string value) => NotifyShape();
    partial void OnActionChanged(string value) => NotifyShape();

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EditorSummary));
    }
}

/// <summary>Persisted structured risk limit for session restore.</summary>
public sealed record DesignRiskLimitSessionV1(
    string Type,
    string ValueText,
    string Unit,
    string Scope,
    string Action,
    string Provenance);

/// <summary>
/// Execution policy — side, order type (시장가/지정가), TIF, optional price rule.
/// </summary>
public sealed partial class DesignOrdersForm : ObservableObject
{
    public const string OrderTypeMarket = "Market · 시장가";
    public const string OrderTypeLimit = "Limit · 지정가";

    public const string SideLong = "Long";
    public const string SideShort = "Short";
    public const string SideBoth = "Both (flip)";

    public static IReadOnlyList<string> SideOptions { get; } =
        ["", SideLong, SideShort, SideBoth];

    public static IReadOnlyList<string> OrderTypeOptions { get; } =
        ["", OrderTypeMarket, OrderTypeLimit];

    public static IReadOnlyList<string> TimeInForceOptions { get; } =
        ["", "Day", "IOC", "GTC", "FOK"];

    public static IReadOnlyList<string> PriceRuleOptions { get; } =
        ["", "last", "mid", "bid", "ask"];

    [ObservableProperty] private string _side = SideLong;
    [ObservableProperty] private string _orderType = "";
    [ObservableProperty] private string _timeInForce = "";
    [ObservableProperty] private string _priceRule = "";
    [ObservableProperty] private DesignValueProvenance _provenance = DesignValueProvenance.Unset;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Side) &&
        !string.IsNullOrWhiteSpace(OrderType) &&
        !string.IsNullOrWhiteSpace(TimeInForce);

    public string SummaryText
    {
        get
        {
            if (!IsComplete) return "";
            var text = $"{Side.Trim()} · {OrderType.Trim()} · {TimeInForce.Trim()}".Trim();
            if (!string.IsNullOrWhiteSpace(PriceRule))
                text += $" · price {PriceRule.Trim()}";
            return text;
        }
    }

    public string SideHint =>
        string.Equals(Side, SideShort, StringComparison.Ordinal) ? "Short only — sell to enter, cover to exit."
        : string.Equals(Side, SideBoth, StringComparison.Ordinal) ? "Both — allow long and short (flip) under this draft."
        : string.Equals(Side, SideLong, StringComparison.Ordinal) ? "Long only — buy to enter, sell to exit."
        : "Choose Long, Short, or Both (flip).";

    public string ProvenanceLabel => DesignValueProvenanceLabels.Label(Provenance);

    /// <summary>Map freeform / legacy "Market" or "Limit" onto bilingual option labels.</summary>
    public static string NormalizeOrderType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var t = value.Trim();
        if (string.Equals(t, OrderTypeMarket, StringComparison.Ordinal) ||
            string.Equals(t, OrderTypeLimit, StringComparison.Ordinal))
            return t;
        if (t.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("지정가", StringComparison.Ordinal))
            return OrderTypeLimit;
        if (t.Contains("Market", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("시장가", StringComparison.Ordinal))
            return OrderTypeMarket;
        return "";
    }

    public static string NormalizeSide(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var t = value.Trim();
        if (string.Equals(t, SideLong, StringComparison.OrdinalIgnoreCase) ||
            t.Contains("long", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("롱", StringComparison.Ordinal))
            return SideLong;
        if (string.Equals(t, SideShort, StringComparison.OrdinalIgnoreCase) ||
            t.Contains("short", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("숏", StringComparison.Ordinal))
            return SideShort;
        if (string.Equals(t, SideBoth, StringComparison.OrdinalIgnoreCase) ||
            t.Contains("flip", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("both", StringComparison.OrdinalIgnoreCase))
            return SideBoth;
        return "";
    }

    partial void OnSideChanged(string value) => NotifyShape();
    partial void OnOrderTypeChanged(string value) => NotifyShape();
    partial void OnTimeInForceChanged(string value) => NotifyShape();
    partial void OnPriceRuleChanged(string value) => NotifyShape();
    partial void OnProvenanceChanged(DesignValueProvenance value) =>
        OnPropertyChanged(nameof(ProvenanceLabel));

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SideHint));
    }

    public void Clear()
    {
        Side = SideLong;
        OrderType = "";
        TimeInForce = "";
        PriceRule = "";
        Provenance = DesignValueProvenance.Unset;
    }
}
