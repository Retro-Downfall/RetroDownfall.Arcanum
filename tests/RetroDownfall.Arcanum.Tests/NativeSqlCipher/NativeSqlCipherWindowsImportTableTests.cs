using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// The manifest's <c>dynamicDependencies</c> for the Windows RIDs, checked against the PE import and
/// export tables of the checked-in DLLs on any host.
/// </summary>
/// <remarks>
/// <c>verify-native-sqlcipher.sh</c> compares the same lists with a real <c>dumpbin</c>, but only the
/// Windows job in <c>verify-native-sqlcipher.yml</c> has one. The first version of the manifest named
/// <c>bcrypt.dll</c>, which neither DLL imports, and omitted <c>WS2_32</c>, <c>CRYPT32</c>,
/// <c>VCRUNTIME140</c> and the Universal CRT forwarders. Parsing the tables directly keeps that
/// correction pinned on the macOS lane too, so the Windows job is not the only place a stale list
/// can be noticed.
/// </remarks>
public sealed class NativeSqlCipherWindowsImportTableTests
{
    [Theory]

    [InlineData("win-x64")]

    [InlineData("win-arm64")]

    public void The_manifest_declares_exactly_the_libraries_the_checked_in_dll_imports(string rid)
    {
        using PEReader image = OpenAsset(rid);

        string[] imported = [.. ImportedLibraries(image).Order(StringComparer.OrdinalIgnoreCase)];

        string[] declared = [.. DeclaredDependencies(rid).Order(StringComparer.OrdinalIgnoreCase)];

        Assert.NotEmpty(imported);

        Assert.Equal(
            declared,
            imported,
            StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("bcrypt.dll", imported, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]

    [InlineData("win-x64")]

    [InlineData("win-arm64")]

    public void The_checked_in_dll_exports_only_the_sqlite_api_by_name(string rid)
    {
        using PEReader image = OpenAsset(rid);

        (string[] names, uint functionCount) = ExportedNames(image);

        Assert.NotEmpty(names);

        Assert.True(
            functionCount == names.Length,
            $"{rid} exports {functionCount} functions but only {names.Length} by name, so some export is ordinal-only.");

        string[] outside = [.. names.Where(static name => !name.StartsWith("sqlite3_", StringComparison.Ordinal))];

        Assert.True(
            outside.Length == 0,
            $"{rid} exports symbols outside the SQLite C API: " + string.Join(", ", outside));

        Assert.Contains("sqlite3_open", names);

        Assert.DoesNotContain("sqlite3_load_extension", names);
    }

    /// <summary>
    /// The DLLs are compiled with <c>/MD</c>, so they import <c>VCRUNTIME140.dll</c>. The Native AOT
    /// apphost links the C++ runtime statically and brings no copy, packaging ships none, and a clean
    /// Windows install does not necessarily carry one: there the Grimoire library fails to load and
    /// the host cannot start. While a shipped DLL imports it, the install instructions that travel in
    /// every Windows zip must name the Visual C++ Redistributable as a prerequisite, with the
    /// official download for each architecture.
    /// </summary>
    [Theory]

    [InlineData("win-x64", "https://aka.ms/vs/17/release/vc_redist.x64.exe")]

    [InlineData("win-arm64", "https://aka.ms/vs/17/release/vc_redist.arm64.exe")]

    public void A_dll_importing_the_visual_cpp_runtime_has_its_prerequisite_in_the_install_instructions(string rid, string download)
    {
        using PEReader image = OpenAsset(rid);

        if (!ImportedLibraries(image).Contains("VCRUNTIME140.dll", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        string readme = File.ReadAllText(Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "README.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int install = readme.IndexOf("\n## Install\n", StringComparison.Ordinal);

        Assert.True(install >= 0, "README.md lost its Install section.");

        int next = readme.IndexOf("\n## ", install + 1, StringComparison.Ordinal);

        string section = readme[install..(next < 0 ? readme.Length : next)];

        Assert.Contains("Visual C++ Redistributable", section, StringComparison.Ordinal);

        Assert.Contains(download, section, StringComparison.Ordinal);
    }

    private static PEReader OpenAsset(string rid)
    {
        string path = NativeSqlCipherTestPaths.AssetPath(rid);

        Assert.True(File.Exists(path), $"Missing checked-in native asset: {path}");

        PEReader reader = new(new MemoryStream(File.ReadAllBytes(path)));

        Assert.True(reader.HasMetadata is false, $"{rid} is a native library and must not carry managed metadata.");

        return reader;
    }

    private static string[] DeclaredDependencies(string rid)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(NativeSqlCipherTestPaths.Manifest));

        foreach (JsonElement asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("rid").GetString() == rid)
            {
                return
                [
                    .. asset
                        .GetProperty("dynamicDependencies")
                        .EnumerateArray()
                        .Select(static element => element.GetString()!),
                ];
            }
        }

        throw new InvalidOperationException($"The manifest has no {rid} asset.");
    }

    /// <summary>
    /// Every library named by the regular import table and the delay-load import table, which is the
    /// set <c>dumpbin /dependents</c> reports.
    /// </summary>
    private static IEnumerable<string> ImportedLibraries(PEReader image)
    {
        PEHeader header = image.PEHeaders.PEHeader!;

        HashSet<string> libraries = new(StringComparer.OrdinalIgnoreCase);

        DirectoryEntry imports = header.ImportTableDirectory;

        if (imports.Size > 0)
        {
            BlobReader table = image.GetSectionData(imports.RelativeVirtualAddress).GetReader();

            while (true)
            {
                _ = table.ReadUInt32();

                _ = table.ReadUInt32();

                _ = table.ReadUInt32();

                uint nameRva = table.ReadUInt32();

                _ = table.ReadUInt32();

                if (nameRva == 0)
                {
                    break;
                }

                libraries.Add(ReadAsciiName(image, checked((int)nameRva)));
            }
        }

        DirectoryEntry delayed = header.DelayImportTableDirectory;

        if (delayed.Size > 0)
        {
            BlobReader table = image.GetSectionData(delayed.RelativeVirtualAddress).GetReader();

            while (true)
            {
                uint attributes = table.ReadUInt32();

                uint nameRva = table.ReadUInt32();

                table.Offset += 24;

                if (attributes == 0 && nameRva == 0)
                {
                    break;
                }

                Assert.True(
                    (attributes & 1) == 1,
                    "A delay-load descriptor uses virtual addresses rather than RVAs, which this reader does not decode.");

                libraries.Add(ReadAsciiName(image, checked((int)nameRva)));
            }
        }

        return libraries;
    }

    private static (string[] Names, uint FunctionCount) ExportedNames(PEReader image)
    {
        DirectoryEntry exports = image.PEHeaders.PEHeader!.ExportTableDirectory;

        Assert.True(exports.Size > 0, "The library has no export table.");

        BlobReader directory = image.GetSectionData(exports.RelativeVirtualAddress).GetReader();

        directory.Offset += 20;

        uint functionCount = directory.ReadUInt32();

        uint nameCount = directory.ReadUInt32();

        directory.Offset += 4;

        uint namesRva = directory.ReadUInt32();

        BlobReader nameTable = image.GetSectionData(checked((int)namesRva)).GetReader();

        string[] names = new string[nameCount];

        for (int index = 0; index < names.Length; index++)
        {
            names[index] = ReadAsciiName(image, checked((int)nameTable.ReadUInt32()));
        }

        return (names, functionCount);
    }

    private static string ReadAsciiName(PEReader image, int rva)
    {
        BlobReader reader = image.GetSectionData(rva).GetReader();

        StringBuilder name = new();

        for (byte next = reader.ReadByte(); next != 0; next = reader.ReadByte())
        {
            name.Append((char)next);
        }

        return name.ToString();
    }
}
