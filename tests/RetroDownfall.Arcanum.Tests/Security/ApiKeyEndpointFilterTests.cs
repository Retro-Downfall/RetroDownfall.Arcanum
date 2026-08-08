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

    ApiKeyEndpointFilter filter = new(
      store,
      cache);

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
    ApiKeyEndpointFilter filter = CreateFilter(ValidKey);

    DefaultHttpContext httpContext = new();

    int maxChars = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
      ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);
    string tooLong = new('k', maxChars + 1);

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

    ApiKeyEndpointFilter filter = new(
      store,
      new ApiKeyDigestCache(timeProvider));

    DefaultHttpContext first = new();

    first.Request.Headers[ArcanumApiHeaders.ApiKey] = ValidKey;

    await filter.InvokeAsync(CreateContext(first), _ => ValueTask.FromResult<object?>(Results.Ok()));

    store.GetCallCount = 0;

    int cacheTtlSeconds = ArcanumSettingClamps.ApiKeyCacheTtlSeconds(
      ArcanumRuntimeDefaults.SecurityApiKeyCacheTtlSeconds);
    timeProvider.Advance(TimeSpan.FromSeconds(cacheTtlSeconds + 1));

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

  [Fact]
  public async Task InvokeAsync_HeaderExactlyAtClampLength_AllowsRequest()
  {
    int maxChars = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
      ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);

    string boundaryKey = new('k', maxChars);

    ApiKeyEndpointFilter filter = CreateFilter(boundaryKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = boundaryKey;

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
  public async Task InvokeAsync_HeaderOverClampLength_FailsClosedEvenWhenItMatchesStoredKey()
  {
    int maxChars = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
      ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);

    string oversizedKey = new('k', maxChars + 1);

    ApiKeyEndpointFilter filter = CreateFilter(oversizedKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers[ArcanumApiHeaders.ApiKey] = oversizedKey;

    bool nextCalled = false;

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(
        CreateContext(httpContext),
        _ =>
        {
          nextCalled = true;

          return ValueTask.FromResult<object?>(Results.Ok());
        }));

    Assert.False(nextCalled);

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  [Fact]
  public async Task InvokeAsync_OversizedBearerToken_FailsClosedEvenWhenItMatchesStoredKey()
  {
    int maxChars = ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars(
      ArcanumRuntimeDefaults.SecurityMaxApiKeyHeaderUtf16Chars);

    string oversizedKey = new('b', maxChars + 1);

    ApiKeyEndpointFilter filter = CreateFilter(oversizedKey);

    DefaultHttpContext httpContext = new();

    httpContext.Request.Headers.Authorization = $"Bearer {oversizedKey}";

    bool nextCalled = false;

    IResult result = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(
      await filter.InvokeAsync(
        CreateContext(httpContext),
        _ =>
        {
          nextCalled = true;

          return ValueTask.FromResult<object?>(Results.Ok());
        }));

    Assert.False(nextCalled);

    Assert.Equal(StatusCodes.Status401Unauthorized, UnauthorizedStatus(result));
  }

  private static ApiKeyEndpointFilter CreateFilter(string? storedKey) =>
    CreateFilter(new FakeSecretStore(storedKey));

  private static ApiKeyEndpointFilter CreateFilter(FakeSecretStore store)
  {
    return new ApiKeyEndpointFilter(
      store,
      new ApiKeyDigestCache(new FakeTimeProvider()));
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
