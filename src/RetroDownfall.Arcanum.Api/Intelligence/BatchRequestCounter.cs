using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Computes <see cref="OpenAiBatchRequestCounts"/> for a batch by reading its input/output/error
/// files directly off disk (there are no dedicated count columns on <c>Batches</c> — see
/// DESIGN.md §11.21). Best-effort: a missing or unreadable file contributes <c>0</c> rather than
/// throwing, since a client polling <c>GET /v1/batches/{id}</c> mid-processing should never see an
/// error from this alone.
/// </summary>
internal static class BatchRequestCounter
{

    public static async Task<OpenAiBatchRequestCounts> ComputeAsync(BatchRecord record, CancellationToken cancellationToken)
    {

        int total = await CountNonEmptyLinesAsync(UploadedFileStorage.ResolvePath(record.InputFileId), cancellationToken).ConfigureAwait(false);

        int completed = 0;

        int failed = 0;

        if (record.OutputFileId is { } outputFileId)
        {

            (int outputCompleted, int outputFailed) = await CountOutputOutcomesAsync(UploadedFileStorage.ResolvePath(outputFileId), cancellationToken).ConfigureAwait(false);

            completed += outputCompleted;

            failed += outputFailed;

        }

        if (record.ErrorFileId is { } errorFileId)
        {

            failed += await CountNonEmptyLinesAsync(UploadedFileStorage.ResolvePath(errorFileId), cancellationToken).ConfigureAwait(false);

        }

        return new OpenAiBatchRequestCounts(total, completed, failed);

    }

    private static async Task<int> CountNonEmptyLinesAsync(string path, CancellationToken cancellationToken)
    {

        if (!File.Exists(path))
        {

            return 0;

        }

        try
        {

            int count = 0;

            using StreamReader reader = new(path);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {

                if (!string.IsNullOrWhiteSpace(line))
                {

                    count++;

                }

            }

            return count;

        }
        catch (IOException)
        {

            return 0;

        }

    }

    private static async Task<(int Completed, int Failed)> CountOutputOutcomesAsync(string path, CancellationToken cancellationToken)
    {

        if (!File.Exists(path))
        {

            return (0, 0);

        }

        int completed = 0;

        int failed = 0;

        try
        {

            using StreamReader reader = new(path);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {

                if (string.IsNullOrWhiteSpace(line))
                {

                    continue;

                }

                BatchJsonlResponseLine? parsed;

                try
                {

                    parsed = System.Text.Json.JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.BatchJsonlResponseLine);

                }
                catch (System.Text.Json.JsonException)
                {

                    failed++;

                    continue;

                }

                if (parsed?.Error is not null)
                {

                    failed++;

                }
                else
                {

                    completed++;

                }

            }

        }
        catch (IOException)
        {

            return (completed, failed);

        }

        return (completed, failed);

    }

}
