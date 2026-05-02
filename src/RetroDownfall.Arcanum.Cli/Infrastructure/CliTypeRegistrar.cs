using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal sealed class CliTypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() => new CliTypeResolver(services.BuildServiceProvider());

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2067",
        Justification = "Spectre may call Register for discovered types; all Arcanum services are pre-registered in Program with concrete types.")]

    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());
}

internal sealed class CliTypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) => type is null ? null : provider.GetRequiredService(type);

    public void Dispose() => (provider as IDisposable)?.Dispose();
}
