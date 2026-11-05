using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildDormancyTests
{
    [Fact]
    public void No_shipping_layer_supplies_a_runtime_implementation_of_a_Guild_port()
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

        Assert.Equal(9, ports.Length);

        var concreteTypes = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface);

        Assert.DoesNotContain(concreteTypes, type => ports.Any(port => port.IsAssignableFrom(type)));
    }

    [Fact]
    public void Guild_data_is_not_registered_on_the_HTTP_or_configuration_contexts()
    {
        foreach (var type in typeof(IGuildPort).Assembly.GetExportedTypes().Where(type =>
            type.Namespace == typeof(IGuildPort).Namespace && !type.ContainsGenericParameters))
        {
            Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(type));

            Assert.Null(ConfigurationJsonContext.Default.GetTypeInfo(type));
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
