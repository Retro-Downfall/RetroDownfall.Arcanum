using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: startup-only security diagnostics.
public sealed class ArcanumSecurityStartupChecks(
    IOptions<ArcanumSettings> options,
    ILogger<ArcanumSecurityStartupChecks> logger) : IHostedService
{

    public Task StartAsync(CancellationToken cancellationToken)
    {

        SecureFilePermissions.RunStartupPermissionSelfCheck(logger);

        bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(options.Value.Host.ListenAny);

        if (listenAny)
        {

            logger.LogWarning(ListenAnySecurityPolicy.SecurityBanner);

        }

        return Task.CompletedTask;

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

}
