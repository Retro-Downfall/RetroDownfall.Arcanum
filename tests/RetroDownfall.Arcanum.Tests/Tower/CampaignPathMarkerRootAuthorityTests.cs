using System.Reflection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// The retained-handle boundary the Campaign marker protocol runs through, against real directories.
/// </summary>
/// <remarks>
/// Real temporary roots rather than a fake filesystem, because every property under test is a property
/// of what the operating system does with <c>O_NOFOLLOW</c>, an exclusive create, and a link that
/// already exists. A fake would only prove the fake agrees with itself (§10.12).
/// </remarks>
public sealed class CampaignPathMarkerRootAuthorityTests : IDisposable
{

    private static readonly byte[] Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private readonly string _parent =
        Directory.CreateTempSubdirectory("arcanum-marker-authority-").FullName;

    private readonly PhysicalCampaignRootOpener _opener = new(new StubKeySource(Key));

    private readonly Guid _campaignId = Guid.Parse("2f1c9a44-6c6d-4f2a-9b21-0c7d4e5f6a7b");

    private readonly string _root;

    public CampaignPathMarkerRootAuthorityTests() =>
        _root = Directory.CreateDirectory(Path.Combine(_parent, "campaign")).FullName;

    [Fact]
    public void The_authority_has_no_visible_constructor_and_no_create_bypass()
    {

        Type authority = typeof(CampaignPathMarkerRootAuthority);

        Assert.All(
            authority.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.True(constructor.IsPrivate));

        Assert.Empty(authority.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        // No alternate way in: no Create, no parameterless construction, no second factory anywhere in
        // the assembly. A capability whose construction can be reproduced by a caller is not a
        // capability, it is a naming convention.
        Assert.DoesNotContain(
            authority.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            static method => method.Name is "Create" or "Open" or "OpenAsync");

        Assert.Equal(
            1,
            typeof(CampaignPathMarkerRootAuthority).Assembly
                .GetTypes()
                .Count(static type =>
                    type.IsClass
                    && !type.IsAbstract
                    && typeof(ICampaignPathMarkerRootAuthorityFactory).IsAssignableFrom(type)));

        Assert.NotNull(CampaignPathMarkerRootAuthority.Instance);

    }

    [Fact]
    public void The_authority_exports_no_raw_handle_interface_downcast_or_path_reopen_surface()
    {

        Type authority = typeof(CampaignPathMarkerRootAuthority);

        // IAsyncDisposable and nothing else. A second interface is a downcast target, and a downcast
        // target is how a caller reaches past the typed operations to the stream underneath.
        Assert.Equal([typeof(IAsyncDisposable)], authority.GetInterfaces());

        foreach (MemberInfo member in authority.GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {

            Type? exposed = member switch
            {
                PropertyInfo property when !property.GetMethod!.IsPrivate => property.PropertyType,
                MethodInfo method when !method.IsPrivate && !method.IsSpecialName => method.ReturnType,
                FieldInfo field when !field.IsPrivate => field.FieldType,
                _ => null,
            };

            if (exposed is null)
            {
                continue;
            }

            Assert.DoesNotContain("SafeHandle", DescribeClosure(exposed), StringComparison.Ordinal);
            Assert.DoesNotContain("FileStream", DescribeClosure(exposed), StringComparison.Ordinal);
            Assert.DoesNotContain("System.IO.Stream", DescribeClosure(exposed), StringComparison.Ordinal);

        }

        // No path in and no path back out. Every operation names a bounded leaf at most, so nothing the
        // caller supplies can reopen the root somewhere else.
        Assert.DoesNotContain(
            authority.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            static property => property.PropertyType == typeof(string) && !property.GetMethod!.IsPrivate);

    }

    [Fact]
    public async Task An_opened_authority_echoes_the_identity_it_was_asked_for()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        Assert.Equal(_campaignId, authority.CampaignId);
        Assert.Equal(3, authority.PathRevision);
        Assert.Equal(_opener.IdentifyExact(_root), authority.PhysicalIdentityDigest);

    }

    [Fact]
    public async Task Existing_only_authority_uses_the_noncreating_retained_opener_path()
    {

        string missingMarkerRoot = Directory.CreateDirectory(
            Path.Combine(_parent, "missing-marker-directory")).FullName;

        Result<CampaignPathMarkerRootAuthority> refused =
            await CampaignPathMarkerRootAuthority.Instance.OpenExistingAsync(
                _opener,
                Guid.NewGuid(),
                1,
                _opener.IdentifyExact(missingMarkerRoot)!.Value,
                missingMarkerRoot,
                CancellationToken.None);

        Assert.True(refused.IsFailure);
        Assert.False(Directory.Exists(Path.Combine(missingMarkerRoot, ".arcanum")));

        await using CampaignPathMarkerRootAuthority ordinary = await OpenAsync();

        Result<CampaignPathMarkerRootAuthority> existing =
            await CampaignPathMarkerRootAuthority.Instance.OpenExistingAsync(
                _opener,
                _campaignId,
                3,
                ordinary.PhysicalIdentityDigest,
                _root,
                CancellationToken.None);

        Assert.True(existing.IsSuccess);

        await existing.Value.DisposeAsync();

    }

    [Fact]
    public async Task A_mismatched_identity_echo_is_refused_and_nothing_is_retained()
    {

        string other = Directory.CreateDirectory(Path.Combine(_parent, "other")).FullName;

        Result<CampaignPathMarkerRootAuthority> mismatched =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _opener,
                _campaignId,
                3,
                _opener.IdentifyExact(other)!.Value,
                _root,
                CancellationToken.None);

        Assert.True(mismatched.IsFailure);

        // Malformed requests never reach the filesystem at all.
        Assert.True((await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
            _opener,
            Guid.Empty,
            3,
            _opener.IdentifyExact(_root)!.Value,
            _root,
            CancellationToken.None)).IsFailure);

