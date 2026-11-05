using Ryn.Executor;

if (args.Length == 0 || args[0] == "--diagnostics")
{
    var module = ModuleInspector.Inspect();
    Console.WriteLine($"Module: {module.ModulePath}");
    Console.WriteLine($"File compatible: {module.IsCompatible}; host supported: {module.IsSupportedHost}");
    Console.WriteLine($"SHA256: {module.Sha256 ?? "unavailable"}");
    foreach (var issue in module.Issues) Console.WriteLine(issue);
    return;
}

using var executor = new ExecutorClient();
if (args[0] == "--clients")
{
    var result = await executor.RefreshAsync();
    if (!result.Success) { Console.Error.WriteLine($"{result.Error!.Code}: {result.Error.Message}"); return; }
    foreach (var client in result.Value!.Clients)
        Console.WriteLine($"PID {client.ProcessId}: attached={client.IsAttached}");
    return;
}

if (args.Length >= 2 && uint.TryParse(args[1], out var pid) && pid != 0)
{
    var target = ClientTarget.ForProcess(pid);
    ExecutorResult<ClientBatchReport>? result = args[0] switch
    {
        "--attach" => await executor.AttachAsync(target),
        "--detach" => await executor.DetachAsync(target),
        "--execute" when args.Length == 3 => await executor.ExecuteAsync(await File.ReadAllTextAsync(args[2]), target),
        _ => null
    };
    if (result is not null)
    {
        foreach (var client in result.Value?.Clients ?? []) Console.WriteLine($"PID {client.ProcessId}: {client.Message}");
        if (!result.Success) Console.Error.WriteLine($"{result.Error!.Code}: {result.Error.Message}");
        return;
    }
}

Console.WriteLine("Usage: --diagnostics | --clients | --attach PID | --detach PID | --execute PID script.lua");
