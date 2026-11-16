using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class LocalRuntimeConfigurationTests
{
    [Fact]
    public void Indexed_configuration_binding_and_config_paths_retain_runtime_declarations()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arcanum:LocalRuntimes:0:Id"] = "desktop",
            ["Arcanum:LocalRuntimes:0:Adapter"] = "ollama",
            ["Arcanum:LocalRuntimes:0:ManagementEndpoint"] = "http://localhost:11434",
            ["Arcanum:Providers:0:LocalRuntimeId"] = "desktop",
        }).Build();

        ArcanumSettings settings = new();

        configuration.GetSection("Arcanum").Bind(settings);

        Assert.Equal("desktop", Assert.Single(settings.LocalRuntimes).Id);

        Assert.Equal("desktop", Assert.Single(settings.Providers).LocalRuntimeId);

        var updated = ConfigurationPathAccessor.Set(settings, "localRuntimes.0.adapter", "ollama");

        Assert.True(updated.IsSuccess);
    }

    [Fact]
    public void Familiar_provider_cannot_declare_a_local_runtime()
    {
        ArcanumSettings settings = new()
        {
            Providers = [new() { Name = "familiar", Type = AiProviderKind.CodexCli, LocalRuntimeId = "desktop" }],
            LocalRuntimes = [new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" }],
        };

        var result = new ConfigurationValidator().Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, error => error.Pointer == "providers[0].localRuntimeId");
    }

    [Fact]
    public void Management_endpoints_are_masked_and_restore_by_runtime_identity()
    {
        ArcanumSettings settings = new() { LocalRuntimes = [new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434/proxy" }] };

        ArcanumSettings redacted = ConfigurationRedactor.Redact(settings);

        Assert.Equal("***", Assert.Single(redacted.LocalRuntimes).ManagementEndpoint);

        Assert.True(ConfigurationPathAccessor.IsSensitive("localRuntimes.0.managementEndpoint"));

        Assert.Equal("http://localhost:11434/proxy", Assert.Single(ConfigurationRedactor.MergeRedactedSecrets(redacted, settings).LocalRuntimes).ManagementEndpoint);

        Assert.True(ConfigurationRedactor.ValidateNoResidualMask(redacted).IsFailure);
    }

    [Fact]
    public void Runtime_management_credentials_are_removed_from_Familiar_child_environments()
    {
        ArcanumSettings settings = new() { LocalRuntimes = [new() { Id = "desktop", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434", CredentialEnvironmentVariable = "CUSTOM_MANAGEMENT_SECRET" }] };

        Assert.Contains("CUSTOM_MANAGEMENT_SECRET", FamiliarSecretEnvironmentNames.Collect(settings));
    }

    [Theory]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"future\",\"managementEndpoint\":\"http://localhost:11434\"}]", "localRuntimes[0].adapter")]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"ollama\",\"managementEndpoint\":\"http://user:secret@localhost:11434\"}]", "localRuntimes[0].managementEndpoint")]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"ollama\",\"managementEndpoint\":\"http://localhost:11434?key=secret\"}]", "localRuntimes[0].managementEndpoint")]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"ollama\",\"managementEndpoint\":\"http://localhost:11434\",\"credentialEnvironmentVariable\":\"bad-name\"}]", "localRuntimes[0].credentialEnvironmentVariable")]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"ollama\",\"managementEndpoint\":\"http://localhost:11434\"},{\"id\":\"DESKTOP\",\"adapter\":\"ollama\",\"managementEndpoint\":\"http://localhost:11434\"}]", "localRuntimes[1].id")]
    [InlineData("[null]", "localRuntimes[0]")]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"ollama\",\"resourceGroup\":\" \",\"managementEndpoint\":\"http://localhost:11434\"}]", "localRuntimes[0].resourceGroup")]
    [InlineData("[{\"id\":\"desktop\",\"adapter\":\"ollama\",\"resourceGroup\":\" padded\",\"managementEndpoint\":\"http://localhost:11434\"}]", "localRuntimes[0].resourceGroup")]
    public void Invalid_runtime_declarations_name_the_exact_pointer(string runtimes, string pointer)
    {
        ArcanumSettings settings = JsonSerializer.Deserialize("{\"localRuntimes\":" + runtimes + "}", ConfigurationJsonContext.Default.ArcanumSettings)!;

        var result = new ConfigurationValidator().Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, error => error.Pointer == pointer);
    }

    [Fact]
    public void Unknown_provider_runtime_reference_is_rejected()
    {
        ArcanumSettings settings = JsonSerializer.Deserialize("""
            {"providers":[{"name":"chat","endpoint":"http://localhost:11434/v1","localRuntimeId":"missing","models":["primary"]}]}
            """, ConfigurationJsonContext.Default.ArcanumSettings)!;

        var result = new ConfigurationValidator().Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, error => error.Pointer == "providers[0].localRuntimeId");
    }

    [Fact]
    public void Runtime_declarations_and_provider_references_survive_configuration_roundtrip()
    {
        const string json = """
            {"localRuntimes":[{"id":"desktop","adapter":"ollama","resourceGroup":"desktop-gpu","managementEndpoint":"http://localhost:11434","credentialEnvironmentVariable":"OLLAMA_MANAGEMENT_KEY"}],"providers":[{"name":"chat","endpoint":"http://localhost:11434/v1","localRuntimeId":"desktop","models":["primary"]}],"defaultModel":"primary"}
            """;

        ArcanumSettings settings = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.ArcanumSettings)!;

        using JsonDocument output = JsonDocument.Parse(JsonSerializer.Serialize(settings, ConfigurationJsonContext.Default.ArcanumSettings));

        Assert.True(output.RootElement.TryGetProperty("localRuntimes", out JsonElement runtimes));

        Assert.Equal("ollama", runtimes[0].GetProperty("adapter").GetString());

        Assert.Equal("desktop-gpu", runtimes[0].GetProperty("resourceGroup").GetString());

        Assert.Equal("OLLAMA_MANAGEMENT_KEY", runtimes[0].GetProperty("credentialEnvironmentVariable").GetString());

        Assert.Equal("desktop", output.RootElement.GetProperty("providers")[0].GetProperty("localRuntimeId").GetString());
    }
}
