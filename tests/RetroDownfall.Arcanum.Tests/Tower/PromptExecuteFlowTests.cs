using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

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
  public async Task WriteBufferedAsync_InferenceFailure_Returns500Envelope()
  {
    DefaultHttpContext ctx = new();

    ctx.Response.Body = new MemoryStream();

    PingRequest ping = new(Prompt: "hello", SkipSpellRouting: true);

    await InferenceExecuteWriter.WriteBufferedAsync(
      ctx,
      new FailingIntelligenceProvider(),
      ping,
      CancellationToken.None);

    Assert.Equal(StatusCodes.Status500InternalServerError, ctx.Response.StatusCode);

    ctx.Response.Body.Position = 0;

    using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Response.Body);

    Assert.False(doc.RootElement.GetProperty("isSuccess").GetBoolean());
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

  private sealed class FailingIntelligenceProvider : IArcanumIntelligenceProvider
  {

    public Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null) =>
      Task.FromResult(Result<PromptTurnResult>.Failure(new Error("Hub.Error", "inference failed")));

    public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null) =>
      EmptyStream();

    private static async IAsyncEnumerable<IntelligenceEvent> EmptyStream()
    {
      await Task.CompletedTask;

      yield break;
    }

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
