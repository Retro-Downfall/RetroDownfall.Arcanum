using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed class ArcanumApiClient(IHttpClientFactory httpClientFactory, ISecretStore secretStore)
{
    public async Task<Result<string>> AskAsync(PingRequest body, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<string>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.PingRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/ping");
        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<PromptResponseDto>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<string>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<string>.Failure(err);
                }

                return Result<string>.Success(envelope.Data?.Text ?? string.Empty);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<string>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<string>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<string>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> SubmitHumanResponseAsync(
        string promptId,
        string answer,
        CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        SubmitHumanResponseRequest body = new(promptId, answer);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.SubmitHumanResponseRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/human-response");
        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<bool>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<bool>.Failure(err);
                }

                if (envelope.Data != true)
                {
                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Expected human-response success envelope with data true."));
                }

                return Result<bool>.Success(true);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(
                    new Error("Intelligence.HumanPromptNotFound", "No active ask_human prompt matches that promptId."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<bool>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<bool>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<bool>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<bool>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<string>> ReloadMcpAsync(OptionalWorkspaceRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<string>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.OptionalWorkspaceRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/mcp/reload");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<string>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<string>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<string>.Failure(err);
                }

                return Result<string>.Success(envelope.Data ?? string.Empty);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<string>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<string>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<string>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<WorkspaceArsenalDto>> GetWorkspaceArsenalAsync(OptionalWorkspaceRequest request, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<WorkspaceArsenalDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, ArcanumJsonContext.Default.OptionalWorkspaceRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/intelligence/arsenal");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<WorkspaceArsenalDto>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseWorkspaceArsenalDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<WorkspaceArsenalDto>.Failure(new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<WorkspaceArsenalDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<WorkspaceArsenalDto>.Failure(new Error("Api.InvalidResponse", "Arsenal payload was empty."));
                }

                return Result<WorkspaceArsenalDto>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<WorkspaceArsenalDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<WorkspaceArsenalDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<WorkspaceArsenalDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<WorkspaceArsenalDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<PatternSnapshot>> PerceivePatternAsync(string directory, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<PatternSnapshot>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        string encoded = Uri.EscapeDataString(directory);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/perception/look?directory={encoded}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<PatternSnapshot>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponsePatternSnapshot);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<PatternSnapshot>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<PatternSnapshot>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<PatternSnapshot>.Failure(
                        new Error("Api.InvalidResponse", "Perception payload was empty."));
                }

                return Result<PatternSnapshot>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<PatternSnapshot>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<PatternSnapshot>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<PatternSnapshot>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<PatternSnapshot>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<List<ConversationSummaryDto>>> GetConversationsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<List<ConversationSummaryDto>>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        int clamped = Math.Clamp(take, 1, 200);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/conversations?take={clamped}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<List<ConversationSummaryDto>>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(
                    responseBytes,
                    ArcanumJsonContext.Default.ApiResponseListConversationSummaryDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<List<ConversationSummaryDto>>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<List<ConversationSummaryDto>>.Failure(err);
                }

                return Result<List<ConversationSummaryDto>>.Success(envelope.Data ?? []);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<List<ConversationSummaryDto>>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<List<ConversationSummaryDto>>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<List<ConversationSummaryDto>>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<List<ConversationSummaryDto>>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result> RestAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, $"api/conversations/{conversationId:D}/rest");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? boolEnvelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                if (boolEnvelope is { IsSuccess: true, Data: true })
                {
                    return Result.Success();
                }

                if (boolEnvelope is not null && boolEnvelope is { IsSuccess: false, Error: not null })
                {
                    return Result.Failure(boolEnvelope.Error.Value);
                }

                return Result.Failure(
                    new Error("Api.InvalidResponse", "Expected ApiResponse JSON body on 202 Accepted."));
            }

            if ((int)response.StatusCode == 404)
            {
                if (boolEnvelope is not null && boolEnvelope is { IsSuccess: false, Error: not null })
                {
                    return Result.Failure(boolEnvelope.Error.Value);
                }

                return Result.Failure(
                    new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));
            }

            ApiResponse<string>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<ConversationDetailDto>> GetConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<ConversationDetailDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/conversations/{id:D}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<ConversationDetailDto>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseConversationDetailDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<ConversationDetailDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<ConversationDetailDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<ConversationDetailDto>.Failure(
                        new Error("Api.InvalidResponse", "Conversation payload was empty."));
                }

                return Result<ConversationDetailDto>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<ConversationDetailDto>.Failure(envelope.Error.Value);
                }

                return Result<ConversationDetailDto>.Failure(
                    new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<ConversationDetailDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<ConversationDetailDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<ConversationDetailDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<ConversationDetailDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<List<ConversationMessageDto>>> GetConversationMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<List<ConversationMessageDto>>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage httpRequest = new(
            HttpMethod.Get,
            $"api/conversations/{conversationId:D}/messages");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<List<ConversationMessageDto>>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseListConversationMessageDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<List<ConversationMessageDto>>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<List<ConversationMessageDto>>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<List<ConversationMessageDto>>.Failure(
                        new Error("Api.InvalidResponse", "Conversation messages payload was empty."));
                }

                return Result<List<ConversationMessageDto>>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<List<ConversationMessageDto>>.Failure(envelope.Error.Value);
                }

                return Result<List<ConversationMessageDto>>.Failure(
                    new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<List<ConversationMessageDto>>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<List<ConversationMessageDto>>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<List<ConversationMessageDto>>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<List<ConversationMessageDto>>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> DeleteConversationAsync(Guid id, CancellationToken cancellationToken)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage httpRequest = new(HttpMethod.Delete, $"api/conversations/{id:D}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if ((int)response.StatusCode == 200)
            {
                if (envelope is { IsSuccess: true, Data: true })
                {
                    return Result<bool>.Success(true);
                }

                if (envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(
                    new Error("Api.InvalidResponse", "Empty or invalid response from API."));
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(
                    new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<bool>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<bool>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<bool>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<bool>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async IAsyncEnumerable<IntelligenceEvent> AskStreamAsync(
        PingRequest body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                "No API key found. Run 'arcanum serve' once to generate and store a key.");

            yield break;
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.PingRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage request = new(HttpMethod.Post, "api/intelligence/ping-stream");
        request.Content = content;

        _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        HttpResponseMessage? response = null;

        string? sendErrorMessage = null;

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sendErrorMessage = "The request to the Arcanum API timed out. The server may be busy with a long-running model operation.";
        }
        catch (HttpRequestException)
        {
            sendErrorMessage = "API is unreachable. Is 'arcanum serve' running in a background terminal?";
        }

        if (sendErrorMessage is not null || response is null)
        {
            yield return new IntelligenceEvent(
                IntelligenceEventType.Error,
                sendErrorMessage ?? "API is unreachable. Is 'arcanum serve' running in a background terminal?");

            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                ApiResponse<string>? envelope = responseBytes.Length == 0
                    ? null
                    : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseString);

                string message;

                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    message = envelope.Error.Value.Message;
                }
                else
                {
                    message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }

                yield return new IntelligenceEvent(IntelligenceEventType.Error, message);

                yield break;
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using StreamReader lineReader = new(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            while (true)
            {
                string? line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IntelligenceEvent? item;
                bool malformed = false;

                try
                {
                    item = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent);
                }
                catch (JsonException)
                {
                    item = null;
                    malformed = true;
                }

                if (malformed)
                {
                    yield return new IntelligenceEvent(
                        IntelligenceEventType.Status,
                        "Malformed data received from server. Skipping frame.");

                    continue;
                }

                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    public async Task<Result<List<LoreDto>>> ListLoreAsync(CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<List<LoreDto>>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, "api/lore");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<List<LoreDto>>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(
                    responseBytes,
                    ArcanumJsonContext.Default.ApiResponseListLoreDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<List<LoreDto>>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<List<LoreDto>>.Failure(err);
                }

                return Result<List<LoreDto>>.Success(envelope.Data ?? []);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<List<LoreDto>>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<List<LoreDto>>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<List<LoreDto>>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<List<LoreDto>>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<LoreDto>> GetLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<LoreDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        string encoded = Uri.EscapeDataString(key);

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"api/lore/{encoded}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<LoreDto>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseLoreDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<LoreDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Lore payload was empty."));
                }

                return Result<LoreDto>.Success(envelope.Data);
            }

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<LoreDto>.Failure(envelope.Error.Value);
                }

                return Result<LoreDto>.Failure(
                    new Error("Grimoire.LoreNotFound", "No lore exists with that key."));
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<LoreDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<LoreDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<LoreDto>> UpsertLoreAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<LoreDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        UpsertLoreRequest body = new(key, value);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.UpsertLoreRequest);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/lore");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<LoreDto>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseLoreDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<LoreDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<LoreDto>.Failure(
                        new Error("Api.InvalidResponse", "Lore payload was empty."));
                }

                return Result<LoreDto>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<LoreDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<LoreDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<LoreDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        string encoded = Uri.EscapeDataString(key);

        using HttpRequestMessage httpRequest = new(HttpMethod.Delete, $"api/lore/{encoded}");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if ((int)response.StatusCode == 404)
            {
                if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
                {
                    return Result<bool>.Failure(envelope.Error.Value);
                }

                return Result<bool>.Failure(
                    new Error("Grimoire.LoreNotFound", "No lore exists with that key."));
            }

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<bool>.Failure(err);
                }

                if (envelope.Data != true)
                {
                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Expected delete-lore success envelope with data true."));
                }

                return Result<bool>.Success(true);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<bool>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<bool>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<bool>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<bool>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<UnseenServantJobStatusDto[]>> GetDaemonJobsAsync(CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<UnseenServantJobStatusDto[]>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, "api/daemon/jobs");

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<UnseenServantJobStatusDto[]>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(
                    responseBytes,
                    ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDtoArray);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<UnseenServantJobStatusDto[]>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<UnseenServantJobStatusDto[]>.Failure(err);
                }

                return Result<UnseenServantJobStatusDto[]>.Success(envelope.Data ?? []);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<UnseenServantJobStatusDto[]>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<UnseenServantJobStatusDto[]>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<UnseenServantJobStatusDto[]>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<UnseenServantJobStatusDto[]>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<UnseenServantJobStatusDto>> AdjustDaemonJobInitiativeAsync(
        string jobName,
        int intervalMinutes,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {
            return Result<UnseenServantJobStatusDto>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));
        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        string encoded = Uri.EscapeDataString(jobName);

        AdjustInitiativeRequestDto body = new(intervalMinutes);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.AdjustInitiativeRequestDto);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, $"api/daemon/jobs/{encoded}/initiative");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<UnseenServantJobStatusDto>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto);

            if (response.IsSuccessStatusCode)
            {
                if (envelope is null)
                {
                    return Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));
                }

                if (!envelope.IsSuccess)
                {
                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<UnseenServantJobStatusDto>.Failure(err);
                }

                if (envelope.Data is null)
                {
                    return Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Api.InvalidResponse", "Daemon job status payload was empty."));
                }

                return Result<UnseenServantJobStatusDto>.Success(envelope.Data);
            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {
                return Result<UnseenServantJobStatusDto>.Failure(envelope.Error.Value);
            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<UnseenServantJobStatusDto>.Failure(new Error("Api.HttpError", fallback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<UnseenServantJobStatusDto>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));
        }
        catch (HttpRequestException)
        {
            return Result<UnseenServantJobStatusDto>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));
        }
    }

    public async Task<Result<bool>> SendCommLinkAlertAsync(
        CommLinkMessageRequestDto body,
        CancellationToken cancellationToken = default)
    {

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is null)
        {

            return Result<bool>.Failure(new Error(
                "Security.MissingApiKey",
                "No API key found. Run 'arcanum serve' once to generate and store a key."));

        }

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, ArcanumJsonContext.Default.CommLinkMessageRequestDto);

        using ByteArrayContent content = new(json);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "api/commlink/send");

        httpRequest.Content = content;

        _ = httpRequest.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);

        try
        {

            using HttpResponseMessage response = await client
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ApiResponse<bool>? envelope = responseBytes.Length == 0
                ? null
                : JsonSerializer.Deserialize(responseBytes, ArcanumJsonContext.Default.ApiResponseBoolean);

            if (response.IsSuccessStatusCode)
            {

                if (envelope is null)
                {

                    return Result<bool>.Failure(
                        new Error("Api.InvalidResponse", "Empty or invalid response from API."));

                }

                if (!envelope.IsSuccess)
                {

                    Error err = envelope.Error ?? new Error("Api.Error", "Request failed.");

                    return Result<bool>.Failure(err);

                }

                return Result<bool>.Success(envelope.Data);

            }

            if (envelope is not null && envelope is { IsSuccess: false, Error: not null })
            {

                return Result<bool>.Failure(envelope.Error.Value);

            }

            string fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            return Result<bool>.Failure(new Error("Api.HttpError", fallback));

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (OperationCanceledException)
        {

            return Result<bool>.Failure(new Error(
                "Connection.Timeout",
                "The request to the Arcanum API timed out. The server may be busy with a long-running model operation."));

        }
        catch (HttpRequestException)
        {

            return Result<bool>.Failure(new Error(
                "Connection",
                "API is unreachable. Is 'arcanum serve' running in a background terminal?"));

        }

    }
}
