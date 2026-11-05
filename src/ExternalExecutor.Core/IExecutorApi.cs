namespace ExternalExecutor.Core;

/// <summary>
/// Synchronous operations exposed by the supplied Quorum module.
/// Callers must serialize calls because the module does not document thread safety.
/// A successful execute result means dispatch succeeded, not that the script completed.
/// </summary>
public interface IExecutorApi
{
    IReadOnlyList<RobloxClient> GetClients();
    bool AttachAll();
    bool Attach(uint processId);
    bool IsAttached();
    bool IsAttachedByPid(uint processId);
    void Detach();
    void DetachByPid(uint processId);
    bool ExecuteScriptAll(string script);
    bool Execute(uint processId, string script);
    bool OpenRoblox();
    bool AutoUpdate();
    bool AllowMultiInstance(bool allow);
    bool IsMultiInstanceAllowed();
}
