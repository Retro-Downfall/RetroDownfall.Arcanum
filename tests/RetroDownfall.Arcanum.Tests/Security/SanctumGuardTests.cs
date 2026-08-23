using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class SanctumGuardTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync()
    {

        return _workspace.InitializeAsync();

    }

    public Task DisposeAsync()
    {

        return _workspace.DisposeAsync();

    }

    [Fact]
    public async Task ValidatePathAsync_EnforcePathBoundaryDisabled_AllowsOutsidePath()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                EnforcePathBoundary = false,
            }));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            "/etc/passwd",
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_InvalidRequestedPath_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            EnabledPathBoundaryConfig()));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            "bad\u0000path",
            "read",
            "read_file_chunk");

        Assert.False(result.Allowed);

        Assert.Contains("Invalid path", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidatePathAsync_SanctumDisabled_AllowsEscapeAttempt()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig { Enabled = false }));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            "/etc/passwd",
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_PathUnderWorkspace_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        string nestedFile = _workspace.WriteFile("docs/readme.md", "hello");

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            EnabledPathBoundaryConfig()));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            nestedFile,
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_PathOutsideWorkspace_DeniesAndRecordsBreach()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            EnabledPathBoundaryConfig()));

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        try
        {
            FakeSanctumBreachRepository breachRepository = new();

            SanctumGuard guard = CreateGuard(repository, breachRepository);

            SanctumResult result = await guard.ValidatePathAsync(
                campaignId.ToString(),
                Path.Combine(outside, "secret.txt"),
                "read",
                "read_file_chunk");

            Assert.False(result.Allowed);

            Assert.Contains("leave the campaign workspace", result.DenyReason, StringComparison.OrdinalIgnoreCase);

            Assert.NotNull(result.Breach);

            Assert.Single(breachRepository.Records, r => r.CampaignId == campaignId.ToString());
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }

    }

    [Fact]
    public async Task ValidatePathAsync_AllowedPathSubdirectory_AllowsNestedFile()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        string campaignRoot = _workspace.CreateSubdir("campaign-root");

        string allowedRoot = _workspace.CreateSubdir("shared");

        string allowedSubdir = _workspace.CreateSubdir(Path.Combine("shared", "nested"));

        string file = _workspace.WriteFile(Path.Combine("shared", "nested", "doc.txt"), "ok");

        SanctumConfig config = EnabledPathBoundaryConfig() with
        {
            AllowedPaths = [allowedSubdir],
        };

        repository.SetCampaign(CreateCampaign(campaignId, campaignRoot, config));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            file,
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [SkippableFact]
    public async Task ValidatePathAsync_AllowedPathSymlinkEntry_AllowsNestedFile()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        string campaignRoot = _workspace.CreateSubdir("campaign-root");

        string allowedReal = _workspace.CreateSubdir("allowed-real");

        string allowedLink = Path.Combine(_workspace.Root, "allowed-link");

        Directory.CreateSymbolicLink(allowedLink, allowedReal);

        string file = Path.Combine(allowedLink, "via-symlink.txt");

        File.WriteAllText(file, "ok");

        SanctumConfig config = EnabledPathBoundaryConfig() with
        {
            AllowedPaths = [allowedLink],
        };

        repository.SetCampaign(CreateCampaign(campaignId, campaignRoot, config));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            file,
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_AllowedPathsFirstMissSecondMatch_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        string campaignRoot = _workspace.CreateSubdir("campaign-root");

        string allowedSibling = _workspace.CreateSubdir("allowed-sibling");

        string file = Path.Combine(allowedSibling, "shared.txt");

        File.WriteAllText(file, "ok");

        string missingAllowed = Path.Combine(_workspace.Root, "does-not-exist");

        SanctumConfig config = EnabledPathBoundaryConfig() with
        {
            AllowedPaths = [missingAllowed, allowedSibling],
        };

        repository.SetCampaign(CreateCampaign(campaignId, campaignRoot, config));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            file,
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_SiblingAllowedPathUnderWorkspace_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        string campaignRoot = _workspace.CreateSubdir("campaign-root");

        string allowedSibling = _workspace.CreateSubdir("allowed-sibling");

        string file = Path.Combine(allowedSibling, "shared.txt");

        File.WriteAllText(file, "ok");

        SanctumConfig config = EnabledPathBoundaryConfig() with
        {
            AllowedPaths = [allowedSibling],
        };

        repository.SetCampaign(CreateCampaign(campaignId, campaignRoot, config));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            file,
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_AllowedPathsExtraRoot_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        string extraRoot = Path.Combine(Path.GetTempPath(), "arcanum-extra-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(extraRoot);

        try
        {
            string file = Path.Combine(extraRoot, "allowed.txt");

            File.WriteAllText(file, "ok");

            SanctumConfig config = EnabledPathBoundaryConfig() with
            {
                AllowedPaths = [extraRoot],
            };

            repository.SetCampaign(CreateCampaign(campaignId, _workspace.Root, config));

            SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
                campaignId.ToString(),
                file,
                "read",
                "read_file_chunk");

            Assert.True(result.Allowed);
        }
        finally
        {
            Directory.Delete(extraRoot, recursive: true);
        }

    }

    [Fact]
    public async Task ValidatePathAsync_InvalidAllowedPathEntry_SkipsAndDeniesOutsidePath()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        SanctumConfig config = EnabledPathBoundaryConfig() with
        {
            AllowedPaths = ["?\u0000invalid", "   "],
        };

        repository.SetCampaign(CreateCampaign(campaignId, _workspace.Root, config));

        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        try
        {
            SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
                campaignId.ToString(),
                Path.Combine(outside, "secret.txt"),
                "read",
                "read_file_chunk");

            Assert.False(result.Allowed);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }

    }

    [Fact]
    public async Task ValidatePathAsync_InvalidCampaignWorkspacePath_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            "?\u0000invalid",
            EnabledPathBoundaryConfig()));

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            campaignId.ToString(),
            _workspace.WriteFile("readme.md", "hello"),
            "read",
            "read_file_chunk");

        Assert.False(result.Allowed);

        Assert.Contains("could not be resolved", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidatePathAsync_UnknownCampaign_Allows()
    {

        SanctumResult result = await CreateGuard(new FakeCampaignRepository()).ValidatePathAsync(
            Guid.NewGuid().ToString(),
            "/etc/passwd",
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidatePathAsync_EmptyCampaignId_AllowsWithoutRepositoryLookup()
    {

        FakeCampaignRepository repository = new();

        SanctumResult result = await CreateGuard(repository).ValidatePathAsync(
            "",
            "/etc/passwd",
            "read",
            "read_file_chunk");

        Assert.True(result.Allowed);

        Assert.False(repository.WasQueried);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowAllPolicy_AllowsPrivateUrl()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowAll,
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1:8080",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_DenyAllPolicy_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.DenyAll,
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://example.com",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("denied by this Sanctum", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListMatchingHost_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://api.example.com/data",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListUnknownHost_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://evil.test/data",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("not in the Sanctum allowed domain list", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateNetworkAsync_EmptyUrl_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.DenyAll,
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_InvalidUrlScheme_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "ftp://example.com/file",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("valid HTTP or HTTPS", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateToolAsync_DisabledTool_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                DisabledTools = ["execute_command"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateToolAsync(
            campaignId.ToString(),
            "execute_command");

        Assert.False(result.Allowed);

        Assert.Contains("disabled in this Sanctum", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateToolAsync_EnabledTool_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                DisabledTools = ["execute_command"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateToolAsync(
            campaignId.ToString(),
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_InvalidCampaignId_Denies()
    {

        SanctumResult result = await CreateGuard(new FakeCampaignRepository()).ValidateNetworkAsync(
            "not-a-valid-guid",
            "https://example.com",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("Invalid campaign identifier", result.DenyReason, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ValidateNetworkAsync_UnknownCampaign_Allows()
    {

        SanctumResult result = await CreateGuard(new FakeCampaignRepository()).ValidateNetworkAsync(
            Guid.NewGuid().ToString(),
            "https://example.com",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_SanctumDisabled_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = false,
                NetworkPolicy = NetworkPolicy.DenyAll,
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://example.com",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListExactHostMatch_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://example.com/data",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListEmptyDomains_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = [],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://example.com",
            "fetch_url");

        Assert.False(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListWithLiteralIp_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["127.0.0.1"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1:8080",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListWithEquivalentIpv6Literal_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["::1"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://[0:0:0:0:0:0:0:1]/",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    /// <summary>
    /// An IP-literal allow entry authorises that address, not every name that happens to resolve to it.
    /// The operator wrote an address; a hostname request is a different subject and carries its own Host
    /// header to its own virtual host, so it has to be listed by name to be admitted.
    /// </summary>
    [Fact]
    public async Task ValidateNetworkAsync_AllowListHostnameResolvingToAllowedIpLiteral_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["127.0.0.1"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1.nip.io/",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("not in the Sanctum allowed domain list", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListUnresolvableAllowedDomain_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["this-domain-definitely-does-not-exist-12345.invalid", "example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://evil.test/data",
            "fetch_url");

        Assert.False(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_NotAbsoluteUrl_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "not-a-valid-uri",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("valid HTTP or HTTPS", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateToolAsync_InvalidCampaignId_Denies()
    {

        SanctumResult result = await CreateGuard(new FakeCampaignRepository()).ValidateToolAsync(
            "not-a-valid-guid",
            "read_file_chunk");

        Assert.False(result.Allowed);

        Assert.Contains("Invalid campaign identifier", result.DenyReason, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ValidateToolAsync_UnknownCampaign_Allows()
    {

        SanctumResult result = await CreateGuard(new FakeCampaignRepository()).ValidateToolAsync(
            Guid.NewGuid().ToString(),
            "read_file_chunk");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateToolAsync_SanctumDisabled_AllowsDisabledTool()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = false,
                DisabledTools = ["execute_command"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateToolAsync(
            campaignId.ToString(),
            "execute_command");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListSkipsWhitespaceWhileResolvingAllowedDomains_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["   ", "127.0.0.1.nip.io"],
            }));

        // An IP-literal request reaches the resolution loop, which is where the blank entry has to be
        // skipped rather than handed to the resolver.
        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1/",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListLeadingDotSubdomain_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = [".example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://api.example.com/data",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    /// <summary>
    /// The allow-list is the operator's containment boundary for URLs a model chose, so sharing an
    /// address with an allowed domain must not admit a name the operator never listed. Address equality
    /// is not domain identity: shared hosting puts unrelated sites on one address, and an attacker who
    /// controls DNS for their own name can publish an allowed domain's address in their own zone. The
    /// request still carries its own Host header and reaches its own virtual host either way.
    /// </summary>
    [Fact]
    public async Task ValidateNetworkAsync_AllowListHostSharingAnAddressWithAnAllowedDomain_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["example.com"],
            }));

        // evil.test and example.com share 93.184.216.34 in the deterministic resolver.
        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://evil.test/?d=exfiltrated",
            "read_url");

        Assert.False(result.Allowed);

        Assert.Contains("not in the Sanctum allowed domain list", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListResolvedAllowedHostname_Allows()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["127.0.0.1.nip.io"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1/",
            "fetch_url");

        Assert.True(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListWhitespaceDomainEntry_IgnoresAndDeniesUnknownHost()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["   ", "example.com"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "https://evil.test/data",
            "fetch_url");

        Assert.False(result.Allowed);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListLiteralIpMismatch_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["127.0.0.2"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1/",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("not in the Sanctum allowed domain list", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListResolvedHostnameMismatch_Denies()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["localhost"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://192.0.2.1/",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("not in the Sanctum allowed domain list", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateNetworkAsync_AllowListUnresolvableAllowedDomain_DeniesResolvableHost()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig
            {
                Enabled = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = ["this-domain-definitely-does-not-exist-12345.invalid"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1/",
            "fetch_url");

        Assert.False(result.Allowed);

        Assert.Contains("not in the Sanctum allowed domain list", result.DenyReason, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task GetEffectiveResourceLimitsForWorkspaceAsync_KnownCampaign_ReturnsClampedLimits()
    {

        Guid campaignId = Guid.NewGuid();

        SanctumConfig config = new SanctumConfig
        {
            Enabled = true,
            ResourceLimits = new ResourceLimits
            {
                MaxProcessMemoryMb = 99999,
                MaxProcessCount = 99999,
                MaxFileWriteMb = 99999,
                ProcessTimeoutSeconds = 99999,
            },
        };

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(campaignId, _workspace.Root, config));

        ResourceLimits limits = await CreateGuard(repository).GetEffectiveResourceLimitsForWorkspaceAsync(_workspace.Root);

        Assert.True(limits.MaxProcessMemoryMb < 99999);

        Assert.True(limits.MaxProcessCount < 99999);

        Assert.True(limits.MaxFileWriteMb < 99999);

        Assert.True(limits.ProcessTimeoutSeconds < 99999);

    }

    [Fact]
    public async Task GetEffectiveResourceLimitsForWorkspaceAsync_EmptyWorkspace_ReturnsDefaultClampedLimits()
    {

        ResourceLimits limits = await CreateGuard(new FakeCampaignRepository())
            .GetEffectiveResourceLimitsForWorkspaceAsync("   ");

        Assert.Equal(ArcanumSettingClamps.SanctumMaxProcessMemoryMb(new ResourceLimits().MaxProcessMemoryMb), limits.MaxProcessMemoryMb);

    }

    [Fact]
    public async Task GetEffectiveResourceLimitsForWorkspaceAsync_UnknownWorkspace_ReturnsDefaultClampedLimits()
    {

        ResourceLimits limits = await CreateGuard(new FakeCampaignRepository())
            .GetEffectiveResourceLimitsForWorkspaceAsync("/no/such/workspace");

        Assert.Equal(ArcanumSettingClamps.SanctumMaxProcessMemoryMb(new ResourceLimits().MaxProcessMemoryMb), limits.MaxProcessMemoryMb);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetChildProcessBoundaryForWorkspaceAsync_EmptyWorkspace_ReturnsNullWithoutLookup(
        string? workspaceRoot)
    {

        FakeCampaignRepository repository = new();

        SanctumChildProcessBoundary? boundary = await CreateGuard(repository)
            .GetChildProcessBoundaryForWorkspaceAsync(workspaceRoot);

        Assert.Null(boundary);

        Assert.False(repository.WasQueried);

    }

    [Fact]
    public async Task GetChildProcessBoundaryForWorkspaceAsync_InvalidWorkspace_ReturnsNullWithoutLookup()
    {

        FakeCampaignRepository repository = new();

        SanctumChildProcessBoundary? boundary = await CreateGuard(repository)
            .GetChildProcessBoundaryForWorkspaceAsync("bad\u0000path");

        Assert.Null(boundary);

        Assert.False(repository.WasQueried);

    }

    [Fact]
    public async Task GetChildProcessBoundaryForWorkspaceAsync_UnknownWorkspace_ReturnsNull()
    {

        FakeCampaignRepository repository = new();

        SanctumChildProcessBoundary? boundary = await CreateGuard(repository)
            .GetChildProcessBoundaryForWorkspaceAsync(_workspace.CreateSubdir("unknown-campaign"));

        Assert.Null(boundary);

        Assert.True(repository.WasQueried);

    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task GetChildProcessBoundaryForWorkspaceAsync_KnownCampaign_CombinesSanctumFlags(
        bool enabled,
        bool enforcePathBoundary,
        bool expectedBoundaryRequired)
    {

        Guid campaignId = Guid.NewGuid();

        string allowedPath = _workspace.CreateSubdir("shared-" + campaignId.ToString("N"));

        SanctumConfig config = new()
        {
            Enabled = enabled,
            EnforcePathBoundary = enforcePathBoundary,
            AllowedPaths = [allowedPath],
        };

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(campaignId, _workspace.Root, config));

        SanctumChildProcessBoundary? boundary = await CreateGuard(repository)
            .GetChildProcessBoundaryForWorkspaceAsync(_workspace.Root);

        Assert.NotNull(boundary);

        Assert.Equal(Path.GetFullPath(_workspace.Root), boundary.WorkspaceRoot);

        Assert.Equal(expectedBoundaryRequired, boundary.PathBoundaryRequired);

        Assert.Collection(boundary.AllowedPaths, path => Assert.Equal(allowedPath, path));

    }

    [Fact]
    public async Task GetChildProcessBoundaryForWorkspaceAsync_InvalidCampaignPath_UsesResolvedLookupPath()
    {

        Guid campaignId = Guid.NewGuid();

        Campaign campaign = CreateCampaign(
            campaignId,
            "bad\u0000campaign-path",
            EnabledPathBoundaryConfig());

        FakeCampaignRepository repository = new();

        repository.SetCampaignForPath(campaign, _workspace.Root);

        SanctumChildProcessBoundary? boundary = await CreateGuard(repository)
            .GetChildProcessBoundaryForWorkspaceAsync(_workspace.Root);

        Assert.NotNull(boundary);

        Assert.Equal(Path.GetFullPath(_workspace.Root), boundary.WorkspaceRoot);

        Assert.True(boundary.PathBoundaryRequired);

    }

    [Theory]
    [InlineData(ResourceLimitKind.Cpu, "CPU time", "31s")]
    [InlineData(ResourceLimitKind.Memory, "memory", null)]
    [InlineData(ResourceLimitKind.FileDescriptors, "open file descriptor", "2049")]
    [InlineData((ResourceLimitKind)int.MaxValue, "resource", "unexpected")]
    public async Task RecordResourceLimitBreachAsync_KnownCampaign_RecordsResourceDescriptionAndDetails(
        ResourceLimitKind resource,
        string resourceName,
        string? actualValue)
    {

        Guid campaignId = Guid.NewGuid();

        const int maxBreachCount = 321;

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(
            campaignId,
            _workspace.Root,
            new SanctumConfig { MaxBreachCount = maxBreachCount }));

        FakeSanctumBreachRepository breachRepository = new();

        await CreateGuard(repository, breachRepository).RecordResourceLimitBreachAsync(
            _workspace.Root,
            "execute_command",
            resource,
            "30",
            actualValue);

        SanctumBreachRecord record = Assert.Single(breachRepository.Records);

        Assert.Equal(campaignId.ToString(), record.CampaignId);

        Assert.Equal("execute_command", record.ToolName);

        Assert.Equal("ResourceLimit", record.BreachType);

        Assert.Equal(
            $"Tool 'execute_command' exceeded {resourceName} limit: {actualValue ?? "unknown"} > 30",
            record.Description);

        Assert.NotNull(record.Details);

        Assert.Equal(_workspace.Root, record.Details.WorkspaceRoot);

        Assert.Equal("30", record.Details.LimitValue);

        Assert.Equal(actualValue, record.Details.ActualValue);

        Assert.Equal(maxBreachCount, breachRepository.LastMaxBreachCount);

    }

    [Fact]
    public async Task RecordResourceLimitBreachAsync_EmptyWorkspace_DoesNotQueryOrPersist()
    {

        FakeCampaignRepository repository = new();

        FakeSanctumBreachRepository breachRepository = new();

        await CreateGuard(repository, breachRepository).RecordResourceLimitBreachAsync(
            "   ",
            "execute_command",
            ResourceLimitKind.Cpu,
            "30s",
            "31s");

        Assert.False(repository.WasQueried);

        Assert.False(breachRepository.WasCalled);

        Assert.Empty(breachRepository.Records);

    }

    [Fact]
    public async Task RecordResourceLimitBreachAsync_UnknownWorkspace_DoesNotPersist()
    {

        FakeCampaignRepository repository = new();

        FakeSanctumBreachRepository breachRepository = new();

        await CreateGuard(repository, breachRepository).RecordResourceLimitBreachAsync(
            _workspace.CreateSubdir("unknown-resource-campaign"),
            "execute_command",
            ResourceLimitKind.Memory,
            "512MB",
            "513MB");

        Assert.True(repository.WasQueried);

        Assert.False(breachRepository.WasCalled);

        Assert.Empty(breachRepository.Records);

    }

    [Fact]
    public async Task RecordResourceLimitBreachAsync_PersistenceFailure_DoesNotEscape()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(campaignId, _workspace.Root, new SanctumConfig()));

        FakeSanctumBreachRepository breachRepository = new()
        {
            ExceptionToThrow = new IOException("simulated persistence failure"),
        };

        await CreateGuard(repository, breachRepository).RecordResourceLimitBreachAsync(
            _workspace.Root,
            "execute_command",
            ResourceLimitKind.Cpu,
            "30s",
            "31s");

        Assert.True(breachRepository.WasCalled);

        Assert.Empty(breachRepository.Records);

    }

    [Fact]
    public async Task RecordResourceLimitBreachAsync_Cancellation_Propagates()
    {

        Guid campaignId = Guid.NewGuid();

        FakeCampaignRepository repository = new();

        repository.SetCampaign(CreateCampaign(campaignId, _workspace.Root, new SanctumConfig()));

        var expected = new OperationCanceledException("simulated cancellation");

        FakeSanctumBreachRepository breachRepository = new()
        {
            ExceptionToThrow = expected,
        };

        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateGuard(repository, breachRepository).RecordResourceLimitBreachAsync(
                _workspace.Root,
                "execute_command",
                ResourceLimitKind.Cpu,
                "30s",
                "31s"));

        Assert.Same(expected, actual);

        Assert.True(breachRepository.WasCalled);

        Assert.Empty(breachRepository.Records);

    }

    private static SanctumGuard CreateGuard(
        FakeCampaignRepository repository,
        FakeSanctumBreachRepository? breachRepository = null,
        IDnsResolver? dnsResolver = null) =>
        new(
            repository,
            breachRepository ?? new FakeSanctumBreachRepository(),
            NullLogger<SanctumGuard>.Instance,
            dnsResolver ?? DeterministicDns());

    /// <summary>
    /// Allow-list evaluation resolves any non-literal allowed domain, so these tests would otherwise
    /// depend on live DNS — which fails in bursts when the resolver is slow or unreachable. This fixed
    /// table covers every host the network tests use; anything else raises
    /// <see cref="System.Net.Sockets.SocketException"/>, exactly as the real resolver does for an
    /// unknown name.
    /// </summary>
    /// <remarks>
    /// evil.test deliberately resolves to the SAME address as example.com. Shared hosting, CDN address
    /// space, and an attacker who simply publishes an allowed domain's address in their own zone all
    /// produce that collision in the wild, so every deny case here has to hold against a name that
    /// resolves and collides — not merely against one that fails to resolve.
    /// </remarks>
    private static FakeDnsResolver DeterministicDns()
    {

        FakeDnsResolver resolver = new();

        // Literal request hosts: the real resolver returns the parsed literal without a lookup.
        resolver.Add("127.0.0.1", IPAddress.Loopback);
        resolver.Add("192.0.2.1", IPAddress.Parse("192.0.2.1"));

        // Names that must resolve. 127.0.0.1.nip.io is a wildcard-DNS host that maps to loopback.
        resolver.Add("127.0.0.1.nip.io", IPAddress.Loopback);
        resolver.Add("localhost", IPAddress.Loopback);
        resolver.Add("example.com", IPAddress.Parse("93.184.216.34"));
        resolver.Add("api.example.com", IPAddress.Parse("93.184.216.34"));
        resolver.Add("evil.test", IPAddress.Parse("93.184.216.34"));

        // Deliberately absent, so it fails to resolve:
        // this-domain-definitely-does-not-exist-12345.invalid.
        return resolver;
    }

    private static SanctumConfig EnabledPathBoundaryConfig() =>
        new()
        {
            Enabled = true,
            EnforcePathBoundary = true,
        };

    private static Campaign CreateCampaign(Guid id, string path, SanctumConfig config) =>
        new()
        {
            Id = id,
            Name = "test-campaign",
            NameLower = "test-campaign",
            Path = path,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(config),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class FakeCampaignRepository : ICampaignRepository
    {

        private readonly Dictionary<Guid, Campaign> _byId = new();

        private readonly Dictionary<string, Campaign> _byPath = new(StringComparer.Ordinal);

        public bool WasQueried { get; private set; }

        public void SetCampaign(Campaign campaign)
        {

            _byId[campaign.Id] = campaign;

            try
            {
                _byPath[Path.GetFullPath(campaign.Path.Trim())] = campaign;
            }
            catch (Exception)
            {
                // Allow campaigns with invalid paths for path-resolution tests.
            }

        }

        public void SetCampaignForPath(Campaign campaign, string lookupPath)
        {

            _byId[campaign.Id] = campaign;

            _byPath[Path.GetFullPath(lookupPath.Trim())] = campaign;

        }

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {

            WasQueried = true;

            _byId.TryGetValue(id, out Campaign? campaign);

            return Task.FromResult(campaign);

        }

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
        {

            WasQueried = true;

            string full = Path.GetFullPath(path.Trim());

            _byPath.TryGetValue(full, out Campaign? campaign);

            return Task.FromResult(campaign);

        }

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

    private sealed class FakeSanctumBreachRepository : ISanctumBreachRepository
    {

        public List<SanctumBreachRecord> Records { get; } = [];

        public bool WasCalled { get; private set; }

        public int? LastMaxBreachCount { get; private set; }

        public Exception? ExceptionToThrow { get; init; }

        public Task RecordAsync(SanctumBreachRecord breach, int maxBreachCount, CancellationToken ct = default)
        {

            WasCalled = true;

            LastMaxBreachCount = maxBreachCount;

            if (ExceptionToThrow is not null)
            {

                return Task.FromException(ExceptionToThrow);

            }

            Records.Add(breach with { Id = Guid.NewGuid().ToString("N") });

            return Task.CompletedTask;

        }

        public Task<IReadOnlyList<SanctumBreachRecord>> QueryAsync(
            string campaignId,
            int limit,
            DateTimeOffset? before = null,
            string? toolName = null,
            CancellationToken ct = default)
        {

            IEnumerable<SanctumBreachRecord> query = Records.Where(r => r.CampaignId == campaignId);

            if (before is not null)
            {
                query = query.Where(r => r.OccurredAt < before.Value);
            }

            if (!string.IsNullOrWhiteSpace(toolName))
            {
                query = query.Where(r => r.ToolName == toolName);
            }

            IReadOnlyList<SanctumBreachRecord> result = query
                .OrderByDescending(r => r.OccurredAt)
                .Take(limit)
                .ToList();

            return Task.FromResult(result);

        }

        public Task<int> GetCountAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult(Records.Count(r => r.CampaignId == campaignId));

        public Task<int> DeleteOldestAsync(string campaignId, int count, CancellationToken ct = default)
        {

            List<SanctumBreachRecord> toRemove = Records
                .Where(r => r.CampaignId == campaignId)
                .OrderBy(r => r.OccurredAt)
                .Take(count)
                .ToList();

            foreach (SanctumBreachRecord record in toRemove)
            {
                Records.Remove(record);
            }

            return Task.FromResult(toRemove.Count);

        }

    }

}
