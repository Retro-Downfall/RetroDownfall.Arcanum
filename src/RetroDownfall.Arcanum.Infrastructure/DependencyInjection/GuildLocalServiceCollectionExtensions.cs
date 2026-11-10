using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

internal static class GuildLocalServiceCollectionExtensions
{
    internal static void AddGuildLocalExecution(this IServiceCollection services)
    {
        services.AddSingleton(static sp => new GuildInboundPeerDenialJournal(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "guild-peer-denials"),
            sp.GetRequiredService<IGrimoireConnectionAdmissionGate>()));

        services.AddSingleton<GuildInboundRequestContext>();

        services.AddScoped<GuildInboundHost>();

        services.AddScoped<GuildConfiguredRemoteResolver>();

        services.AddScoped<GuildRemoteCallbackReceiver>();

        services.AddSingleton<GuildLocalHostPolicy>();

        services.AddSingleton<IGuildQuestHostPolicy>(static sp => sp.GetRequiredService<GuildLocalHostPolicy>());

        services.AddSingleton<IGuildSignetHostPolicy>(static sp => sp.GetRequiredService<GuildLocalHostPolicy>());

        services.AddSingleton<GuildSignetStore>();

        services.AddSingleton<SignetAuthority>();

        services.AddSingleton<ISignetAuthority>(static sp => sp.GetRequiredService<SignetAuthority>());

        services.AddSingleton<IGuildToolAuthority>(static sp => sp.GetRequiredService<SignetAuthority>());

        services.AddSingleton(static sp => new GuildLocalHostReadiness(
            sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
            sp.GetRequiredService<IGrimoireOrdinaryConnectionFactory>(),
            sp.GetRequiredService<GuildLocalHostPolicy>(),
            sp.GetRequiredService<SignetAuthority>(),
            sp.GetRequiredService<TimeProvider>(),
            ArcanumPaths.GrimoireDatabaseFile + ".guild-mutation.lock",
            sp.GetRequiredService<IGrimoireConnectionAdmissionGate>()));

        services.AddSingleton<IGuildLocalHostReadiness>(static sp => sp.GetRequiredService<GuildLocalHostReadiness>());

        // Install physical protection before any hosted component can start opaque process work.
        services.AddInstallationResetRecoveryAwareHostedService<GuildLocalHostReadiness>();

        services.AddInstallationResetRecoveryAwareHostedService<GuildInboundPeerReloadService>();

        services.AddScoped(static sp => new GuildBudgetLedger(
            sp.GetRequiredService<IBudgetReservationService>() as BudgetReservationService
                ?? throw new InvalidOperationException("Guild execution requires the physical daily budget participant.")));

        services.AddScoped<IGuildQuestAllocationParticipant>(static sp => sp.GetRequiredService<GuildBudgetLedger>());

        services.AddScoped<IGuildLocalExecutionAdmission, GuildLocalExecutionAdmission>();

        services.AddScoped<GuildQuestStore>();

        services.AddScoped<GuildPortalStore>();

        services.AddScoped<GuildNativeHost>();

        services.AddScoped<GuildLocalPort>();

        services.AddScoped<GuildRemotePort>();

        services.AddScoped<GuildPortRouter>();

        services.AddScoped<IGuildPort>(static sp => sp.GetRequiredService<GuildPortRouter>());

        services.AddScoped<ConclaveArchmage>();

        services.AddScoped<IGuildQuestCoordinator>(static sp => sp.GetRequiredService<ConclaveArchmage>());

        services.AddScoped<IGuildLocalChildToolDispatcher>(static sp => sp.GetRequiredService<ConclaveArchmage>());

        services.AddSingleton<IGuildLocalExecutionEngine>(static sp => sp.GetRequiredService<ApprenticeService>());

        services.AddSingleton<GuildLocalQuestScheduler>();

        services.AddSingleton<IGuildLocalQuestScheduler>(static sp => sp.GetRequiredService<GuildLocalQuestScheduler>());
    }
}
