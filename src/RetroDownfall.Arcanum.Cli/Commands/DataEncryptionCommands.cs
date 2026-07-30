using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class DataEncryptionCommands(
    IGrimoireCliInitialization initialization,
    IServiceScopeFactory scopeFactory)
{
    public async Task<int> Status(CancellationToken cancellationToken)
    {
        await initialization.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IBlobEncryptionLifecycleService lifecycle =
            scope.ServiceProvider.GetRequiredService<IBlobEncryptionLifecycleService>();
        BlobEncryptionStatus status = await lifecycle
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("State");
        table.AddColumn("Files");
        table.AddColumn("Bytes");
        table.AddRow("Encrypted", status.EncryptedFiles.ToString(), status.EncryptedBytes.ToString());
        table.AddRow(
            "Legacy plaintext",
            status.LegacyPlaintextFiles.ToString(),
            status.LegacyPlaintextBytes.ToString());
        table.AddRow("Reconciliation", status.FilesNeedingReconciliation.ToString(), "-");
        table.AddRow("Invalid/excluded", status.InvalidFiles.ToString(), "-");
        table.AddRow("Total", status.TotalFiles.ToString(), status.TotalBytes.ToString());
        AnsiConsole.Write(table);
        return status.InvalidFiles == 0 ? 0 : 1;
    }

    public Task<int> Migrate(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken) =>
        Run(
            (service, token) => service.MigrateAsync(
                maxConcurrency,
                maxBytesPerSecond,
                token),
            cancellationToken);

    public Task<int> Verify(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken) =>
        Run(
            (service, token) => service.VerifyAsync(
                maxConcurrency,
                maxBytesPerSecond,
                token),
            cancellationToken);

    public Task<int> RotateKey(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken) =>
        Run(
            (service, token) => service.RotateKeyAsync(
                maxConcurrency,
                maxBytesPerSecond,
                token),
            cancellationToken);

    private async Task<int> Run(
        Func<IBlobEncryptionLifecycleService, CancellationToken, Task<BlobEncryptionOperationResult>> run,
        CancellationToken cancellationToken)
    {
        await initialization.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IBlobEncryptionLifecycleService lifecycle =
            scope.ServiceProvider.GetRequiredService<IBlobEncryptionLifecycleService>();
        BlobEncryptionOperationResult result = await run(lifecycle, cancellationToken)
            .ConfigureAwait(false);
        AnsiConsole.MarkupLine(
            $"Processed [cyan]{result.ProcessedFiles}[/] files / [cyan]{result.ProcessedBytes}[/] bytes; "
            + $"remaining [yellow]{result.RemainingFiles}[/] files / [yellow]{result.RemainingBytes}[/] bytes; "
            + $"excluded [red]{result.FailedFiles}[/].");
        if (result.OperationId != Guid.Empty)
        {
            AnsiConsole.MarkupLine($"Durable operation: [grey]{result.OperationId:D}[/]");
        }

        foreach ((BlobEncryptionVerificationIssue issue, int count) in result.Issues
                     .OrderBy(static pair => pair.Key))
        {
            AnsiConsole.MarkupLine($"[yellow]{issue}[/]: {count}");
        }

        return result.RemainingFiles == 0 && result.FailedFiles == 0 ? 0 : 1;
    }
}
