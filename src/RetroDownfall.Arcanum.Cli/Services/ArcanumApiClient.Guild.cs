using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed partial class ArcanumApiClient
{
    internal Task<Result<T>> GetGuildAsync<T>(
        string path,
        JsonTypeInfo<ApiResponse<T>> responseType,
        CancellationToken cancellationToken) =>
        SendRequestAsync(HttpMethod.Get, path, null, null, responseType, cancellationToken);

    internal Task<Result<T>> PostGuildAsync<TRequest, T>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<ApiResponse<T>> responseType,
        CancellationToken cancellationToken) =>
        SendRequestAsync(HttpMethod.Post, path, JsonSerializer.SerializeToUtf8Bytes(request, requestType), JsonUtf8ContentType,
            responseType, cancellationToken);

    internal static string GuildQuery(string path, params (string Name, string? Value)[] values) => BuildQueryString(path, values);
}
