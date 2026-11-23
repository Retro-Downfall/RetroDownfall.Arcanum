using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("WorkspacePathPolicy")]
public sealed class OrdinaryWorkspaceWriterEndpointTests
{
    private const string ProbePrompt = "Inspect the workspace tool catalog.";
    private const string EditPrompt = "Update the test fixtures.";
    private const string Before = "original fixture text";
    private const string After = "updated fixture text";
    private const string FileName = "sample.txt";
    private static readonly string[] Writers = ["write_file", "edit_file", "apply_patch"];

    [SkippableTheory]
    [InlineData("unregistered")]
    [InlineData("registered-global")]
    [InlineData("pinned-global")]
    [InlineData("campaign-bound")]
    [InlineData("new-session")]
    public async Task Ordinary_session_discovers_and_executes_writers_only_for_enrolled_workspace(string binding)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));

        CancellationToken ct = deadline.Token;

        WorkspaceChat chat = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Providers = [settings.Providers[0] with { ContextWindowLimit = 65_536 }],
                Features = settings.Features with
                {
                    Guild = true,
                    Apprentices = true,
                    Lexicon = false,
                    Reasoning = false,
                    WorkspaceChecks = false,
                    LongRest = false,
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IArcanumIntelligenceProvider>();

                services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());

                services.RemoveAll<IChatClientFactory>();

                services.AddSingleton<IChatClientFactory>(sp => new WorkspaceChatFactory(
                    chat, sp.GetRequiredService<IOptions<ArcanumSettings>>().Value.Providers[0]));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        var ready = factory.Services.GetRequiredService<IGuildLocalHostReadiness>().GetWorkspaceMutationFence();

        Assert.True(ready.IsSuccess, ready.IsFailure ? ready.Error.ToString() : null);

        string workspace = Path.Combine(factory.TempHome, "ordinary-writer");

        Directory.CreateDirectory(workspace);

        string path = Path.Combine(workspace, FileName);

        string patchPath = Path.Combine(workspace, "patch.txt");

        string newPath = Path.Combine(workspace, "new.txt");

        string outsidePath = Path.Combine(factory.TempHome, "outside.txt");

        await File.WriteAllTextAsync(path, Before, ct);

        await File.WriteAllTextAsync(patchPath, Before + "\n", ct);

        await File.WriteAllTextAsync(outsidePath, Before, ct);

        bool newSession = binding == "new-session";

        Guid? campaignId = binding == "campaign-bound" ? await RegisterWorkspaceAsync() : null;

        Guid? sessionId = null;

        if (newSession)
        {
            await RegisterWorkspaceAsync();
        }
        else
        {
            using HttpResponseMessage create = await client.PostAsync("/api/sessions", JsonContent.Create(
                new CreateSessionRequest(campaignId, "Ordinary workspace writer"), ArcanumJsonContext.Default.CreateSessionRequest), ct);

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var session = await create.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionDetailDto, ct);

            sessionId = Assert.IsType<SessionDetailDto>(session?.Data).Id;
        }

        bool globalEnrollment = binding is "registered-global" or "pinned-global";

        if (globalEnrollment)
        {
            await SendAsync(workspace, ProbePrompt);

            Guid workspaceId = await RegisterWorkspaceAsync();

            using HttpResponseMessage policy = await client.PutAsync($"/api/campaigns/{workspaceId:D}/sanctum", JsonContent.Create(
                new SanctumConfig { Enabled = true, Mode = SanctumMode.Strict, DisabledTools = ["read_file"] },
                ArcanumJsonContext.Default.SanctumConfig), ct);

            Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

            if (binding == "pinned-global") await PinWorkspaceAsync(workspaceId);
        }

        string workingDirectory = campaignId is null ? workspace : "";

        bool enrolled = binding != "unregistered";

        await SendAsync(workingDirectory, EditPrompt);

        Assert.Contains("read_file", chat.AdvertisedTools);

        Assert.Contains("delegate_task", chat.AdvertisedTools);

        if (enrolled)
        {
            Assert.All(Writers, writer => Assert.Contains(writer, chat.AdvertisedTools));

            string actual = await File.ReadAllTextAsync(path, ct);

            Assert.True(actual == After, $"Expected '{After}', got '{actual}'. Tool results: {string.Join("; ", chat.Results.Values)}");

            Assert.Contains("Replaced 1 occurrence", chat.Results["ordinary-edit"], StringComparison.Ordinal);

            Assert.Equal(After, await File.ReadAllTextAsync(newPath, ct));

            Assert.Equal(After + "\n", await File.ReadAllTextAsync(patchPath, ct));

            Assert.Equal(Before, await File.ReadAllTextAsync(outsidePath, ct));

            Assert.DoesNotContain("Replaced 1 occurrence", chat.Results["outside-edit"], StringComparison.Ordinal);

            if (globalEnrollment)
            {
                Assert.Contains("read_file", chat.PreEnrollmentTools);

                Assert.DoesNotContain(chat.PreEnrollmentTools, Writers.Contains);

                Assert.Contains("The Sanctum Guard has blocked this action", chat.Results["sanctum-read"], StringComparison.Ordinal);

                Assert.DoesNotContain(Before, chat.Results["sanctum-read"], StringComparison.Ordinal);

                Assert.Contains("The Sanctum Guard has blocked this action", chat.Results["outside-edit"], StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.DoesNotContain(chat.AdvertisedTools, Writers.Contains);

            Assert.Equal(Before, await File.ReadAllTextAsync(path, ct));

            Assert.Contains("not advertised", chat.Results["ordinary-edit"], StringComparison.Ordinal);
        }

        if (sessionId is { } existingSessionId)
        {
            using HttpResponseMessage inspect = await client.GetAsync($"/api/sessions/{existingSessionId:D}", ct);

            Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);

            var persisted = await inspect.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionDetailDto, ct);

            Assert.Equal(campaignId, Assert.IsType<SessionDetailDto>(persisted?.Data).CampaignId);
        }

        if (enrolled && sessionId is not null)
        {
            using HttpResponseMessage mismatch = await client.PostAsync("/api/intelligence/ping", JsonContent.Create(
                new PingRequest(EditPrompt, WorkingDirectory: workingDirectory, SessionId: sessionId,
                    CampaignId: Guid.NewGuid(), SkipSpellRouting: true), ArcanumJsonContext.Default.PingRequest), ct);

            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);

            Assert.Contains("Covenant.CampaignBindingConflict", await mismatch.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            using HttpClient unauthenticated = factory.CreateClient();

            using HttpResponseMessage unauthorized = await unauthenticated.PostAsync("/api/intelligence/ping", JsonContent.Create(
                new PingRequest(EditPrompt, WorkingDirectory: workingDirectory, SessionId: sessionId,
                    SkipSpellRouting: true), ArcanumJsonContext.Default.PingRequest), ct);

            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using HttpResponseMessage stateless = await client.PostAsync("/api/intelligence/ping", JsonContent.Create(
                new PingRequest(ProbePrompt, WorkingDirectory: workspace, SkipSpellRouting: true,
                    StatelessMessages: [new("user", ProbePrompt)]), ArcanumJsonContext.Default.PingRequest), ct);

            Assert.Equal(HttpStatusCode.OK, stateless.StatusCode);

            Assert.Contains("read_file", chat.AdvertisedTools);

            Assert.DoesNotContain(chat.AdvertisedTools, Writers.Contains);
        }

        async Task<Guid> RegisterWorkspaceAsync()
        {
            using HttpResponseMessage registration = await client.PostAsync("/api/workspaces", JsonContent.Create(
                new CreateWorkspaceRequest("ordinary-writer", workspace, WorkspaceType.Custom),
                ArcanumJsonContext.Default.CreateWorkspaceRequest), ct);

            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

            var result = await registration.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseWorkspaceInfo, ct);

            WorkspaceInfo info = Assert.IsType<WorkspaceInfo>(result?.Data);

            Assert.True(info.Persisted);

            return Guid.Parse(info.Id);
        }

        async Task PinWorkspaceAsync(Guid workspaceId)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            ICampaignRootIdentityKeyCreator keys = scope.ServiceProvider.GetRequiredService<ICampaignRootIdentityKeyCreator>();

            Assert.Equal(CampaignRootIdentityKeyState.Created, keys.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

            PhysicalCampaignRootOpener opener = scope.ServiceProvider.GetRequiredService<PhysicalCampaignRootOpener>();

            CovenantDigest digest = Assert.IsType<CovenantDigest>(opener.IdentifyExact(workspace));

            IGrimoireOrdinaryConnectionFactory connections = scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

            var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, ct);

            Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.ToString() : null);

            await using IGrimoireOrdinaryConnectionLease held = opened.Value;

            await using SqliteCommand command = held.Connection.CreateCommand();

            // A pre-existing canonical registration is valid alongside an immutable Global Session.
            command.CommandText = """
                INSERT INTO campaign_path_identities(CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
                VALUES($campaign, 1, 1, $path, 1, $digest, '2026-11-08T00:00:00.0000000Z');
                """;

            command.Parameters.AddWithValue("$campaign", workspaceId.ToString("D").ToUpperInvariant());
            command.Parameters.AddWithValue("$path", workspace);
            command.Parameters.AddWithValue("$digest", digest.Bytes);

            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        }

        async Task SendAsync(string directory, string prompt)
        {
            using HttpResponseMessage response = await client.PostAsync("/api/intelligence/ping", JsonContent.Create(
                new PingRequest(prompt, WorkingDirectory: directory, SessionId: sessionId,
                    SkipSpellRouting: true), ArcanumJsonContext.Default.PingRequest), ct);

            string body = await response.Content.ReadAsStringAsync(ct);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {response.StatusCode}: {body}");
        }
    }

    private sealed class WorkspaceChatFactory(WorkspaceChat chat, ProviderSettings provider) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(chat, provider, provider.Models[0].Name, ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings resolvedProvider, string resolvedModel,
            CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);
    }

    private sealed class WorkspaceChat : IChatClient
    {
        private int _editCalls;

        internal IReadOnlyList<string> AdvertisedTools { get; private set; } = [];

        internal IReadOnlyDictionary<string, string?> Results { get; private set; } = new Dictionary<string, string?>();

        internal IReadOnlyList<string> PreEnrollmentTools { get; private set; } = [];

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? latestPrompt = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;

            if (latestPrompt is EditPrompt or ProbePrompt)
            {
                AdvertisedTools = options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            }

            if (latestPrompt == ProbePrompt)
            {
                PreEnrollmentTools = AdvertisedTools;
            }

            if (latestPrompt == EditPrompt)
            {
                if (Interlocked.Increment(ref _editCalls) == 1)
                {
                    FunctionCallContent edit = new("ordinary-edit", "edit_file", new Dictionary<string, object?>
                    {
                        ["relativePath"] = FileName,
                        ["exactSearchText"] = Before,
                        ["replacementText"] = After,
                    });

                    FunctionCallContent write = new("ordinary-write", "write_file", new Dictionary<string, object?>
                    {
                        ["relativePath"] = "new.txt",
                        ["content"] = After,
                    });

                    FunctionCallContent patch = new("ordinary-patch", "apply_patch", new Dictionary<string, object?>
                    {
                        ["patch"] = $"--- a/patch.txt\n+++ b/patch.txt\n@@ -1 +1 @@\n-{Before}\n+{After}\n",
                    });

                    FunctionCallContent escape = new("outside-edit", "edit_file", new Dictionary<string, object?>
                    {
                        ["relativePath"] = "../outside.txt",
                        ["exactSearchText"] = Before,
                        ["replacementText"] = After,
                    });

                    FunctionCallContent read = new("sanctum-read", "read_file", new Dictionary<string, object?>
                    {
                        ["relativePath"] = FileName,
                    });

                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [read, edit, write, patch, escape]))
                    {
                        FinishReason = ChatFinishReason.ToolCalls,
                    });
                }

                Results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .ToDictionary(result => result.CallId, result => result.Result?.ToString(), StringComparer.Ordinal);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Fixture turn settled."))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);

            foreach (ChatMessage message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents) { FinishReason = response.FinishReason };
            }
        }
    }
}
