using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildData(IServiceProvider serviceProvider)
    {
        DataEncryptionCommands handler =
            serviceProvider.GetRequiredService<DataEncryptionCommands>();
        Command data = new("data", "Inspect and maintain persisted Arcanum data.");
        Command encryption = new(
            "encryption",
            "Migrate, verify, and rotate authenticated encrypted blob storage.");

        Command status = new("status", "Show encrypted, legacy, invalid, and remaining blob counts.");
        status.SetAction(async (ParseResult _, CancellationToken cancellationToken) =>
            await handler.Status(cancellationToken).ConfigureAwait(false));

        Command migrate = BuildWorkerCommand(
            "migrate",
            "Resumably encrypt every verified legacy plaintext blob.",
            handler.Migrate);
        Command verify = BuildWorkerCommand(
            "verify",
            "Verify metadata, envelope authentication, plaintext length, and SHA-256.",
            handler.Verify);
        Command rotate = BuildWorkerCommand(
            "rotate-key",
            "Create a new key, incrementally re-encrypt, verify, then retire unreferenced prior keys.",
            handler.RotateKey);

        encryption.Add(status);
        encryption.Add(migrate);
        encryption.Add(verify);
        encryption.Add(rotate);
        data.Add(encryption);
        return data;
    }

    private static Command BuildWorkerCommand(
        string name,
        string description,
        Func<int, long, CancellationToken, Task<int>> action)
    {
        Command command = new(name, description);
        Option<int> concurrency = new("--max-concurrency")
        {
            Description = "Bounded worker count (1-8; default 2).",
        };
        Option<long> bytesPerSecond = new("--max-bytes-per-second")
        {
            Description = "Aggregate I/O throttle in bytes/second (default 67108864).",
        };
        command.Add(concurrency);
        command.Add(bytesPerSecond);
        command.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await action(
                    NormalizeConcurrency(result.GetValue(concurrency)),
                    NormalizeRate(result.GetValue(bytesPerSecond)),
                    cancellationToken)
                .ConfigureAwait(false));
        return command;
    }

    private static int NormalizeConcurrency(int value) =>
        value <= 0 ? 2 : Math.Clamp(value, 1, 8);

    private static long NormalizeRate(long value) =>
        value <= 0 ? 64L * 1024 * 1024 : value;
}
