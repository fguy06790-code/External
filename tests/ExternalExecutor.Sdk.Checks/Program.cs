using System.Collections.Concurrent;
using ExternalExecutor.Core;
using Ryn.Executor;

int passed = 0;
await Check("invalid scripts and PID zero never reach the backend", async () =>
{
    var api = new FakeApi(new RobloxClient(1, true));
    using var client = new ExecutorClient(api);
    foreach (string? text in new string?[] { null, "", " \r\n", "print('a')\0print('b')" })
        Error(ExecutorErrorCode.InvalidScript, await client.ExecuteAsync(text!, ClientTarget.All));
    Error(ExecutorErrorCode.InvalidTarget, await client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(0)));
    Error(ExecutorErrorCode.InvalidTarget, await client.AttachAsync(new(0)));
    Error(ExecutorErrorCode.InvalidTarget, await client.DetachAsync(new(0)));
    Equal(0, api.Calls.Count);
});

await Check("SDK instances serialize native calls process-wide", async () =>
{
    using var release = new ManualResetEventSlim(false);
    var entered = Signal();
    var firstApi = new FakeApi { OnOpen = () => Block(entered, release) };
    var secondApi = new FakeApi(new RobloxClient(2, false));
    using var first = new ExecutorClient(firstApi);
    using var second = new ExecutorClient(secondApi);
    Task<ExecutorResult<string>> active = first.OpenRobloxAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task<ExecutorResult<ClientSnapshot>> queued = second.RefreshAsync();
    try
    {
        await Task.Delay(60);
        Equal(0, secondApi.Calls.Count);
        Equal(false, queued.IsCompleted);
    }
    finally { release.Set(); }
    Success(await active.WaitAsync(TimeSpan.FromSeconds(5)));
    Success(await queued.WaitAsync(TimeSpan.FromSeconds(5)));
});

await Check("queued cancellation completes without discovery or execute", async () =>
{
    using var release = new ManualResetEventSlim(false);
    var entered = Signal();
    var blockingApi = new FakeApi { OnOpen = () => Block(entered, release) };
    var queuedApi = new FakeApi(new RobloxClient(3, true));
    using var blocker = new ExecutorClient(blockingApi);
    using var queuedClient = new ExecutorClient(queuedApi);
    using var cancellation = new CancellationTokenSource();
    var active = blocker.OpenRobloxAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var queued = queuedClient.ExecuteAsync("print('ok')", ClientTarget.All, cancellation.Token);
    cancellation.Cancel();
    try
    {
        Error(ExecutorErrorCode.Cancelled, await queued.WaitAsync(TimeSpan.FromSeconds(3)));
        Equal(0, queuedApi.Calls.Count);
    }
    finally { release.Set(); }
    Success(await active.WaitAsync(TimeSpan.FromSeconds(5)));
});

await Check("cancellation after dispatch retains success and does not repeat execute", async () =>
{
    using var release = new ManualResetEventSlim(false);
    var entered = Signal();
    var api = new FakeApi(new RobloxClient(4, true)) { OnExecute = _ => Block(entered, release) };
    using var client = new ExecutorClient(api);
    using var cancellation = new CancellationTokenSource();
    var execution = client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(4), cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    release.Set();
    var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
    Success(result);
    Equal(1, result.Value!.SuccessCount);
    Equal(1, api.Executed.Count);
});

await Check("all execute dispatches per PID only to attached clients", async () =>
{
    var api = new FakeApi(new RobloxClient(11, true), new(12, false), new(13, true));
    using var client = new ExecutorClient(api);
    var result = await client.ExecuteAsync("print('ok')", ClientTarget.All);
    Success(result);
    Equal("11,13", string.Join(',', api.Executed));
    Equal(2, result.Value!.SuccessCount);
    Equal(false, api.Calls.Contains("ExecuteScriptAll"));
});

await Check("single execute requires the exact running and attached PID", async () =>
{
    var api = new FakeApi(new RobloxClient(21, true), new(22, false));
    using var client = new ExecutorClient(api);
    Error(ExecutorErrorCode.ClientNotFound, await client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(99)));
    Error(ExecutorErrorCode.NotAttached, await client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(22)));
    Equal(0, api.Executed.Count);
    Success(await client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(21)));
    Equal("21", string.Join(',', api.Executed));
});

