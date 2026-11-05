using System.Runtime.InteropServices;

namespace ExternalExecutor.Core;

/// <summary>
/// Binds the C ABI described in the upstream Tutorial.txt. Construction does not
/// load the DLL. No updater or other native operation runs automatically.
/// </summary>
public sealed class QuorumExecutorApi : IExecutorApi
{
    public const string ModuleFileName = "Quorum-External-Module.dll";
    public static string ModulePath => Path.Combine(AppContext.BaseDirectory, ModuleFileName);

    public IReadOnlyList<RobloxClient> GetClients()
    {
        EnsureModuleAvailable();
        IntPtr pointer = NativeMethods.GetClientsList();
        if (pointer == IntPtr.Zero)
            return Array.Empty<RobloxClient>();

        try
        {
            return ClientsListParser.Parse(Marshal.PtrToStringAnsi(pointer));
        }
        finally
        {
            NativeMethods.FreeString(pointer);
        }
    }

    public bool AttachAll()
    {
        EnsureModuleAvailable();
        return NativeMethods.AttachAll();
    }

    public bool Attach(uint processId)
    {
        ValidateProcessId(processId);
        EnsureModuleAvailable();
        return NativeMethods.Attach(processId);
    }

    public bool IsAttached()
    {
        EnsureModuleAvailable();
        return NativeMethods.IsAttached();
    }

    public bool IsAttachedByPid(uint processId)
    {
        ValidateProcessId(processId);
        EnsureModuleAvailable();
        return NativeMethods.IsAttachedByPid(processId);
    }

    public void Detach()
    {
        EnsureModuleAvailable();
        NativeMethods.Detach();
    }

    public void DetachByPid(uint processId)
    {
        ValidateProcessId(processId);
        EnsureModuleAvailable();
        NativeMethods.DetachByPid(processId);
    }

    public bool ExecuteScriptAll(string script)
    {
        ValidateScript(script);
        EnsureModuleAvailable();
        return NativeMethods.ExecuteScriptAll(script);
    }

    public bool Execute(uint processId, string script)
    {
        ValidateProcessId(processId);
        ValidateScript(script);
        EnsureModuleAvailable();
        return NativeMethods.Execute(processId, script);
    }

    public bool OpenRoblox()
    {
        EnsureModuleAvailable();
        return NativeMethods.OpenRoblox();
    }

    public bool AutoUpdate()
    {
        EnsureModuleAvailable();
        return NativeMethods.AutoUpdate();
    }

    public bool AllowMultiInstance(bool allow)
    {
        EnsureModuleAvailable();
        return NativeMethods.AllowMultiInstance(allow);
    }

    public bool IsMultiInstanceAllowed()
    {
        EnsureModuleAvailable();
        return NativeMethods.IsMultiInstanceAllowed();
    }

    private static void EnsureModuleAvailable()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Quorum requires a Windows x64 process. Run the win-x64 build on Windows.");

        if (!File.Exists(ModulePath))
            throw new DllNotFoundException($"Missing {ModuleFileName}. Place the module beside the application executable: {ModulePath}");
    }

    private static void ValidateProcessId(uint processId)
    {
        if (processId == 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Select a valid Roblox process.");
    }

    private static void ValidateScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (script.Contains('\0'))
            throw new ArgumentException("Scripts cannot contain a NUL character; the native API would truncate the text.", nameof(script));
    }

    private static class NativeMethods
    {
        private const CallingConvention Convention = CallingConvention.Cdecl;

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool AttachAll();

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Attach(uint processId);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool IsAttached();

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool IsAttachedByPid(uint processId);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        internal static extern void Detach();

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        internal static extern void DetachByPid(uint processId);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        internal static extern IntPtr GetClientsList();

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        internal static extern void FreeString(IntPtr pointer);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Ansi)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool ExecuteScriptAll([MarshalAs(UnmanagedType.LPStr)] string script);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Ansi)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Execute(uint processId, [MarshalAs(UnmanagedType.LPStr)] string script);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool OpenRoblox();

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool AutoUpdate();

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool AllowMultiInstance([MarshalAs(UnmanagedType.I1)] bool allow);

        [DllImport(ModuleFileName, CallingConvention = Convention, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool IsMultiInstanceAllowed();
    }
}
