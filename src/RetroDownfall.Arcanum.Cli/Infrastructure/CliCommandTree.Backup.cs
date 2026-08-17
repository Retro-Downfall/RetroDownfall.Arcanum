using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildBackup(IServiceProvider serviceProvider)
    {

        Command backup = new(
            "backup",
            "Create and validate encrypted, portable Arcanum backups.");

        Command create = new(
            "create",
            "Plan or create an encrypted, authenticated backup archive.");

        Option<string?> scope = new("--scope")
        {

            Description = "Typed scope: " + BackupCliCatalog.ScopeHelp + ". Default: full.",

        };

        Option<Guid?> sessionId = new("--session-id")
        {

            Description = "Session ID required only for --scope specific-session.",

        };

        Option<string[]> include = new("--include")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Typed component to include; repeat or provide several. Values: "
                + BackupCliCatalog.ComponentHelp + ".",

        };

        Option<string[]> exclude = new("--exclude")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Typed component to exclude; repeat or provide several. Values: "
                + BackupCliCatalog.ComponentHelp + ".",

        };

        Option<string?> output = new("--output", "-o")
        {

            Description = "Destination .arcbackup path; defaults to Arcanum's backup directory.",

        };

        Option<bool> dryRun = new("--dry-run")
        {

            Description = "Show the shared inventory plan without reading a passphrase or writing an archive.",

        };

        Option<bool> overwrite = new("--overwrite")
        {

            Description = "Explicitly allow atomic replacement of an existing output archive.",

        };

        Option<string?> createPassphraseEnvironment = PassphraseEnvironmentOption();

        Option<int?> createPassphraseFileDescriptor = PassphraseFileDescriptorOption();

        create.Add(scope);

        create.Add(sessionId);

        create.Add(include);

        create.Add(exclude);

        create.Add(output);

        create.Add(dryRun);

        create.Add(overwrite);

        create.Add(createPassphraseEnvironment);

        create.Add(createPassphraseFileDescriptor);

        create.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await serviceProvider
                    .GetRequiredService<BackupCommands>()
                    .Create(
                        result.GetValue(scope),
                        result.GetValue(sessionId),
                        result.GetValue(include) ?? [],
                        result.GetValue(exclude) ?? [],
                        result.GetValue(output),
                        result.GetValue(dryRun),
                        result.GetValue(overwrite),
                        result.GetValue(createPassphraseEnvironment),
                        result.GetValue(createPassphraseFileDescriptor),
                        cancellationToken)
                    .ConfigureAwait(false));

        Argument<string> inspectArchive = ArchiveArgument();

        Command inspect = new(
            "inspect",
            "Show safe outer metadata, or decrypt the manifest when explicitly requested.");

        Option<bool> decrypt = new("--decrypt")
        {

            Description = "Decrypt and show manifest metadata; prompts securely when no explicit source is provided.",

        };

        Option<string?> inspectPassphraseEnvironment = PassphraseEnvironmentOption();

        Option<int?> inspectPassphraseFileDescriptor = PassphraseFileDescriptorOption();

        inspect.Add(inspectArchive);

        inspect.Add(decrypt);

        inspect.Add(inspectPassphraseEnvironment);

        inspect.Add(inspectPassphraseFileDescriptor);

        inspect.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await serviceProvider
                    .GetRequiredService<BackupCommands>()
                    .Inspect(
                        result.GetValue(inspectArchive)!,
                        result.GetValue(decrypt),
                        result.GetValue(inspectPassphraseEnvironment),
                        result.GetValue(inspectPassphraseFileDescriptor),
                        cancellationToken)
                    .ConfigureAwait(false));

        Argument<string> verifyArchive = ArchiveArgument();

        Command verify = new(
            "verify",
            "Authenticate and verify every archive entry and database readability.");

        Option<string?> verifyPassphraseEnvironment = PassphraseEnvironmentOption();

        Option<int?> verifyPassphraseFileDescriptor = PassphraseFileDescriptorOption();

        verify.Add(verifyArchive);

        verify.Add(verifyPassphraseEnvironment);

        verify.Add(verifyPassphraseFileDescriptor);

        verify.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await serviceProvider
                    .GetRequiredService<BackupCommands>()
                    .Verify(
                        result.GetValue(verifyArchive)!,
                        result.GetValue(verifyPassphraseEnvironment),
                        result.GetValue(verifyPassphraseFileDescriptor),
                        cancellationToken)
                    .ConfigureAwait(false));

        Command list = new(
            "list",
            "List backup archive headers without decrypting their manifests.");

        Option<string?> directory = new("--directory")
        {

            Description = "Directory to scan; defaults to Arcanum's backup directory.",

        };

        list.Add(directory);

        list.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await serviceProvider
                    .GetRequiredService<BackupCommands>()
                    .List(
                        result.GetValue(directory),
                        cancellationToken)
                    .ConfigureAwait(false));

        backup.Add(create);

        backup.Add(inspect);

        backup.Add(verify);

        backup.Add(list);

        backup.Add(BuildRestore(serviceProvider));

        backup.Add(BuildMigrate(serviceProvider));

        return backup;

    }

    private static Command BuildRestore(IServiceProvider serviceProvider)
    {

        Command restore = new(
            "restore",
            "Verify an archive completely, then commit it atomically or leave this installation unchanged.");

        Argument<string> archive = ArchiveArgument();

        Option<string?> conflictMode = new("--conflict-mode")
        {

            Description = "Typed mode: " + BackupCliCatalog.ConflictModeHelp
                + ". Default: replace-installation.",

        };

        Option<string?> destination = new("--destination")
        {

            Description = "Empty or absent profile root; required by --conflict-mode new-profile-root.",

        };

        Option<Guid[]> sessions = new("--session-id")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Session to import; repeat or provide several. "
                + "Required by --conflict-mode import-selected-sessions.",

        };

        Option<string[]> map = new("--map")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Typed root rewrite as <kind>=<from>=<to>. Kinds: "
                + BackupCliCatalog.MappingKindHelp + ".",

        };

        Option<string[]> mapCampaign = new("--map-campaign")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Explicit Campaign binding as <source-campaign-id>=<destination-campaign-id>. "
                + "Repeat or provide several; --conflict-mode import-selected-sessions needs one for "
                + "every Campaign-bound Session.",

        };

        Option<string?> protectedState = new("--protected-state")
        {

            Description = "What this restore may do with the protected state the archive carries: "
                + BackupCliCatalog.ProtectedStateModeHelp + ". Default: reject.",

        };

        Option<bool> masterApiKey = new("--restore-master-api-key")
        {

            Description = "Adopt the archived master API key on this machine. Off by default.",

        };

        Option<bool> dryRun = new("--dry-run")
        {

            Description = "Authenticate, validate, and plan everything without mutating the destination.",

        };

        Option<bool> skipSafetyBackup = new("--no-safety-backup")
        {

            Description = "Skip the pre-restore safety backup; the decision is recorded in the result.",

        };

        Option<string?> passphraseEnvironment = PassphraseEnvironmentOption();

        Option<int?> passphraseFileDescriptor = PassphraseFileDescriptorOption();

        restore.Add(archive);

        restore.Add(conflictMode);

        restore.Add(destination);

        restore.Add(sessions);

        restore.Add(map);

        restore.Add(mapCampaign);

        restore.Add(protectedState);

        restore.Add(masterApiKey);

        restore.Add(dryRun);

        restore.Add(skipSafetyBackup);

        restore.Add(passphraseEnvironment);

        restore.Add(passphraseFileDescriptor);

        restore.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await serviceProvider
                    .GetRequiredService<BackupCommands>()
                    .Restore(
                        result.GetValue(archive)!,
                        result.GetValue(conflictMode),
                        result.GetValue(destination),
                        result.GetValue(sessions) ?? [],
                        result.GetValue(map) ?? [],
                        result.GetValue(mapCampaign) ?? [],
                        result.GetValue(protectedState),
                        result.GetValue(masterApiKey),
                        result.GetValue(dryRun),
                        result.GetValue(skipSafetyBackup),
                        result.GetValue(passphraseEnvironment),
                        result.GetValue(passphraseFileDescriptor),
                        cancellationToken)
                    .ConfigureAwait(false));

        return restore;

    }

    private static Command BuildMigrate(IServiceProvider serviceProvider)
    {

        Command migrate = new(
            "migrate",
            "Rewrite a supported archive at the current format; the source archive is never modified.");

        Argument<string> archive = ArchiveArgument();

        Option<string?> output = new("--output", "-o")
        {

            Description = "Required destination .arcbackup path for the migrated archive.",

        };

        Option<bool> overwrite = new("--overwrite")
        {

            Description = "Explicitly allow replacing an existing migrated archive.",

        };

        Option<string?> passphraseEnvironment = PassphraseEnvironmentOption();

        Option<int?> passphraseFileDescriptor = PassphraseFileDescriptorOption();

        migrate.Add(archive);

        migrate.Add(output);

        migrate.Add(overwrite);

        migrate.Add(passphraseEnvironment);

        migrate.Add(passphraseFileDescriptor);

        migrate.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await serviceProvider
                    .GetRequiredService<BackupCommands>()
                    .Migrate(
                        result.GetValue(archive)!,
                        result.GetValue(output),
                        result.GetValue(overwrite),
                        result.GetValue(passphraseEnvironment),
                        result.GetValue(passphraseFileDescriptor),
                        cancellationToken)
                    .ConfigureAwait(false));

        return migrate;

    }

    private static Argument<string> ArchiveArgument() =>
        new("archive")
        {

            Description = "Path to a .arcbackup archive.",

        };

    private static Option<string?> PassphraseEnvironmentOption() =>
        new("--passphrase-env")
        {

            Description = "Read the passphrase from the named environment variable.",

        };

    private static Option<int?> PassphraseFileDescriptorOption() =>
        new("--passphrase-fd")
        {

            Description = "Read one UTF-8 passphrase line from an inherited file descriptor; 0 is supported.",

        };

}
