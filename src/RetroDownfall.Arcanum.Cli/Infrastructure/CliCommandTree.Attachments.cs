using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildAttachment(IServiceProvider serviceProvider)
    {

        AttachmentCommands handler = serviceProvider
            .GetRequiredService<AttachmentCommands>();

        Command attachment = new(
            "attachment",
            "Manage session attachment snapshots, live references, versions, pins, and exports.");

        Command list = new(
            "list",
            "List the latest version of each attachment in a session.");

        Argument<string?> listIdentifier = new("session")
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = "Optional session GUID, title, or unique title prefix.",

        };

        Option<string?> listSession = SessionOption();

        list.Add(listIdentifier);

        list.Add(listSession);

        list.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await handler
                .List(
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(listSession)
                        ?? parseResult.GetValue(listIdentifier)),
                    cancellationToken)
                .ConfigureAwait(false));

        attachment.Add(list);

        Command add = new(
            "add",
            "Create a snapshot from any local path, or use '-' to stream stdin.");

        Argument<string> addPath = new("path")
        {

            Description = "Local path to snapshot, or '-' for stdin.",

        };

        Option<string?> addMime = new("--mime")
        {

            Description = "Optional MIME type hint; the server remains authoritative.",

        };

        Option<string?> addName = new("--name")
        {

            Description = "Filename metadata, especially useful with stdin.",

        };

        Option<string?> addSession = SessionOption();

        add.Add(addPath);

        add.Add(addMime);

        add.Add(addName);

        add.Add(addSession);

        add.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await handler
                .Add(
                    parseResult.GetValue(addPath)!,
                    parseResult.GetValue(addMime),
                    parseResult.GetValue(addName),
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(addSession)),
                    cancellationToken)
                .ConfigureAwait(false));

        attachment.Add(add);

        Command reference = new(
            "reference",
            "Create a refreshable reference to a server workspace path.");

        Argument<string> referencePath = new("workspace-path")
        {

            Description = "Workspace-relative path interpreted only by the server host.",

        };

        Option<string?> referenceWorkspace = new("--workspace")
        {

            Description = "Registered workspace ID, name, or saved workspace path.",

        };

        Option<string?> referenceName = new("--name")
        {

            Description = "Optional logical attachment key.",

        };

        Option<string?> referenceSession = SessionOption();

        reference.Add(referencePath);

        reference.Add(referenceWorkspace);

        reference.Add(referenceName);

        reference.Add(referenceSession);

        reference.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await handler
                .Reference(
                    parseResult.GetValue(referencePath)!,
                    ActiveWorkspace(
                        serviceProvider,
                        parseResult.GetValue(referenceWorkspace)),
                    parseResult.GetValue(referenceName),
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(referenceSession)),
                    cancellationToken)
                .ConfigureAwait(false));

        attachment.Add(reference);

        Command show = new(
            "show",
            "Show attachment metadata, or use --privacy for the attachment privacy model.");

        Argument<string?> showIdentifier = AttachmentArgument();

        Option<bool> showPrivacy = new("--privacy")
        {

            Description = "Explain snapshot, reference, export, and terminal-byte privacy semantics.",

        };

        Option<string?> showSession = SessionOption();

        show.Add(showIdentifier);

        show.Add(showPrivacy);

        show.Add(showSession);

        show.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await handler
                .Show(
                    parseResult.GetValue(showIdentifier),
                    parseResult.GetValue(showPrivacy),
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(showSession)),
                    cancellationToken)
                .ConfigureAwait(false));

        attachment.Add(show);

        Command versions = new(
            "versions",
            "List every version for an attachment logical key.");

        Argument<string?> versionsIdentifier = AttachmentArgument();

        Option<string?> versionsSession = SessionOption();

        versions.Add(versionsIdentifier);

        versions.Add(versionsSession);

        versions.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await handler
                .Versions(
                    parseResult.GetValue(versionsIdentifier),
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(versionsSession)),
                    cancellationToken)
                .ConfigureAwait(false));

        attachment.Add(versions);

        AddAttachmentMutation(
            attachment,
            serviceProvider,
            "refresh",
            "Ask the server to refresh a live reference through the shared refresh service.",
            handler.Refresh);

        AddAttachmentMutation(
            attachment,
            serviceProvider,
            "pin",
            "Pin an attachment version into durable session context.",
            handler.Pin);

        AddAttachmentMutation(
            attachment,
            serviceProvider,
            "unpin",
            "Remove an attachment version from durable session context.",
            handler.Unpin);

        Command export = new(
            "export",
            "Export decrypted attachment content atomically to a local file.");

        Argument<string?> exportIdentifier = AttachmentArgument();

        Option<string?> exportOutput = new("--output", "-o")
        {

            Description = "Destination file. Attachment bytes are never written to stdout.",

        };

        Option<string?> exportSession = SessionOption();

        export.Add(exportIdentifier);

        export.Add(exportOutput);

        export.Add(exportSession);

        export.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await handler
                .Export(
                    parseResult.GetValue(exportIdentifier),
                    parseResult.GetValue(exportOutput),
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(exportSession)),
                    cancellationToken)
                .ConfigureAwait(false));

        attachment.Add(export);

        AddAttachmentMutation(
            attachment,
            serviceProvider,
            "reveal",
            "Reveal the encrypted stored snapshot artifact in the operating system file manager.",
            handler.Reveal);

        return attachment;

    }

    private static void AddAttachmentMutation(
        Command parent,
        IServiceProvider serviceProvider,
        string name,
        string description,
        Func<string?, string?, CancellationToken, Task<int>> action)
    {

        Command command = new(name, description);

        Argument<string?> identifier = AttachmentArgument();

        Option<string?> session = SessionOption();

        command.Add(identifier);

        command.Add(session);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
            await action(
                    parseResult.GetValue(identifier),
                    ActiveSession(
                        serviceProvider,
                        parseResult.GetValue(session)),
                    cancellationToken)
                .ConfigureAwait(false));

        parent.Add(command);

    }

    private static Argument<string?> AttachmentArgument() =>
        OptionalResourceArgument(
            "attachment",
            "attachment GUID, logical key, or unique logical-key prefix");

    private static Option<string?> SessionOption() =>
        new("--session")
        {

            Description = "Session GUID, title, or unique title prefix.",

        };

}