        Assert.True((await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
            _opener,
            _campaignId,
            0,
            _opener.IdentifyExact(_root)!.Value,
            _root,
            CancellationToken.None)).IsFailure);

        Assert.True((await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
            _opener,
            _campaignId,
            3,
            default,
            _root,
            CancellationToken.None)).IsFailure);

        Assert.True((await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
            _opener,
            _campaignId,
            3,
            _opener.IdentifyExact(_root)!.Value,
            "   ",
            CancellationToken.None)).IsFailure);

    }

    [SkippableFact]
    public async Task A_symlinked_root_is_refused_rather_than_followed()
    {

        Skip.If(OperatingSystem.IsWindows(), "Creating a directory symlink on Windows needs elevation.");

        string link = Path.Combine(_parent, "link");

        Directory.CreateSymbolicLink(link, _root);

        // The link is handed the target's own identity digest, which is exactly the confusion a
        // follow-the-link open would fall for: the digests would agree and the marker would be written
        // through a link an attacker controls.
        Result<CampaignPathMarkerRootAuthority> opened =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _opener,
                _campaignId,
                3,
                _opener.IdentifyExact(_root)!.Value,
                link,
                CancellationToken.None);

        Assert.True(opened.IsFailure);

    }

    [SkippableFact]
    public async Task A_symlinked_marker_directory_is_refused_rather_than_followed()
    {

        Skip.If(OperatingSystem.IsWindows(), "Creating a directory symlink on Windows needs elevation.");

        string elsewhere = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(_root, ".arcanum"), elsewhere);

        Result<CampaignPathMarkerRootAuthority> opened =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _opener,
                _campaignId,
                3,
                _opener.IdentifyExact(_root)!.Value,
                _root,
                CancellationToken.None);

        Assert.True(opened.IsFailure);

    }

    [SkippableFact]
    public async Task A_symlinked_marker_file_is_refused_rather_than_followed()
    {

        Skip.If(OperatingSystem.IsWindows(), "Creating a file symlink on Windows needs elevation.");

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        string decoy = Path.Combine(_parent, "decoy");

        await File.WriteAllBytesAsync(decoy, [1, 2, 3], CancellationToken.None);

        File.CreateSymbolicLink(
            Path.Combine(_root, ".arcanum", "campaign-root.marker"),
            decoy);

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(CancellationToken.None);

        // Not Absent and not Opened: a link where the marker belongs is a positive refusal, because
        // reporting absence here would invite the caller to create a marker straight through it.
        Assert.True(opened.IsFailure);

    }

    [Fact]
    public async Task An_absent_marker_is_proven_absent_rather_than_guessed()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(CancellationToken.None);

        Assert.True(opened.IsSuccess);
        Assert.IsType<PhysicalCampaignMarkerOpenResult.Absent>(opened.Value);

    }

    [Fact]
    public async Task A_temporary_is_created_written_flushed_and_renamed_through_retained_handles()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        byte[] bytes = MarkerBytes();

        Result<PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability> created =
            await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-1a2b3c4d.tmp",
                CancellationToken.None);

        Assert.True(created.IsSuccess);

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary = created.Value;

        Assert.True(temporary.PhysicalIdentityDigest.IsValid);

        Assert.True((await temporary.WriteAllAsync(bytes, CancellationToken.None)).IsSuccess);
        Assert.True((await temporary.FlushToDiskAsync(CancellationToken.None)).IsSuccess);
        Assert.Equal(bytes.Length, temporary.Length);

        Result<PhysicalCampaignRootOpener.MarkerCodecBytesLease> read =
            await temporary.ReadAllBoundedAsync(4096, CancellationToken.None);

        Assert.True(read.IsSuccess);

        using (PhysicalCampaignRootOpener.MarkerCodecBytesLease lease = read.Value)
        {
            Assert.True(lease.Bytes.Span.SequenceEqual(bytes));
        }

        Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
            temporary,
            temporary.PhysicalIdentityDigest,
            bytes,
            CancellationToken.None)).IsSuccess);

        Assert.False(File.Exists(Path.Combine(_root, ".arcanum", "marker-1a2b3c4d.tmp")));

        Result<PhysicalCampaignMarkerOpenResult> reopened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(CancellationToken.None);

        Assert.True(reopened.IsSuccess);

        PhysicalCampaignMarkerOpenResult.Opened opened =
            Assert.IsType<PhysicalCampaignMarkerOpenResult.Opened>(reopened.Value);

        await using PhysicalCampaignRootOpener.MarkerHandleCapability marker = opened.Marker;

        using PhysicalCampaignRootOpener.MarkerCodecBytesLease markerBytes =
            (await marker.ReadAllBoundedAsync(4096, CancellationToken.None)).Value;

        Assert.True(markerBytes.Bytes.Span.SequenceEqual(bytes));

        Assert.True((await authority.FlushMarkerDirectoryAsync(CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Rename_to_an_existing_marker_never_replaces_it()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        byte[] first = MarkerBytes();

        await RenameOneAsync(authority, "marker-aaaa1111.tmp", first);

        byte[] second = MarkerBytes(0x55);

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-bbbb2222.tmp",
                CancellationToken.None)).Value;

        _ = await temporary.WriteAllAsync(second, CancellationToken.None);

        Result renamed = await authority.RenameTemporaryToMarkerNoReplaceAsync(
            temporary,
            temporary.PhysicalIdentityDigest,
            second,
            CancellationToken.None);

        Assert.True(renamed.IsFailure);

        Assert.Equal(
            first,
            await File.ReadAllBytesAsync(
                Path.Combine(_root, ".arcanum", "campaign-root.marker"),
                CancellationToken.None));

        await temporary.DisposeAsync();

    }

    [Fact]
    public async Task Compare_delete_removes_only_the_same_verified_temporary()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        byte[] bytes = MarkerBytes();

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability target =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-cccc3333.tmp",
                CancellationToken.None)).Value;

        _ = await target.WriteAllAsync(bytes, CancellationToken.None);

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability bystander =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-dddd4444.tmp",
                CancellationToken.None)).Value;

        _ = await bystander.WriteAllAsync(bytes, CancellationToken.None);

        string targetPath = Path.Combine(_root, ".arcanum", "marker-cccc3333.tmp");

        string bystanderPath = Path.Combine(_root, ".arcanum", "marker-dddd4444.tmp");

        // Wrong bytes: the file stays. This is the compensation path, so a mismatch has to mean "leave
        // it for a human" rather than "remove whatever is there".
        Assert.True((await authority.CompareDeleteTemporaryAsync(
            target,
            target.PhysicalIdentityDigest,
            MarkerBytes(0x77),
            CancellationToken.None)).IsFailure);

        Assert.True(File.Exists(targetPath));

        // Wrong identity: the file stays.
        Assert.True((await authority.CompareDeleteTemporaryAsync(
            target,
            bystander.PhysicalIdentityDigest,
            bytes,
            CancellationToken.None)).IsFailure);

        Assert.True(File.Exists(targetPath));

        Assert.True((await authority.CompareDeleteTemporaryAsync(
            target,
            target.PhysicalIdentityDigest,
            bytes,
            CancellationToken.None)).IsSuccess);

        Assert.False(File.Exists(targetPath));
        Assert.True(File.Exists(bystanderPath));

        await bystander.DisposeAsync();

    }

    [Fact]
    public async Task A_temporary_from_a_different_root_grants_nothing()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        string stranger = Directory.CreateDirectory(Path.Combine(_parent, "stranger")).FullName;

        await using CampaignPathMarkerRootAuthority strangerAuthority =
            (await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _opener,
                Guid.Parse("8d5a1f3c-2b4e-4a6d-9f10-3c5e7a9b1d2f"),
                1,
                _opener.IdentifyExact(stranger)!.Value,
                stranger,
                CancellationToken.None)).Value;

        byte[] bytes = MarkerBytes();

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability foreignTemporary =
            (await strangerAuthority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-eeee5555.tmp",
                CancellationToken.None)).Value;

        _ = await foreignTemporary.WriteAllAsync(bytes, CancellationToken.None);

        // Same bytes, same shape, verified handle — and still refused, because it belongs to a
        // different retained root pair. Byte evidence alone is not authority over a file.
        Assert.True((await authority.CompareDeleteTemporaryAsync(
            foreignTemporary,
            foreignTemporary.PhysicalIdentityDigest,
            bytes,
            CancellationToken.None)).IsFailure);

        Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
            foreignTemporary,
            foreignTemporary.PhysicalIdentityDigest,
            bytes,
            CancellationToken.None)).IsFailure);

        Assert.True(File.Exists(Path.Combine(stranger, ".arcanum", "marker-eeee5555.tmp")));
        Assert.False(File.Exists(Path.Combine(_root, ".arcanum", "campaign-root.marker")));

        await foreignTemporary.DisposeAsync();

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.tmp")]
    [InlineData("nested/leaf.tmp")]
    [InlineData("leaf.tmp:stream")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("campaign-root.marker")]
    [InlineData("CON")]
    public async Task A_temporary_leaf_that_is_not_one_bounded_segment_is_refused(string leaf)
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        Assert.True((await authority.CreateTemporaryExclusiveNoFollowAsync(
            leaf,
            CancellationToken.None)).IsFailure);

        Assert.True((await authority.OpenTemporaryNoFollowAsync(
            leaf,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task A_recovered_temporary_is_reopened_and_verified_on_the_same_handle()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        byte[] bytes = MarkerBytes();

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability created =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-ffff6666.tmp",
                CancellationToken.None)).Value;

        _ = await created.WriteAllAsync(bytes, CancellationToken.None);
        _ = await created.FlushToDiskAsync(CancellationToken.None);

        CovenantDigest journaled = created.PhysicalIdentityDigest;

        await created.DisposeAsync();

        // The restart case: nothing survives but the journaled leaf, identity, and bytes.
        Result<PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability> reopened =
            await authority.OpenTemporaryNoFollowAsync(
                "marker-ffff6666.tmp",
                CancellationToken.None);

        Assert.True(reopened.IsSuccess);

        await using PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability adopted = reopened.Value;

        Assert.Equal(journaled, adopted.PhysicalIdentityDigest);

        using PhysicalCampaignRootOpener.MarkerCodecBytesLease lease =
            (await adopted.ReadAllBoundedAsync(4096, CancellationToken.None)).Value;

        Assert.True(lease.Bytes.Span.SequenceEqual(bytes));

    }

    [Fact]
    public async Task A_bounded_read_refuses_a_file_larger_than_the_bound()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-1111aaaa.tmp",
                CancellationToken.None)).Value;

        _ = await temporary.WriteAllAsync(
            new byte[64],
            CancellationToken.None);

        Assert.True((await temporary.ReadAllBoundedAsync(
            32,
            CancellationToken.None)).IsFailure);

        await temporary.DisposeAsync();

    }

    [Fact]
    public async Task The_bytes_lease_zeroes_its_backing_buffer_exactly_once()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        byte[] bytes = MarkerBytes();

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-2222bbbb.tmp",
                CancellationToken.None)).Value;

        _ = await temporary.WriteAllAsync(bytes, CancellationToken.None);

        PhysicalCampaignRootOpener.MarkerCodecBytesLease lease =
            (await temporary.ReadAllBoundedAsync(4096, CancellationToken.None)).Value;

        Assert.False(lease.Bytes.Span.IndexOfAnyExcept((byte)0) < 0);

        lease.Dispose();
        lease.Dispose();

        Assert.True(lease.Bytes.IsEmpty);

        await temporary.DisposeAsync();

    }

    [Fact]
    public async Task Double_dispose_releases_the_retained_handles_exactly_once()
    {

        CampaignPathMarkerRootAuthority authority = await OpenAsync();

        await authority.DisposeAsync();
        await authority.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await authority.FlushMarkerDirectoryAsync(CancellationToken.None));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await authority.OpenMarkerOrProveAbsentNoFollowAsync(
                CancellationToken.None));

        // The handles really were released rather than leaked into the disposed instance: the same root
        // opens cleanly again.
        await using CampaignPathMarkerRootAuthority reopened = await OpenAsync();

        Assert.Equal(_campaignId, reopened.CampaignId);

    }

    [Fact]
    public async Task A_disposed_child_capability_grants_nothing_and_disposes_once()
    {

        await using CampaignPathMarkerRootAuthority authority = await OpenAsync();

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                "marker-3333cccc.tmp",
                CancellationToken.None)).Value;

        await temporary.DisposeAsync();
        await temporary.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await temporary.WriteAllAsync(
                MarkerBytes(),
                CancellationToken.None));

        Assert.True((await authority.CompareDeleteTemporaryAsync(
            temporary,
            default,
            MarkerBytes(),
            CancellationToken.None)).IsFailure);

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a suite over.
        }

    }

    private static byte[] MarkerBytes(byte seed = 0x11) =>
        [.. Enumerable.Range(0, 141).Select(value => (byte)(value + seed))];

    private static string DescribeClosure(Type type) =>
        type.FullName
        + string.Concat(type.GetGenericArguments().Select(DescribeClosure));

    private async Task<CampaignPathMarkerRootAuthority> OpenAsync()
    {

        Result<CampaignPathMarkerRootAuthority> opened =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _opener,
                _campaignId,
                3,
                _opener.IdentifyExact(_root)!.Value,
                _root,
                CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : string.Empty);

        return opened.Value;

    }

    private async Task RenameOneAsync(
        CampaignPathMarkerRootAuthority authority,
        string leaf,
        byte[] bytes)
    {

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync(
                leaf,
                CancellationToken.None)).Value;

        _ = await temporary.WriteAllAsync(bytes, CancellationToken.None);
        _ = await temporary.FlushToDiskAsync(CancellationToken.None);

        Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
            temporary,
            temporary.PhysicalIdentityDigest,
            bytes,
            CancellationToken.None)).IsSuccess);

    }

    private sealed class StubKeySource(byte[]? key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (key is null || destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

}
