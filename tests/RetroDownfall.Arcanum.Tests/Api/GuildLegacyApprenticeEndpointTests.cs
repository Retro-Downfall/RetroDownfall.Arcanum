using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("WorkspacePathPolicy")]
public sealed class GuildLegacyApprenticeEndpointTests
{
    [SkippableFact]
    public async Task Guild_mode_refuses_independent_native_Apprentice_creation_without_minting()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                return settings;
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        CreateApprenticeRequest request = new("Legacy mint", "Must use explicit Guild admission", WorkspacePath: factory.TempHome);

        using HttpResponseMessage response = await client.PostAsync("/api/apprentices", new StringContent(
            JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateApprenticeRequest), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var rows = await scope.ServiceProvider.GetRequiredService<IApprenticeRepository>().ListAsync(null, null, null, null, default);

        Assert.Empty(rows.Items);
    }
}
