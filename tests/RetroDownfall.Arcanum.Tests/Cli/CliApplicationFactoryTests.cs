using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console.Cli.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliApplicationFactoryTests
{

    [Fact]
    public void Help_smoke_lists_core_commands()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CommandAppTester tester = new(new CliTypeRegistrar(services));

        tester.Configure(CliApplicationFactory.ConfigureCommands);

        CommandAppResult result = tester.Run("--help");

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("serve", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("ask", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("chat", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("look", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("key", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Help_smoke_lists_branch_commands()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CommandAppTester tester = new(new CliTypeRegistrar(services));

        tester.Configure(CliApplicationFactory.ConfigureCommands);

        CommandAppResult result = tester.Run("--help");

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("daemon", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("lore", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("llama", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void ConfigureCliServices_registers_streaming_and_bounded_request_clients()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        HttpClient streamingClient = factory.CreateClient(ArcanumApiClient.StreamingHttpClientName);

        HttpClient requestClient = factory.CreateClient(ArcanumApiClient.RequestHttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, streamingClient.Timeout);

        Assert.Equal(TimeSpan.FromSeconds(60), requestClient.Timeout);

    }

    [Fact]
    public void ConfigureCliServices_registers_api_key_digest_cache_so_secret_store_resolves()
    {

        // Regression guard for DX5: DataProtectionSecretStore requires IApiKeyDigestCache,
        // which the CLI DI wiring previously omitted, so constructing AskCommand/ChatCommand
        // via the real container threw. Resolve the secret store and an AskCommand to prove
        // the full construction path now works.

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        IApiKeyDigestCache digestCache = provider.GetRequiredService<IApiKeyDigestCache>();

        Assert.NotNull(digestCache);

        AskCommand askCommand = provider.GetRequiredService<AskCommand>();

        Assert.NotNull(askCommand);

    }

}
