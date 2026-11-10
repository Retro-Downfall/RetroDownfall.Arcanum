using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildDormancyTests
{
    [Fact]
    public void Guild_execution_ports_are_registered_while_general_roster_management_remains_unavailable()
    {
        var assemblies = new[]
        {
            typeof(IGuildPort).Assembly,
            typeof(ArcanumDbContext).Assembly,
            typeof(ApiBootstrapper).Assembly,
            typeof(ArcanumApiCredentialLease).Assembly
        };

        var ports = typeof(IGuildPort).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(IGuildPort).Namespace && type.IsInterface)
            .ToArray();

        Assert.Equal(10, ports.Length);

        var concreteTypes = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface);

        var implementations = concreteTypes.Where(type => ports.Any(port => port.IsAssignableFrom(type))).ToArray();

        Assert.Equal(new[] { typeof(ConclaveArchmage), typeof(GuildLocalPort), typeof(GuildRemotePort), typeof(GuildPortRouter), typeof(GuildQuestStore), typeof(GuildRoster), typeof(GuildWorkspaceLeaseBroker), typeof(SignetAuthority) }.OrderBy(type => type.FullName),
            implementations.OrderBy(type => type.FullName));

        ServiceCollection services = new();

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        Type[] executable = [typeof(IGuildPort), typeof(IGuildQuestCoordinator), typeof(ISignetAuthority), typeof(IGuildToolAuthority)];

        Assert.All(executable, port => Assert.Contains(services, descriptor => descriptor.ServiceType == port));

        Assert.DoesNotContain(services, descriptor => ports.Contains(descriptor.ServiceType) && !executable.Contains(descriptor.ServiceType)
            || descriptor.ServiceType == typeof(GuildRosterContext));
    }

    [Fact]
    public void Native_HTTP_and_peer_configuration_records_do_not_register_host_authority()
    {
        Type[] native = [typeof(GuildIssueRootRequest), typeof(GuildCancelQuestRequest), typeof(GuildContinueRequest),
            typeof(GuildQuestDto), typeof(GuildQuestInputDto), typeof(GuildStatusDto), typeof(GuildCancellationDto), typeof(GuildResultReceipt), typeof(GuildContentDto),
            typeof(GuildResolveRemoteAgentRequest), typeof(GuildAgentCard), typeof(GuildRemoteInvocationOptions)];

        Assert.All(native, type => Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(type)));

        Type[] privateAuthority = [typeof(GuildCallerContext), typeof(GuildExecutionHandle), typeof(GuildRecoveryContext),
            typeof(GuildAdmissionContext), typeof(CohortDto), typeof(GuildCircleInboxItem)];

        Assert.All(privateAuthority, type => Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(type)));

        Type[] configuredEnums = [typeof(GuildWorkspaceAccess), typeof(GuildToolSourceKind), typeof(GuildMemoryStore),
            typeof(GuildMemoryOperation), typeof(GuildMemoryScopeKind), typeof(GuildBudgetMode), typeof(GuildDelegationKind)];

        foreach (var type in typeof(IGuildPort).Assembly.GetExportedTypes().Where(type =>
            type.Namespace == typeof(IGuildPort).Namespace && !type.ContainsGenericParameters))
        {
            if (configuredEnums.Contains(type)) Assert.NotNull(ConfigurationJsonContext.Default.GetTypeInfo(type));
            else Assert.Null(ConfigurationJsonContext.Default.GetTypeInfo(type));
        }
    }

    [Fact]
    public void Guild_contract_signatures_do_not_depend_on_transport_process_or_persistence_types()
    {
        var types = typeof(IGuildPort).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(IGuildPort).Namespace);

        var signatures = types.SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
            .Concat(type.GetMethods().Select(method => method.ReturnType))
            .Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType))));

        Assert.All(signatures, VerifyDependency);
    }

    private static void VerifyDependency(Type type)
    {
        string dependency = type.FullName ?? type.Name;

        Assert.DoesNotContain("EntityFramework", dependency, StringComparison.Ordinal);

        Assert.DoesNotContain("A2A", dependency, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Sqlite", dependency, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("System.Diagnostics.Process", dependency, StringComparison.Ordinal);

        foreach (Type argument in type.GetGenericArguments())
        {
            VerifyDependency(argument);
        }
    }
}
