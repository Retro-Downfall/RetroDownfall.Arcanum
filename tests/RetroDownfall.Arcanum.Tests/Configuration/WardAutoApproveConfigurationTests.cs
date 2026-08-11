using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

/// <summary>
/// Issue #53: <c>Arcanum:Security:Ward:AutoApprove</c> is a first-class configuration path — it binds
/// through the source-generated context, is walked by the strict schema check, and rejects blank or
/// duplicated tool names at startup rather than at the first gated tool call.
/// </summary>
public sealed class WardAutoApproveConfigurationTests
{

    private readonly ConfigurationValidator _validator = new();

    [Fact]
    public void AutoApprove_is_registered_on_the_source_generated_configuration_context()
    {

        Assert.NotNull(
            ConfigurationJsonContext.Default.GetTypeInfo(typeof(WardAutoApprovePolicySettings)));

    }

    [Fact]
    public void AutoApprove_round_trips_through_the_source_generated_context()
    {

        WardPolicySettings policy = new()
        {
            AutoApprove = new WardAutoApprovePolicySettings
            {
                Enabled = true,
                Tools = ["apply_patch"],
            },
        };

        string json = JsonSerializer.Serialize(
            policy,
            ConfigurationJsonContext.Default.WardPolicySettings);

        WardPolicySettings? restored = JsonSerializer.Deserialize(
            json,
            ConfigurationJsonContext.Default.WardPolicySettings);

        Assert.NotNull(restored);

        Assert.True(restored.AutoApprove.Enabled);

        Assert.Equal(["apply_patch"], restored.AutoApprove.Tools);

    }

    [Fact]
    public void AutoApprove_binds_from_configuration_and_defaults_to_off()
    {

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Security:Ward:AutoApprove:Enabled"] = "true",
                ["Arcanum:Security:Ward:AutoApprove:Tools:0"] = "apply_patch",
            })
            .Build();

        ArcanumSettings settings = new();

        configuration.GetSection("Arcanum").Bind(settings);

        Assert.True(settings.Security!.Ward.AutoApprove.Enabled);

        Assert.Equal(["apply_patch"], settings.Security.Ward.AutoApprove.Tools);

        Assert.False(new WardPolicySettings().AutoApprove.Enabled);

        Assert.Empty(new WardPolicySettings().AutoApprove.Tools);

    }

    [Fact]
    public void The_strict_schema_walk_accepts_the_autoApprove_block()
    {

        const string json =
            """
            {
              "security": {
                "ward": {
                  "autoApprove": {
                    "enabled": true,
                    "tools": ["apply_patch"]
                  }
                }
              }
            }
            """;

        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void The_strict_schema_walk_fails_closed_on_an_unknown_autoApprove_path()
    {

        const string json =
            """
            {
              "security": {
                "ward": {
                  "autoApprove": {
                    "enabled": true,
                    "pathPrefixes": ["src/"]
                  }
                }
              }
            }
            """;

        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "security.ward.autoApprove.pathPrefixes");

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_auto_approve_tool_name_is_rejected(string toolName)
    {

        Result result = _validator.Validate(SettingsWithAutoApproveTools(toolName));

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer.StartsWith(
                "security.ward.autoApprove.tools",
                StringComparison.Ordinal));

    }

    [Fact]
    public void A_duplicated_auto_approve_tool_name_is_rejected()
    {

        Result result = _validator.Validate(
            SettingsWithAutoApproveTools("apply_patch", "APPLY_PATCH"));

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "security.ward.autoApprove.tools[1]");

    }

    [Fact]
    public void A_well_formed_auto_approve_list_validates()
    {

        Result result = _validator.Validate(
            SettingsWithAutoApproveTools("apply_patch", "workspace_check"));

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void AutoApprove_is_reachable_from_arcanum_config_set()
    {

        ConfigurationPathUpdate enabled = ConfigurationPathAccessor.Set(
            new ArcanumSettings(),
            "security.ward.autoApprove.enabled",
            "true");

        Assert.True(enabled.IsSuccess, enabled.Error);

        Assert.True(enabled.Settings!.Security!.Ward.AutoApprove.Enabled);

        Assert.Equal(
            "True",
            ConfigurationPathAccessor.GetDisplayValue(
                enabled.Settings,
                "security.ward.autoApprove.enabled"),
            ignoreCase: true);

        // Collection-valued paths take either a JSON array or the plain comma-separated form, matching
        // every other array setting the accessor exposes.
        ConfigurationPathUpdate tools = ConfigurationPathAccessor.Set(
            enabled.Settings,
            "security.ward.autoApprove.tools",
            """["apply_patch","workspace_check"]""");

        Assert.True(tools.IsSuccess, tools.Error);

        Assert.Equal(
            ["apply_patch", "workspace_check"],
            tools.Settings!.Security!.Ward.AutoApprove.Tools);

        ConfigurationPathUpdate plainTools = ConfigurationPathAccessor.Set(
            enabled.Settings,
            "security.ward.autoApprove.tools",
            "apply_patch, workspace_check");

        Assert.True(plainTools.IsSuccess, plainTools.Error);

        Assert.Equal(
            ["apply_patch", "workspace_check"],
            plainTools.Settings!.Security!.Ward.AutoApprove.Tools);

    }

    private static ArcanumSettings SettingsWithAutoApproveTools(params string[] tools) =>
        new()
        {
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings
                {
                    AutoApprove = new WardAutoApprovePolicySettings
                    {
                        Enabled = true,
                        Tools = [.. tools],
                    },
                },
            },
        };

}
