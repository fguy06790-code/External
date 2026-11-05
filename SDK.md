# RYN Executor API 1.2.0

A reusable .NET 10 C# SDK for the Quorum External Module supplied in [fguy06790-code/External](https://github.com/fguy06790-code/External). The improved managed layer is included in the Windows app and available as the local NuGet package `Ryn.Executor.Api`.

The upstream repository contains a native DLL and usage guide. This SDK adds input validation, asynchronous serialization, typed errors, per-client results, discovery tracking, and offline file diagnostics around its documented exports. It does not replace or improve the closed-source native execution engine. Its Roblox-version compatibility and script capabilities still depend on that DLL.

## Installation

Use the `.nupkg` in the SDK download as a local NuGet source:

```powershell
dotnet add package Ryn.Executor.Api --version 1.2.0 --source ./packages
```

For native operations, run a Windows x64 .NET 10 process and place `Quorum-External-Module.dll` beside the application executable. The upstream module requires administrator privileges and the Microsoft Visual C++ x64 runtime. The standalone SDK does not require WebView2; the desktop app uses WebView2 for its interface.

## Basic use

```csharp
using Ryn.Executor;

using var api = new ExecutorClient();
var clients = await api.RefreshAsync();
if (!clients.Success)
{
    Console.WriteLine($"{clients.Error!.Code}: {clients.Error.Message}");
    return;
}

foreach (var client in clients.Value!.Clients)
    Console.WriteLine($"PID {client.ProcessId}: attached={client.IsAttached}");

var target = ClientTarget.ForProcess(1234); // Choose a PID from discovery.
var attached = await api.AttachAsync(target);
if (!attached.Success) return;

var dispatched = await api.ExecuteAsync("print('hello')", target);
foreach (var client in dispatched.Value?.Clients ?? [])
    Console.WriteLine($"PID {client.ProcessId}: {client.Message}");
```

`ClientTarget.All` is explicit. Attach targets all running clients; execute targets the attached clients found at the start of that operation. A missing single-client target is rejected. Already attached/detached clients are treated as satisfied operations. A successful execute result acknowledges dispatch; script completion and output remain in Roblox's client log.

## Available operations

| Method | Result |
| --- | --- |
| `RefreshAsync(autoAttachNewClients, cancellationToken)` | Client snapshot, discovery time, and any auto-attach attempts. Auto-attach defaults off. |
| `AttachAsync(target, cancellationToken)` | Per-client attach results. |
| `DetachAsync(target, cancellationToken)` | Per-client detach requests. |
| `ExecuteAsync(script, target, cancellationToken)` | Per-client dispatch results. |
| `WaitForAttachmentAsync(target, timeout, cancellationToken)` | Wait for a selected target's attachment state. |
| `OpenRobloxAsync(cancellationToken)` | Native launch request. |
| `UpdateApiAsync(cancellationToken)` | Explicit call to the native updater. Restart after a module replacement. |
| `SetMultiInstanceAsync(allowed, cancellationToken)` | Calls the supplied module's multi-instance setting and returns the requested state when accepted. Use the getter to confirm current state. |
| `GetMultiInstanceStateAsync(cancellationToken)` | Reads the native multi-instance state. |
| `ModuleInspector.Inspect(modulePath)` | Offline PE architecture, required exports, checksum, and compatibility report. No DLL loading. |

`LastSnapshot` is an immutable, readable snapshot. Failed discovery marks state stale. Auto-attach makes one attempt for each newly discovered client while enabled; failed attempts do not loop every refresh. Existing clients observed while disabled stay known when it is enabled.

## Errors, partial results, and cancellation

Every asynchronous call returns `ExecutorResult<T>` with `Success`, `Value`, `Error`, `OperationId`, and `Duration`. Errors distinguish invalid input, missing clients, missing attachment, native rejection, module/dependency/export/architecture problems, cancellation, timeout, and disposal.

For a batch, inspect the retained `Value.Clients` even if `Success` is false: some clients may have accepted the operation while others failed. Never retry the entire batch blindly. A successful dispatch remains successful when the later status refresh fails; its report carries a stale snapshot and the refresh error separately.

SDK native calls use a process-wide serialized gate. Cancellation can prevent a queued call from dispatching. An in-progress synchronous native call cannot be forcibly interrupted; cancellation does not undo a script or attachment already sent. Attachment-wait deadlines apply to waiting and polling, and cannot terminate a blocked native call. Execution is never automatically retried. Disposing the client prevents future calls and does not send detach or execute operations.

The native ABI uses ANSI `LPStr` strings. Characters outside the Windows system code page may not survive marshalling. Empty scripts and embedded NUL characters are rejected. Premium functions mentioned in the upstream guide are not exported by the supplied DLL and are not fabricated by this SDK.

## File checks

```csharp
var report = ModuleInspector.Inspect();
Console.WriteLine(report.Sha256);
foreach (var issue in report.Issues) Console.WriteLine(issue);
```

`IsCompatible` describes static file format and required exports. `CanLoadOnCurrentHost` also checks the host OS/architecture; it does not prove dependencies are installed or that attachment will work. Inspection never calls the module. The pinned DLL's provenance is recorded in `native/PROVENANCE.md`.

The SDK package includes a console example. Its default action is offline diagnostics; native operations require an explicit command. Automated SDK checks use fake backends and never load the DLL. Live Roblox execution remains a Windows verification step.
