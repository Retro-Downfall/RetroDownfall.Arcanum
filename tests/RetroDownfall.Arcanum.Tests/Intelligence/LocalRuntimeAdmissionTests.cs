using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LocalRuntimeAdmissionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unbound_http_provider_is_not_coordinated(string? binding)
    {
        var provider = new ProviderSettings { Endpoint = "https://example.com/v1", LocalRuntimeId = binding };

        Assert.Null(ProviderResourceKey.FromProvider(provider));
    }

    [Fact]
    public void Aliases_use_management_origin_not_inference_endpoint_or_provider_name()
    {
        var settings = new ArcanumSettings
        {
            LocalRuntimes = [new() { Id = "local", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434/api" }],
        };
        var first = new ProviderSettings { Name = "chat", Endpoint = "http://127.0.0.1:5000/v1", LocalRuntimeId = "LOCAL" };
        var second = new ProviderSettings { Name = "embedding", Endpoint = "http://different:6000/other", LocalRuntimeId = "local" };

        Assert.Equal(ProviderResourceKey.FromEndpoint("http://127.0.0.1:11434/other"), ProviderResourceKey.FromProvider(first, settings));
        Assert.Equal(ProviderResourceKey.FromProvider(first, settings), ProviderResourceKey.FromProvider(second, settings));
    }

    [Fact]
    public void Distinct_runtime_entries_at_the_same_origin_coalesce()
    {
        var settings = new ArcanumSettings
        {
            LocalRuntimes =
            [
                new() { Id = "a", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" },
                new() { Id = "b", Adapter = "ollama", ManagementEndpoint = "http://[::1]:11434/api" },
            ],
        };

        Assert.Equal(ProviderResourceKey.FromProvider(new() { LocalRuntimeId = "a" }, settings),
            ProviderResourceKey.FromProvider(new() { LocalRuntimeId = "b" }, settings));
    }

    [Fact]
    public void Invalid_missing_or_ambiguous_bindings_fail_closed()
    {
        var provider = new ProviderSettings { LocalRuntimeId = "local" };
        var runtime = new LocalRuntimeSettings { Id = "local", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" };
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider));
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider, new ArcanumSettings()));
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider, new ArcanumSettings { LocalRuntimes = [runtime, runtime with { Id = "LOCAL" }] }));
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider, new ArcanumSettings { LocalRuntimes = [runtime with { Adapter = "unknown" }] }));
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider, new ArcanumSettings { LocalRuntimes = [runtime with { ManagementEndpoint = "http://user:password@localhost/" }] }));
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider with { LocalRuntimeId = " local " }, new ArcanumSettings { LocalRuntimes = [runtime] }));
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(provider with { Type = AiProviderKind.CodexCli }, new ArcanumSettings { LocalRuntimes = [runtime] }));
    }
    [Fact]
    public void Explicit_resource_group_coalesces_runtimes_on_distinct_origins()
    {
        var settings = new ArcanumSettings
        {
            LocalRuntimes =
            [
                new() { Id = "a", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434", ResourceGroup = "shared-gpu" },
                new() { Id = "b", Adapter = "ollama", ManagementEndpoint = "http://localhost:1234", ResourceGroup = "SHARED-GPU" },
                new() { Id = "c", Adapter = "ollama", ManagementEndpoint = "http://localhost:11434" },
            ],
        };
        var a = ProviderResourceKey.FromProvider(new() { LocalRuntimeId = "a" }, settings);
        var b = ProviderResourceKey.FromProvider(new() { LocalRuntimeId = "b" }, settings);
        var c = ProviderResourceKey.FromProvider(new() { LocalRuntimeId = "c" }, settings);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        settings.LocalRuntimes[0].ResourceGroup = " ";
        Assert.Throws<InvalidOperationException>(() => ProviderResourceKey.FromProvider(new() { LocalRuntimeId = "a" }, settings));
    }

}
