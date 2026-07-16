using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class ValidationRoutingTests : IDisposable
{

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string _tempRoot;

    public ValidationRoutingTests()
    {

        _originalHome = global::System.Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = global::System.Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-validation-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        global::System.Environment.SetEnvironmentVariable("HOME", _tempRoot);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

    }

    [Fact]

    public async Task WriteAsync_returns_validation_error_pointers_that_match_descriptor_keys()
    {

        ArcanumDataProtectionSecretProtector protector = new();

        using ArcanumConfigurationStore store = new(protector);

        ArcanumSettings settings = new()

        {

            Intelligence = new IntelligenceSettings { ExecuteCommandTimeoutSeconds = 600 },

            Mcp = new McpSettings { RequestTimeoutSeconds = 1 },

        };

        ConfigurationWriteResult result = await store.WriteAsync(settings, CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.NotEmpty(result.ValidationErrors);

        Assert.Contains(result.ValidationErrors, e => e.Pointer == "mcp.requestTimeoutSeconds");

        Assert.NotNull(SettingDescriptors.Find("mcp.requestTimeoutSeconds"));

    }

    [Fact]

    public void Descriptor_keys_cover_all_validator_pointer_targets()
    {

        string[] validatorPointers =

        [

            "mcp.requestTimeoutSeconds",

            "mcp.maxJsonRpcLineBytes",

            "defaultModel",

            "fastModel",

            "campaigns.allowedRoots",

            "spells.allowedWorkspaceRoots",

            "perception.allowedWorkspaceRoots",

            "host.workspace",

        ];

        foreach (string pointer in validatorPointers)

        {

            Assert.True(SettingDescriptors.Find(pointer) is not null,

                $"No SettingDescriptor with Key '{pointer}' — validation routing cannot light up this field.");

        }

    }

    [Fact]

    public async Task WriteAsync_validation_error_detail_is_non_empty()
    {

        ArcanumDataProtectionSecretProtector protector = new();

        using ArcanumConfigurationStore store = new(protector);

        ArcanumSettings settings = new()

        {

            Intelligence = new IntelligenceSettings { ExecuteCommandTimeoutSeconds = 600 },

            Mcp = new McpSettings { RequestTimeoutSeconds = 1 },

        };

        ConfigurationWriteResult result = await store.WriteAsync(settings, CancellationToken.None);

        ConfigurationValidationError? error = result.ValidationErrors

            .FirstOrDefault(e => e.Pointer == "mcp.requestTimeoutSeconds");

        Assert.NotNull(error);

        Assert.False(string.IsNullOrWhiteSpace(error.Detail));

    }

    public void Dispose()
    {

        try

        {

            Directory.Delete(_tempRoot, recursive: true);

        }
        catch

        {

            // Best-effort cleanup.

        }

        global::System.Environment.SetEnvironmentVariable("HOME", _originalHome);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

    }

}
