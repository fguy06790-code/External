using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using ExternalExecutor.Core;

namespace Ryn.Executor;

/// <summary>A read-only snapshot of a native module's static preflight checks.</summary>
public sealed record ModuleDiagnosticReport
{
    internal ModuleDiagnosticReport(string modulePath, bool exists, bool isWindowsX64,
        bool isCompatible, string? sha256, IEnumerable<string> exports,
        IEnumerable<string> missingExports, IEnumerable<string> issues)
    {
        ModulePath = modulePath;
        Exists = exists;
        IsWindowsX64 = isWindowsX64;
        IsCompatible = isCompatible;
        Sha256 = sha256;
        Exports = Array.AsReadOnly(exports.ToArray());
        MissingExports = Array.AsReadOnly(missingExports.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public string ModulePath { get; }
    public bool Exists { get; }
    /// <summary>True for an AMD64 PE32+ image with the DLL characteristic.</summary>
    public bool IsWindowsX64 { get; }
    /// <summary>True when the image and export table pass inspection and all required exports exist.</summary>
    public bool IsCompatible { get; }
    public bool IsSupportedHost { get; } = OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    /// <summary>
    /// Static preflight only. No DLL is loaded; native dependencies and actual
    /// runtime behavior are not verified, so true does not guarantee loading succeeds.
    /// </summary>
    public bool CanLoadOnCurrentHost => IsCompatible && IsSupportedHost;
    public string? Sha256 { get; }
    public IReadOnlyList<string> Exports { get; }
    public IReadOnlyList<string> MissingExports { get; }
    public IReadOnlyList<string> Issues { get; }
}

/// <summary>Inspects PE metadata without loading the DLL or invoking any native API.</summary>
public static class ModuleInspector
{
    private const uint MaximumExportEntries = 65_536;
    private const int MaximumExportNameLength = 1_024;
    private static readonly string[] RequiredExports =
    [
        "Attach", "AttachAll", "IsAttached", "IsAttachedByPid", "Detach", "DetachByPid",
        "GetClientsList", "FreeString", "Execute", "ExecuteScriptAll", "AllowMultiInstance",
        "IsMultiInstanceAllowed", "OpenRoblox", "AutoUpdate"
    ];

    public static ModuleDiagnosticReport Inspect(string? modulePath = null)
    {
        string path = modulePath ?? QuorumExecutorApi.ModulePath;
        bool exists = false;
        bool windowsX64 = false;
        bool validExports = false;
        string? sha256 = null;
        var exports = new SortedSet<string>(StringComparer.Ordinal);
        var issues = new List<string>();

        try
        {
            path = Path.GetFullPath(path);
            exists = File.Exists(path);
            if (!exists)
            {
                issues.Add("The native module file does not exist or cannot be accessed.");
            }
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                sha256 = HashFile(stream);
                stream.Position = 0;
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                var headers = pe.PEHeaders;
                var optional = headers.PEHeader
                    ?? throw new InvalidDataException("The file has no PE optional header.");
                windowsX64 = headers.CoffHeader.Machine == Machine.Amd64
                    && optional.Magic == PEMagic.PE32Plus
                    && (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0;
                if (!windowsX64)
                    issues.Add("The module must be an AMD64 PE32+ image with the DLL characteristic.");

                ReadExports(stream, headers, optional, exports);
                validExports = true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException
            or BadImageFormatException or InvalidOperationException or OverflowException)
        {
            issues.Add("Module inspection failed: " + ex.Message);
        }

        var missing = RequiredExports.Where(name => !exports.Contains(name))
            .Order(StringComparer.Ordinal).ToArray();
        if (exists && missing.Length > 0)
            issues.Add("Missing required exports: " + string.Join(", ", missing) + ".");
        return new ModuleDiagnosticReport(path, exists, windowsX64,
            windowsX64 && validExports && missing.Length == 0, sha256, exports, missing, issues);
    }

    private static string HashFile(FileStream stream)
    {
        if (!stream.CanSeek) throw new InvalidDataException("Module inspection requires a seekable file.");
        // Read exactly the opened file length rather than hashing an unbounded
        // stream. A special file or a file changing during the read is rejected.
        long remaining = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("The module changed or was truncated while hashing.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The module changed size or is not a regular bounded file.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ReadExports(FileStream stream, PEHeaders headers, PEHeader optional,
        SortedSet<string> exports)
    {
        var directory = optional.ExportTableDirectory;
        uint directoryRva = unchecked((uint)directory.RelativeVirtualAddress);
        uint directorySize = unchecked((uint)directory.Size);
        if (directoryRva == 0 || directorySize < 40)
            throw new InvalidDataException("The PE file has no complete export directory.");

        var image = new BoundedImage(stream, headers, optional);
        // Check the declared extent, not just the first 40 bytes, so a truncated
        // export directory cannot pass because its small tables happen to fit.
        image.RequireRange(directoryRva, directorySize);
        ulong directoryEnd = (ulong)directoryRva + directorySize;
        if (directoryEnd > (ulong)uint.MaxValue + 1)
            throw new InvalidDataException("The export directory RVA range overflows.");
        byte[] header = image.Read(directoryRva, 40);
        uint functionCount = U32(header, 20);
        uint nameCount = U32(header, 24);
        if (functionCount > MaximumExportEntries || nameCount > MaximumExportEntries)
            throw new InvalidDataException("The export table exceeds the supported entry limit.");
        if (nameCount > 0 && functionCount == 0)
            throw new InvalidDataException("Named exports have no function address table.");
        byte[] functions = image.ReadTable(U32(header, 28), functionCount, 4);
        byte[] names = image.ReadTable(U32(header, 32), nameCount, 4);
        byte[] ordinals = image.ReadTable(U32(header, 36), nameCount, 2);

        string? previous = null;
        for (int index = 0; index < nameCount; index++)
        {
            uint nameRva = U32(names, index * 4);
            string name = image.ReadAscii(nameRva);
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
                throw new InvalidDataException("Export names are duplicated or not sorted as required by the PE format.");
            previous = name;
            ushort ordinal = BinaryPrimitives.ReadUInt16LittleEndian(ordinals.AsSpan(index * 2, 2));
            if (ordinal >= functionCount)
                throw new InvalidDataException("An export name ordinal is outside the function table.");
            uint functionRva = U32(functions, ordinal * 4);
            image.RequireTarget(functionRva);
            if (functionRva >= directoryRva && (ulong)functionRva < directoryEnd)
                _ = image.ReadAscii(functionRva, directoryEnd); // A bounded forwarder string.
            exports.Add(name);
        }
    }

    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private sealed class BoundedImage(FileStream stream, PEHeaders headers, PEHeader optional)
    {
        private readonly long _fileLength = stream.Length;

        public void RequireRange(uint rva, long length) => _ = Map(rva, length);

        public byte[] ReadTable(uint rva, uint count, int width)
        {
            if (count == 0) return [];
            if (rva == 0) throw new InvalidDataException("An export table has a null RVA.");
            return Read(rva, checked((int)count * width));
        }

        public byte[] Read(uint rva, int length)
        {
            var (offset, _) = Map(rva, length);
            var bytes = new byte[length];
            stream.Position = offset;
            stream.ReadExactly(bytes);
            return bytes;
        }

        public string ReadAscii(uint rva, ulong? endRva = null)
        {
            if (rva == 0) throw new InvalidDataException("An export string has a null RVA.");
            var (offset, available) = Map(rva, 1);
            long length = Math.Min(available, MaximumExportNameLength + 1);
            if (endRva.HasValue) length = Math.Min(length, checked((long)(endRva.Value - rva)));
            if (length <= 0) throw new InvalidDataException("An export string has an invalid RVA range.");
            var bytes = new byte[(int)length];
            stream.Position = offset;
            stream.ReadExactly(bytes);
            int terminator = Array.IndexOf(bytes, (byte)0);
            if (terminator < 0 || terminator > MaximumExportNameLength)
                throw new InvalidDataException("An export string is unterminated or exceeds the supported length limit.");
            if (terminator == 0 || bytes.AsSpan(0, terminator).ContainsAnyExceptInRange((byte)0x20, (byte)0x7e))
                throw new InvalidDataException("An export string is empty or contains invalid ASCII characters.");
            return Encoding.ASCII.GetString(bytes, 0, terminator);
        }

        public void RequireTarget(uint rva)
        {
            if (rva == 0 || rva >= unchecked((uint)optional.SizeOfImage))
                throw new InvalidDataException("An exported function has an invalid target RVA.");
            if (rva < unchecked((uint)optional.SizeOfHeaders)) return;
            foreach (var section in headers.SectionHeaders)
            {
                ulong start = unchecked((uint)section.VirtualAddress);
                ulong extent = Math.Max(unchecked((uint)section.VirtualSize), unchecked((uint)section.SizeOfRawData));
                if ((ulong)rva >= start && (ulong)rva - start < extent) return;
            }
            throw new InvalidDataException("An exported function RVA is outside all image sections.");
        }

        private (long Offset, long Available) Map(uint rva, long length)
        {
            if (length < 0 || (ulong)rva + (ulong)length > (ulong)uint.MaxValue + 1)
                throw new InvalidDataException("An export RVA range overflows.");
            long headerSize = unchecked((uint)optional.SizeOfHeaders);
            if (rva < headerSize)
            {
                long available = Math.Min(headerSize - rva, _fileLength - rva);
                if (available < length) throw new InvalidDataException("An export RVA extends beyond the PE headers.");
                return (rva, available);
            }
            foreach (var section in headers.SectionHeaders)
            {
                long start = unchecked((uint)section.VirtualAddress);
                long rawSize = unchecked((uint)section.SizeOfRawData);
                long extent = Math.Max(unchecked((uint)section.VirtualSize), rawSize);
                long delta = (long)rva - start;
                if (delta < 0 || delta >= extent) continue;
                long offset = (long)unchecked((uint)section.PointerToRawData) + delta;
                long available = Math.Min(rawSize - delta, _fileLength - offset);
                if (available < length || offset < 0 || offset > _fileLength)
                    throw new InvalidDataException("An export RVA is outside file-backed section data.");
                return (offset, available);
            }
            throw new InvalidDataException("An export RVA is outside all image sections.");
        }
    }
}
