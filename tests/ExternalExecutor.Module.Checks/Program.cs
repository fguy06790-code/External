using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Ryn.Executor;

string[] required =
[
    "Attach", "AttachAll", "IsAttached", "IsAttachedByPid", "Detach", "DetachByPid",
    "GetClientsList", "FreeString", "Execute", "ExecuteScriptAll", "AllowMultiInstance",
    "IsMultiInstanceAllowed", "OpenRoblox", "AutoUpdate"
];
int checks = 0;
string temporary = Path.Combine(Path.GetTempPath(), "ryn-module-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    Check("pinned module exposes the complete API and exact SHA256", () =>
    {
        string path = Path.Combine(FindRoot(), "native", "Quorum-External-Module.dll");
        var report = ModuleInspector.Inspect(path);
        True(report.Exists && report.IsWindowsX64 && report.IsCompatible);
        Equal("f2c2999ab2410c4da5169d4abbe9e083f39bdec7c6f5580438618d6771bb7bf6", report.Sha256);
        Equal(14, report.Exports.Count);
        True(required.All(report.Exports.Contains));
        Equal(0, report.MissingExports.Count);
        Equal(0, report.Issues.Count);
        Equal(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64, report.IsSupportedHost);
        Equal(report.IsCompatible && report.IsSupportedHost, report.CanLoadOnCurrentHost);
        Equal(string.Join(',', report.Exports.Order(StringComparer.Ordinal)), string.Join(',', report.Exports));
        True(report.Exports is IList<string> { IsReadOnly: true });
    });
    Check("missing files return diagnostics", () =>
    {
        var report = ModuleInspector.Inspect(Path.Combine(temporary, "missing.dll"));
        True(!report.Exists && !report.IsCompatible && !report.CanLoadOnCurrentHost);
        Equal<string?>(null, report.Sha256);
        Equal(14, report.MissingExports.Count);
        True(report.Issues.Count > 0);
    });
    Check("invalid path returns diagnostics", () =>
    {
        var report = ModuleInspector.Inspect("invalid\0path.dll");
        True(!report.Exists && !report.IsCompatible && report.Issues.Count > 0);
    });
    Check("malformed and truncated PE files return diagnostics", () =>
    {
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), Encoding.ASCII.GetBytes("not a DLL"), new byte[] { 0x4d, 0x5a, 0, 0 } })
        {
            var report = InspectBytes(bytes);
            True(report.Exists && !report.IsCompatible && report.Issues.Count > 0);
            True(report.Sha256 is { Length: 64 });
        }
    });
    Check("complete synthetic PE and unrecognized valid export are accepted", () =>
    {
        var report = InspectBytes(Fixture(required.Append("OtherFunction")));
        True(report.IsWindowsX64 && report.IsCompatible);
        Equal(15, report.Exports.Count);
        True(report.Exports.Contains("OtherFunction"));
    });
    Check("missing exports produce an incompatible report", () =>
    {
        var report = InspectBytes(Fixture(required.Where(name => name != "Attach")));
        True(report.IsWindowsX64 && !report.IsCompatible);
        Equal("Attach", report.MissingExports.Single());
    });
    Check("non-AMD64 images are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W16(bytes, 0x84, 0xaa64); // ARM64, while retaining PE32+.
        var report = InspectBytes(bytes);
        True(!report.IsWindowsX64 && !report.IsCompatible);
        Equal(0, report.MissingExports.Count);
    });
    Check("non-DLL images are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W16(bytes, 0x96, 0x0002);
        True(!InspectBytes(bytes).IsCompatible);
    });
    Check("overflowing name table RVAs are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x200 + 32, 0xfffffffc);
        Rejected(bytes);
    });
    Check("truncated function table is rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x200 + 28, 0x1ffe);
        Rejected(bytes);
    });
    Check("declared export directory truncation is rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x98 + 116, 0x1001);
        Rejected(bytes);
    });
    Check("huge counts are rejected without allocating their declared sizes", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x200 + 24, uint.MaxValue);
        Rejected(bytes);
    });
    Check("out-of-range ordinals are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W16(bytes, 0x380, ushort.MaxValue);
        Rejected(bytes);
    });
    Check("unbacked name strings are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x300, 0x1fff);
        bytes[^1] = (byte)'X';
        Rejected(bytes);
    });
    Check("overlong export strings are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x300, 0x1600);
        bytes.AsSpan(0x800, 1025).Fill((byte)'X');
        Rejected(bytes);
    });
    Check("function RVAs outside the image are rejected", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x240, uint.MaxValue);
        Rejected(bytes);
    });
    Check("unterminated forwarders cannot escape the export directory", () =>
    {
        byte[] bytes = Fixture(required);
        W32(bytes, 0x240, 0x16ff);
        bytes[0x8ff] = (byte)'X';
        Rejected(bytes);
    });
    Check("malformed trailing exports cannot leave a report compatible", () =>
    {
        byte[] bytes = Fixture(required.Append("ZLastExport"));
        W32(bytes, 0x300 + 14 * 4, uint.MaxValue);
        var report = InspectBytes(bytes);
        Equal(0, report.MissingExports.Count);
        True(!report.IsCompatible && report.Issues.Count > 0);
    });
    Console.WriteLine($"Passed {checks} static module checks. No native module was loaded or executed.");
}
finally { Directory.Delete(temporary, recursive: true); }

