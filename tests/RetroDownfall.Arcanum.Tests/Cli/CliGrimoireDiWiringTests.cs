using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Guards the CLI Grimoire stack against drifting from host wiring when
/// <see cref="IGrimoireRepository"/> gains new constructor dependencies (e.g. session attachments).
/// </summary>
public sealed class CliGrimoireDiWiringTests
{

    [Fact]
    public void ConfigureCliServices_registers_session_attachment_store_required_by_grimoire_repository()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        Assert.Contains(services, static d => d.ServiceType == typeof(ISessionAttachmentStore));

        Assert.Contains(services, static d => d.ServiceType == typeof(IGrimoireRepository));

    }


    [Fact]
    public void ConfigureCliServices_does_not_register_local_chronosync_for_run_or_ask()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IChronosyncEngine));

    }

    [Fact]
    public void ConfigureCliServices_aliases_one_context_store_to_reader_and_exclusive_writer()
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        using ServiceProvider provider = services.BuildServiceProvider();

        CliContextStore concrete = provider.GetRequiredService<CliContextStore>();

        Assert.Same(
            concrete,
            provider.GetRequiredService<ICliContextStore>());

        Assert.Same(
            concrete,
            provider.GetRequiredService<ICliContextExclusiveWriter>());

    }

}
