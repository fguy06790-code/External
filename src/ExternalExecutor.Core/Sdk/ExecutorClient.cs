using System.Diagnostics;
using ExternalExecutor.Core;

namespace Ryn.Executor;

/// <summary>
/// Serializes native calls across SDK instances. Cancellation stops queued work;
/// a dispatched native call always runs to completion and is never retried.
/// </summary>
public sealed class ExecutorClient : IDisposable
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private readonly IExecutorApi _api;
    private readonly ClientAutoAttacher _autoAttacher = new();
    private ClientSnapshot _lastSnapshot = new(capturedAt: DateTimeOffset.MinValue, isStale: true);
    private int _disposed;

    public ExecutorClient(IExecutorApi? api = null) => _api = api ?? new QuorumExecutorApi();
    public ClientSnapshot LastSnapshot => Volatile.Read(ref _lastSnapshot);

    public Task<ExecutorResult<ClientSnapshot>> RefreshAsync(bool autoAttachNewClients = false,
        CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        ClientSnapshot snapshot = CaptureSnapshot();
        if (snapshot.IsStale)
            return ExecutorResult<ClientSnapshot>.Fail(snapshot.Error!, snapshot);

        var attempts = _autoAttacher.Observe(snapshot.Clients, autoAttachNewClients, processId =>
        {
            try { return _api.Attach(processId); }
            catch (Exception error) { throw new InvalidOperationException(MapError(error).Message); }
        });
        snapshot = attempts.Count > 0
            ? CaptureSnapshot(attempts)
            : Save(new ClientSnapshot(snapshot.Clients, snapshot.CapturedAt, autoAttachAttempts: attempts));
        // A failed follow-up discovery must not imply that a dispatched attach can be retried.
        return ExecutorResult<ClientSnapshot>.Ok(snapshot);
    }, cancellationToken);

    public Task<ExecutorResult<ClientBatchReport>> AttachAsync(ClientTarget target,
        CancellationToken cancellationToken = default)
        => RunAsync(() => Batch("Attach", target, null), cancellationToken);

    public Task<ExecutorResult<ClientBatchReport>> DetachAsync(ClientTarget target,
        CancellationToken cancellationToken = default)
        => RunAsync(() => Batch("Detach", target, null), cancellationToken);

    public Task<ExecutorResult<ClientBatchReport>> ExecuteAsync(string script, ClientTarget target,
        CancellationToken cancellationToken = default)
        => RunAsync(() => Batch("Execute", target, script), cancellationToken);

    public Task<ExecutorResult<string>> OpenRobloxAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => _api.OpenRoblox()
            ? ExecutorResult<string>.Ok("Roblox launch requested.")
            : Rejected<string>("The module rejected the Roblox launch request."), cancellationToken);

    public Task<ExecutorResult<string>> UpdateApiAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => _api.AutoUpdate()
            ? ExecutorResult<string>.Ok("The module updater completed. Restart the application before using an updated module.")
            : Rejected<string>("The module updater reported failure."), cancellationToken);

    /// <summary>On success Value is the requested state, including false when disabling.</summary>
    public Task<ExecutorResult<bool>> SetMultiInstanceAsync(bool enabled,
        CancellationToken cancellationToken = default)
        => RunAsync(() => _api.AllowMultiInstance(enabled)
            ? ExecutorResult<bool>.Ok(enabled)
            : Rejected<bool>("The module rejected the multi-instance setting."), cancellationToken);

    public Task<ExecutorResult<bool>> GetMultiInstanceStateAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => ExecutorResult<bool>.Ok(_api.IsMultiInstanceAllowed()), cancellationToken);

    /// <summary>
    /// Waits for attachment without attaching or executing. The deadline cancels
    /// queued polls; it cannot interrupt a native call already dispatched.
    /// </summary>
    public async Task<ExecutorResult<ClientSnapshot>> WaitForAttachmentAsync(ClientTarget target,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Guid operationId = Guid.NewGuid();
        var watch = Stopwatch.StartNew();
        ExecutorResult<ClientSnapshot> Finish(ExecutorErrorCode code, string message)
            => ExecutorResult<ClientSnapshot>.Fail(new(code, message), LastSnapshot, operationId, watch.Elapsed);
        if (Volatile.Read(ref _disposed) != 0) return Finish(ExecutorErrorCode.Disposed, "The executor client is disposed.");
        if (target.ProcessId == 0 || timeout < TimeSpan.Zero)
            return Finish(ExecutorErrorCode.InvalidTarget, "Select a nonzero PID and a nonnegative attachment wait timeout.");

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return Finish(ExecutorErrorCode.Cancelled, "The attachment wait was cancelled.");
            TimeSpan remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return Finish(ExecutorErrorCode.TimedOut, "The attachment wait timed out.");

            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollCancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1)));
            var poll = await RunAsync(() =>
            {
                ClientSnapshot snapshot = CaptureSnapshot();
                if (snapshot.IsStale) return ExecutorResult<ClientSnapshot>.Fail(snapshot.Error!, snapshot);
                if (target.ProcessId is uint pid && !snapshot.Clients.Any(client => client.ProcessId == pid))
                    return ExecutorResult<ClientSnapshot>.Fail(new(ExecutorErrorCode.ClientNotFound,
                        $"Roblox client PID {pid} is no longer running."), snapshot);
                return ExecutorResult<ClientSnapshot>.Ok(snapshot);
            }, pollCancellation.Token).ConfigureAwait(false);
            if (!poll.Success)
            {
                if (poll.Error!.Code == ExecutorErrorCode.Cancelled && !cancellationToken.IsCancellationRequested)
                    return Finish(ExecutorErrorCode.TimedOut, "The attachment wait timed out.");
                return poll.WithMetadata(operationId, watch.Elapsed);
            }
            RobloxClient[] relevant = poll.Value!.Clients
                .Where(client => client.ProcessId != 0 && (target.ProcessId is null || client.ProcessId == target.ProcessId))
                .ToArray();
            if (relevant.Length > 0 && relevant.All(client => client.IsAttached))
                return poll.WithMetadata(operationId, watch.Elapsed);

            remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) continue;
            try
            {
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Finish(ExecutorErrorCode.Cancelled, "The attachment wait was cancelled.");
            }
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private ExecutorResult<ClientBatchReport> Batch(string operation, ClientTarget target, string? script)
    {
        if (target.ProcessId == 0)
            return ExecutorResult<ClientBatchReport>.Fail(new(ExecutorErrorCode.InvalidTarget, "A target PID must be nonzero."));
        if (operation == "Execute" && (string.IsNullOrWhiteSpace(script) || script.Contains('\0')))
            return ExecutorResult<ClientBatchReport>.Fail(new(ExecutorErrorCode.InvalidScript,
                "Provide a nonempty script without NUL characters."));

        ClientSnapshot initial = CaptureSnapshot();
        if (initial.IsStale) return ExecutorResult<ClientBatchReport>.Fail(initial.Error!);
        RobloxClient[] running = initial.Clients.Where(client => client.ProcessId != 0)
            .GroupBy(client => client.ProcessId).Select(group => group.Last()).ToArray();
        RobloxClient[] selected = target.ProcessId is uint pid
            ? running.Where(client => client.ProcessId == pid).ToArray()
            : operation == "Execute" ? running.Where(client => client.IsAttached).ToArray() : running;
        if (selected.Length == 0)
        {
            var code = target.ProcessId is null && operation == "Execute" && running.Length > 0
                ? ExecutorErrorCode.NotAttached : ExecutorErrorCode.ClientNotFound;
            var error = new ExecutorError(code, code == ExecutorErrorCode.NotAttached
                ? "No running Roblox clients are attached." : "The selected Roblox client is not running.");
            return ExecutorResult<ClientBatchReport>.Fail(error,
                new ClientBatchReport(operation, target, [], initial, error.Message));
        }

        var outcomes = new List<ClientOutcome>(selected.Length);
        foreach (RobloxClient client in selected)
        {
            uint processId = client.ProcessId;
            try
            {
                bool attached = _api.IsAttachedByPid(processId);
                if (operation == "Execute" && !attached)
                {
                    outcomes.Add(FailedClient(processId, new(ExecutorErrorCode.NotAttached, "The client is not attached.")));
                    continue;
                }
                bool accepted = true;
                string message;
                if (operation == "Attach")
                {
                    if (!attached) accepted = _api.Attach(processId);
                    message = attached ? "Already attached." : "Attach request accepted.";
                }
                else if (operation == "Detach")
                {
                    if (attached) _api.DetachByPid(processId);
                    message = attached ? "Detach requested." : "Already detached.";
                }
                else
                {
                    accepted = _api.Execute(processId, script!);
                    message = "Script dispatched. Completion is reported by the Roblox client.";
                }
                outcomes.Add(accepted ? new ClientOutcome(processId, true, message)
                    : FailedClient(processId, new(ExecutorErrorCode.NativeRejected, "The module rejected the request.")));
            }
            catch (Exception error) { outcomes.Add(FailedClient(processId, MapError(error))); }
        }

        ClientSnapshot after = CaptureSnapshot();
        int successCount = outcomes.Count(client => client.Success);
        int failureCount = outcomes.Count - successCount;
        string summary = $"{operation}: {successCount} succeeded, {failureCount} failed.";
        var report = new ClientBatchReport(operation, target, outcomes, after, summary);
        if (failureCount == 0) return ExecutorResult<ClientBatchReport>.Ok(report);
        ExecutorErrorCode errorCode = successCount > 0 || outcomes.Select(client => client.Error!.Code).Distinct().Count() > 1
            ? ExecutorErrorCode.PartialFailure : outcomes[0].Error!.Code;
        string errorMessage = errorCode == ExecutorErrorCode.PartialFailure ? summary : outcomes[0].Error!.Message;
        return ExecutorResult<ClientBatchReport>.Fail(new(errorCode, errorMessage), report);
    }

    private ClientSnapshot CaptureSnapshot(IEnumerable<AutoAttachAttempt>? attempts = null)
    {
        try { return Save(new ClientSnapshot(_api.GetClients(), autoAttachAttempts: attempts)); }
        catch (Exception error)
        {
            ClientSnapshot previous = LastSnapshot;
            return Save(new ClientSnapshot(previous.Clients, previous.CapturedAt, true, MapError(error), attempts));
        }
    }

    private ClientSnapshot Save(ClientSnapshot snapshot)
    {
        Volatile.Write(ref _lastSnapshot, snapshot);
        return snapshot;
    }

    private async Task<ExecutorResult<T>> RunAsync<T>(Func<ExecutorResult<T>> work, CancellationToken cancellationToken)
    {
        Guid operationId = Guid.NewGuid();
        var watch = Stopwatch.StartNew();
        ExecutorResult<T> result;
        bool entered = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                result = ExecutorResult<T>.Fail(new(ExecutorErrorCode.Disposed, "The executor client is disposed."));
            else
            {
                await NativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                result = await Task.Run(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        return ExecutorResult<T>.Fail(new(ExecutorErrorCode.Disposed, "The executor client is disposed."));
                    if (cancellationToken.IsCancellationRequested)
                        return ExecutorResult<T>.Fail(new(ExecutorErrorCode.Cancelled, "The queued operation was cancelled."));
                    try { return work(); }
                    catch (Exception error) { return ExecutorResult<T>.Fail(MapError(error)); }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = ExecutorResult<T>.Fail(new(ExecutorErrorCode.Cancelled, "The queued operation was cancelled."));
        }
        finally { if (entered) NativeGate.Release(); }
        return result.WithMetadata(operationId, watch.Elapsed);
    }

    private static ClientOutcome FailedClient(uint processId, ExecutorError error)
        => new(processId, false, error.Message, error);

    private static ExecutorResult<T> Rejected<T>(string message)
        => ExecutorResult<T>.Fail(new(ExecutorErrorCode.NativeRejected, message));

    // Deliberately use fixed diagnostic text: backend exception messages can contain script contents.
    private static ExecutorError MapError(Exception error) => error switch
    {
        DllNotFoundException => File.Exists(QuorumExecutorApi.ModulePath)
            ? new(ExecutorErrorCode.DependencyMissing, "The module exists, but a required native dependency could not be loaded.")
            : new(ExecutorErrorCode.ModuleMissing, "The Quorum module is missing beside the application executable."),
        BadImageFormatException => new(ExecutorErrorCode.WrongArchitecture, "The native module or a dependency is incompatible with this Windows x64 process."),
        EntryPointNotFoundException => new(ExecutorErrorCode.MissingExport, "The native module does not expose a required API function."),
        PlatformNotSupportedException => new(ExecutorErrorCode.UnsupportedPlatform, "The native module requires a Windows x64 process."),
        ObjectDisposedException => new(ExecutorErrorCode.Disposed, "The backend has been disposed."),
        _ => new(ExecutorErrorCode.NativeFailure, "The native operation failed. Consult the Roblox client or module logs for details.")
    };
}
