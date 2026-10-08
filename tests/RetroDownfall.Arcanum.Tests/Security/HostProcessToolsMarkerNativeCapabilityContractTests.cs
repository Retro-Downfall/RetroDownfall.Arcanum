using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Who may mint a fixed-slot capability, and what each platform arm is allowed to do to delete one.
/// </summary>
/// <remarks>
/// Inventory assertions over production source rather than behaviour tests, because the failure they
/// prevent is a new call site or a fallback quietly reintroduced, not a wrong result. A backend that
/// went back to looking its target up by service and account would still pass every behavioural test
/// in this suite — the value it deletes is the value it compared — and would still be deleting an
/// item that may have been replaced since the comparison.
///
/// <para>The one arm that can be exercised for real on a developer machine is macOS, and it is
/// opt-in behind the same variable the credential round-trip suite uses, because it reaches the
/// machine's own login keychain.</para>
/// </remarks>
public sealed class HostProcessToolsMarkerNativeCapabilityContractTests
{
    private const string OptInVariable = "ARCANUM_TEST_OS_CREDENTIAL_STORE";

    private const string CapabilityFile = "HostProcessToolsMarkerCredentialCapability.cs";

    private const string SourceFile = "HostProcessToolsMarkerCredentialCapabilitySource.cs";

    private const string MacOsFile = "MacOsHostProcessToolsMarkerSlot.cs";

    private const string WindowsFile = "WindowsHostProcessToolsMarkerSlot.cs";

    private const string LinuxFile = "LinuxHostProcessToolsMarkerSlot.cs";

    private const string AdapterFile = "HostProcessToolsMarkerStore.cs";

