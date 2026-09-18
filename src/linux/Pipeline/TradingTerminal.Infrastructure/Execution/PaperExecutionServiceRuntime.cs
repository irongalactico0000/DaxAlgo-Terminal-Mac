using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Infrastructure.Execution;

/// <summary>
/// Owns one durable, simulation-only execution service lifetime. It composes existing Mac
/// implementations only and cannot load a live broker adapter.
/// </summary>
public sealed class PaperExecutionServiceRuntime : IDisposable
{
    private readonly IClock _clock;
    private ExecutionLeaseGrant _leaseGrant;
    private bool _disposed;

    private PaperExecutionServiceRuntime(
        SqliteOrderEventStore ledger,
        DeterministicPaperVenue venue,
        ReconciliationEngine reconciliation,
        OrderManagementService oms,
        ExecutionServiceEngine service,
        ExecutionLeaseGrant leaseGrant,
        IClock clock)
    {
        Ledger = ledger;
        Venue = venue;
        Reconciliation = reconciliation;
        Oms = oms;
        Service = service;
        _leaseGrant = leaseGrant;
        _clock = clock;
    }

    public SqliteOrderEventStore Ledger { get; }
    public DeterministicPaperVenue Venue { get; }
    public ReconciliationEngine Reconciliation { get; }
    public OrderManagementService Oms { get; }
    public ExecutionServiceEngine Service { get; }
    public ExecutionLeaseGrant LeaseGrant => _leaseGrant;

    /// <summary>
    /// Opens one account-isolated ledger, acquires its durable writer generation, restores Paper
    /// venue truth, and completes startup reconciliation before returning.
    /// </summary>
    public static PaperExecutionServiceRuntime Create(
        string databasePath,
        ExecutionResource resource,
        IClock clock,
        ExecutionLeaseId leaseId,
        RuntimeInstanceId ownerId,
        TimeSpan? leaseDuration = null,
        PaperFillFidelityOptions? fillFidelity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!resource.IsValid) throw new ArgumentException("The execution resource is invalid.", nameof(resource));
        ArgumentNullException.ThrowIfNull(clock);
        if (leaseId.IsEmpty) throw new ArgumentException("A lease id is required.", nameof(leaseId));
        if (ownerId.IsEmpty) throw new ArgumentException("A runtime owner id is required.", nameof(ownerId));
        var duration = leaseDuration ?? TimeSpan.FromMinutes(5);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        var now = UtcNow(clock);
        var ledger = new SqliteOrderEventStore(databasePath, now);
        ExecutionLeaseGrant? acquiredGrant = null;
        try
        {
            if (!ledger.Integrity.IsValid)
                throw new InvalidDataException($"Execution ledger integrity failed: {ledger.Integrity.Fault}: {ledger.Integrity.Detail}");
            var acquired = ledger.Acquire(resource, leaseId, ownerId, now, now.Add(duration));
            if (!acquired.IsSuccess || acquired.Grant is null)
                throw new InvalidOperationException($"Paper execution lease acquisition failed: {acquired.Fault}: {acquired.Reason}");
            acquiredGrant = acquired.Grant.Value;

            var venue = new DeterministicPaperVenue(fillFidelity);
            // Terminal fills still carry broker identities, positions, and cash. Restore the
            // Paper venue whenever the ledger has any execution history, not only when the
            // startup classifier finds a nonterminal order requiring command recovery.
            var hasPersistedExecution = ledger.ReadOutbox().Count > 0;
            if (hasPersistedExecution)
            {
                var recovered = venue.RestoreFromLedger(resource, ledger, now);
                if (!recovered.IsSuccess)
                {
                    throw new InvalidDataException(
                        $"Paper venue recovery failed: {recovered.Fault}: {recovered.Reason}");
                }
            }

            var reconciliation = new ReconciliationEngine(ledger);
            var oms = new OrderManagementService(ledger, venue, ledger, clock, reconciliation);
            var runner = new PaperExecutionServiceReconciliationRunner(
                ledger,
                ledger,
                venue,
                reconciliation,
                clock);
            if (hasPersistedExecution)
            {
                var recoveredCallbacks = oms.ProcessVenueEvents();
                var callbackFailure = recoveredCallbacks.FirstOrDefault(result => !result.IsSuccess);
                if (callbackFailure.Fault != OmsCommandFault.None)
                {
                    throw new InvalidDataException(
                        $"Paper recovery callback failed: {callbackFailure.Fault}: {callbackFailure.Reason}");
                }

                var startup = runner.Run(ReconciliationTrigger.Startup, resource);
                if (!startup.IsSuccess || startup.IsAdmissionBlocked || !ledger.CanAdmitAfterStartupReconciliation)
                {
                    throw new InvalidDataException(
                        $"Paper startup reconciliation failed: {startup.Fault}: {startup.Reason}");
                }
            }

            var service = new ExecutionServiceEngine(
                ledger,
                oms,
                ledger,
                acquiredGrant.Value,
                clock,
                runner);
            return new PaperExecutionServiceRuntime(
                ledger,
                venue,
                reconciliation,
                oms,
                service,
                acquiredGrant.Value,
                clock);
        }
        catch
        {
            if (acquiredGrant.HasValue)
                ledger.Release(acquiredGrant.Value, UtcNow(clock));
            ledger.Dispose();
            throw;
        }
    }

    /// <summary>Renews the current durable owner generation without changing its fencing token.</summary>
    public ExecutionLeaseMutationResult RenewLease(TimeSpan leaseDuration)
    {
        ThrowIfDisposed();
        if (leaseDuration <= TimeSpan.Zero)
            return new ExecutionLeaseMutationResult(
                ExecutionLeaseFault.InvalidInput,
                null,
                "Lease duration must be positive.");
        var now = UtcNow(_clock);
        var result = Ledger.Renew(_leaseGrant, now, now.Add(leaseDuration));
        if (result.IsSuccess && result.Grant.HasValue)
            _leaseGrant = result.Grant.Value;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Ledger.Release(_leaseGrant, UtcNow(_clock));
        }
        finally
        {
            Ledger.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static DateTimeOffset UtcNow(IClock clock)
    {
        var now = clock.UtcNow;
        if (now.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("The Paper execution runtime clock must return UTC.");
        return new DateTimeOffset(now);
    }
}
