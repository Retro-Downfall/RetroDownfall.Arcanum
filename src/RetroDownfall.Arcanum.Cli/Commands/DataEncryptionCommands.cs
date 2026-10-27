using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class DataEncryptionCommands(IGrimoireCliInitialization initialization)
{
    public async Task<int> Status(CancellationToken cancellationToken)
    {
        BlobEncryptionStatus status = await initialization
            .RunExclusiveWithBootstrapAsync(
                async (services, token) =>
                {
                    IBlobEncryptionLifecycleService lifecycle = services
                        .GetRequiredService<IBlobEncryptionLifecycleService>();

                    return await lifecycle
                        .GetStatusAsync(token)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("State");
        table.AddColumn("Files");
        table.AddColumn("Bytes");
        table.AddRow("Encrypted", status.EncryptedFiles.ToString(), FormatBytes(status.EncryptedBytes));
        table.AddRow(
            "Legacy plaintext",
            status.LegacyPlaintextFiles.ToString(),
            FormatBytes(status.LegacyPlaintextBytes));
        table.AddRow("Reconciliation", status.FilesNeedingReconciliation.ToString(), "-");
        table.AddRow("Invalid/excluded", status.InvalidFiles.ToString(), "-");
        table.AddRow("Total", status.TotalFiles.ToString(), FormatBytes(status.TotalBytes));
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
        BlobEncryptionOperationResult result = await initialization
            .RunExclusiveWithBootstrapAsync(
                async (services, token) =>
                {
                    IBlobEncryptionLifecycleService lifecycle = services
                        .GetRequiredService<IBlobEncryptionLifecycleService>();

                    return await run(lifecycle, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"Processed [cyan]{result.ProcessedFiles}[/] files / [cyan]{FormatBytes(result.ProcessedBytes)}[/] bytes; "
            + $"remaining [yellow]{result.RemainingFiles}[/] files / [yellow]{FormatBytes(result.RemainingBytes)}[/] bytes; "
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

    private static string FormatBytes(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
}
