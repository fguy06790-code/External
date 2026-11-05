namespace ExternalExecutor.Core;

public sealed record AutoAttachAttempt(uint ProcessId, bool Success, string? Error);

/// <summary>
/// Makes one optional attach attempt when a client first appears in an observed
/// snapshot. Call Observe serially, after discovery has completed successfully.
/// </summary>
public sealed class ClientAutoAttacher
{
    private HashSet<uint> _observedProcessIds = [];

    /// <summary>
    /// Tracks clients even while disabled. Enabling therefore applies to future
    /// discoveries, not clients already seen. A disappeared PID can be discovered
    /// again; failed attempts do not retry while the PID remains present.
    /// </summary>
    public IReadOnlyList<AutoAttachAttempt> Observe(
        IReadOnlyList<RobloxClient> clients, bool enabled, Func<uint, bool> attach)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(attach);

        var currentProcessIds = new HashSet<uint>();
        var attachedProcessIds = new HashSet<uint>();
        var orderedProcessIds = new List<uint>();
        foreach (RobloxClient client in clients)
        {
            if (client is null)
                throw new ArgumentException("Client snapshots cannot contain null entries.", nameof(clients));
            if (client.ProcessId == 0)
                continue;

            if (currentProcessIds.Add(client.ProcessId))
                orderedProcessIds.Add(client.ProcessId);
            if (client.IsAttached)
                attachedProcessIds.Add(client.ProcessId);
        }

        // Complete discovery and update the snapshot before invoking callbacks.
        // Duplicate records produce at most one attempt, and an attached record
        // takes precedence if duplicates disagree about a client's state.
        uint[] newClients = enabled
            ? orderedProcessIds.Where(processId =>
                !_observedProcessIds.Contains(processId)
                && !attachedProcessIds.Contains(processId)).ToArray()
            : [];
        _observedProcessIds = currentProcessIds;

        if (newClients.Length == 0)
            return Array.Empty<AutoAttachAttempt>();

        var attempts = new List<AutoAttachAttempt>(newClients.Length);
        foreach (uint processId in newClients)
        {
            try
            {
                bool success = attach(processId);
                attempts.Add(new AutoAttachAttempt(processId, success,
                    success ? null : "Attach returned false."));
            }
            catch (Exception error)
            {
                attempts.Add(new AutoAttachAttempt(processId, false, error.Message));
            }
        }

        return attempts.AsReadOnly();
    }
}