await Check("attachment is rechecked immediately before execute", async () =>
{
    var api = new FakeApi(new RobloxClient(31, true)) { OnIsAttached = _ => false };
    using var client = new ExecutorClient(api);
    Error(ExecutorErrorCode.NotAttached, await client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(31)));
    Equal(0, api.Executed.Count);
    Equal(true, api.Calls.Contains("IsAttached:31"));
});

await Check("attach all covers running clients and skips already attached clients", async () =>
{
    var api = new FakeApi(new RobloxClient(41, true), new(42, false), new(43, true));
    using var client = new ExecutorClient(api);
    var result = await client.AttachAsync(ClientTarget.All);
    Success(result);
    Equal("42", string.Join(',', api.Attached));
    Equal(3, result.Value!.SuccessCount);
    Equal(false, api.Calls.Contains("AttachAll"));
    Success(await client.DetachAsync(ClientTarget.ForProcess(42)));
    int detachCalls = api.Detached.Count;
    Success(await client.DetachAsync(ClientTarget.ForProcess(42)));
    Equal(detachCalls, api.Detached.Count);
});

await Check("empty clients and missing exact targets produce typed failures", async () =>
{
    var api = new FakeApi();
    using var client = new ExecutorClient(api);
    Error(ExecutorErrorCode.ClientNotFound, await client.AttachAsync(ClientTarget.All));
    Error(ExecutorErrorCode.ClientNotFound, await client.DetachAsync(ClientTarget.ForProcess(50)));
    Error(ExecutorErrorCode.ClientNotFound, await client.ExecuteAsync("print('ok')", ClientTarget.All));
    api.Clients.Add(new(51, false));
    Error(ExecutorErrorCode.NotAttached, await client.ExecuteAsync("print('ok')", ClientTarget.All));
});

await Check("partial batches retain individual outcomes and successful dispatches", async () =>
{
    var api = new FakeApi(new RobloxClient(61, true), new(62, true)) { OnExecute = pid => pid == 61 };
    using var client = new ExecutorClient(api);
    var result = await client.ExecuteAsync("print('ok')", ClientTarget.All);
    Error(ExecutorErrorCode.PartialFailure, result);
    Equal(1, result.Value!.SuccessCount);
    Equal(1, result.Value.FailureCount);
    Equal(ExecutorErrorCode.NativeRejected, result.Value.Clients.Single(c => c.ProcessId == 62).Error!.Code);
    Equal("61,62", string.Join(',', api.Executed));
});

await Check("post-dispatch discovery failure preserves success and stale snapshot", async () =>
{
    var api = new FakeApi(new RobloxClient(71, true));
    int discoveries = 0;
    api.OnGetClients = () => ++discoveries == 1
        ? api.Clients.ToArray()
        : throw new InvalidOperationException("discovery failed after dispatch");
    using var client = new ExecutorClient(api);
    var result = await client.ExecuteAsync("print('ok')", ClientTarget.ForProcess(71));
    Success(result);
    Equal(1, result.Value!.SuccessCount);
    Equal(true, result.Value.Snapshot.IsStale);
    Equal(true, result.Value.Snapshot.Error is not null);
    Equal(true, client.LastSnapshot.IsStale);
    Equal(1, api.Executed.Count);
});

await Check("refresh failures retain previous clients and mark snapshots stale", async () =>
{
    var api = new FakeApi(new RobloxClient(81, false));
    using var client = new ExecutorClient(api);
    Success(await client.RefreshAsync());
    api.OnGetClients = () => throw new EntryPointNotFoundException("not present");
    Error(ExecutorErrorCode.MissingExport, await client.RefreshAsync());
    Equal(true, client.LastSnapshot.IsStale);
    Equal((uint)81, client.LastSnapshot.Clients.Single().ProcessId);
    Equal(ExecutorErrorCode.MissingExport, client.LastSnapshot.Error!.Code);
});

