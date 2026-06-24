using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ApiKeyEndpointFilterTests
{

  private const string ValidKey = "test-api-key-12345";

  [Fact]
  public async Task InvokeAsync_ValidApiKeyHeader_AllowsRequest()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    bool nextCalled = false;

    object? result = await filter.InvokeAsync(
      CreateContext(httpContext),
      _ =>
      {
        nextCalled = true;

        return ValueTask.FromResult<object?>(Results.Ok());
      });

        Assert.True(nextCalled);

        Assert.NotNull(result);
  }

  [Fact]
  public async Task InvokeAsync_ValidBearerHeader_AllowsRequest()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = $"Bearer {ValidKey}";

    bool nextCalled = false;

    await filter.InvokeAsync(
      CreateContext(httpContext),
      _ =>
      {
        nextCalled = true;

        return ValueTask.FromResult<object?>(Results.Ok());
      });

    Assert.True(nextCalled);
  }

  [Fact]
  public async Task InvokeAsync_MissingHeader_ReturnsAuthUnauthorizedCode()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    JsonHttpResult<ApiResponse<string>> raw = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(new DefaultHttpContext()), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal("Auth.Unauthorized", raw.Value!.Error!.Value.Code);
  }

  [Fact]
  public async Task InvokeAsync_InvalidatesDigestWhenCacheIsClearedAfterRotation()
  {

    FakeSecretStore store = new(ValidKey);

    ApiKeyDigestCache cache = new(new FakeTimeProvider());

    ArcanumSettings settings = new()
    {
      Security = new SecuritySettings
      {
        MaxApiKeyHeaderUtf16Chars = 256,
        ApiKeyCacheTtlSeconds = 60,
      },
    };

    ApiKeyEndpointFilter filter = new(store, cache, new TestOptionsMonitor<ArcanumSettings>(settings));

    DefaultHttpContext first = new();

    first.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    await filter.InvokeAsync(CreateContext(first), _ => ValueTask.FromResult<object?>(Results.Ok()));

    store.GetCallCount = 0;

    store.ApiKey = "rotated-key-value-999";

    cache.Invalidate();

    DefaultHttpContext second = new();

    second.Request.Headers[ArcanumApiHeaders.ApiKey] = "rotated-key-value-999";

    await filter.InvokeAsync(CreateContext(second), _ => ValueTask.FromResult<object?>(Results.Ok()));

    Assert.Equal(1, store.GetCallCount);

  }

  [Fact]
  public async Task InvokeAsync_MissingHeader_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    IResult raw = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(new DefaultHttpContext()), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(raw));
  }

  private static int UnauthorizedStatus(IResult result) =>
    ((JsonHttpResult<ApiResponse<string>>)result).StatusCode ?? StatusCodes.Status500InternalServerError;

  [Fact]
  public async Task InvokeAsync_NoStoredKey_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(storedKey: null);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_WrongKey_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = "wrong-key";

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_EmptyHeader_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = string.Empty;

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_HeaderTooLong_Returns401()
  {
    ArcanumSettings settings = new()
    {
      Security = new SecuritySettings { MaxApiKeyHeaderUtf16Chars = 128 },
    };

    ApiKeyEndpointFilter filter = new(new FakeSecretStore(ValidKey), new ApiKeyDigestCache(new FakeTimeProvider()), new TestOptionsMonitor<ArcanumSettings>(settings));

    DefaultHttpContext httpContext = new();

    string tooLong = new string('k', 200);

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = tooLong;

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_DuplicateApiKeyHeaders_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = new[] { ValidKey, ValidKey };

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_DuplicateAuthorizationHeaders_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = new[] { $"Bearer {ValidKey}", $"Bearer {ValidKey}" };

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_NonBearerAuthorization_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = $"Basic {ValidKey}";

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_EmptyBearerToken_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = "Bearer ";

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_CaseInsensitiveBearerPrefix_AllowsRequest()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = $"bearer {ValidKey}";

    bool nextCalled = false;

    await filter.InvokeAsync(
      CreateContext(httpContext),
      _ =>
      {
        nextCalled = true;

        return ValueTask.FromResult<object?>(Results.Ok());
      });

    Assert.True(nextCalled);
  }

  [Fact]
  public async Task InvokeAsync_BearerTokenWithSurroundingWhitespace_AllowsRequest()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = $"Bearer   {ValidKey}  ";

    bool nextCalled = false;

    await filter.InvokeAsync(
      CreateContext(httpContext),
      _ =>
      {
        nextCalled = true;

        return ValueTask.FromResult<object?>(Results.Ok());
      });

    Assert.True(nextCalled);
  }

  [Fact]
  public async Task InvokeAsync_ApiKeyHeaderPreferredOverAuthorization_AllowsWithApiKey()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    httpContext.Request.Headers.Authorization = "Bearer wrong-key";

    bool nextCalled = false;

    await filter.InvokeAsync(
      CreateContext(httpContext),
      _ =>
      {
        nextCalled = true;

        return ValueTask.FromResult<object?>(Results.Ok());
      });

    Assert.True(nextCalled);
  }

  [Fact]
  public async Task InvokeAsync_EmptyAuthorizationHeaderValue_Returns401()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = string.Empty;

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_LargeUtf8ApiKeyWithinCharLimit_AllowsRequest()
  {
    string emojiKey = new string('\u30a2', 86);

    ApiKeyEndpointFilter filter = CreateFilter(emojiKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = emojiKey;

    Assert.True(Encoding.UTF8.GetByteCount(emojiKey) > 256);

    bool nextCalled = false;

    await filter.InvokeAsync(
      CreateContext(httpContext),
      _ =>
      {
        nextCalled = true;

        return ValueTask.FromResult<object?>(Results.Ok());
      });

    Assert.True(nextCalled);
  }

  [Fact]
  public async Task InvokeAsync_UnauthorizedUsesActivityTraceIdWhenPresent()
  {
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    using Activity activity = new("api-key-filter-test");

    activity.Start();

    try
    {
      JsonHttpResult<ApiResponse<string>> result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
        await filter.InvokeAsync(CreateContext(httpContext), _ => ValueTask.FromResult<object?>(Results.Ok())));

      Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);

      Assert.Equal(activity.Id, result.Value!.TraceId);
    }
    finally
    {
      activity.Stop();
    }
  }

  [Fact]
  public async Task InvokeAsync_RefreshesDigestAfterCacheTtlExpires()
  {
    FakeSecretStore store = new(ValidKey);

    FakeTimeProvider timeProvider = new();

    ArcanumSettings settings = new()
    {
      Security = new SecuritySettings
      {
        MaxApiKeyHeaderUtf16Chars = 256,
        ApiKeyCacheTtlSeconds = 1,
      },
    };

    ApiKeyEndpointFilter filter = new(store, new ApiKeyDigestCache(timeProvider), new TestOptionsMonitor<ArcanumSettings>(settings));

    DefaultHttpContext first = new();

    first.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    await filter.InvokeAsync(CreateContext(first), _ => ValueTask.FromResult<object?>(Results.Ok()));

    store.GetCallCount = 0;

    timeProvider.Advance(TimeSpan.FromSeconds(2));

    DefaultHttpContext second = new();

    second.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    await filter.InvokeAsync(CreateContext(second), _ => ValueTask.FromResult<object?>(Results.Ok()));

    Assert.Equal(1, store.GetCallCount);
  }

  [Fact]
  public async Task InvokeAsync_CachesDigestAcrossCalls()
  {
    FakeSecretStore store = new(ValidKey);

    ApiKeyEndpointFilter filter = CreateFilter(store);

    DefaultHttpContext first = new();

    first.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    await filter.InvokeAsync(CreateContext(first), _ => ValueTask.FromResult<object?>(Results.Ok()));

    store.GetCallCount = 0;

    DefaultHttpContext second = new();

    second.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    await filter.InvokeAsync(CreateContext(second), _ => ValueTask.FromResult<object?>(Results.Ok()));

    Assert.Equal(0, store.GetCallCount);
  }

  private static ApiKeyEndpointFilter CreateFilter(string? storedKey) =>
    CreateFilter(new FakeSecretStore(storedKey));

  private static ApiKeyEndpointFilter CreateFilter(FakeSecretStore store)
  {
    ArcanumSettings settings = new()
    {
      Security = new SecuritySettings
      {
        MaxApiKeyHeaderUtf16Chars = 256,
        ApiKeyCacheTtlSeconds = 60,
      },
    };

    return new ApiKeyEndpointFilter(store, new ApiKeyDigestCache(new FakeTimeProvider()), new TestOptionsMonitor<ArcanumSettings>(settings));
  }

  private static EndpointFilterInvocationContext CreateContext(HttpContext httpContext) =>
    new TestEndpointFilterInvocationContext(httpContext);

  private sealed class FakeSecretStore : ISecretStore
  {

    public FakeSecretStore(string? apiKey) => ApiKey = apiKey;

    public string? ApiKey { get; set; }

    public int GetCallCount { get; set; }

    public Task<string?> GetApiKeyAsync()
    {
      GetCallCount++;

      return Task.FromResult(ApiKey);
    }

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync()
    {
      GetCallCount++;

      return Task.FromResult(
        string.IsNullOrWhiteSpace(ApiKey)
          ? SecretStoreReadResult.Missing()
          : SecretStoreReadResult.Ok(ApiKey));
    }

    public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

    public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

    public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

  }

  private sealed class TestEndpointFilterInvocationContext(HttpContext httpContext) : EndpointFilterInvocationContext
  {

    public override HttpContext HttpContext { get; } = httpContext;

    public override IList<object?> Arguments { get; } = [];

    public override T GetArgument<T>(int index) => default!;

  }

}
