using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api.Middleware;

/// <summary>
/// The one writer both maintenance refusals go through — the pre-endpoint one and the one the
/// exception handler produces for a gate that closed mid-request.
/// </summary>
/// <remarks>
/// It is tested directly rather than only through a host, because the two surfaces that use it must
/// be provably identical: an operator who sees the same window from a middleware refusal and from an
/// in-flight one must not have to tell them apart.
/// </remarks>
public sealed class GrimoireMaintenanceRefusalTests
{

    private const string ExpectedMessage =
        "The Grimoire is temporarily unavailable while maintenance owns connection admission.";

    [Fact]
    public async Task An_api_refusal_is_a_source_generated_envelope_carrying_the_maintenance_code()
    {

        HttpContext context = CreateContext("/api/sessions");

        Assert.True(await GrimoireMaintenanceRefusal.TryWriteAsync(context));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadBody(context),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Null(body.Data);

        Assert.Equal(ErrorCodes.Grimoire.MaintenanceUnavailable, body.Error?.Code);

        Assert.Equal(ExpectedMessage, body.Error?.Message);

        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));

    }

    [Fact]
    public async Task A_v1_refusal_is_the_openai_envelope_with_the_service_unavailable_type()
    {

        HttpContext context = CreateContext("/v1/chat/completions");

        Assert.True(await GrimoireMaintenanceRefusal.TryWriteAsync(context));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            ReadBody(context),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("service_unavailable", body.Error.Type);

        Assert.Equal("grimoire_maintenance", body.Error.Code);

        Assert.Equal(ExpectedMessage, body.Error.Message);

        Assert.Null(body.Error.Param);

    }

    /// <summary>
    /// The refusal is written before Covenant pre-binding installs its response-start hook, so the
    /// tuple that hook would have applied is applied here instead — on every route, because a cached
    /// maintenance answer outlives the window that produced it.
    /// </summary>
    [Theory]
    [InlineData("/api/data/memory/reset/plan")]
    [InlineData("/api/sessions")]
    [InlineData("/v1/models")]
    public async Task Every_refusal_carries_the_no_store_tuple_and_no_validator(string path)
    {

        HttpContext context = CreateContext(path);

        context.Response.Headers[HeaderNames.ETag] = "\"cached\"";

        context.Response.Headers[HeaderNames.LastModified] = "Mon, 01 Sep 2026 00:00:00 GMT";

        Assert.True(await GrimoireMaintenanceRefusal.TryWriteAsync(context));

        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());

        Assert.Equal("0", context.Response.Headers.Expires.ToString());

        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ETag));

        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.LastModified));

    }

    /// <summary>
    /// Nothing an operator could use to locate an installation, or to learn what it is doing, may
    /// travel in a refusal a caller was always going to receive.
    /// </summary>
    [Theory]
    [InlineData("/api/workspaces/secret-root/files")]
    [InlineData("/v1/chat/completions")]
    public async Task A_refusal_discloses_no_path_owner_phase_or_native_detail(string path)
    {

        HttpContext context = CreateContext(path);

        Assert.True(await GrimoireMaintenanceRefusal.TryWriteAsync(context));

        string json = ReadBody(context);

        Assert.DoesNotContain("secret-root", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("workspaces", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("sqlite", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("Exception", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("operationId", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("phase", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("owner", json, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// A response whose first byte has left cannot become a refusal, and pretending otherwise would
    /// corrupt the body a stream already committed to.
    /// </summary>
    [Fact]
    public async Task A_response_that_has_started_is_left_exactly_as_it_was()
    {

        HttpContext context = CreateContext("/api/events/logs");

        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature(context.Response.Body));

        context.Response.StatusCode = StatusCodes.Status200OK;

        Assert.False(await GrimoireMaintenanceRefusal.TryWriteAsync(context));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        Assert.Empty(ReadBody(context));

    }

    private static HttpContext CreateContext(string path)
    {

        ServiceCollection services = new();

        _ = services.AddLogging();

        services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        DefaultHttpContext context = new()
        {
            RequestServices = services.BuildServiceProvider(),
        };

        context.Request.Path = path;

        context.Response.Body = new MemoryStream();

        context.TraceIdentifier = "trace-251";

        return context;

    }

    private static string ReadBody(HttpContext context)
    {

        MemoryStream body = (MemoryStream)context.Response.Body;

        return Encoding.UTF8.GetString(body.ToArray());

    }

    /// <summary>
    /// A response whose first byte has already left, which the default in-memory feature cannot be.
    /// </summary>
    private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
    {

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = body;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

    }

}