await Check("auto attach tracks disabled discoveries and tries reappearances once", async () =>
{
    var api = new FakeApi(new RobloxClient(91, false));
    using var client = new ExecutorClient(api);
    var first = await client.RefreshAsync(autoAttachNewClients: true);
    Success(first);
    Equal(1, first.Value!.AutoAttachAttempts.Count);
    Equal(true, first.Value.Clients.Single().IsAttached);
    api.Clients[0] = new(91, false);
    Success(await client.RefreshAsync(autoAttachNewClients: true));
    Equal(1, api.Attached.Count);
    api.Clients.Add(new(92, false));
    Success(await client.RefreshAsync());
    Success(await client.RefreshAsync(autoAttachNewClients: true));
    Equal(1, api.Attached.Count);
    api.Clients.Clear();
    Success(await client.RefreshAsync(autoAttachNewClients: true));
    api.Clients.Add(new(92, false));
    Success(await client.RefreshAsync(autoAttachNewClients: true));
    Equal("91,92", string.Join(',', api.Attached));
});

await Check("manual action snapshots do not consume future auto attach discovery", async () =>
{
    var api = new FakeApi(new RobloxClient(101, false));
    using var client = new ExecutorClient(api);
    Success(await client.AttachAsync(ClientTarget.ForProcess(101)));
    api.Clients[0] = new(101, false);
    Success(await client.RefreshAsync(autoAttachNewClients: true));
    Equal("101,101", string.Join(',', api.Attached));
});

await Check("wait returns typed timeout and never attempts attach", async () =>
{
    var api = new FakeApi(new RobloxClient(111, false));
    using var client = new ExecutorClient(api);
    Error(ExecutorErrorCode.TimedOut,
        await client.WaitForAttachmentAsync(ClientTarget.ForProcess(111), TimeSpan.FromMilliseconds(60)));
    Equal(0, api.Attached.Count);
    api.Clients.Clear();
    Error(ExecutorErrorCode.TimedOut,
        await client.WaitForAttachmentAsync(ClientTarget.All, TimeSpan.FromMilliseconds(40)));
    Error(ExecutorErrorCode.ClientNotFound,
        await client.WaitForAttachmentAsync(ClientTarget.ForProcess(112), TimeSpan.FromSeconds(1)));
});

await Check("wait responds to cancellation and discovers later attachment", async () =>
{
    var api = new FakeApi(new RobloxClient(121, false));
    using var client = new ExecutorClient(api);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
    Error(ExecutorErrorCode.Cancelled,
        await client.WaitForAttachmentAsync(ClientTarget.ForProcess(121), TimeSpan.FromSeconds(3), cancellation.Token));
    int polls = 0;
    api.OnGetClients = () => [new RobloxClient(121, ++polls >= 2)];
    var result = await client.WaitForAttachmentAsync(ClientTarget.ForProcess(121), TimeSpan.FromSeconds(2));
    Success(result);
    Equal(true, result.Value!.Clients.Single().IsAttached);
});

await Check("attachment wait deadline cancels queued discovery before backend dispatch", async () =>
{
    using var release = new ManualResetEventSlim(false);
    var entered = Signal();
    var blockerApi = new FakeApi { OnOpen = () => Block(entered, release) };
    var waitingApi = new FakeApi(new RobloxClient(126, true));
    using var blocker = new ExecutorClient(blockerApi);
    using var waiting = new ExecutorClient(waitingApi);
    var active = blocker.OpenRobloxAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    try
    {
        var result = await waiting.WaitForAttachmentAsync(ClientTarget.ForProcess(126), TimeSpan.FromMilliseconds(60))
            .WaitAsync(TimeSpan.FromSeconds(3));
        Error(ExecutorErrorCode.TimedOut, result);
        Equal(0, waitingApi.Calls.Count);
    }
    finally { release.Set(); }
    Success(await active.WaitAsync(TimeSpan.FromSeconds(5)));
});

await Check("disposed clients reject future and queued work without disposing the shared gate", async () =>
{
    using var release = new ManualResetEventSlim(false);
    var entered = Signal();
    var blockerApi = new FakeApi { OnOpen = () => Block(entered, release) };
    var queuedApi = new FakeApi(new RobloxClient(131, true));
    using var blocker = new ExecutorClient(blockerApi);
    var queuedClient = new ExecutorClient(queuedApi);
    var active = blocker.OpenRobloxAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var queued = queuedClient.ExecuteAsync("print('ok')", ClientTarget.All);
    queuedClient.Dispose();
    release.Set();
    Success(await active.WaitAsync(TimeSpan.FromSeconds(5)));
    Error(ExecutorErrorCode.Disposed, await queued.WaitAsync(TimeSpan.FromSeconds(5)));
    Error(ExecutorErrorCode.Disposed, await queuedClient.RefreshAsync());
    Equal(0, queuedApi.Calls.Count);
    using var another = new ExecutorClient(new FakeApi());
    Success(await another.RefreshAsync());
});

