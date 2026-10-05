using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// A JSON media type whose <c>charset</c> .NET cannot decode is the caller's mistake, answered 415 --
/// not a logged 500.
/// </summary>
/// <remarks>
/// <c>HttpRequest.HasJsonContentType()</c> checks only the media type. <c>ReadFromJsonAsync</c> then asks
/// <c>Encoding.GetEncoding</c> for the charset and raises <see cref="InvalidOperationException"/> when it
/// does not know it, which is the one <see cref="InvalidOperationException"/> the pre-check in front of the
/// read does not cover. The routes below read their body by hand, and every one of them answered this 415
/// until the catches that mapped it were removed; the exception then reached
/// <c>ArcanumExceptionHandler</c> as an Error-level log and a 500. The parity cases pin the pre-check to
/// the framework's own decision, because a pre-check that disagrees with it brings the 500 back for
/// exactly the charsets they disagree on.
/// </remarks>
[Collection("ApiHost")]
public sealed class ApiRequestJsonCharsetTests(ArcanumWebApplicationFactory factory)
{
    [SkippableTheory]
    [InlineData("/api/lore", "windows-1252")]
    [InlineData("/api/lore", "shift_jis")]
    [InlineData("/api/lore", "gbk")]
    [InlineData("/api/lore", "bogus")]
    [InlineData("/api/lore", "\"bogus\"")]
    [InlineData("/api/intelligence/ping-stream", "windows-1252")]
    [InlineData("/api/intelligence/ping-stream", "bogus")]
    // A route that binds its body as a handler parameter: the framework's generated reader does the same
    // charset resolution, so it has the same hole.
    [InlineData("/api/prompts", "windows-1252")]
    [InlineData("/api/prompts", "bogus")]
    public async Task An_unknown_charset_on_an_api_route_that_reads_its_body_is_a_415_envelope(string route, string charset)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SkipWhenTheProcessCanDecode(charset);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostWithContentTypeAsync(client, route, $"application/json; charset={charset}");

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        Assert.DoesNotContain("Hub.Unhandled", json, StringComparison.Ordinal);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.UnsupportedMediaType, body.Error?.Code);
    }

    [SkippableTheory]
    [InlineData("/v1/chat/completions", "windows-1252")]
    [InlineData("/v1/chat/completions", "bogus")]
    [InlineData("/v1/embeddings", "windows-1252")]
    [InlineData("/v1/embeddings", "bogus")]
    [InlineData("/v1/batches", "windows-1252")]
    [InlineData("/v1/batches", "bogus")]
    public async Task An_unknown_charset_on_a_v1_route_is_a_415_in_the_openai_envelope(string route, string charset)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SkipWhenTheProcessCanDecode(charset);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostWithContentTypeAsync(client, route, $"application/json; charset={charset}");

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_request_error", body!.Error.Type);

        Assert.Equal("unsupported_media_type", body.Error.Code);
    }

    /// <summary>
    /// The pre-check rejects the unknown charset only: a charset .NET can decode still reaches the read.
    /// </summary>
    [SkippableTheory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    [InlineData("us-ascii")]
    [InlineData("iso-8859-1")]
    [InlineData("utf-16")]
    public async Task A_charset_dotnet_can_decode_is_not_refused_as_a_media_type(string charset)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostWithContentTypeAsync(client, "/api/lore", $"application/json; charset={charset}");

        Assert.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The pre-check and the framework agree on every charset, so the read cannot raise the exception
    /// the pre-check exists to answer.
    /// </summary>
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=us-ascii")]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=windows-1252")]
    [InlineData("application/json; charset=shift_jis")]
    [InlineData("application/json; charset=gbk")]
    [InlineData("application/json; charset=bogus")]
    [InlineData("application/json; charset=\"bogus\"")]
    [InlineData("application/json; charset=")]
    [InlineData("application/json; charset=utf-7")]
    [InlineData("application/ld+json; charset=bogus")]
    [InlineData("application/vnd.arcanum+json; charset=utf-8")]
    public async Task The_pre_check_agrees_with_what_ReadFromJsonAsync_can_decode(string contentType)
    {
        DefaultHttpContext httpContext = new();

        httpContext.Request.ContentType = contentType;

        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        bool frameworkRefusedTheCharset = false;

        try
        {
            _ = await httpContext.Request.ReadFromJsonAsync(
                ArcanumJsonContext.Default.PingRequest,
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            frameworkRefusedTheCharset = true;
        }
        catch (JsonException)
        {
            // A body the charset could not carry is a different mistake; the charset itself was accepted.
        }

        Assert.Equal(!frameworkRefusedTheCharset, ApiRequestJson.HasReadableJsonContentType(httpContext.Request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/jsonx")]
    public void A_media_type_that_is_not_json_is_not_readable_whatever_its_charset(string? contentType)
    {
        DefaultHttpContext httpContext = new();

        httpContext.Request.ContentType = contentType;

        Assert.False(ApiRequestJson.HasReadableJsonContentType(httpContext.Request));
    }

    private static async Task<HttpResponseMessage> PostWithContentTypeAsync(HttpClient client, string route, string contentType)
    {
        using StringContent content = new("{}", Encoding.UTF8);

        content.Headers.Remove("Content-Type");

        Assert.True(content.Headers.TryAddWithoutValidation("Content-Type", contentType));

        return await client.PostAsync(route, content);
    }

    private static void SkipWhenTheProcessCanDecode(string charset)
    {
        bool known;

        try
        {
            _ = Encoding.GetEncoding(charset.Trim('"'));

            known = true;
        }
        catch (ArgumentException)
        {
            known = false;
        }

        Skip.If(known, $"This process registers an encoding provider that decodes '{charset}', so it is not an unknown charset here.");
    }
}