ModuleDiagnosticReport InspectBytes(byte[] bytes)
{
    string path = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".dll");
    File.WriteAllBytes(path, bytes);
    return ModuleInspector.Inspect(path);
}

void Rejected(byte[] bytes)
{
    var report = InspectBytes(bytes);
    True(!report.IsCompatible && !report.CanLoadOnCurrentHost && report.Issues.Count > 0);
}

void Check(string name, Action action)
{
    try { action(); checks++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { throw new InvalidOperationException("FAIL " + name + ": " + ex.Message, ex); }
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; got {actual}.");
}

static string FindRoot()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "native", "Quorum-External-Module.dll"))) return directory.FullName;
    throw new FileNotFoundException("Place the pinned module in the repository's native directory before running checks.");
}

static byte[] Fixture(IEnumerable<string> names)
{
    string[] exports = names.Order(StringComparer.Ordinal).ToArray();
    var bytes = new byte[0x1200];
    W16(bytes, 0, 0x5a4d);
    W32(bytes, 0x3c, 0x80);
    W32(bytes, 0x80, 0x00004550);
    W16(bytes, 0x84, 0x8664); // Machine: AMD64.
    W16(bytes, 0x86, 1); // One section.
    W16(bytes, 0x94, 0xf0); // Optional header size.
    W16(bytes, 0x96, 0x2002); // Executable image and DLL.
    W16(bytes, 0x98, 0x20b); // PE32+.
    W32(bytes, 0x98 + 32, 0x1000); // Section alignment.
    W32(bytes, 0x98 + 36, 0x200); // File alignment.
    W32(bytes, 0x98 + 56, 0x2000); // SizeOfImage.
    W32(bytes, 0x98 + 60, 0x200); // SizeOfHeaders.
    W32(bytes, 0x98 + 108, 16); // Data directory count.
    W32(bytes, 0x98 + 112, 0x1000); // Export directory RVA.
    W32(bytes, 0x98 + 116, 0x700); // Export directory size.
    Encoding.ASCII.GetBytes(".text").CopyTo(bytes, 0x188);
    W32(bytes, 0x188 + 8, 0x1000); // VirtualSize.
    W32(bytes, 0x188 + 12, 0x1000); // VirtualAddress.
    W32(bytes, 0x188 + 16, 0x1000); // SizeOfRawData.
    W32(bytes, 0x188 + 20, 0x200); // PointerToRawData.
    W32(bytes, 0x188 + 36, 0x60000040);
    W32(bytes, 0x200 + 16, 1); // Ordinal base.
    W32(bytes, 0x200 + 20, (uint)exports.Length);
    W32(bytes, 0x200 + 24, (uint)exports.Length);
    W32(bytes, 0x200 + 28, 0x1040); // Functions at raw 0x240.
    W32(bytes, 0x200 + 32, 0x1100); // Name pointers at raw 0x300.
    W32(bytes, 0x200 + 36, 0x1180); // Ordinals at raw 0x380.
    int stringOffset = 0x400;
    for (int i = 0; i < exports.Length; i++)
    {
        W32(bytes, 0x240 + i * 4, 0x1800); // A target outside the export directory.
        W32(bytes, 0x300 + i * 4, (uint)(stringOffset - 0x200 + 0x1000));
        W16(bytes, 0x380 + i * 2, (ushort)i);
        byte[] encoded = Encoding.ASCII.GetBytes(exports[i]);
        encoded.CopyTo(bytes, stringOffset);
        stringOffset += encoded.Length + 1;
    }
    bytes[0xa00] = 0xc3; // Never executed.
    return bytes;
}

static void W16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
