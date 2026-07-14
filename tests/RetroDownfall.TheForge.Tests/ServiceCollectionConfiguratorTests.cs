using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// Guards against DI registration traps that crash The Forge before MainWindow appears.
/// </summary>
public sealed class ServiceCollectionConfiguratorTests
{

    [Fact]
    public void Build_ResolvesApiKeyResolver_WithoutCircularDependency()
    {

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        IOsCredentialStore store = services.GetRequiredService<IOsCredentialStore>();

        ApiKeyResolver resolver = services.GetRequiredService<ApiKeyResolver>();

        Assert.NotNull(store);

        Assert.NotNull(resolver);

    }

}
