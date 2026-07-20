using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// One-shot startup GC for stale pending session attachments (rows + <c>_pending/{turnId}</c> directories).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService bootstrap; store GC covered by SessionAttachmentStoreTests.
public sealed class SessionAttachmentPendingGcHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ArcanumSettings> options,
    ILogger<SessionAttachmentPendingGcHostedService> logger) : IHostedService
{

    public async Task StartAsync(CancellationToken cancellationToken)
    {

        AttachmentsSettings attachments = options.Value.Attachments ?? new AttachmentsSettings();

        int retentionHours = ArcanumSettingClamps.AttachmentsPendingRetentionHours(attachments.PendingRetentionHours);

        TimeSpan olderThan = TimeSpan.FromHours(retentionHours);

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            await store.DeleteStalePendingAsync(olderThan, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Session attachment pending GC completed (retention {RetentionHours}h).",
                retentionHours);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "Session attachment pending GC failed; continuing startup.");

        }

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

}
