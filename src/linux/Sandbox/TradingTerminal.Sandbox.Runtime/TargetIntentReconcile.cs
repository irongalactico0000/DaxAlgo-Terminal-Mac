namespace TradingTerminal.Sandbox.Runtime;

/// <summary>
/// Canon S06 / V09 pure math: needed = target − filled − working.
/// PaperExecutionBookTargetIntake applies a richer cancel-then-replan cycle;
/// this helper documents the shared reconcile identity for host intake tests.
/// </summary>
public static class TargetIntentReconcile
{
    public readonly record struct Result(
        double Target,
        double Filled,
        double Working,
        double Needed,
        bool IsNoop);

    public static Result Reconcile(
        double target,
        double filled,
        double working = 0d,
        double minTradeQuantity = 0d)
    {
        var needed = target - filled - working;
        if (Math.Abs(needed) <= minTradeQuantity)
            return new Result(target, filled, working, 0d, IsNoop: true);
        return new Result(target, filled, working, needed, IsNoop: false);
    }
}
