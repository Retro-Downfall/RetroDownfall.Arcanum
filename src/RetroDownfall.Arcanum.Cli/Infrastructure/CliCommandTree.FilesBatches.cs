using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildFileCommands(IServiceProvider serviceProvider)
    {

        FileBatchCommands handler = serviceProvider
            .GetRequiredService<FileBatchCommands>();

        Command file = new(
            "file",
            "Upload, inspect, download, and delete OpenAI-compatible files.");

        Command upload = new(
            "upload",
            "Stream a local file to /v1/files.");

        Argument<string> uploadPath = new("path")
        {

            Description = "Local file path.",

        };

        Option<string?> purpose = new("--purpose")
        {

            Description = "OpenAI file purpose (default: batch).",

        };

        Option<string?> contentType = new("--content-type")
        {

            Description = "Declared MIME type; otherwise inferred conservatively from the extension.",

        };

        upload.Add(uploadPath);

        upload.Add(purpose);

        upload.Add(contentType);

        upload.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.UploadFile(
                    result.GetValue(uploadPath)!,
                    result.GetValue(purpose) ?? "batch",
                    result.GetValue(contentType),
                    cancellationToken).ConfigureAwait(false));

        Command list = new(
            "list",
            "List uploaded file metadata.");

        Option<string?> listPurpose = new("--purpose")
        {

            Description = "Filter by exact file purpose.",

        };

        list.Add(listPurpose);

        list.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.ListFiles(
                    result.GetValue(listPurpose),
                    cancellationToken).ConfigureAwait(false));

        Command show = new(
            "show",
            "Show one uploaded file's metadata.");

        Argument<string> showId = FileIdArgument();

        show.Add(showId);

        show.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.ShowFile(
                    result.GetValue(showId)!,
                    cancellationToken).ConfigureAwait(false));

        Command download = new(
            "download",
            "Stream a file to a safe local filename; existing files require confirmation.");

        Argument<string> downloadId = FileIdArgument();

        Option<string?> downloadOutput = OutputOption();

        download.Add(downloadId);

        download.Add(downloadOutput);

        download.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.DownloadFile(
                    result.GetValue(downloadId)!,
                    result.GetValue(downloadOutput),
                    cancellationToken).ConfigureAwait(false));

        Command delete = new(
            "delete",
            "Delete uploaded file metadata and content after confirmation.");

        Argument<string> deleteId = FileIdArgument();

        delete.Add(deleteId);

        delete.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.DeleteFile(
                    result.GetValue(deleteId)!,
                    cancellationToken).ConfigureAwait(false));

        file.Add(upload);

        file.Add(list);

        file.Add(show);

        file.Add(download);

        file.Add(delete);

        return file;

    }

    private static Command BuildBatchCommands(IServiceProvider serviceProvider)
    {

        FileBatchCommands handler = serviceProvider
            .GetRequiredService<FileBatchCommands>();

        Command batch = new(
            "batch",
            "Create and operate asynchronous OpenAI-compatible batches.");

        Command create = new(
            "create",
            "Create a batch from a local JSONL file or an existing uploaded file ID.");

        Argument<string> input = new("input-file")
        {

            Description = "Local JSONL path or file-* uploaded ID.",

        };

        create.Add(input);

        create.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.CreateBatch(
                    result.GetValue(input)!,
                    cancellationToken).ConfigureAwait(false));

        Command list = new(
            "list",
            "List batches with request counts and status.");

        Option<string?> status = new("--status")
        {

            Description = "Filter by exact batch status.",

        };

        Option<string?> cursor = new("--cursor")

        {

            Description = "Opaque continuation cursor returned by the previous batch-list page.",

        };

        list.Add(status);

        list.Add(cursor);

        list.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.ListBatches(
                    result.GetValue(status),
                    result.GetValue(cursor),
                    cancellationToken).ConfigureAwait(false));

        Command show = new(
            "show",
            "Show one batch with request counts and artifact IDs.");

        Argument<string> showId = BatchIdArgument();

        show.Add(showId);

        show.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.ShowBatch(
                    result.GetValue(showId)!,
                    cancellationToken).ConfigureAwait(false));

        // Deliberately not "watch": `watch <source>` is the live SSE-stream family, while this
        // polls a REST resource until it reaches a terminal state. Sharing the verb implied a
        // shared mechanism (and a shared --reconnect/--event-type contract) that does not exist.
        Command wait = new(
            "wait",
            "Poll with bounded exponential backoff until the batch reaches a terminal state.");

        Argument<string> waitId = BatchIdArgument();

        Option<int?> pollInterval = new("--poll-interval")
        {

            Description = "Initial poll interval in milliseconds (1-10000; default: 1000).",

        };

        wait.Add(waitId);

        wait.Add(pollInterval);

        wait.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.WatchBatch(
                    result.GetValue(waitId)!,
                    result.GetValue(pollInterval) ?? 1_000,
                    cancellationToken).ConfigureAwait(false));

        Command cancel = BatchMutationCommand(
            "cancel",
            "Request cancellation using the server's idempotent semantics.",
            handler.CancelBatch);

        Command reset = BatchMutationCommand(
            "reset",
            "Reset a server-classified stuck batch for retry.",
            handler.ResetBatch);

        Command output = BatchArtifactCommand(
            "output",
            "Download the batch output JSONL file.",
            handler.DownloadBatchOutput);

        Command errors = BatchArtifactCommand(
            "errors",
            "Download the batch error JSONL file.",
            handler.DownloadBatchErrors);

        batch.Add(create);

        batch.Add(list);

        batch.Add(show);

        batch.Add(wait);

        batch.Add(cancel);

        batch.Add(reset);

        batch.Add(output);

        batch.Add(errors);

        return batch;

    }

    private static Command BatchMutationCommand(
        string name,
        string description,
        Func<string, CancellationToken, Task<int>> action)
    {

        Command command = new(name, description);

        Argument<string> id = BatchIdArgument();

        command.Add(id);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await action(
                    result.GetValue(id)!,
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

    private static Command BatchArtifactCommand(
        string name,
        string description,
        Func<string, string?, CancellationToken, Task<int>> action)
    {

        Command command = new(name, description);

        Argument<string> id = BatchIdArgument();

        Option<string?> output = OutputOption();

        command.Add(id);

        command.Add(output);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await action(
                    result.GetValue(id)!,
                    result.GetValue(output),
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

    private static Argument<string> FileIdArgument() =>
        new("id")
        {

            Description = "OpenAI-compatible file-* ID.",

        };

    private static Argument<string> BatchIdArgument() =>
        new("id")
        {

            Description = "OpenAI-compatible batch_* ID.",

        };

    private static Option<string?> OutputOption() =>
        new("--output")
        {

            Description = "Explicit local destination path.",

        };

}
