using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
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

        Skip.IfNot(
            string.Equals(
                global::System.Environment.GetEnvironmentVariable(OptInVariable),
                "true",
                StringComparison.OrdinalIgnoreCase),
            $"Set {OptInVariable}=true to exercise the real Keychain host-tools marker slot.");

        HostProcessToolsMarkerCredentialCapabilitySource source = new();

        Skip.IfNot(
            source.ProveFixedSlotDurablyAbsent().Status
                is HostProcessToolsMarkerCredentialAbsenceStatus.Absent,
            "The fixed host-tools marker slot is not empty on this machine; refusing to overwrite it.");

        OsCredentialStore credentials = new();

        Skip.IfNot(credentials.IsAvailable, "Requires a usable OS credential backend.");

        const string value = "arcanum-marker-slot-native-capability-probe";

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
