using System.Net;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class DataRetentionFileMeasurementTests
{
    [SkippableFact]
    public async Task Measurement_recovery_current_status_route_survives_real_attachment_staging_churn()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        using HttpClient client = new(factory.Server.CreateHandler(LoopbackTransportPeer.Apply)) { BaseAddress = new Uri("http://localhost") };

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

        string root = ArcanumPaths.AttachmentsDirectory;

        Directory.CreateDirectory(root);

        for (int index = 0; index < 2000; index++) await File.WriteAllBytesAsync(Path.Combine(root, $"stable-{index}.bin"), [1]);

        RetentionMeasurementChurn churn = await RetentionMeasurementChurn.StartAsync(root);

        try
        {
            for (int index = 0; index < 40; index++)
            {
                using HttpResponseMessage response = await client.GetAsync("/api/data/status");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var status = await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseDataRetentionStatus);

                Assert.True(status.Files >= 2000);
            }
        }
        finally
        {
            await churn.DisposeAsync();
        }

        Assert.True(churn.Cycles > 0);
    }
}
