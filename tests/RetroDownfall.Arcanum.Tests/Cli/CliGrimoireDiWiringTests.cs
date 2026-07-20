using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
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

}
