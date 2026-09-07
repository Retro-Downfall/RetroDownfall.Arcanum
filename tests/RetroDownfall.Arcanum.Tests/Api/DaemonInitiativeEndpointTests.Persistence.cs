using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Tests.Hosting;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class DaemonInitiativeEndpointTests
{
    [Fact]
    public async Task InitiativeDoesNotReportSuccessWhenThePacerDeclinesThePreviouslyConfiguredJob()
    {
        (WebApplication app, RecordingPacer pacer) = await CreateHostAsync();

        await using (app)
        {
            pacer.Accepted = false;

            using HttpClient client = app.GetTestClient();

            HttpResponseMessage response = await PostInitiativeAsync(client, ConfiguredJob, 15);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            await app.StopAsync();
        }
    }

    [Fact]
    public async Task InitiativeEndpointDelegateOwnsThePacerWrite()
    {
        await using UnseenServantPacerHarness harness = new(ConfiguredJob, "extract-saga");

        UnseenServantAdmissionHarness.Checkpoint save = new();

        harness.Watermarks.BeforeSave = save.PauseAsync;

        (WebApplication app, _) = await CreateHostAsync(harness.Pacer);

        await using (app)
        {
            RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(static endpoint => endpoint.RoutePattern.RawText == "/api/unseen-servant/jobs/{name}/initiative");

            DefaultHttpContext context = new() { RequestServices = app.Services };

            byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new AdjustInitiativeRequestDto(15), ArcanumJsonContext.Default.AdjustInitiativeRequestDto));

            context.Request.Method = "POST";

            context.Request.ContentType = "application/json";

            context.Request.ContentLength = body.Length;

            context.Request.Body = new MemoryStream(body);

            context.Request.RouteValues["name"] = ConfiguredJob;

            context.Response.Body = new MemoryStream();

            Task response = endpoint.RequestDelegate!(context);

            try
            {
                await save.WaitAsync();

                Assert.False(response.IsCompleted);
            }
            finally
            {
                save.Release.TrySetResult();

                await response.WaitAsync(TimeSpan.FromSeconds(10));

                await app.StopAsync();
            }
        }
    }

    [Fact]
    public async Task InitiativeResponseWaitsForThePacerWrite()
    {
        await using UnseenServantPacerHarness harness = new(ConfiguredJob, "extract-saga");

        UnseenServantAdmissionHarness.Checkpoint save = new();

        harness.Watermarks.BeforeSave = save.PauseAsync;

        (WebApplication app, _) = await CreateHostAsync(harness.Pacer);

        await using (app)
        {
            using HttpClient client = app.GetTestClient();

            Task<HttpResponseMessage> response = PostInitiativeAsync(client, ConfiguredJob, 15);

            try
            {
                await save.WaitAsync();

                Assert.False(response.IsCompleted);
            }
            finally
            {
                save.Release.TrySetResult();

                Assert.Equal(HttpStatusCode.OK, (await response.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);

                await app.StopAsync();
            }
        }
    }
}
