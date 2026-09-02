using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Tower;

public sealed class PromptExecuteFlowTests
{

  [Fact]
  public void BuildPingRequest_MapsRenderedTemplateAndSkipsSpellRouting()
  {
    Prompt prompt = new()
    {
      Id = Guid.NewGuid(),
      Name = "Summarize",
      Template = "Summarize: {{topic}}",
      ParameterSchema = """{"type":"object","properties":{"topic":{"type":"string"}},"required":["topic"]}""",
      Model = "mistral:latest",
      Temperature = 0.2,
    };

    PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new FakeTokenCounter());

    Result<PromptRenderResultDto> render = renderer.Render(prompt, new Dictionary<string, string> { ["topic"] = "logs" });

    Assert.True(render.IsSuccess);

    PromptExecuteRequest body = new(
      UserMessage: "Run it",
      Parameters: new Dictionary<string, string> { ["topic"] = "logs" },
      Model: null,
      Temperature: null);

    PingRequest ping = new(
      Prompt: body.UserMessage,
      Model: body.Model ?? prompt.Model,
      WorkingDirectory: string.Empty,
      SessionId: body.SessionId,
      SkipSpellRouting: true,
      Temperature: body.Temperature ?? (float?)prompt.Temperature,
      AdditionalSystemPrompt: render.Value!.RenderedText);

    Assert.True(ping.SkipSpellRouting);

    Assert.Equal("Summarize: \"logs\"", ping.AdditionalSystemPrompt);

    Assert.Equal("mistral:latest", ping.Model);

    Assert.Equal(0.2f, ping.Temperature);
  }

  [Fact]
  public void Render_MissingRequiredParameter_ReturnsPromptMissingParameter()
  {
    Prompt prompt = new()
    {
      Name = "NeedsParam",
      Template = "Hello {{name}}",
      ParameterSchema = """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
    };

    PromptRenderer renderer = PromptRendererTestSupport.CreateRenderer(new FakeTokenCounter());

    Result<PromptRenderResultDto> render = renderer.Render(prompt, new Dictionary<string, string>());

    Assert.True(render.IsFailure);

    Assert.Equal("Prompt.MissingParameter", render.Error.Code);
  }

  [Fact]
  public async Task WriteStreamAsync_InferenceFailure_EmitsErrorNdjsonFrame()
  {
    DefaultHttpContext ctx = new();

    ctx.Response.Body = new MemoryStream();

    PingRequest ping = new(Prompt: "hello", SkipSpellRouting: true);

    await InferenceExecuteWriter.WriteStreamAsync(
      ctx,
      new FailingStreamIntelligenceProvider(),
      ping,
      CancellationToken.None);

    Assert.Equal("application/x-ndjson; charset=utf-8", ctx.Response.ContentType);

    ctx.Response.Body.Position = 0;

    using StreamReader reader = new(ctx.Response.Body);

    string content = await reader.ReadToEndAsync();

    Assert.Contains("\"error\"", content, StringComparison.OrdinalIgnoreCase);
  }

  private sealed class FakeTokenCounter : IManaMeter
  {

    public int CountTokens(string text) => text.Length;

  }

  private sealed class FailingStreamIntelligenceProvider : IArcanumIntelligenceProvider
  {

    public Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null) =>
      Task.FromResult(Result<PromptTurnResult>.Success(new PromptTurnResult("ok", null)));

    // W3.4 Group A: fails before streaming any frame, so the response has not started and the
    // error NDJSON frame is emitted (per the responseStarted guard, a late failure AFTER the
    // stream has started suppresses the error frame; that case is covered by
    // InferenceExecuteWriterTests.WriteStreamAsync_LateStreamExceptionAfterStart_DoesNotWriteErrorFrame).
    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
      PingRequest request,
      ArcanumInvocationContext invocationContext,
      [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
      InferenceAuditContext? auditContext = null)
    {
      await Task.Yield();

      throw new InvalidOperationException("stream failed");

#pragma warning disable CS0162 // Unreachable code — yield break satisfies the async-iterator requirement.
      yield break;
#pragma warning restore CS0162
    }

  }

}

/// <summary>
/// POST /api/prompts/{id}/execute through the real host (W2-4) — the buffered failure-envelope
/// contract this route builds inline, replacing the deleted InferenceExecuteWriter.WriteBufferedAsync
/// (no production caller) and the dead test that exercised it.
/// </summary>
[Collection("ApiHost")]
public sealed class PromptExecuteHandlerFlowTests
{

  private readonly ArcanumWebApplicationFactory _factory;

  public PromptExecuteHandlerFlowTests(ArcanumWebApplicationFactory factory)
  {

    _factory = factory;

  }

  private static async Task<Guid> CreatePromptAsync(HttpClient client, string name)
  {

    CreatePromptRequest request = new(
      name,
      "1.0.0",
      "Hello {{name}}",
      "PromptExecuteHandlerFlowTests fixture prompt",
      [],
      null,
      null,
      null,
      null,
      null,
      null,
      null,
      null);

    string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest);

    HttpResponseMessage response = await client.PostAsync(
      "/api/prompts",
      new StringContent(payload, Encoding.UTF8, "application/json"));

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    string json = await response.Content.ReadAsStringAsync();

    ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

    return body!.Data!.Id;

  }

  [SkippableFact]
  public async Task PostPromptExecute_InferenceFailure_ReturnsMappedStatusAndFailureEnvelope()
  {

    Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    HttpClient client = _factory.CreateAuthenticatedClient();

    Guid promptId = await CreatePromptAsync(client, $"execute-failure-{Guid.NewGuid():N}");

    // Connection.Unreachable maps to 503, not the mapper's 500 fallback — picked deliberately so
    // this test cannot pass against a handler that hardcodes a status instead of routing through
    // ArcanumErrorMapper.
    _factory.FakeIntelligence.NextFailure = new Error(ErrorCodes.Connection.Unreachable, "provider unreachable");

    PromptExecuteRequest execute = new(UserMessage: "run it");

    string payload = JsonSerializer.Serialize(execute, ArcanumJsonContext.Default.PromptExecuteRequest);

    HttpResponseMessage response = await client.PostAsync(
      $"/api/prompts/{promptId}/execute",
      new StringContent(payload, Encoding.UTF8, "application/json"));

    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

    string body = await response.Content.ReadAsStringAsync();

    using JsonDocument doc = JsonDocument.Parse(body);

    Assert.False(doc.RootElement.GetProperty("isSuccess").GetBoolean());

    _factory.FakeIntelligence.NextFailure = null;

  }

}
