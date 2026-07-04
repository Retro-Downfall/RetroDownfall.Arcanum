using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
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

    [Fact]
    public async Task ValidatePathAsync_AllowedPathSymlinkEntry_AllowsNestedFile()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

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
    public async Task ValidateNetworkAsync_AllowListResolvesHostToAllowedIp_Allows()
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

        Assert.True(result.Allowed);

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
    public async Task ValidateNetworkAsync_AllowListSkipsWhitespaceAndMatchesIpLiteral_Allows()
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
                AllowedDomains = ["   ", "127.0.0.1"],
            }));

        SanctumResult result = await CreateGuard(repository).ValidateNetworkAsync(
            campaignId.ToString(),
            "http://127.0.0.1.nip.io/",
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

    [Fact]
    public async Task ValidateNetworkAsync_AllowListResolvedIpLiteral_AllowsHostname()
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

        Assert.True(result.Allowed);

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

    private static SanctumGuard CreateGuard(FakeCampaignRepository repository, FakeSanctumBreachRepository? breachRepository = null) =>
        new(repository, breachRepository ?? new FakeSanctumBreachRepository(), NullLogger<SanctumGuard>.Instance);

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

        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {

            WasQueried = true;

            _byId.TryGetValue(id, out Campaign? campaign);

            return Task.FromResult(campaign);

        }

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
        {

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

        public Task<Campaign> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

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

        public Task RecordAsync(SanctumBreachRecord breach, int maxBreachCount, CancellationToken ct = default)
        {

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
