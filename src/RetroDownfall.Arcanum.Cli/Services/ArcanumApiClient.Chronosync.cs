using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed partial class ArcanumApiClient
{

    public Task<Result<ChronosyncReport>> SynchronizePatternAsync(
        PatternSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            ArcanumJsonContext.Default.PatternSnapshot);

        string idempotencyKey = $"chronosync-{Guid.NewGuid():N}";

        return SendRequestAsync(
            HttpMethod.Post,
            "api/perception/chronosync",
            json,
            JsonUtf8ContentType,
            ArcanumJsonContext.Default.ApiResponseChronosyncReport,
            static envelope => envelope.Data is null
                ? Result<ChronosyncReport>.Failure(
                    new Error(
                        "Api.InvalidResponse",
                        "Chronosync payload was empty."))
                : Result<ChronosyncReport>.Success(envelope.Data),
            cancellationToken,
            idempotencyKey: idempotencyKey,
            retryResponseBodyIOExceptionOnce: true);

    }

}
