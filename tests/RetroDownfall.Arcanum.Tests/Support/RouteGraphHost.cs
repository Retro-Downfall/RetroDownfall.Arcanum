using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A host for a test that maps real endpoints in order to inspect their metadata, or to drive a few of
/// them, without composing the services behind every handler parameter.
/// </summary>
/// <remarks>
/// <para>Building an endpoint decides, for each handler parameter the framework cannot place by its type
/// alone, whether it is a service or the request body, by asking the container. A parameter the container
/// has never heard of is a body, and a body type needs serializer metadata. That used to pass because
/// reflection built the metadata for an interface nobody would ever deserialize; with reflection off, as
/// the production binary has it, the same host throws before the test's first assertion.</para>
/// <para>This registers every service contract the Api, Core and Infrastructure assemblies declare that
/// <see cref="ArcanumJsonContext"/> does not serialize as a service that resolves to nothing. The container
/// then recognizes the type as a service, and a handler's optional service parameter still receives
/// <see langword="null"/>, exactly as it did when the type was simply unregistered. The host also gets the
/// same JSON resolver chain the real one has, so a genuine request body resolves. A test that wants a real
/// service registers it afterwards, and the later registration wins.</para>
/// </remarks>
internal static class RouteGraphHost
{
    private static readonly Lazy<Type[]> ServiceContracts = new(DiscoverServiceContracts);

    public static WebApplicationBuilder CreateBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        builder.Services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        RegisterServiceContracts(builder.Services);

        return builder;
    }

    /// <summary>
    /// Registers an absent-service placeholder for every service contract not already present in
    /// <paramref name="services"/>.
    /// </summary>
    public static void RegisterServiceContracts(IServiceCollection services)
    {
        foreach (Type contract in ServiceContracts.Value)
        {
            services.TryAdd(ServiceDescriptor.Singleton(contract, static _ => null!));
        }
    }

    private static Type[] DiscoverServiceContracts()
    {
        Assembly[] assemblies =
        [
            typeof(ApiBootstrapper).Assembly,
            typeof(ApiResponse<>).Assembly,
            typeof(ArcanumDbContext).Assembly,
        ];

        ConcurrentBag<Type> contracts = [];

        foreach (Assembly assembly in assemblies)
        {
            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = [.. exception.Types.OfType<Type>()];
            }

            foreach (Type type in types)
            {
                if (type.IsGenericTypeDefinition
                    || type.IsNested
                    || type.IsEnum
                    || type.IsValueType
                    || (type.IsAbstract && type.IsSealed)
                    || ArcanumJsonContext.Default.GetTypeInfo(type) is not null
                    || (!type.IsInterface && type.IsAbstract))
                {
                    continue;
                }

                if (type.IsInterface || type.IsClass)
                {
                    contracts.Add(type);
                }
            }
        }

        return [.. contracts];
    }
}
