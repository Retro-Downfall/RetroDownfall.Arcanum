using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed class GuildWorkspaceAuthorityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("guild-workspace-").FullName;

    private readonly PhysicalCampaignRootOpener _opener = new(new KeySource());

    private GuildWorkspaceRegistration? _current;

    private Action? _onResolve;

    private static GuildGrant Grant(params GuildWorkspaceGrant[] roots) => new([.. roots], [], [],
        new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null);

    private GuildWorkspaceAuthority Create()
    {
        string root = Directory.CreateDirectory(Path.Combine(_directory, "workspace")).FullName;

        _current = new(Guid.NewGuid(), root, 1, 1, _opener.IdentifyExact(root)!.Value);

        return new GuildWorkspaceAuthority(_current, _opener, _ =>
        {
            _onResolve?.Invoke();

            return Task.FromResult(_current is null
                ? Result<GuildWorkspaceRegistration>.Failure(new(ErrorCodes.Guild.OwnerUnavailable, "Owner unavailable."))
                : Result<GuildWorkspaceRegistration>.Success(_current));
        });
    }

    [Fact]
    public void Workspace_authority_has_a_host_owned_physical_boundary()
    {
        Type? boundary = typeof(GuildRoster).Assembly.GetType(
            "RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceAuthority");

        Assert.NotNull(boundary);

        Assert.False(boundary.IsPublic);
    }

    [Fact]
    public async Task A_read_uses_the_opened_file_inside_the_exact_granted_subtree()
    {
        GuildWorkspaceAuthority authority = Create();

        Directory.CreateDirectory(Path.Combine(_current!.RootPath, "allowed"));

        await File.WriteAllTextAsync(Path.Combine(_current.RootPath, "allowed", "read.txt"), "inside");

        await File.WriteAllTextAsync(Path.Combine(_current.RootPath, "private.txt"), "private");

        GuildGrant grant = Grant(new GuildWorkspaceGrant(_current.WorkspaceId, "allowed", GuildWorkspaceAccess.Read));

        Result<FileStream> read = await authority.OpenReadAsync(grant, _current.WorkspaceId, "allowed/read.txt", default);

        Assert.True(read.IsSuccess, read.Error.ToString());

        using StreamReader reader = new(read.Value);

        Assert.Equal("inside", await reader.ReadToEndAsync());

        Assert.True((await authority.OpenReadAsync(grant, _current.WorkspaceId, "private.txt", default)).IsFailure);

        Assert.True((await authority.OpenReadAsync(Grant(), _current.WorkspaceId, "allowed/read.txt", default)).IsFailure);

        Assert.True((await authority.OpenReadAsync(grant, Guid.NewGuid(), "allowed/read.txt", default)).IsFailure);
    }

    [Fact]
    public async Task Root_replacement_and_registration_revision_changes_refuse_existing_grants()
    {
        GuildWorkspaceAuthority authority = Create();

        GuildGrant grant = Grant(new GuildWorkspaceGrant(_current!.WorkspaceId, ".", GuildWorkspaceAccess.Read));

        Assert.True((await authority.ValidateAsync(grant, default)).IsSuccess);

        GuildWorkspaceRegistration original = _current;

        _current = _current with { Revision = 2 };

        Assert.True((await authority.ValidateAsync(grant, default)).IsFailure);

        _current = original;

        Directory.Move(original.RootPath, original.RootPath + "-moved");

        Directory.CreateDirectory(original.RootPath);

        Assert.Equal(ErrorCodes.Guild.WorkspaceIdentityUnavailable, (await authority.ValidateAsync(grant, default)).Error.Code);
    }

    [Fact]
    public async Task Physical_containment_proves_equality_and_narrowing_but_not_siblings()
    {
        GuildWorkspaceAuthority authority = Create();

        Directory.CreateDirectory(Path.Combine(_current!.RootPath, "allowed", "nested"));

        Directory.CreateDirectory(Path.Combine(_current.RootPath, "sibling"));

        GuildGrant parent = Grant(new GuildWorkspaceGrant(_current.WorkspaceId, "allowed", GuildWorkspaceAccess.ReadWrite));

        GuildGrant child = Grant(new GuildWorkspaceGrant(_current.WorkspaceId, "allowed/nested", GuildWorkspaceAccess.Read));

        Result<GuildGrantScopeEvidence> evidence = await authority.ResolveContainmentAsync(parent, child, default);

        Assert.True(evidence.IsSuccess, evidence.Error.ToString());

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, child.Workspaces)), evidence.Value).IsSuccess);

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, parent.Workspaces)),
            (await authority.ResolveContainmentAsync(parent, parent, default)).Value).IsSuccess);

        GuildGrant sibling = Grant(new GuildWorkspaceGrant(_current.WorkspaceId, "sibling", GuildWorkspaceAccess.Read));

        Assert.True(GuildGrantPolicy.Attenuate(parent, new(Workspaces: new(true, sibling.Workspaces)),
            (await authority.ResolveContainmentAsync(parent, sibling, default)).Value).IsFailure);
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("/private")]
    [InlineData("allowed/../private")]
    [InlineData("allowed\\private")]
    [InlineData("allowed//private")]
    public async Task Noncanonical_resource_paths_refuse_before_open(string relativePath)
    {
        GuildWorkspaceAuthority authority = Create();

        Result<FileStream> result = await authority.OpenReadAsync(Grant(new GuildWorkspaceGrant(_current!.WorkspaceId, ".", GuildWorkspaceAccess.Read)),
            _current.WorkspaceId, relativePath, default);

        Assert.True(result.IsFailure);
    }

    [SkippableFact]
    public async Task Symbolic_subtree_cannot_supply_physical_containment()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory symlinks requires Windows elevation.");

        GuildWorkspaceAuthority authority = Create();

        string outside = Directory.CreateDirectory(Path.Combine(_directory, "outside")).FullName;

        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");

        Directory.CreateSymbolicLink(Path.Combine(_current!.RootPath, "link"), outside);

        GuildGrant grant = Grant(new GuildWorkspaceGrant(_current.WorkspaceId, ".", GuildWorkspaceAccess.Read));

        Assert.True((await authority.OpenReadAsync(grant, _current.WorkspaceId, "link/secret.txt", default)).IsFailure);

        Assert.True((await authority.ValidateAsync(Grant(new GuildWorkspaceGrant(_current.WorkspaceId, "link", GuildWorkspaceAccess.Read)), default)).IsFailure);
    }

    [Fact]
    public async Task Root_swap_after_open_refuses_before_disclosing_the_file()
    {
        GuildWorkspaceAuthority authority = Create();

        string root = _current!.RootPath;

        await File.WriteAllTextAsync(Path.Combine(root, "read.txt"), "original");

        SecureFileReader.AfterRegularFileOpenedForTests = _ =>
        {
            Directory.Move(root, root + "-moved");

            Directory.CreateDirectory(root);
        };

        try
        {
            Assert.True((await authority.OpenReadAsync(Grant(new GuildWorkspaceGrant(_current.WorkspaceId, ".", GuildWorkspaceAccess.Read)),
                _current.WorkspaceId, "read.txt", default)).IsFailure);
        }
        finally
        {
            SecureFileReader.AfterRegularFileOpenedForTests = null;
        }
    }

    [SkippableFact]
    public async Task Alternating_scope_symlink_cannot_adopt_an_outside_directory_as_the_granted_root()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating directory symlinks requires Windows elevation.");

        GuildWorkspaceAuthority authority = Create();

        string allowed = Directory.CreateDirectory(Path.Combine(_current!.RootPath, "allowed")).FullName;

        string moved = allowed + "-moved";

        string outside = Directory.CreateDirectory(Path.Combine(_directory, "outside")).FullName;

        string resource = Path.Combine(allowed, "read.txt");

        await File.WriteAllTextAsync(resource, "inside");

        await File.WriteAllTextAsync(Path.Combine(outside, "read.txt"), "outside");

        int phase = 0;

        void SwapToLink()
        {
            Directory.Move(allowed, moved);

            Directory.CreateSymbolicLink(allowed, outside);
        }

        _onResolve = () =>
        {
            if (phase == 1)
            {
                Directory.Delete(allowed);

                Directory.Move(moved, allowed);

                phase = 2;
            }
        };

        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
        {
            if (phase == 0 && path == resource)
            {
                SwapToLink();

                phase = 1;
            }

            bool found = FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(path, out FileHandleMetadata metadata);

            if (phase == 2 && path == allowed)
            {
                SwapToLink();

                phase = 3;
            }

            return found ? metadata : null;
        };

        try
        {
            Result<FileStream> result = await authority.OpenReadAsync(Grant(new GuildWorkspaceGrant(_current.WorkspaceId, "allowed", GuildWorkspaceAccess.Read)),
                _current.WorkspaceId, "allowed/read.txt", default);

            if (result.IsSuccess)
            {
                using StreamReader reader = new(result.Value);

                string disclosed = await reader.ReadToEndAsync();

                Assert.Fail($"A replaced scope root disclosed {disclosed} bytes.");
            }

            Assert.True(phase >= 1, "The actual filesystem race was exercised.");
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;

            _onResolve = null;
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class KeySource : ICampaignRootIdentityKeyProvider
    {
        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {
            destination[..32].Fill(0x42);

            return true;
        }
    }
}
