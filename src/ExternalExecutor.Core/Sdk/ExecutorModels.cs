using ExternalExecutor.Core;

namespace Ryn.Executor;

/// <summary>A null ProcessId means all relevant clients. Zero is an invalid target.</summary>
public readonly record struct ClientTarget(uint? ProcessId)
{
    public static ClientTarget All => new(null);
    public static ClientTarget ForProcess(uint processId) => new(processId);
}

public enum ExecutorErrorCode
{
    None, InvalidTarget, InvalidScript, ClientNotFound, NotAttached, NativeRejected,
    PartialFailure, ModuleMissing, DependencyMissing, WrongArchitecture, MissingExport,
    UnsupportedPlatform, Cancelled, TimedOut, NativeFailure, Disposed
}

public sealed record ExecutorError(ExecutorErrorCode Code, string Message);

/// <summary>Success is explicit: a successful bool value may be false.</summary>
public sealed class ExecutorResult<T>
{
    public bool Success { get; }
    public T? Value { get; }
    public ExecutorError? Error { get; }
    public Guid OperationId { get; }
    public TimeSpan Duration { get; }

    private ExecutorResult(bool success, T? value, ExecutorError? error, Guid operationId, TimeSpan duration)
        => (Success, Value, Error, OperationId, Duration) = (success, value, error, operationId, duration);

    public static ExecutorResult<T> Ok(T value, Guid operationId = default, TimeSpan duration = default)
        => new(true, value, null, operationId, duration);

    public static ExecutorResult<T> Fail(ExecutorError error, T? value = default,
        Guid operationId = default, TimeSpan duration = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code == ExecutorErrorCode.None)
            throw new ArgumentException("A failed result must have an error code.", nameof(error));
        return new(false, value, error, operationId, duration);
    }

    internal ExecutorResult<T> WithMetadata(Guid operationId, TimeSpan duration)
        => new(Success, Value, Error, operationId, duration);
}

/// <summary>A copied, read-only snapshot. Stale snapshots retain their last successful capture time.</summary>
public sealed record ClientSnapshot
{
    public IReadOnlyList<RobloxClient> Clients { get; }
    public DateTimeOffset CapturedAt { get; }
    public bool IsStale { get; }
    public ExecutorError? Error { get; }
    public IReadOnlyList<AutoAttachAttempt> AutoAttachAttempts { get; }

    public ClientSnapshot(IEnumerable<RobloxClient>? clients = null, DateTimeOffset? capturedAt = null,
        bool isStale = false, ExecutorError? error = null,
        IEnumerable<AutoAttachAttempt>? autoAttachAttempts = null)
    {
        Clients = Array.AsReadOnly((clients ?? []).ToArray());
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
        IsStale = isStale;
        Error = error;
        AutoAttachAttempts = Array.AsReadOnly((autoAttachAttempts ?? []).ToArray());
    }
}

public sealed record ClientOutcome(uint ProcessId, bool Success, string Message, ExecutorError? Error = null);

public sealed record ClientBatchReport
{
    public string Operation { get; }
    public ClientTarget Target { get; }
    public IReadOnlyList<ClientOutcome> Clients { get; }
    public ClientSnapshot Snapshot { get; }
    public string Summary { get; }
    public int SuccessCount => Clients.Count(client => client.Success);
    public int FailureCount => Clients.Count - SuccessCount;

    public ClientBatchReport(string operation, ClientTarget target, IEnumerable<ClientOutcome> clients,
        ClientSnapshot snapshot, string summary)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(snapshot);
        Operation = operation;
        Target = target;
        Clients = Array.AsReadOnly(clients.ToArray());
        Snapshot = snapshot;
        Summary = summary;
    }
}
