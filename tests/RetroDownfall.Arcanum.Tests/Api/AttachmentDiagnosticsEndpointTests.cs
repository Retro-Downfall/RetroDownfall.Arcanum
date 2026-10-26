using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class AttachmentDiagnosticsEndpointTests
{
    private const string Canary = "/private/operator-secret/context.sqlite private document text";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AttachmentDiagnostics_Internal_reference_fault_is_a_safe_server_failure(bool stream, bool disposed)
    {
        DiagnosticChat chat = new();
        await using ArcanumWebApplicationFactory factory = CreateFactory(chat, () => disposed
            ? new ObjectDisposedException(Canary)
            : new InvalidOperationException(Canary));
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid sessionId = await CreateSessionAsync(client);
        string payload = "{\"prompt\":\"hello\",\"skipSpellRouting\":true,\"disableMcpTools\":true,\"sessionId\":\"" + sessionId + "\",\"attachmentReferences\":[\"" + Guid.NewGuid() + "\"]}";
        using HttpResponseMessage response = await client.PostAsync(stream ? "/api/intelligence/ping-stream" : "/api/intelligence/ping", new StringContent(payload, Encoding.UTF8, "application/json"));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(stream ? HttpStatusCode.OK : HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(ErrorCodes.Hub.Error, body, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectDisposedException", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCodes.Validation.AttachedFiles, body, StringComparison.Ordinal);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task AttachmentDiagnostics_Missing_reference_remains_client_validation_and_valid_text_reaches_model()
    {
        DiagnosticChat chat = new();
        await using ArcanumWebApplicationFactory factory = CreateFactory(chat);
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid sessionId = await CreateSessionAsync(client);
        string prefix = "{\"prompt\":\"hello\",\"skipSpellRouting\":true,\"disableMcpTools\":true,\"sessionId\":\"" + sessionId + "\",\"attachmentReferences\":[\"";
        using HttpResponseMessage missing = await client.PostAsync("/api/intelligence/ping", new StringContent(prefix + Guid.NewGuid() + "\"]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains(ErrorCodes.Validation.AttachedFiles, await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, chat.Calls);
        using MultipartFormDataContent form = new();
        using ByteArrayContent file = new("safe document"u8.ToArray());
        file.Headers.ContentType = new("text/plain");
        form.Add(file, "file", "notes.txt");
        using HttpResponseMessage uploaded = await client.PostAsync($"/api/sessions/{sessionId}/attachments", form);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        Guid id = document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using HttpResponseMessage valid = await client.PostAsync("/api/intelligence/ping", new StringContent(prefix + id + "\"]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(1, chat.Calls);
    }

    private static ArcanumWebApplicationFactory CreateFactory(DiagnosticChat chat, Func<Exception>? failure = null) => new()
    {
        SettingsOverride = settings => settings with { Features = settings.Features with { Attachments = true, Lexicon = false, Reasoning = false } },
        ServiceOverrides = services =>
        {
            services.RemoveAll<IArcanumIntelligenceProvider>();
            services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(sp => new DiagnosticChatFactory(chat, sp.GetRequiredService<IOptions<ArcanumSettings>>().Value.Providers[0]));
            if (failure is not null)
            {
                services.RemoveAll<ISessionAttachmentStore>();
                services.AddSingleton<ISessionAttachmentStore>(new NoOpSessionAttachmentStore(validationFailure: failure));
            }
        },
    };

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/api/sessions", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private sealed class DiagnosticChatFactory(DiagnosticChat chat, ProviderSettings provider) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) => Task.FromResult(new ChatClientLease(chat, provider, provider.Models[0].Name, ownedHttpClient: null));
        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings resolvedProvider, string resolvedModel, CancellationToken cancellationToken) => ResolveClientAsync(resolvedModel, cancellationToken);
    }

    private sealed class DiagnosticChat : IChatClient
    {
        public int Calls { get; private set; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")) { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer") { FinishReason = ChatFinishReason.Stop };
        }
    }
}
