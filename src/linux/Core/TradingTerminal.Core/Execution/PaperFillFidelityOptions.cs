using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Execution;

/// <summary>
/// Optional Paper fill fidelity — reuses Validate helpers (FIFO-ahead / book-walk).
/// Defaults preserve historic Paper venue behavior (L1 available-qty depletion only).
/// Not Nautilus matching / MBO; not broker LIVE fills.
/// </summary>
public sealed record PaperFillFidelityOptions(
    bool EnableFifoQueueAhead = false,
    bool EnableL2BookWalk = false,
    long MaxFillQuantityPerTouch = 0)
{
    public static PaperFillFidelityOptions Default { get; } = new();

    /// <summary>Validate-parity defaults for local Paper when operator opts in.</summary>
    public static PaperFillFidelityOptions ValidateParity { get; } = new(
        EnableFifoQueueAhead: true,
        EnableL2BookWalk: true,
        MaxFillQuantityPerTouch: 0);
}
