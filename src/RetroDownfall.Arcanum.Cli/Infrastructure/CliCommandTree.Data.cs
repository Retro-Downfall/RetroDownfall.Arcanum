using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildData(IServiceProvider serviceProvider)
    {

        DataEncryptionCommands encryptionHandler = serviceProvider
            .GetRequiredService<DataEncryptionCommands>();

        DataRetentionCommands retentionHandler = serviceProvider
            .GetRequiredService<DataRetentionCommands>();

        Command data = new(
            "data",
            "Inspect and maintain persisted Arcanum data.");

        AddDataLifecycleCommands(data, retentionHandler);

        Command encryption = new(
            "encryption",
            "Migrate, verify, and rotate authenticated encrypted blob storage.");

        Command encryptionStatus = new(
            "status",
            "Show encrypted, legacy, invalid, and remaining blob counts.");

        encryptionStatus.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await encryptionHandler
                    .Status(cancellationToken)
                    .ConfigureAwait(false));

        Command migrate = BuildWorkerCommand(
            "migrate",
            "Resumably encrypt every verified legacy plaintext blob.",
            encryptionHandler.Migrate);

        Command verify = BuildWorkerCommand(
            "verify",
            "Verify metadata, envelope authentication, plaintext length, and SHA-256.",
            encryptionHandler.Verify);

        Command rotate = BuildWorkerCommand(
            "rotate-key",
            "Create a new key, incrementally re-encrypt, verify, then retire unreferenced prior keys.",
            encryptionHandler.RotateKey);

        encryption.Add(encryptionStatus);

        encryption.Add(migrate);

        encryption.Add(verify);

        encryption.Add(rotate);

        data.Add(encryption);

        return data;

    }

    private static void AddDataLifecycleCommands(
        Command data,
        DataRetentionCommands handler)
    {

        Command status = new(
            "status",
            "Show retained rows, files, estimated bytes, policies, and provenance by data class.");

        status.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler
                    .Status(cancellationToken)
                    .ConfigureAwait(false));

        data.Add(status);

        Command retention = new(
            "retention",
            "Inspect or update a typed retention rule.");

        Command retentionShow = new(
            "show",
            "Show the effective retention policy.");

        retentionShow.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler
                    .RetentionShow(cancellationToken)
                    .ConfigureAwait(false));

        Command retentionSet = new(
            "set",
            "Set one retention class to a number of days or disable it.");

        Argument<string> retentionClass = new("class")
        {

            Description = "Retention class, for example archived-sessions.",

        };

        Argument<string> retentionValue = new("days|disabled")
        {

            Description = "Integer retention days, or 'disabled'.",

        };

        retentionSet.Add(retentionClass);

        retentionSet.Add(retentionValue);

        retentionSet.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .RetentionSet(
                        result.GetValue(retentionClass)!,
                        result.GetValue(retentionValue)!,
                        cancellationToken)
                    .ConfigureAwait(false));

        retention.Add(retentionShow);

        retention.Add(retentionSet);

        data.Add(retention);

        Command prune = new(
            "prune",
            "Preview or apply the current retention policy.");

        Option<bool> dryRun = new("--dry-run")
        {

            Description = "Return the deletion plan without mutating data.",

        };

        Option<bool> apply = new("--apply")
        {

            Description = "Apply the retention policy after confirmation.",

        };

        prune.Add(dryRun);

        prune.Add(apply);

        prune.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .Prune(
                        result.GetValue(dryRun),
                        result.GetValue(apply),
                        cancellationToken)
                    .ConfigureAwait(false));

        data.Add(prune);

        Command deleteSession = new(
            "delete-session",
            "Delete a session and its session-scoped dependents after confirmation.");

        Argument<Guid> sessionId = new("id")
        {

            Description = "Session ID.",

        };

        deleteSession.Add(sessionId);

        deleteSession.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .DeleteSession(
                        result.GetValue(sessionId),
                        cancellationToken)
                    .ConfigureAwait(false));

        data.Add(deleteSession);

        Command deleteAttachment = new(
            "delete-attachment",
            "Delete an attachment version, bytes, chunks, and embeddings after confirmation.");

        Argument<Guid> attachmentId = new("id")
        {

            Description = "Attachment ID.",

        };

        deleteAttachment.Add(attachmentId);

        deleteAttachment.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .DeleteAttachment(
                        result.GetValue(attachmentId),
                        cancellationToken)
                    .ConfigureAwait(false));

        data.Add(deleteAttachment);

        Command resetMemory = new(
            "reset-memory",
            "Reset one explicit memory scope after confirmation.");

        Option<string> memoryScope = new("--scope")
        {

            Description = "Required scope: entry, attachments, workspace, saga, or lexicon.",

            Required = true,

        };

        resetMemory.Add(memoryScope);

        resetMemory.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .ResetMemory(
                        result.GetValue(memoryScope)!,
                        cancellationToken)
                    .ConfigureAwait(false));

        data.Add(resetMemory);

        Command factoryReset = new(
            "factory-reset",
            "Reset data under the configured root; backups and data outside that root remain.");

        factoryReset.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler
                    .FactoryReset(cancellationToken)
                    .ConfigureAwait(false));

        data.Add(factoryReset);

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

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
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
