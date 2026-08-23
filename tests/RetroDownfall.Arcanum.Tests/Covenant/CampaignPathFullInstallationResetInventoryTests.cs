using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CampaignPathFullInstallationResetInventoryTests
{

    static CampaignPathFullInstallationResetInventoryTests() =>
        SqliteNativeRuntime.Instance.Initialize();

    private static readonly byte[] RootIdentityKey = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] MarkerKey = Convert.FromHexString(
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

    [Fact]
    public async Task Empty_initial_inventory_is_a_positive_authenticated_vector()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        Guid ownerOperationId = Guid.NewGuid();

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                ownerOperationId,
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));
        Assert.Equal(ownerOperationId, inventory.Value.OwnerOperationId);
        Assert.Empty(inventory.Value.Entries);
        Assert.Equal(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(
                ImmutableArray<CampaignMarkerInventoryEntryV1>.Empty).Value,
            inventory.Value.InventoryDigest);
        Assert.Equal(0, harness.RecoveryKeys.Calls);

    }

    [Fact]
    public async Task Initial_full_reset_inventory_reads_the_complete_registry_from_the_borrowed_core_connection()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot first = await harness.AddMarkedRootAsync(
            Guid.Parse("4e6f280b-5a3f-4ed1-8c72-303644c7c824"),
            3,
            "alpha");

        RegisteredRoot second = await harness.AddMarkedRootAsync(
            Guid.Parse("ab86c1d4-70b8-4bf3-a6fe-27b068dd936d"),
            7,
            "beta");

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));
        Assert.Equal(2, inventory.Value.Entries.Length);
        Assert.Equal([first.CampaignId, second.CampaignId],
            inventory.Value.Entries.Select(static entry => entry.CampaignId));
        Assert.Equal([first.Revision, second.Revision],
            inventory.Value.Entries.Select(static entry => entry.PriorPathRevision));
        Assert.Equal([first.MarkerDigest, second.MarkerDigest],
            inventory.Value.Entries.Select(static entry => entry.MarkerDigest));
        Assert.Equal(0, harness.LiveConnectionSource.Calls);

    }

    [Fact]
    public async Task Initial_inventory_orders_by_rfc4122_campaign_bytes_not_sqlite_or_guid_runtime_order()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        Guid firstByRfcBytes = Guid.Parse("00000001-0000-0000-0000-000000000000");

        Guid secondByRfcBytes = Guid.Parse("00000100-0000-0000-0000-000000000000");

        Assert.True(firstByRfcBytes.CompareTo(secondByRfcBytes) < 0);
        Assert.True(firstByRfcBytes.ToByteArray().AsSpan().SequenceCompareTo(
            secondByRfcBytes.ToByteArray()) > 0);

        _ = await harness.AddMarkedRootAsync(secondByRfcBytes, 2, "inserted-first");

        _ = await harness.AddMarkedRootAsync(firstByRfcBytes, 1, "inserted-second");

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));
        Assert.Equal(
            [firstByRfcBytes, secondByRfcBytes],
            inventory.Value.Entries.Select(static entry => entry.CampaignId));

    }

    [SkippableFact]
    public async Task Initial_inventory_opens_each_registered_root_once_without_following_links()
    {

        Skip.If(OperatingSystem.IsWindows(), "Creating a directory symlink on Windows needs elevation.");

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            4,
            "real-root");

        string link = harness.CreateDirectorySymlink("linked-root", root.DisplayPath);

        await harness.UpdateDisplayPathAsync(root.CampaignId, link);

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsFailure);
        Assert.True(File.Exists(Path.Combine(
            root.DisplayPath,
            ".arcanum",
            "campaign-root.marker")));

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate =>
                candidate.Is("CampaignPathMarkerLifecycle.FullInstallationReset.cs"));

        Assert.DoesNotContain("IdentifyExact", source.Text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source.Text, ".OpenExistingAsync("));

    }

    [Fact]
    public async Task Initial_inventory_authenticates_exact_marker_bytes_and_same_handle_root_ownership()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            11,
            "owned-root");

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));

        CampaignMarkerInventoryEntryV1 entry = Assert.Single(inventory.Value.Entries);

        Assert.Equal(root.CampaignId, entry.CampaignId);
        Assert.Equal(root.Revision, entry.PriorPathRevision);
        Assert.Equal(root.MarkerDigest, entry.MarkerDigest);
        Assert.Equal(root.IdentityDigest, entry.IndexedPhysicalIdentityDigest);
        Assert.Equal(
            FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(
                root.DisplayPath).Value,
            entry.CanonicalDisplayPathDigest);
        Assert.Equal(
            FullInstallationResetMarkerPairResetDigests.SameHandleOwnership(
                root.CampaignId,
                root.Revision,
                root.MarkerDigest,
                root.IdentityDigest,
                root.IdentityDigest,
                root.RootVolumeId,
                root.RootFileId).Value,
            entry.SameHandleOwnershipEvidenceDigest);

    }

    [Theory]
    [InlineData("root")]
    [InlineData("marker")]
    [InlineData("ownership")]
    public async Task Initial_inventory_refuses_before_journal_when_any_root_marker_or_ownership_is_unavailable(
        string unavailable)
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            5,
            "refused-root");

        switch (unavailable)
        {
            case "root":
                Directory.Delete(root.DisplayPath, recursive: true);
                break;
            case "marker":
                File.Delete(harness.MarkerPath(root));
                break;
            default:
                await File.WriteAllBytesAsync(
                    harness.MarkerPath(root),
                    [0x01, 0x02, 0x03],
                    CancellationToken.None);
                break;
        }

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsFailure);

    }

    [Theory]
    [InlineData("physical-identity")]
    [InlineData("marker-campaign")]
    [InlineData("marker-revision")]
    [InlineData("root-binding")]
    public async Task Initial_inventory_refuses_physical_identity_marker_campaign_revision_or_root_binding_mismatch(
        string mismatch)
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            9,
            "mismatched-root");

        switch (mismatch)
        {
            case "physical-identity":
                await harness.UpdateIdentityDigestAsync(root.CampaignId, Digest(0xE1));
                break;
            case "marker-campaign":
                await harness.ReplaceMarkerAsync(
                    root,
                    Guid.NewGuid(),
                    root.Revision,
                    root.RootVolumeId,
                    root.RootFileId);
                break;
            case "marker-revision":
                await harness.ReplaceMarkerAsync(
                    root,
                    root.CampaignId,
                    root.Revision + 1,
                    root.RootVolumeId,
                    root.RootFileId);
                break;
            default:
                (ulong volumeId, ulong fileId) = harness.CreateUnregisteredRootTuple();
                await harness.ReplaceMarkerAsync(
                    root,
                    root.CampaignId,
                    root.Revision,
                    volumeId,
                    fileId);
                break;
        }

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsFailure);

    }

    [Theory]
    [InlineData("default")]
    [InlineData("duplicate")]
    [InlineData("zero-revision")]
    [InlineData("wrong-policy")]
    [InlineData("campaign-storage")]
    [InlineData("revision-storage")]
    [InlineData("noncanonical-path")]
    [InlineData("digest-storage")]
    [InlineData("digest-size")]
    [InlineData("oversized")]
    public async Task Initial_inventory_rejects_default_duplicate_zero_revision_and_more_than_4096_entries(
        string invalid)
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        object campaign = Guid.NewGuid().ToString("D");

        object policy = (long)CampaignPathIdentityPolicy.Version;

        object revision = 1L;

        object displayPath = harness.ScratchPath;

        object digest = Digest(0x55).Bytes.ToArray();

        switch (invalid)
        {
            case "default":
                campaign = Guid.Empty.ToString("D");
                break;
            case "duplicate":
                await harness.InsertRawRegistryRowAsync(
                    campaign,
                    policy,
                    revision,
                    displayPath,
                    digest);
                break;
            case "zero-revision":
                revision = 0L;
                break;
            case "wrong-policy":
                policy = checked((long)CampaignPathIdentityPolicy.Version + 1);
                break;
            case "campaign-storage":
                campaign = 42L;
                break;
            case "revision-storage":
                revision = "not-a-revision";
                break;
            case "noncanonical-path":
                displayPath = Path.Combine(harness.ScratchPath, ".");
                break;
            case "digest-storage":
                digest = new string('a', CovenantLimits.DigestBytes);
                break;
            case "digest-size":
                digest = new byte[CovenantLimits.DigestBytes - 1];
                break;
            case "oversized":
                for (int index = 0;
                    index <= HostToolsMarkerPairResetCheckpointBounds.MaximumVectorCount;
                    index++)
                {

                    await harness.InsertRawRegistryRowAsync(
                        GuidFromIndex(index).ToString("D"),
                        policy,
                        revision,
                        displayPath,
                        digest);

                }
                break;
        }

        if (invalid is not "oversized")
        {

            await harness.InsertRawRegistryRowAsync(
                campaign,
                policy,
                revision,
                displayPath,
                digest);

        }

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsFailure);
        Assert.Equal(0, harness.RecoveryKeys.Calls);

    }

    [Fact]
    public async Task Nonempty_initial_inventory_with_missing_root_identity_key_makes_no_credential_write_root_open_codec_or_marker_call()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            6,
            "key-preflight-root");

        byte[] markerBefore = await File.ReadAllBytesAsync(
            harness.MarkerPath(root),
            CancellationToken.None);

        RecordingCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider missingKeys = new(credentials);

        CampaignPathMarkerLifecycle lifecycle = new(
            new CampaignPathMarkerCodec(missingKeys),
            new PhysicalCampaignRootOpener(missingKeys),
            harness.LiveConnectionSource,
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System,
            missingKeys);

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsFailure);
        Assert.Equal(1, credentials.GetCount);
        Assert.Equal(0, credentials.SetCount);
        Assert.Equal(
            markerBefore,
            await File.ReadAllBytesAsync(
                harness.MarkerPath(root),
                CancellationToken.None));

    }

    [Fact]
    public async Task Empty_initial_inventory_never_reads_or_creates_the_root_identity_key()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RecordingCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider missingKeys = new(credentials);

        CampaignPathMarkerLifecycle lifecycle = new(
            new CampaignPathMarkerCodec(missingKeys),
            new PhysicalCampaignRootOpener(missingKeys),
            harness.LiveConnectionSource,
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System,
            missingKeys);

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));
        Assert.Empty(inventory.Value.Entries);
        Assert.Equal(0, credentials.GetCount);
        Assert.Equal(0, credentials.SetCount);

    }

    [Fact]
    public async Task Full_reset_lifecycle_methods_without_recovery_port_fail_content_free()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        CampaignPathMarkerLifecycle legacy = new(
            harness.Codec,
            harness.Opener,
            harness.LiveConnectionSource,
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await legacy.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Result revalidated = await legacy.RevalidateFullInstallationResetInventoryAsync(
            null!,
            harness.Connection,
            CancellationToken.None);

        Result<CampaignPathFullInstallationResetCleanupReceipt> prepared =
            await legacy.PrepareFullInstallationResetCleanupAsync(
                null!,
                null,
                null!,
                harness.Connection,
                null!,
                CancellationToken.None);

        Result<CampaignPathFullInstallationResetCleanupReceipt> reconciled =
            await legacy.ReconcileFullInstallationResetCleanupAsync(
                null!,
                null!,
                harness.Connection,
                CancellationToken.None);

        Error[] errors =
        [
            inventory.Error,
            revalidated.Error,
            prepared.Error,
            reconciled.Error,
        ];

        Assert.All(errors, error =>
        {
            Assert.Equal(ErrorCodes.Data.RecoveryRequired, error.Code);
            Assert.DoesNotContain(harness.ScratchPath, error.Message, StringComparison.Ordinal);
        });

    }

    [Fact]
    public async Task Initial_inventory_refuses_registry_change_before_pair_journal_publication()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            8,
            "revalidated-root");

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                Guid.NewGuid(),
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));

        Assert.True((await harness.Lifecycle.RevalidateFullInstallationResetInventoryAsync(
            inventory.Value,
            harness.Connection,
            CancellationToken.None)).IsSuccess);

        await harness.UpdateRevisionAsync(root.CampaignId, root.Revision + 1);

        Result changed = await harness.Lifecycle.RevalidateFullInstallationResetInventoryAsync(
            inventory.Value,
            harness.Connection,
            CancellationToken.None);

        Assert.True(changed.IsFailure);

    }

    [Fact]
    public async Task Initial_inventory_retains_only_runtime_root_authority_and_returns_detached_digest_evidence()
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            12,
            "detached-evidence-root");

        Guid ownerOperationId = Guid.NewGuid();

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                ownerOperationId,
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));

        CampaignMarkerInventoryEntryV1 entry = Assert.Single(inventory.Value.Entries);

        byte[] callerMarkerDigest = entry.MarkerDigest.Bytes;

        byte[] callerInventoryDigest = inventory.Value.InventoryDigest.Bytes;

        callerMarkerDigest.AsSpan().Fill(0xD1);

        callerInventoryDigest.AsSpan().Fill(0xD2);

        Assert.Equal(root.MarkerDigest, entry.MarkerDigest);

        Assert.Equal(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(
                inventory.Value.Entries).Value,
            inventory.Value.InventoryDigest);

        Assert.DoesNotContain(
            typeof(CampaignPathFullInstallationResetInventory)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            static field => field.FieldType == typeof(string)
                || typeof(Delegate).IsAssignableFrom(field.FieldType)
                || typeof(CampaignPathMarkerRootAuthority).IsAssignableFrom(field.FieldType));

        Assert.DoesNotContain(
            typeof(CampaignMarkerInventoryEntryV1).GetProperties(),
            static property => property.PropertyType == typeof(string)
                || typeof(Delegate).IsAssignableFrom(property.PropertyType)
                || typeof(CampaignPathMarkerRootAuthority).IsAssignableFrom(property.PropertyType));

        CampaignPathMarkerRootAuthority retained = Assert.Single(
            RetainedFullResetRoots(harness.Lifecycle));

        Assert.Equal(root.CampaignId, retained.CampaignId);

        await harness.Lifecycle.ReleaseRetainedRootsAsync(ownerOperationId);

    }

    [Theory]
    [InlineData("count")]
    [InlineData("campaign-row")]
    [InlineData("revision")]
    [InlineData("display-path")]
    [InlineData("identity")]
    [InlineData("marker")]
    [InlineData("root-binding")]
    public async Task Inventory_revalidation_failure_releases_every_attempt_owned_root_exactly_once(
        string change)
    {

        await using InventoryHarness harness = await InventoryHarness.CreateAsync();

        RegisteredRoot root = await harness.AddMarkedRootAsync(
            Guid.NewGuid(),
            13,
            $"revalidation-{change}");

        Guid ownerOperationId = Guid.NewGuid();

        Result<CampaignPathFullInstallationResetInventory> inventory =
            await harness.Lifecycle.InventoryFullInstallationResetCleanupAsync(
                ownerOperationId,
                harness.Connection,
                CancellationToken.None);

        Assert.True(inventory.IsSuccess, Describe(inventory));

        CampaignPathMarkerRootAuthority retained = Assert.Single(
            RetainedFullResetRoots(harness.Lifecycle));

        Result unchanged = await harness.Lifecycle.RevalidateFullInstallationResetInventoryAsync(
            inventory.Value,
            harness.Connection,
            CancellationToken.None);

        Assert.True(unchanged.IsSuccess);

        Assert.Same(retained, Assert.Single(RetainedFullResetRoots(harness.Lifecycle)));

        switch (change)
        {
            case "count":
                await harness.DeleteRegistryRowAsync(root.CampaignId);
                break;
            case "campaign-row":
                await harness.UpdateCampaignIdAsync(root.CampaignId, Guid.NewGuid());
                break;
            case "revision":
                await harness.UpdateRevisionAsync(root.CampaignId, root.Revision + 1);
                break;
            case "display-path":
                await harness.UpdateDisplayPathAsync(root.CampaignId, harness.ScratchPath);
                break;
            case "identity":
                await harness.UpdateIdentityDigestAsync(root.CampaignId, Digest(0xD3));
                break;
            case "marker":
                await File.WriteAllBytesAsync(
                    harness.MarkerPath(root),
                    [0x01, 0x02, 0x03],
                    CancellationToken.None);
                break;
            default:
                (ulong volumeId, ulong fileId) = harness.CreateUnregisteredRootTuple();
                await harness.ReplaceMarkerAsync(
                    root,
                    root.CampaignId,
                    root.Revision,
                    volumeId,
                    fileId);
                break;
        }

        Result changed = await harness.Lifecycle.RevalidateFullInstallationResetInventoryAsync(
            inventory.Value,
            harness.Connection,
            CancellationToken.None);

        Assert.True(changed.IsFailure);

        Assert.Empty(RetainedFullResetRoots(harness.Lifecycle));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await retained.OpenMarkerOrProveAbsentNoFollowAsync(CancellationToken.None));

        await harness.Lifecycle.ReleaseRetainedRootsAsync(ownerOperationId);

        Assert.Empty(RetainedFullResetRoots(harness.Lifecycle));

    }

    private static Guid GuidFromIndex(int index)
    {

        Span<byte> bytes = stackalloc byte[16];

        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes[12..], index + 1);

        return new Guid(bytes, bigEndian: true);

    }

    private static CovenantDigest Digest(byte seed) =>
        new([.. Enumerable.Repeat(seed, CovenantLimits.DigestBytes)]);

    private static CampaignPathMarkerRootAuthority[] RetainedFullResetRoots(
        CampaignPathMarkerLifecycle lifecycle)
    {

        FieldInfo retainedRootsField = typeof(CampaignPathMarkerLifecycle).GetField(
            "_fullResetRetainedRoots",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        object retainedRoots = retainedRootsField.GetValue(lifecycle)!;

        PropertyInfo valuesProperty = retainedRoots.GetType().GetProperty("Values")!;

        return ((IEnumerable<CampaignPathMarkerRootAuthority>)valuesProperty.GetValue(
            retainedRoots)!).ToArray();

    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    private sealed class InventoryHarness : IAsyncDisposable
    {

        private readonly string _parent;

        private InventoryHarness(
            string parent,
            SqliteConnection connection,
            CampaignPathMarkerLifecycle lifecycle,
            RecordingRecoveryKeyProvider recoveryKeys,
            PhysicalCampaignRootOpener opener,
            CampaignPathMarkerCodec codec,
            ForbiddenConnectionSource liveConnectionSource)
        {

            _parent = parent;

            Connection = connection;

            Lifecycle = lifecycle;

            RecoveryKeys = recoveryKeys;

            Opener = opener;

            Codec = codec;

            LiveConnectionSource = liveConnectionSource;

        }

        internal SqliteConnection Connection { get; }

        internal CampaignPathMarkerLifecycle Lifecycle { get; }

        internal RecordingRecoveryKeyProvider RecoveryKeys { get; }

        internal PhysicalCampaignRootOpener Opener { get; }

        internal CampaignPathMarkerCodec Codec { get; }

        internal ForbiddenConnectionSource LiveConnectionSource { get; }

        internal string ScratchPath => _parent;

        internal string MarkerPath(RegisteredRoot root) =>
            Path.Combine(root.DisplayPath, ".arcanum", "campaign-root.marker");

        internal string CreateDirectorySymlink(string leaf, string target)
        {

            string link = Path.Combine(_parent, leaf);

            _ = Directory.CreateSymbolicLink(link, target);

            return link;

        }

        internal async Task UpdateDisplayPathAsync(Guid campaignId, string displayPath)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                UPDATE campaign_path_identities
                SET DisplayPath = $displayPath
                WHERE CampaignId = $campaignId;
                """;

            _ = command.Parameters.AddWithValue("$displayPath", displayPath);

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task DeleteRegistryRowAsync(Guid campaignId)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                DELETE FROM campaign_path_identities
                WHERE CampaignId = $campaignId;
                """;

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task UpdateCampaignIdAsync(Guid campaignId, Guid replacementCampaignId)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                UPDATE campaign_path_identities
                SET CampaignId = $replacementCampaignId
                WHERE CampaignId = $campaignId;
                """;

            _ = command.Parameters.AddWithValue(
                "$replacementCampaignId",
                replacementCampaignId.ToString("D"));

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task UpdateIdentityDigestAsync(
            Guid campaignId,
            CovenantDigest identityDigest)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                UPDATE campaign_path_identities
                SET PhysicalIdentityDigest = $identity
                WHERE CampaignId = $campaignId;
                """;

            _ = command.Parameters.AddWithValue("$identity", identityDigest.Bytes.ToArray());

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task UpdateRevisionAsync(Guid campaignId, long revision)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                UPDATE campaign_path_identities
                SET Revision = $revision
                WHERE CampaignId = $campaignId;
                """;

            _ = command.Parameters.AddWithValue("$revision", revision);

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));

        }

        internal async Task InsertRawRegistryRowAsync(
            object campaignId,
            object policyVersion,
            object revision,
            object displayPath,
            object identityDigest)
        {

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO campaign_path_identities (
                    CampaignId,
                    PolicyVersion,
                    Revision,
                    DisplayPath,
                    Depth,
                    PhysicalIdentityDigest,
                    UpdatedAtUtc)
                VALUES (
                    $campaignId,
                    $policyVersion,
                    $revision,
                    $displayPath,
                    1,
                    $identity,
                    '2026-08-22T00:00:00.0000000+00:00');
                """;

            _ = command.Parameters.AddWithValue("$campaignId", campaignId);

            _ = command.Parameters.AddWithValue("$policyVersion", policyVersion);

            _ = command.Parameters.AddWithValue("$revision", revision);

            _ = command.Parameters.AddWithValue("$displayPath", displayPath);

            _ = command.Parameters.AddWithValue("$identity", identityDigest);

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        internal async Task ReplaceMarkerAsync(
            RegisteredRoot root,
            Guid markerCampaignId,
            long markerRevision,
            ulong rootVolumeId,
            ulong rootFileId)
        {

            Result<byte[]> encoded = Codec.Encode(
                new CampaignPathMarkerContent(
                    CampaignPathMarkerPolicy.Version,
                    markerCampaignId,
                    markerRevision,
                    rootVolumeId,
                    rootFileId,
                    [.. Enumerable.Repeat(
                        (byte)0x73,
                        CampaignPathMarkerPolicy.MarkerSecretByteCount)]));

            Assert.True(encoded.IsSuccess, Describe(encoded));

            await File.WriteAllBytesAsync(
                MarkerPath(root),
                encoded.Value,
                CancellationToken.None);

        }

        internal (ulong VolumeId, ulong FileId) CreateUnregisteredRootTuple()
        {

            string directory = Directory.CreateDirectory(
                Path.Combine(_parent, $"unregistered-{Guid.NewGuid():N}")).FullName;

            Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                directory,
                out FileHandleMetadata metadata));

            return (metadata.Identity.VolumeId, metadata.Identity.FileId);

        }

        internal static async Task<InventoryHarness> CreateAsync()
        {

            string parent = Directory.CreateTempSubdirectory(
                "arcanum-full-reset-inventory-").FullName;

            SqliteConnection connection = new("Data Source=:memory:");

            await connection.OpenAsync(CancellationToken.None);

            await using (SqliteCommand command = connection.CreateCommand())
            {

                command.CommandText = """
                    CREATE TABLE campaign_path_identities (
                        CampaignId TEXT NOT NULL,
                        PolicyVersion INTEGER NOT NULL,
                        Revision INTEGER NOT NULL,
                        DisplayPath TEXT NOT NULL,
                        Depth INTEGER NOT NULL,
                        PhysicalIdentityDigest BLOB NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );
                    """;

                _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

            }

            StubKeySource rootKeys = new(RootIdentityKey);

            RecordingRecoveryKeyProvider recoveryKeys = new(RootIdentityKey);

            PhysicalCampaignRootOpener opener = new(rootKeys);

            CampaignPathMarkerCodec codec = new(new StubKeySource(MarkerKey));

            ForbiddenConnectionSource liveConnectionSource = new();

            CampaignPathMarkerLifecycle lifecycle = new(
                codec,
                opener,
                liveConnectionSource,
                CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System,
                recoveryKeys);

            return new InventoryHarness(
                parent,
                connection,
                lifecycle,
                recoveryKeys,
                opener,
                codec,
                liveConnectionSource);

        }

        internal async Task<RegisteredRoot> AddMarkedRootAsync(
            Guid campaignId,
            long revision,
            string leaf)
        {

            string directory = Directory.CreateDirectory(Path.Combine(_parent, leaf)).FullName;

            CovenantDigest identity = Opener.IdentifyExact(directory)!.Value;

            Result<CampaignPathMarkerRootAuthority> opened =
                await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                    Opener,
                    campaignId,
                    revision,
                    identity,
                    directory,
                    CancellationToken.None);

            Assert.True(opened.IsSuccess, Describe(opened));

            await using CampaignPathMarkerRootAuthority authority = opened.Value;

            Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                directory,
                out FileHandleMetadata metadata));

            Result<byte[]> encoded = Codec.Encode(
                new CampaignPathMarkerContent(
                    CampaignPathMarkerPolicy.Version,
                    campaignId,
                    revision,
                    metadata.Identity.VolumeId,
                    metadata.Identity.FileId,
                    [.. Enumerable.Repeat(
                        (byte)0x42,
                        CampaignPathMarkerPolicy.MarkerSecretByteCount)]));

            Assert.True(encoded.IsSuccess, Describe(encoded));

            PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
                (await authority.CreateTemporaryExclusiveNoFollowAsync(
                    $"marker-{Guid.NewGuid():N}.tmp",
                    CancellationToken.None)).Value;

            Assert.True((await temporary.WriteAllAsync(
                encoded.Value,
                CancellationToken.None)).IsSuccess);

            Assert.True((await temporary.FlushToDiskAsync(CancellationToken.None)).IsSuccess);

            Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
                temporary,
                temporary.PhysicalIdentityDigest,
                encoded.Value,
                CancellationToken.None)).IsSuccess);

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO campaign_path_identities (
                    CampaignId,
                    PolicyVersion,
                    Revision,
                    DisplayPath,
                    Depth,
                    PhysicalIdentityDigest,
                    UpdatedAtUtc)
                VALUES (
                    $campaignId,
                    1,
                    $revision,
                    $displayPath,
                    1,
                    $identity,
                    '2026-08-22T00:00:00.0000000+00:00');
                """;

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString("D"));

            _ = command.Parameters.AddWithValue("$revision", revision);

            _ = command.Parameters.AddWithValue("$displayPath", directory);

            _ = command.Parameters.AddWithValue("$identity", identity.Bytes.ToArray());

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

            return new RegisteredRoot(
                campaignId,
                revision,
                directory,
                identity,
                new CovenantDigest(SHA256.HashData(encoded.Value)),
                metadata.Identity.VolumeId,
                metadata.Identity.FileId);

        }

        public async ValueTask DisposeAsync()
        {

            await Connection.DisposeAsync();

            try
            {
                Directory.Delete(_parent, recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory is not worth failing a suite over.
            }

        }

    }

    private sealed record RegisteredRoot(
        Guid CampaignId,
        long Revision,
        string DisplayPath,
        CovenantDigest IdentityDigest,
        CovenantDigest MarkerDigest,
        ulong RootVolumeId,
        ulong RootFileId);

    private sealed class RecordingRecoveryKeyProvider(byte[] key)
        : ICampaignRootIdentityRecoveryKeyProvider
    {

        internal int Calls { get; private set; }

        public bool TryCopyExistingRootIdentityKey(Span<byte> destination)
        {

            Calls++;

            if (destination.Length != key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

    private sealed class StubKeySource(byte[] key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

    private sealed class ForbiddenConnectionSource : ICovenantConnectionSource
    {

        internal int Calls { get; private set; }

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(
            CancellationToken cancellationToken)
        {

            Calls++;

            throw new InvalidOperationException(
                "The full-reset inventory must borrow its caller's Core connection.");

        }

    }

    private sealed class RecordingCredentialStore : IOsCredentialStore
    {

        internal int GetCount { get; private set; }

        internal int SetCount { get; private set; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            GetCount++;

            return OsCredentialStoreResult.NotFound();

        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            SetCount++;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Ok(string.Empty);

    }

}