await Check("disposal after dispatch does not rewrite completed execution as failure", async () =>
{
    using var release = new ManualResetEventSlim(false);
    var entered = Signal();
    var api = new FakeApi(new RobloxClient(141, true)) { OnExecute = _ => Block(entered, release) };
    var client = new ExecutorClient(api);
    var active = client.ExecuteAsync("print('ok')", ClientTarget.All);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    client.Dispose();
    release.Set();
    Success(await active.WaitAsync(TimeSpan.FromSeconds(5)));
    Error(ExecutorErrorCode.Disposed, await client.RefreshAsync());
});

await Check("multi-instance false is a successful value, and rejection is a typed error", async () =>
{
    var api = new FakeApi();
    using var client = new ExecutorClient(api);
    var state = await client.GetMultiInstanceStateAsync();
    Success(state);
    Equal(false, state.Value);
    var changed = await client.SetMultiInstanceAsync(false);
    Success(changed);
    Equal(false, changed.Value);
    api.OnMultiInstance = _ => false;
    Error(ExecutorErrorCode.NativeRejected, await client.SetMultiInstanceAsync(true));
});

await Check("snapshot and report collections are copied and cannot be mutated", async () =>
{
    var source = new List<RobloxClient> { new(151, true) };
    var api = new FakeApi(new RobloxClient(151, true)) { OnGetClients = () => source };
    using var client = new ExecutorClient(api);
    var refreshed = await client.RefreshAsync();
    Success(refreshed);
    source.Clear();
    Equal(1, refreshed.Value!.Clients.Count);
    Throws<NotSupportedException>(() => ((IList<RobloxClient>)refreshed.Value.Clients).Clear());
    source.Add(new(151, true));
    var execution = await client.ExecuteAsync("print('ok')", ClientTarget.All);
    Success(execution);
    Throws<NotSupportedException>(() => ((IList<ClientOutcome>)execution.Value!.Clients).Clear());
    Equal(true, execution.OperationId != Guid.Empty);
    Equal(true, execution.Duration >= TimeSpan.Zero);
    Equal(true, execution.OperationId != refreshed.OperationId);
});

await Check("native discovery exceptions receive meaningful typed errors", async () =>
{
    (Exception Exception, ExecutorErrorCode Code)[] cases =
    [
        (new DllNotFoundException("missing"), ExecutorErrorCode.ModuleMissing),
        (new BadImageFormatException("architecture"), ExecutorErrorCode.WrongArchitecture),
        (new EntryPointNotFoundException("export"), ExecutorErrorCode.MissingExport),
        (new PlatformNotSupportedException("platform"), ExecutorErrorCode.UnsupportedPlatform),
        (new IOException("generic"), ExecutorErrorCode.NativeFailure)
    ];
    foreach (var item in cases)
    {
        using var client = new ExecutorClient(new FakeApi { OnGetClients = () => throw item.Exception });
        Error(item.Code, await client.RefreshAsync());
    }
});

await Check("execution diagnostics never include script contents from backend exceptions", async () =>
{
    const string script = "print('SCRIPT_CONTENT_SHOULD_STAY_PRIVATE_7C92')";
    var api = new FakeApi(new RobloxClient(161, true)) { OnExecute = _ => throw new InvalidOperationException(script) };
    using var client = new ExecutorClient(api);
    var result = await client.ExecuteAsync(script, ClientTarget.All);
    Equal(false, result.Success);
    Equal(false, result.Error!.Message.Contains(script, StringComparison.Ordinal));
    if (result.Value is { } report)
    {
        Equal(false, report.Summary.Contains(script, StringComparison.Ordinal));
        foreach (var outcome in report.Clients)
        {
            Equal(false, outcome.Message.Contains(script, StringComparison.Ordinal));
            Equal(false, outcome.Error?.Message.Contains(script, StringComparison.Ordinal) ?? false);
        }
    }
});

