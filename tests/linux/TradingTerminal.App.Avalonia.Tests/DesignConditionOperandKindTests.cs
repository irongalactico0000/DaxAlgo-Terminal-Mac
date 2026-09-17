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
            RightConstantText = "100",
        };

        Assert.Equal("close", row.LeftOperand);
        Assert.Equal("100", row.RightOperand);
        Assert.True(row.IsComplete);
        Assert.Equal("close is above 100", row.SummaryText);
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
}
