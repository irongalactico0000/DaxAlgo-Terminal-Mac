using TradingTerminal.App.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class DesignOrdersFormTests
{
    [Fact]
    public void Summary_includes_side_and_bilingual_order_type()
    {
        var form = new DesignOrdersForm
        {
            Side = DesignOrdersForm.SideLong,
            OrderType = DesignOrdersForm.OrderTypeMarket,
            TimeInForce = "Day",
        };

        Assert.True(form.IsComplete);
        Assert.Equal("Long · Market · 시장가 · Day", form.SummaryText);
        Assert.Contains("Long only", form.SideHint, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeOrderType_maps_legacy_and_korean()
    {
        Assert.Equal(DesignOrdersForm.OrderTypeLimit, DesignOrdersForm.NormalizeOrderType("Limit"));
        Assert.Equal(DesignOrdersForm.OrderTypeMarket, DesignOrdersForm.NormalizeOrderType("시장가"));
        Assert.Equal(DesignOrdersForm.OrderTypeLimit, DesignOrdersForm.NormalizeOrderType("지정가 Day"));
        Assert.Equal("", DesignOrdersForm.NormalizeOrderType("Day only"));
    }

    [Fact]
    public void NormalizeSide_maps_long_short_both()
    {
        Assert.Equal(DesignOrdersForm.SideLong, DesignOrdersForm.NormalizeSide("long"));
        Assert.Equal(DesignOrdersForm.SideShort, DesignOrdersForm.NormalizeSide("숏"));
        Assert.Equal(DesignOrdersForm.SideBoth, DesignOrdersForm.NormalizeSide("Both flip"));
    }
}