Console.WriteLine($"Passed {passed} SDK checks. All backend calls used fakes; no native module was loaded.");
return;

async Task Check(string name, Func<Task> test)
{
    try
    {
        await test().WaitAsync(TimeSpan.FromSeconds(15));
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}: {error}");
        Environment.Exit(1);
    }
}

static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static bool Block(TaskCompletionSource entered, ManualResetEventSlim release)
{
    entered.TrySetResult();
    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release fake backend.");
    return true;
}
static void Success<T>(ExecutorResult<T> result)
{
    if (!result.Success) throw new InvalidOperationException($"Expected success; got {result.Error?.Code}: {result.Error?.Message}");
}
static void Error<T>(ExecutorErrorCode code, ExecutorResult<T> result)
{
    Equal(false, result.Success);
    Equal(code, result.Error!.Code);
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}
static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class FakeApi(params RobloxClient[] clients) : IExecutorApi
{
    public List<RobloxClient> Clients { get; } = clients.ToList();
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<uint> Attached { get; } = new();
    public ConcurrentQueue<uint> Detached { get; } = new();
    public ConcurrentQueue<uint> Executed { get; } = new();
    public Func<IReadOnlyList<RobloxClient>>? OnGetClients { get; set; }
    public Func<uint, bool>? OnIsAttached { get; set; }
    public Func<uint, bool>? OnAttach { get; set; }
    public Func<uint, bool>? OnExecute { get; set; }
    public Func<bool>? OnOpen { get; set; }
    public Func<bool>? OnUpdate { get; set; }
    public Func<bool, bool>? OnMultiInstance { get; set; }
    private bool _multiInstance;

    public IReadOnlyList<RobloxClient> GetClients()
    {
        Calls.Enqueue("GetClients");
        return OnGetClients?.Invoke() ?? Clients.ToArray();
    }
    public bool AttachAll() { Calls.Enqueue("AttachAll"); throw new InvalidOperationException("SDK should use per-PID attach."); }
    public bool Attach(uint processId)
    {
        Calls.Enqueue($"Attach:{processId}");
        Attached.Enqueue(processId);
        bool accepted = OnAttach?.Invoke(processId) ?? true;
        if (accepted) SetAttached(processId, true);
        return accepted;
    }
    public bool IsAttached() { Calls.Enqueue("IsAttached"); return Clients.Any(c => c.IsAttached); }
    public bool IsAttachedByPid(uint processId)
    {
        Calls.Enqueue($"IsAttached:{processId}");
        return OnIsAttached?.Invoke(processId) ?? Clients.Any(c => c.ProcessId == processId && c.IsAttached);
    }
    public void Detach() { Calls.Enqueue("Detach"); throw new InvalidOperationException("SDK should use per-PID detach."); }
    public void DetachByPid(uint processId)
    {
        Calls.Enqueue($"Detach:{processId}");
        Detached.Enqueue(processId);
        SetAttached(processId, false);
    }
    public bool ExecuteScriptAll(string script) { Calls.Enqueue("ExecuteScriptAll"); throw new InvalidOperationException("SDK should use per-PID execute."); }
    public bool Execute(uint processId, string script)
    {
        Calls.Enqueue($"Execute:{processId}");
        Executed.Enqueue(processId);
        return OnExecute?.Invoke(processId) ?? true;
    }
    public bool OpenRoblox() { Calls.Enqueue("OpenRoblox"); return OnOpen?.Invoke() ?? true; }
    public bool AutoUpdate() { Calls.Enqueue("AutoUpdate"); return OnUpdate?.Invoke() ?? true; }
    public bool AllowMultiInstance(bool allow)
    {
        Calls.Enqueue($"AllowMultiInstance:{allow}");
        bool accepted = OnMultiInstance?.Invoke(allow) ?? true;
        if (accepted) _multiInstance = allow;
        return accepted;
    }
    public bool IsMultiInstanceAllowed() { Calls.Enqueue("IsMultiInstanceAllowed"); return _multiInstance; }
    private void SetAttached(uint pid, bool attached)
    {
        int index = Clients.FindIndex(c => c.ProcessId == pid);
        if (index >= 0) Clients[index] = new(pid, attached);
    }
}
