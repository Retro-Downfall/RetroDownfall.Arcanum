using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The compiled shipping assemblies name the erasure evidence tables in one type only, and no embedded
/// SQL resource writes to them.
/// </summary>
/// <remarks>
/// <para>The source pins in <see cref="MemoryErasureEvidenceDeleterTests"/> read spellings, and a table
/// name assembled from parts never appears as one spelling: <c>"DELETE FROM memory_" + "erasure_receipts"</c>
/// or a name taken from a constant field. The compiler folds every such constant expression into one
/// string, so these pins read the strings each method body actually loads (<c>ldstr</c>) and attribute
/// each to the outermost type that declares it, which also covers async state machines, lambdas and
/// local functions.</para>
///
/// <para>What no compiled-string pin can see is a name built at run time from non-constant parts, such
/// as an interpolation over a variable. That case is caught only by the lifecycle survival rows in
/// <c>MemoryErasureLifecycleSurvivalTests</c>, which run the paths and compare every evidence row. The
/// cheapest such part is a non-constant string member of the owner itself: its literal is the owner's,
/// and any caller could concatenate it into SQL. So the owner may expose exactly one string member,
/// <c>InventoryStore</c>, the status label, which must never be used to build SQL.</para>
///
/// <para>The owner lists are closed. <c>MemoryErasureEvidence</c> holds every evidence statement and
/// is the only type that names the tables at all: every other caller, including the retention
/// inventory, goes through its members. A full installation reset removes the database file rather
/// than rows, so it needs no entry.</para>
/// </remarks>
public sealed class MemoryErasureEvidenceCompiledPinTests
{
    private const string Owner = "RetroDownfall.Arcanum.Infrastructure.Data.MemoryErasureEvidence";

    private const string OwnerAssembly = "RetroDownfall.Arcanum.Infrastructure.dll";

    /// <summary>Closed: the types whose compiled code may hold a statement that writes evidence.</summary>
    private static readonly (string Assembly, string Type)[] StatementOwners = [(OwnerAssembly, Owner)];

    /// <summary>Closed: the types whose compiled code may name an evidence table at all.</summary>
    private static readonly (string Assembly, string Type)[] NameOwners = [(OwnerAssembly, Owner)];

    private const string EvidenceTable =
        @"memory_erasure_(?:fingerprints|receipts|receipt_subjects)\b";