    /// <summary>
    /// Only the fixed-slot backends may take ownership of a value and a record.
    /// </summary>
    /// <remarks>
    /// Minting a capability is minting deletion authority over the marker slot. A caller elsewhere
    /// could hand it any bytes and any record, and the layer above compares against exactly what it
    /// was given — so the set of files that can construct one is the set of files that decide what
    /// the slot contains.
    /// </remarks>
    [Fact]
    public void Only_the_secrets_fixed_slot_backends_mint_a_capability()
    {
        string[] permitted = [CapabilityFile, SourceFile, MacOsFile, WindowsFile];

        string[] offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => !permitted.Any(source.Is))
                .Where(static source =>
                    source.Names("HostProcessToolsMarkerCredentialCapability.CreateOwned("))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Length == 0,
            "Minting a fixed-slot capability is minting deletion authority over the host-tools "
            + "marker. Only the Secrets backends that read the slot may do it: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// No fixed-slot backend reaches the ordinary name-addressed credential operations.
    /// </summary>
    /// <remarks>
    /// Those take a service and an account and act on whatever currently matches. Used from a reset
    /// arm they would delete the item that answers the name now rather than the record that was
    /// compared, which is the exact substitution the retained record exists to refuse.
    /// </remarks>
    [Theory]
    [InlineData("secret_password_clear_sync")]
    [InlineData("secret_password_lookup_sync")]
    [InlineData("SecKeychainAddGenericPassword")]
    [InlineData("IOsCredentialStore")]
    public void No_fixed_slot_backend_reaches_a_name_addressed_credential_operation(string forbidden)
    {
        string[] backends = [SourceFile, MacOsFile, WindowsFile, LinuxFile];

        string[] offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => backends.Any(source.Is))
                .Where(source => source.Names(forbidden))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(offenders.Length == 0, $"{forbidden} must not be reachable from: " + string.Join(", ", offenders));
    }

    /// <summary>The macOS arm deletes the reference it retained, and releases it exactly once.</summary>
    [Fact]
    public void The_macos_arm_rereads_and_deletes_the_exact_retained_item_reference()
    {
        string source = Source(MacOsFile);

        // The reread comes from the retained reference rather than from a fresh lookup by name.
        Assert.Contains("SecKeychainItemCopyAttributesAndData(\n                _itemRef,", source, StringComparison.Ordinal);

        Assert.Contains("SecKeychainItemDelete(_itemRef)", source, StringComparison.Ordinal);

        // Exactly one delete call site, and it takes the retained reference. The second occurrence
        // is the LibraryImport declaration, which is the only other way the name may appear.
        Assert.Equal(1, Occurrences(source, "SecKeychainItemDelete(_itemRef)"));

        Assert.Equal(2, Occurrences(source, "SecKeychainItemDelete("));

        Assert.Contains("CFRelease(held)", source, StringComparison.Ordinal);

        Assert.Contains("FixedTimeEquals", source, StringComparison.Ordinal);
    }

    /// <summary>The Windows arm compares the complete record, stamp included, before deleting.</summary>
    [Fact]
    public void The_windows_arm_compares_the_complete_record_immediately_before_deleting()
    {
        string source = Source(WindowsFile);

        Assert.Contains("LastWritten", source, StringComparison.Ordinal);

        Assert.Contains("live.LastWritten != retained.LastWritten", source, StringComparison.Ordinal);

        Assert.Contains("FixedTimeEquals(live.Blob, retained.Blob)", source, StringComparison.Ordinal);

        // One call site plus its LibraryImport declaration, and the call names the fixed target.
        Assert.Equal(1, Occurrences(source, "CredDeleteW(TargetName(), CredTypeGeneric, 0)"));

        Assert.Equal(2, Occurrences(source, "CredDeleteW("));
    }

    /// <summary>
    /// The Windows arm keeps the marker value only in buffers it can zero.
    /// </summary>
    /// <remarks>
    /// The capability's contract is that the secret never becomes a garbage-collected string. The
    /// Windows snapshot used to carry the decoded value beside the blob, which is a second,
    /// unzeroable copy of exactly what the capability exists not to create. Read from the syntax tree
    /// rather than searched for as text, so it asks what the arm declares and calls and is neither
    /// fooled by a name that merely contains a word nor blind to a decoder spelled another way.
    /// </remarks>
    [Fact]
    public void The_windows_arm_never_decodes_the_marker_value_into_a_string()
    {
        CompilationUnitSyntax root = Parse(Source(WindowsFile));

        // The snapshot is the blob and the write stamp, and nothing else.
        RecordDeclarationSyntax snapshot = Assert.Single(
            root.DescendantNodes().OfType<RecordDeclarationSyntax>(),
            static record => record.Identifier.ValueText == "CredentialSnapshot");

        Assert.Equal(
            ["byte[] Blob", "long LastWritten"],
            snapshot.ParameterList!.Parameters.Select(static parameter => parameter.ToString()));

        // The only strings the arm names are the credential target it looks up; a new string-typed
        // variable, parameter or property here has to be read and shown not to hold the value.
        Assert.Equal(["target", "targetName"], StringTypedNames(root));

        // Nothing in the arm turns bytes, native or managed, into text.
        string[] decoders =
        [
            "GetString",
            "PtrToStringUni",
            "PtrToStringAuto",
            "PtrToStringAnsi",
            "PtrToStringUTF8",
            "PtrToStringBSTR",
            "ToBase64String",
        ];

        string[] called =
        [
            .. root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(static call => call.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    IdentifierNameSyntax name => name.Identifier.ValueText,
                    _ => string.Empty,
                }),
        ];

        Assert.Empty(called.Intersect(decoders, StringComparer.Ordinal));

        // Both the open and the compare re-encode the blob straight to UTF-8 bytes.
        Assert.Equal(2, called.Count(static name => name == "TryEncodeUtf16Blob"));
    }

    /// <summary>
    /// The capability contract names the one property Windows cannot give: an atomic compare and delete.
    /// </summary>
    /// <remarks>
    /// Credential Manager deletes by target name, so between the reread that compared the record and
    /// the delete there is a window in which a replacement is deleted and reported as
    /// <c>Deleted</c>, because the readback then sees an empty slot; the readback detects only a
    /// record re-created after the delete. The window is microseconds, but a contract that did not
    /// say so would read as an atomicity guarantee the Windows arm does not provide.
    ///
    /// <para>Asserted on the documentation of the one member that carries the contract, and as a
    /// sequence rather than as loose words: a file that mentions "Windows" and "microsecond"
    /// somewhere satisfies a substring check without saying that a replacement is deleted and
    /// reported as <c>Deleted</c>, which is the sentence that matters.</para>
    /// </remarks>
    [Fact]
    public void The_capability_contract_names_the_windows_compare_delete_window()
    {
        // The raw file rather than the inventory's text: the contract lives in the remarks, and the
        // inventory strips comments.
        string raw = File.ReadAllText(Path.Combine(
            NativeSqlCipherTestPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Secrets",
            "Security",
            CapabilityFile));

        MethodDeclarationSyntax compareDelete = Parse(raw)
            .DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "IHostProcessToolsMarkerNativeRecordCapability")
            .Members
            .OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "CompareDeleteExact");

        string contract = Regex.Replace(
            string.Concat(compareDelete.GetLeadingTrivia().Select(static trivia => trivia.ToFullString()))
                .Replace("///", " ", StringComparison.Ordinal),
            @"\s+",
            " ");

        // Each arm's account of what "exact" means, in the order the remarks give them.
        Assert.Matches(@"macOS deletes the very keychain item reference it retained.*never touched", contract);

        Assert.Matches(
            @"Windows has no handle to retain and deletes by target name.*"
            + @"window of a few microseconds in which a replacement would be deleted too.*"
            + @"reports <see cref=""HostProcessToolsMarkerCredentialDeleteStatus\.Deleted""/>.*"
            + @"readback sees an empty slot.*"
            + @"re-created after the delete.*HostProcessToolsMarkerCredentialDeleteStatus\.Mismatch",
            contract);

        Assert.Matches(@"Linux retains nothing.*HostProcessToolsMarkerCredentialOpenStatus\.Unavailable", contract);
    }

    /// <summary>
    /// The design document says the same thing about the Windows arm that the capability contract does.
    /// </summary>
    [Fact]
    public void The_design_marker_slot_table_names_the_windows_compare_delete_window()
    {
        string design = File.ReadAllText(Path.Combine(
            NativeSqlCipherTestPaths.RepositoryRoot(),
            "docs",
            "Arcanum.DESIGN.md"));

        int section = design.IndexOf("### 10.15 ", StringComparison.Ordinal);

        Assert.True(section >= 0, "DESIGN section 10.15 was not found.");

        int next = design.IndexOf("\n### ", section + 1, StringComparison.Ordinal);

        string windows = Assert.Single(
            design[section..(next < 0 ? design.Length : next)].Split('\n'),
            static line => line.StartsWith("| Windows |", StringComparison.Ordinal));

        Assert.Matches(
            @"CredDeleteW.*by target name.*no handle.*few microseconds.*deleted too and reported as `Deleted`.*"
            + @"readback detects only a record re-created after the delete.*`Mismatch`",
            windows);
    }

    [Fact]
    public void A_utf16_marker_blob_encodes_to_exactly_its_utf8_bytes_without_a_string_round_trip()
    {
        byte[] blob = Encoding.Unicode.GetBytes("QUJD\u00e9\0");

        Assert.True(HostProcessToolsMarkerSlotIdentity.TryEncodeUtf16Blob(blob, out byte[] encoded));

        Assert.Equal(Encoding.UTF8.GetBytes("QUJD\u00e9"), encoded);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0, 0 })]
    [InlineData(new byte[] { 0x41, 0x00, 0x42 })]
    [InlineData(new byte[] { 0x41, 0x00, 0x00, 0xD8, 0x00, 0x00 })]
    public void An_unusable_utf16_marker_blob_is_present_but_invalid(byte[] blob)
    {
        Assert.False(HostProcessToolsMarkerSlotIdentity.TryEncodeUtf16Blob(blob, out byte[] encoded));

        Assert.Empty(encoded);
    }

    /// <summary>
    /// The Linux arm blocks rather than clearing by attributes, and never reports absence.
    /// </summary>
    /// <remarks>
    /// Retaining a stable Secret Service item needs the item API family rather than the password
    /// helpers this project has proven, so this arm refuses instead of performing a delete it cannot
    /// prove acted on the item it compared. Reporting absence would be the one answer that lets a
    /// reset continue past a marker that is still there, so it is the answer this arm never gives.
    /// </remarks>
    [Fact]
    public void The_linux_arm_refuses_rather_than_clearing_by_attributes()
    {
        string source = Source(LinuxFile);

        Assert.Contains("HostProcessToolsMarkerCredentialOpenResult.Unavailable()", source, StringComparison.Ordinal);

        Assert.Contains("HostProcessToolsMarkerCredentialAbsenceResult.Unavailable()", source, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "HostProcessToolsMarkerCredentialAbsenceResult.Absent()",
            source,
            StringComparison.Ordinal);

        Assert.DoesNotContain("CreateOwned", source, StringComparison.Ordinal);
    }

    /// <summary>The reset adapter is the only production implementation of the reset port.</summary>
    [Fact]
    public void One_production_type_implements_the_reset_operating_system_port()
    {
        string[] implementers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names(": IHostToolsMarkerPairResetOsPort"))
                .Select(static source => Path.GetFileName(source.RelativePath))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([AdapterFile], implementers);
    }

    /// <summary>
    /// The real macOS keychain arm, end to end, when a lane has asked for it.
    /// </summary>
    /// <remarks>
    /// Skipped rather than silently passing everywhere else, and it refuses to run at all unless the
    /// fixed slot is already provably empty: this is the installation's own marker account, and a
    /// test that wrote over a live marker would destroy the evidence a real reset depends on.
    /// </remarks>
    [SkippableFact]
    public void The_macos_arm_opens_compare_deletes_and_proves_the_real_fixed_slot_absent()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The macOS keychain arm runs only on macOS.");

        ExerciseTheRealFixedSlot("arcanum-marker-slot-native-capability-probe");
    }

    /// <summary>
    /// The real Windows Credential Manager arm, end to end, in the Windows lane.
    /// </summary>
    /// <remarks>
    /// Everything the Windows arm decides can be checked on any machine through the platform-neutral
    /// UTF-16 re-encoding cases above, but what Credential Manager actually hands back — the blob's
    /// terminator, its stamp, a delete by target name followed by a readback — can only be seen on
    /// Windows. The value carries a non-ASCII character so the stored UTF-16 blob has to be
    /// re-encoded to different UTF-8 bytes rather than copied. Same opt-in and same refusal to touch
    /// a live marker as the macOS test; skipped everywhere else.
    /// </remarks>
    [SkippableFact]
    public void The_windows_arm_opens_compare_deletes_and_proves_the_real_fixed_slot_absent()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The Windows Credential Manager arm runs only on Windows.");

        ExerciseTheRealFixedSlot("arcanum-marker-slot-native-capability-probe-\u00e9");
    }

    private static void ExerciseTheRealFixedSlot(string value)
    {
        Skip.IfNot(
            string.Equals(
                global::System.Environment.GetEnvironmentVariable(OptInVariable),
                "true",
                StringComparison.OrdinalIgnoreCase),
            $"Set {OptInVariable}=true to exercise the real host-tools marker slot.");

        HostProcessToolsMarkerCredentialCapabilitySource source = new();

        Skip.IfNot(
            source.ProveFixedSlotDurablyAbsent().Status
                is HostProcessToolsMarkerCredentialAbsenceStatus.Absent,
            "The fixed host-tools marker slot is not empty on this machine; refusing to overwrite it.");

        OsCredentialStore credentials = new();

        Skip.IfNot(credentials.IsAvailable, "Requires a usable OS credential backend.");

        try
        {
            Assert.Equal(
                OsCredentialStoreStatus.Ok,
                credentials.Set(
                    ArcanumCredentialIdentity.Service,
                    ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
                    value).Status);

            HostProcessToolsMarkerCredentialOpenResult opened = source.OpenFixedSlot();

            Assert.Equal(HostProcessToolsMarkerCredentialOpenStatus.Opened, opened.Status);

            using HostProcessToolsMarkerCredentialCapability capability =
                Assert.IsType<HostProcessToolsMarkerCredentialCapability>(opened.Capability);

            byte[] expected = Encoding.UTF8.GetBytes(value);

            Assert.Equal(expected.Length, capability.EncodedSecretUtf8Length);

            byte[] copied = new byte[capability.EncodedSecretUtf8Length];

            Assert.True(capability.TryCopyEncodedSecretUtf8(copied, out _));

            Assert.Equal(expected, copied);

            Assert.Equal(
                HostProcessToolsMarkerCredentialDeleteStatus.Deleted,
                capability.CompareDeleteExact(expected));

            Assert.Equal(
                HostProcessToolsMarkerCredentialAbsenceStatus.Absent,
                source.ProveFixedSlotDurablyAbsent().Status);
        }
        finally
        {
            _ = credentials.Delete(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.HostProcessToolsTaintAccount);
        }
    }

    private static CompilationUnitSyntax Parse(string source) =>
        CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    /// <summary>
    /// The distinct names of every variable, parameter and property in <paramref name="root"/> that is
    /// declared as a <see cref="string"/>.
    /// </summary>
    private static string[] StringTypedNames(CompilationUnitSyntax root)
    {
        static bool IsString(TypeSyntax? type) =>
            type switch
            {
                PredefinedTypeSyntax predefined => predefined.Keyword.IsKind(SyntaxKind.StringKeyword),
                NullableTypeSyntax nullable => IsString(nullable.ElementType),
                _ => false,
            };

        IEnumerable<string> variables = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .Where(static declaration => IsString(declaration.Type))
            .SelectMany(static declaration => declaration.Variables)
            .Select(static variable => variable.Identifier.ValueText);

        IEnumerable<string> parameters = root.DescendantNodes()
            .OfType<ParameterSyntax>()
            .Where(static parameter => IsString(parameter.Type))
            .Select(static parameter => parameter.Identifier.ValueText);

        IEnumerable<string> properties = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Where(static property => IsString(property.Type))
            .Select(static property => property.Identifier.ValueText);

        return
        [
            .. variables.Concat(parameters).Concat(properties)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static string Source(string fileName) =>
        ProductionSourceInventory.Sources().Single(source => source.Is(fileName)).Text;

    private static int Occurrences(string source, string value)
    {
        int count = 0;

        int offset = 0;

        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;

            offset += value.Length;
        }

        return count;
    }
}
