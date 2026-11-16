using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.LocalRuntimes;

/// <summary>Ollama model residency controls. Residency is not KV-cache persistence or a hard memory pin.</summary>
public sealed class OllamaRuntimeAdapter(IHttpClientFactory httpClientFactory, Func<string, string?>? readEnvironmentVariable = null,
    TimeSpan? requestTimeout = null) : ILocalRuntimeAdapter
{
    public const string HttpClientName = "OllamaRuntimeManagement";

    private const int ResponseLimitBytes = 1_048_576;

    private readonly Func<string, string?> _readEnvironmentVariable = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);

    public string AdapterId => LocalRuntimeAdapterCatalog.Ollama;

    public async Task<Result<LocalRuntimeInspection>> InspectAsync(LocalRuntimeSettings runtime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        runtime = runtime with { };

        if (!Valid(runtime)) return Result<LocalRuntimeInspection>.Failure(InvalidConfiguration());

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(_requestTimeout);

        try
        {
            return await InspectCoreAsync(runtime, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<LocalRuntimeInspection>.Failure(new(ErrorCodes.LocalRuntime.Timeout, "The runtime management request timed out."));
        }
    }

    private async Task<Result<LocalRuntimeInspection>> InspectCoreAsync(LocalRuntimeSettings runtime, CancellationToken cancellationToken)
    {
        Result<OllamaVersionResponse> version = await SendAsync(runtime, HttpMethod.Get, "api/version", null,
            OllamaRuntimeJsonContext.Default.OllamaVersionResponse, cancellationToken).ConfigureAwait(false);

        if (version.IsFailure) return Result<LocalRuntimeInspection>.Failure(version.Error);

        if (string.IsNullOrWhiteSpace(version.Value.Version)) return Result<LocalRuntimeInspection>.Failure(InvalidResponse());

        Result<OllamaLoadedModelsResponse> loaded = await SendAsync(runtime, HttpMethod.Get, "api/ps", null,
            OllamaRuntimeJsonContext.Default.OllamaLoadedModelsResponse, cancellationToken).ConfigureAwait(false);

        if (loaded.IsFailure) return Result<LocalRuntimeInspection>.Failure(loaded.Error);

        if (loaded.Value.Models is null || loaded.Value.Models.Any(static model => model is null || string.IsNullOrWhiteSpace(model.Name)))
            return Result<LocalRuntimeInspection>.Failure(InvalidResponse());

        LocalRuntimeLoadedModel[] models = loaded.Value.Models.Select(static model =>
            new LocalRuntimeLoadedModel(model.Name!, model.Digest, model.ExpiresAt, model.Size)).ToArray();

        return Result<LocalRuntimeInspection>.Success(new(version.Value.Version, new(true, false, false), Array.AsReadOnly(models)));
    }

    public async Task<Result> RetainModelAsync(LocalRuntimeSettings runtime, string model, TimeSpan retention, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        runtime = runtime with { };

        if (!Valid(runtime) || string.IsNullOrWhiteSpace(model)
            || (retention <= TimeSpan.Zero && retention != Timeout.InfiniteTimeSpan))
            return Result.Failure(InvalidConfiguration());

        // Empty native generate requests schedule/load the runner without evaluating a prompt.
        // Negative one means no idle expiry; Ollama can still evict under memory pressure.
        OllamaRetentionRequest body = new(model, false, retention == Timeout.InfiniteTimeSpan ? -1 : (decimal)retention.TotalSeconds);

        using HttpContent content = JsonContent.Create(body, OllamaRuntimeJsonContext.Default.OllamaRetentionRequest);

        Result<OllamaRetentionResponse> result = await SendAsync(runtime, HttpMethod.Post, "api/generate", content,
            OllamaRuntimeJsonContext.Default.OllamaRetentionResponse, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure) return Result.Failure(result.Error);

        return result.Value.Done && result.Value.DoneReason == "load" && string.Equals(result.Value.Model, model, StringComparison.Ordinal)
            ? Result.Success() : Result.Failure(InvalidResponse());
    }

    private static bool Valid(LocalRuntimeSettings runtime) => runtime.Adapter == LocalRuntimeAdapterCatalog.Ollama
        && LocalRuntimeAdapterCatalog.TryGetManagementUri(runtime, out _)
        && (string.IsNullOrWhiteSpace(runtime.CredentialEnvironmentVariable)
            || EnvironmentCredentialResolver.IsValidEnvironmentVariableName(runtime.CredentialEnvironmentVariable.Trim()));

    private async Task<Result<T>> SendAsync<T>(LocalRuntimeSettings runtime, HttpMethod method, string path, HttpContent? content,
        JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(_requestTimeout);

        CancellationToken operationToken = deadline.Token;

        try
        {
            Uri endpoint = new(runtime.ManagementEndpoint.TrimEnd('/') + "/" + path, UriKind.Absolute);

            using HttpRequestMessage request = new(method, endpoint) { Content = content };

            if (!string.IsNullOrWhiteSpace(runtime.CredentialEnvironmentVariable))
            {
                string? credential = _readEnvironmentVariable(runtime.CredentialEnvironmentVariable.Trim());

                if (string.IsNullOrWhiteSpace(credential))
                    return Result<T>.Failure(new(ErrorCodes.LocalRuntime.CredentialUnavailable, "The configured runtime management credential is unavailable."));

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }

            using HttpClient client = httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Result<T>.Failure(new(ErrorCodes.LocalRuntime.HttpFailure, "The runtime management request did not return a successful HTTP status."));

            if (response.Content.Headers.ContentLength > ResponseLimitBytes) return Result<T>.Failure(InvalidResponse());

            await using Stream stream = await response.Content.ReadAsStreamAsync(operationToken).ConfigureAwait(false);

            using MemoryStream bytes = new();

            byte[] buffer = new byte[8192];

            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), operationToken).ConfigureAwait(false);

                if (read == 0) break;

                if (bytes.Length + read > ResponseLimitBytes) return Result<T>.Failure(InvalidResponse());

                bytes.Write(buffer, 0, read);
            }

            T? value = JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), typeInfo);

            return value is null ? Result<T>.Failure(InvalidResponse()) : Result<T>.Success(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<T>.Failure(new(ErrorCodes.LocalRuntime.Timeout, "The runtime management request timed out."));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or FormatException)
        {
            return Result<T>.Failure(new(ErrorCodes.LocalRuntime.RequestFailed, "The runtime management request failed or returned an invalid response."));
        }
    }

    private static Error InvalidConfiguration() => new(ErrorCodes.LocalRuntime.InvalidConfiguration, "The runtime declaration, model or retention duration is invalid.");

    private static Error InvalidResponse() => new(ErrorCodes.LocalRuntime.InvalidResponse, "The runtime management response was incomplete, invalid or exceeded one MiB.");
}

internal sealed record OllamaVersionResponse(string? Version);

internal sealed record OllamaLoadedModelsResponse(OllamaLoadedModelResponse[]? Models);

internal sealed record OllamaLoadedModelResponse(string? Name, string? Digest, DateTimeOffset? ExpiresAt, long? Size);

internal sealed record OllamaRetentionRequest(string Model, bool Stream, decimal KeepAlive);

internal sealed record OllamaRetentionResponse(string? Model, bool Done, string? DoneReason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OllamaVersionResponse))]
[JsonSerializable(typeof(OllamaLoadedModelsResponse))]
[JsonSerializable(typeof(OllamaRetentionRequest))]
[JsonSerializable(typeof(OllamaRetentionResponse))]
internal sealed partial class OllamaRuntimeJsonContext : JsonSerializerContext;