    /// <summary>An evidence table named anywhere in a string.</summary>
    private static readonly Regex NamesEvidence = new(
        EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Any keyword that changes rows or tables, anywhere in the same string.</summary>
    private static readonly Regex MutatingKeyword = new(
        @"\b(?:DELETE|UPDATE|INSERT|REPLACE|UPSERT|DROP|TRUNCATE|ALTER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A statement that writes rows of an evidence table: a delete, an insert or replace, or an update,
    /// with the table optionally schema-qualified and quoted. A trigger declared <c>BEFORE UPDATE ON</c>
    /// a table is DDL and is not a write to it.
    /// </summary>
    private static readonly Regex EvidenceRowWrite = new(
        @"(?:\bDELETE\s+FROM|\b(?:INSERT(?:\s+OR\s+\w+)?|REPLACE)\s+INTO|\bUPDATE(?:\s+OR\s+\w+)?)\s+"
            + @"(?:(?:""\w+""|\[\w+\]|`\w+`|'\w+'|\w+)\s*\.\s*)?[""\[`']?" + EvidenceTable,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Compiled_strings_that_write_evidence_belong_to_the_evidence_store_alone()
    {
        CompiledString[] statements =
        [
            .. ShippingStrings().Where(static found => NamesEvidence.IsMatch(found.Value) && MutatingKeyword.IsMatch(found.Value)),
        ];

        Assert.Contains(statements, static found => found.Owner == Owner && found.Value.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase));

        string[] violations =
        [
            .. statements
                .Where(static found => !StatementOwners.Contains((found.Assembly, found.Owner)))
                .Select(static found => found.ToString()),
        ];

        Assert.True(violations.Length == 0, "Evidence statements outside the evidence store:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Compiled_strings_that_name_evidence_belong_to_the_evidence_store_alone()
    {
        CompiledString[] names = [.. ShippingStrings().Where(static found => NamesEvidence.IsMatch(found.Value))];

        Assert.Contains(names, static found => found.Owner == Owner);

        string[] violations =
        [
            .. names
                .Where(static found => !NameOwners.Contains((found.Assembly, found.Owner)))
                .Select(static found => found.ToString()),
        ];

        Assert.True(violations.Length == 0, "Evidence table names outside the evidence store:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The owner exposes no string but the inventory label, so no caller can take a table name from it
    /// and build a statement the compiled-string pins never see.
    /// </summary>
    [Fact]
    public void The_evidence_store_exposes_no_string_member_but_the_inventory_label()
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        Type owner = typeof(ArcanumDbContext).Assembly.GetType(Owner, throwOnError: true)!;

        string[] fields =
        [
            .. owner.GetFields(members)
                .Where(static field => field.FieldType == typeof(string) && !field.IsPrivate)
                .Select(static field => field.Name),
        ];

        string[] properties =
        [
            .. owner.GetProperties(members)
                .Where(static property => property.PropertyType == typeof(string)
                    && property.GetMethod is { IsPrivate: false })
                .Select(static property => property.Name),
        ];

        Assert.Equal(["InventoryStore"], fields.Concat(properties).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The schema files that create the evidence tables are DDL. No embedded SQL resource holds a
    /// statement that writes evidence rows.
    /// </summary>
    [Fact]
    public void No_embedded_sql_resource_writes_evidence_rows()
    {
        EmbeddedSql[] resources = [.. ShippingAssemblyPaths().SelectMany(CompiledStringScanner.ReadSqlResources)];

        Assert.Contains(resources, static resource => NamesEvidence.IsMatch(resource.Text));

        string[] violations =
        [
            .. resources
                .Where(static resource => EvidenceRowWrite.IsMatch(resource.Text))
                .Select(static resource => $"{resource.Assembly}: {resource.Name}"),
        ];

        Assert.True(violations.Length == 0, "Embedded SQL that writes evidence rows:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The scan reads the folded value of a constant expression, and attributes a string in an async
    /// state machine or a lambda to the type that declares the method.
    /// </summary>
    [Fact]
    public void The_scan_reads_folded_constants_and_attributes_them_to_their_declaring_type()
    {
        string fixture = typeof(MemoryErasureCompiledPinFixture).FullName!;

        CompiledString[] strings =
        [
            .. CompiledStringScanner
                .Read(typeof(MemoryErasureCompiledPinFixture).Assembly.Location)
                .Where(found => found.Owner == fixture),
        ];

        Assert.Contains(strings, static found => found.Value == "DELETE FROM memory_erasure_receipts WHERE 0");

        Assert.Contains(strings, static found => found.Value == "memory_erasure_fingerprints");

        Assert.Contains(strings, static found => found.Value == "UPDATE \"main\".memory_erasure_receipt_subjects SET SubjectDigest = SubjectDigest");

        Assert.Contains(strings, static found => found.Value == "memory_erasure_receipts" && found.Method == "FoldedAsyncLambda");

        Assert.Contains(strings, static found => found.Value == "DELETE FROM memory_erasure_receipts WHERE 0" && found.Method == "FoldedDeleteAsync");

        Assert.Matches(EvidenceRowWrite, "DELETE FROM memory_erasure_receipts WHERE 0");

        Assert.Matches(EvidenceRowWrite, "INSERT OR IGNORE INTO [memory_erasure_fingerprints] (Fingerprint) VALUES ($f)");

        Assert.Matches(EvidenceRowWrite, "UPDATE \"main\".memory_erasure_receipt_subjects SET SubjectDigest = SubjectDigest");

        Assert.DoesNotMatch(EvidenceRowWrite, "BEFORE UPDATE ON memory_erasure_receipts");

        Assert.DoesNotMatch(NamesEvidence, "memory_erasure_receipts_guard_update");
    }

    [Fact]
    public void The_scan_reads_every_shipping_assembly()
    {
        string[] names = [.. ShippingAssemblyPaths().Select(static path => Path.GetFileNameWithoutExtension(path))];

        Assert.Equal(
            [
                "RetroDownfall.Arcanum.Api",
                "RetroDownfall.Arcanum.Cli",
                "RetroDownfall.Arcanum.Core",
                "RetroDownfall.Arcanum.Infrastructure",
                "RetroDownfall.Arcanum.Secrets",
            ],
            names.Order(StringComparer.Ordinal));

        Assert.All(ShippingAssemblyPaths(), static path => Assert.NotEmpty(CompiledStringScanner.Read(path)));
    }

    private static IEnumerable<CompiledString> ShippingStrings() =>
        ShippingAssemblyPaths().SelectMany(CompiledStringScanner.Read);

    private static string[] ShippingAssemblyPaths() =>
    [
        typeof(RetentionDataClass).Assembly.Location,
        typeof(InMemoryOsCredentialStore).Assembly.Location,
        typeof(ArcanumDbContext).Assembly.Location,
        typeof(ApiBootstrapper).Assembly.Location,
        typeof(ArcanumApiCredentialLease).Assembly.Location,
    ];
}

/// <summary>Strings the scanner must read as one folded value, each owned by this type.</summary>
internal static class MemoryErasureCompiledPinFixture
{
    private const string Prefix = "memory_";

    private const string Fingerprints = Prefix + "erasure_fingerprints";

    private const string Subjects = Prefix + "erasure_receipt_subjects";

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<string> FoldedDeleteAsync()
    {
        await Task.Yield();

        return "DELETE FROM memory_" + "erasure_receipts WHERE 0";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Func<string> FoldedName() => static () => Fingerprints;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string FoldedUpdate() => $"UPDATE \"main\".{Subjects} SET SubjectDigest = SubjectDigest";

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Func<Task<string>> FoldedAsyncLambda() =>
        static async () =>
        {
            await Task.Yield();

            return "memory_erasure_" + "receipts";
        };
}

/// <summary>One string a compiled method body loads, and where it lives.</summary>
internal sealed record CompiledString(string Assembly, string Owner, string Method, string Value)
{
    public override string ToString() => $"{Assembly}: {Owner}.{Method}: \"{Value}\"";
}

/// <summary>One embedded SQL resource and its text.</summary>
internal sealed record EmbeddedSql(string Assembly, string Name, string Text);

/// <summary>Reads the strings compiled method bodies load, and the SQL resources an assembly embeds.</summary>
internal static class CompiledStringScanner
{
    private static readonly Dictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static opcode => unchecked((ushort)opcode.Value), static opcode => opcode.OperandType);

    private static readonly ushort Ldstr = unchecked((ushort)OpCodes.Ldstr.Value);

    internal static IReadOnlyList<CompiledString> Read(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);

        using PEReader pe = new(stream);

        MetadataReader metadata = pe.GetMetadataReader();

        string assembly = Path.GetFileName(assemblyPath);

        List<CompiledString> strings = [];

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);

            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            string owner = OutermostTypeName(metadata, method.GetDeclaringType());

            string name = SourceMethodName(metadata, method);

            BlobReader il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();

            while (il.RemainingBytes > 0)
            {
                ushort opcode = il.ReadByte();

                if (opcode == 0xFE)
                {
                    opcode = (ushort)(0xFE00 | il.ReadByte());
                }

                if (opcode == Ldstr)
                {
                    int token = il.ReadInt32();

                    strings.Add(new(assembly, owner, name, metadata.GetUserString((UserStringHandle)MetadataTokens.Handle(token))));

                    continue;
                }

                SkipOperand(ref il, OperandTypes[opcode]);
            }
        }

        return strings;
    }

    internal static IReadOnlyList<EmbeddedSql> ReadSqlResources(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);

        using PEReader pe = new(stream);

        MetadataReader metadata = pe.GetMetadataReader();

        string assembly = Path.GetFileName(assemblyPath);

        List<EmbeddedSql> resources = [];

        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);

            string name = metadata.GetString(resource.Name);

            if (!resource.Implementation.IsNil || !name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PEMemoryBlock block = pe.GetSectionData(
                pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress + (int)resource.Offset);

            BlobReader reader = block.GetReader();

            int length = reader.ReadInt32();

            resources.Add(new(assembly, name, Encoding.UTF8.GetString(reader.ReadBytes(length))));
        }

        return resources;
    }

    private static string OutermostTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);

        while (type.GetDeclaringType() is { IsNil: false } declaring)
        {
            type = metadata.GetTypeDefinition(declaring);
        }

        string ns = metadata.GetString(type.Namespace);

        string name = metadata.GetString(type.Name);

        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>
    /// The method a body was written in: an async or iterator state machine's <c>MoveNext</c>, and a
    /// lambda's or local function's body, are reported by the source method the compiler named them for.
    /// </summary>
    private static string SourceMethodName(MetadataReader metadata, MethodDefinition method)
    {
        string name = metadata.GetString(method.Name);

        TypeDefinition type = metadata.GetTypeDefinition(method.GetDeclaringType());

        string typeName = metadata.GetString(type.Name);

        string generated = typeName.StartsWith('<') ? typeName : name;

        // An async lambda's state machine nests one generated name in another (<<Outer>b__0>d), so
        // every leading bracket goes before the source name is read.
        string trimmed = generated.TrimStart('<');

        int close = trimmed.IndexOf('>', StringComparison.Ordinal);

        return generated.StartsWith('<') && close > 0 ? trimmed[..close] : name;
    }

    private static void SkipOperand(ref BlobReader il, OperandType operand)
    {
        switch (operand)
        {
            case OperandType.InlineNone:
                return;

            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                il.Offset += 1;
                return;

            case OperandType.InlineVar:
                il.Offset += 2;
                return;

            case OperandType.InlineI8:
            case OperandType.InlineR:
                il.Offset += 8;
                return;

            case OperandType.InlineSwitch:
                int count = il.ReadInt32();
                il.Offset += 4 * count;
                return;

            default:
                il.Offset += 4;
                return;
        }
    }
}
