using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

/// <summary>
/// Issue #219: configuration that existed only to decide or pre-answer a Ward prompt is removed.
/// Retired keys fail closed with an actionable path instead of binding silently, while the two
/// retained Ward choices continue to describe advertisement and operator-facing attendance only.
/// </summary>
public sealed class WardAutoApproveConfigurationTests
{

    private readonly ConfigurationValidator _validator = new();

    [Theory]
    [InlineData("Arcanum:Security:Ward:Enabled", "security.ward.enabled")]
    [InlineData(
        "Arcanum:Security:Ward:AutoDenyInUnattendedMode",
        "security.ward.autoDenyInUnattendedMode")]
    [InlineData(
        "Arcanum:Security:Ward:AutoApprove:Enabled",
        "security.ward.autoApprove")]
    [InlineData(
        "Arcanum:Security:Ward:AutoApprove:Tools:0",
        "security.ward.autoApprove")]
    public void Removed_Ward_approval_key_is_rejected_from_configuration(
        string key,
        string expectedPointer)
    {

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "true",
            })
            .Build();

        Result result = _validator.RejectObsoleteKeys(configuration);

        Assert.False(result.IsSuccess);

        ConfigurationValidationError error = Assert.Single(
            result.Error.Details!,
            candidate => candidate.Pointer == expectedPointer);

        Assert.Contains("remove", error.Detail, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData(
        """{"security":{"ward":{"enabled":false}}}""",
        "security.ward.enabled")]
    [InlineData(
        """{"security":{"ward":{"autoDenyInUnattendedMode":true}}}""",
        "security.ward.autoDenyInUnattendedMode")]
    [InlineData(
        """{"security":{"ward":{"autoApprove":{"enabled":true}}}}""",
        "security.ward.autoApprove")]
    [InlineData(
        """{"security":{"ward":{"autoApprove":{"tools":["apply_patch"]}}}}""",
        "security.ward.autoApprove")]
    public void Removed_Ward_approval_key_is_rejected_from_json(
        string json,
        string expectedPointer)
    {

        using JsonDocument document = JsonDocument.Parse(json);

        Result result = _validator.RejectObsoleteJsonKeys(document.RootElement);

        Assert.True(result.IsFailure);

        ConfigurationValidationError error = Assert.Single(
            result.Error.Details!,
            candidate => candidate.Pointer == expectedPointer);

        Assert.Contains("remove", error.Detail, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData("security.ward.enabled")]
    [InlineData("security.ward.autoDenyInUnattendedMode")]
    [InlineData("security.ward.autoApprove.enabled")]
    [InlineData("security.ward.autoApprove.tools")]
    public void Removed_Ward_approval_key_is_not_reachable_from_config_set(string path)
    {

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            new ArcanumSettings(),
            path,
            "true");

        Assert.False(result.IsSuccess);

        Assert.Contains("remove", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Public_Ward_configuration_exports_only_retained_choices()
    {

        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings
                {
                    ForbiddenArts = ["write_file"],
                    UnattendedMode = true,
                },
            },
        };

        string json = JsonSerializer.Serialize(
            settings,
            ConfigurationJsonContext.Default.ArcanumSettings);

        using JsonDocument document = JsonDocument.Parse(json);

        string[] propertyNames = document.RootElement
            .GetProperty("security")
            .GetProperty("ward")
            .EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["forbiddenArts", "unattendedMode"], propertyNames);

    }

}
