using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class DesignConditionOperandKindTests
{
    [Fact]
    public void Price_kind_emits_close_token()
    {
        var row = new DesignConditionRow
        {
            LeftKind = DesignConditionRow.KindPrice,
            OperatorKey = "is above",
            RightKind = DesignConditionRow.KindConstant,
            RightConstantParameter = DesignConditionRow.ConstantParameterPrice,
            RightConstantText = "100",
        };

        Assert.Equal("close", row.LeftOperand);
        Assert.Equal("100", row.RightOperand);
        Assert.True(row.IsComplete);
        Assert.Equal("close is above 100", row.SummaryText);
        Assert.Contains("Absolute market price", row.RightConstantHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Constant_rejects_non_numeric_value()
    {
        var row = new DesignConditionRow
        {
            LeftKind = DesignConditionRow.KindPrice,
            OperatorKey = "is above",
            RightKind = DesignConditionRow.KindConstant,
            RightConstantParameter = DesignConditionRow.ConstantParameterNumber,
            RightConstantText = "not-a-number",
        };

        Assert.Equal("", row.RightOperand);
        Assert.False(row.IsComplete);
    }

    [Fact]
    public void Constant_number_parameter_hint_is_unitless()
    {
        var row = new DesignConditionRow
        {
            RightKind = DesignConditionRow.KindConstant,
            RightConstantParameter = DesignConditionRow.ConstantParameterNumber,
            RightConstantText = "70",
        };

        Assert.Contains("Unitless threshold", row.RightConstantHint, StringComparison.Ordinal);
        Assert.Equal("70", row.RightOperand);
    }

    [Fact]
    public void Saved_indicator_kind_uses_label_token()
    {
        var row = new DesignConditionRow
        {
            LeftKind = DesignConditionRow.KindIndicator,
            LeftIndicatorLabel = "ema(20)",
            OperatorKey = "crosses above",
            RightKind = DesignConditionRow.KindIndicator,
            RightIndicatorLabel = "ema(50)",
        };

        Assert.Equal("ema(20) crosses above ema(50)", row.SummaryText);
        Assert.True(DesignConditionChartPreviewEvaluatorV1.TryParseSeriesOperand(row.LeftOperand, out var kind, out var period));
        Assert.Equal("ema", kind);
        Assert.Equal(20, period);
    }

    [Fact]
    public void Free_text_operand_infers_kind_without_clearing_token()
    {
        var row = new DesignConditionRow();
        row.LeftOperand = "sma(10)";
        Assert.Equal(DesignConditionRow.KindIndicator, row.LeftKind);
        Assert.Equal("sma(10)", row.LeftIndicatorLabel);
        Assert.Equal("sma(10)", row.LeftOperand);
    }

    [Fact]
    public void Saved_condition_alone_is_complete_without_right_operand()
    {
        var row = new DesignConditionRow
        {
            LeftKind = DesignConditionRow.KindSavedCondition,
            LeftSignalId = "Finding A · vol×2 · volx20",
        };

        Assert.True(row.UsesSavedConditionAlone);
        Assert.True(row.IsComplete);
        Assert.False(row.ShowRightOperandEditors);
        Assert.Equal("Finding A · vol×2 · volx20", row.SummaryText);
    }

    [Fact]
    public void Saved_condition_kind_alias_matches_signal_legacy()
    {
        Assert.Equal(DesignConditionRow.KindSavedCondition, DesignConditionRow.KindSignal);
        Assert.Equal(DesignConditionRow.KindSavedCondition, DesignConditionRow.OperandKindOptions[0]);
        Assert.Contains(DesignConditionRow.KindSavedCondition, DesignConditionRow.OperandKindOptions);
        Assert.DoesNotContain("Saved signal", DesignConditionRow.OperandKindOptions);
    }

    [Fact]
    public void Saved_condition_kind_uses_selected_id_token()
    {
        var row = new DesignConditionRow
        {
            LeftKind = DesignConditionRow.KindSavedCondition,
            LeftSignalId = "Finding A · close crosses above ema(20) · cond-1",
            OperatorKey = "is above",
            RightKind = DesignConditionRow.KindConstant,
            RightConstantParameter = DesignConditionRow.ConstantParameterNumber,
            RightConstantText = "0",
        };

        Assert.Equal("Finding A · close crosses above ema(20) · cond-1", row.LeftOperand);
        Assert.True(row.ShowLeftSignalPicker);
        Assert.Contains("Research composites", row.LeftSignalEmptyHint, StringComparison.Ordinal);
    }
}
