using TradingTerminal.Sandbox.Runtime;
using Xunit;

namespace TradingTerminal.Sandbox.Runtime.Tests;

public sealed class TargetIntentReconcileTests
{
    [Fact]
    public void V09_target_minus_filled_minus_working_buys_remainder()
    {
        // Canon example: target +10, filled +4, working +3 → needed +3
        var r = TargetIntentReconcile.Reconcile(target: 10, filled: 4, working: 3);
        Assert.False(r.IsNoop);
        Assert.Equal(3d, r.Needed);
    }

    [Fact]
    public void V09_noop_when_filled_plus_working_equals_target()
    {
        var r = TargetIntentReconcile.Reconcile(target: 10, filled: 7, working: 3);
        Assert.True(r.IsNoop);
        Assert.Equal(0d, r.Needed);
    }

    [Fact]
    public void V09_sell_when_over_target()
    {
        var r = TargetIntentReconcile.Reconcile(target: 0, filled: 5, working: 0);
        Assert.False(r.IsNoop);
        Assert.Equal(-5d, r.Needed);
    }
}
