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

        return backup;

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
