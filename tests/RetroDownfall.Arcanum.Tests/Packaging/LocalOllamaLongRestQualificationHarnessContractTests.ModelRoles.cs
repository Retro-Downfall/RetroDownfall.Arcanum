using System.Text.Json.Nodes;
using RetroDownfall.Arcanum.Core.LongRest;
using Xunit.Sdk;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class LocalOllamaLongRestQualificationHarnessContractTests
{
    [Theory]
    [InlineData("flint:40b", "flint:40b")]
    [InlineData("gemma4:e4b", "qwen3.8:27b")]
    [InlineData("flint:40b", "Flint:40b")]
    public async Task Recorded_summary_adjudication_and_embeddings_are_graded_by_their_roles(string chatModel, string defaultModel)
    {
        LongRestQualificationOptions options = ModelRoleOptions(chatModel, defaultModel);

        LongRestRecordedRequest[] traffic = await RecordModelRoleTrafficAsync(options);

        LongRestQualificationRun run = ModelRoleRun(options);

        run.AssertServedBy(traffic, requireBothKinds: true);

        Assert.Empty(run.Findings);
        Assert.Equal(
            [LongRestProviderRequestKind.Summary, LongRestProviderRequestKind.Adjudication, LongRestProviderRequestKind.Embedding],
            traffic.Select(static request => request.Kind));
        Assert.Contains(run.Observations, observation => observation.Contains(
            chatModel == defaultModel ? "share the same tag" : "distinct default-tag requests: 0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("summary-model", "not the fast model", true)]
    [InlineData("summary-model", "not the fast model", false)]
    [InlineData("adjudication-model", "not the fast model", true)]
    [InlineData("adjudication-model", "not the fast model", false)]
    [InlineData("embedding-model", "not the embedding model", true)]
    [InlineData("embedding-model", "not the embedding model", false)]
    [InlineData("summary-credential", "not the chat entry's", true)]
    [InlineData("summary-credential", "not the chat entry's", false)]
    [InlineData("adjudication-credential", "not the chat entry's", true)]
    [InlineData("adjudication-credential", "not the chat entry's", false)]
    [InlineData("embedding-credential", "not the embedding entry's", true)]
    [InlineData("embedding-credential", "not the embedding entry's", false)]
    [InlineData("unknown-credential", "Unknown credential", true)]
    [InlineData("unknown-credential", "Unknown credential", false)]
    [InlineData("missing-credential", "None credential", true)]
    [InlineData("missing-credential", "None credential", false)]
    [InlineData("generic-chat", "unexpected request purpose", true)]
    [InlineData("generic-chat", "unexpected request purpose", false)]
    [InlineData("other-purpose", "unexpected request purpose", true)]
    [InlineData("other-purpose", "unexpected request purpose", false)]
    public async Task Recorded_roles_do_not_authorize_the_wrong_model_credential_or_request_purpose(string fault, string finding, bool shared)
    {
        LongRestQualificationOptions options = ModelRoleOptions("flint:40b", shared ? "flint:40b" : "qwen3.8:27b");

        LongRestRecordedRequest[] traffic = await RecordModelRoleTrafficAsync(options, fault);

        LongRestQualificationRun run = ModelRoleRun(options);

        run.AssertServedBy(traffic, requireBothKinds: true);

        Assert.Contains(run.Findings, message => message.Contains(finding, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("summary-default")]
    [InlineData("generic-default")]
    public async Task A_distinct_default_tag_still_refuses_misrouting(string fault)
    {
        LongRestQualificationOptions options = ModelRoleOptions("gemma4:e4b", "qwen3.8:27b");

        LongRestRecordedRequest[] traffic = await RecordModelRoleTrafficAsync(options, fault);

        LongRestQualificationRun run = ModelRoleRun(options);

        run.AssertServedBy(traffic, requireBothKinds: true);

        Assert.Contains(run.Findings, static message => message.Contains("The default model", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("omit-summary", "No Long Rest summary")]
    [InlineData("omit-adjudication", "No Long Rest adjudication")]
    public async Task Shared_roles_still_require_both_positive_request_purposes(string fault, string finding)
    {
        LongRestQualificationOptions options = ModelRoleOptions("flint:40b", "flint:40b");

        LongRestRecordedRequest[] traffic = await RecordModelRoleTrafficAsync(options, fault);

        LongRestQualificationRun run = ModelRoleRun(options);

        run.AssertServedBy(traffic, requireBothKinds: true);

        Assert.Contains(run.Findings, message => message.Contains(finding, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovery_can_have_only_a_summary_but_metadata_cannot_replace_positive_chat_evidence()
    {
        LongRestQualificationOptions options = ModelRoleOptions("flint:40b", "flint:40b");

        LongRestRecordedRequest[] traffic = await RecordModelRoleTrafficAsync(options, "omit-adjudication");

        LongRestQualificationRun run = ModelRoleRun(options);

        run.AssertServedBy(traffic, requireBothKinds: false);

        Assert.Empty(run.Findings);

        LongRestRecordedRequest[] metadata = await RecordModelRoleTrafficAsync(options, "metadata-only");

        Assert.Equal(LongRestProviderRequestKind.Models, Assert.Single(metadata).Kind);
        Assert.ThrowsAny<XunitException>(() => ModelRoleRun(options).AssertServedBy(metadata, requireBothKinds: false));
    }

    private static LongRestQualificationOptions ModelRoleOptions(string chatModel, string defaultModel) =>
        new("/unused-published-apphost", chatModel, defaultModel, "qwen3-embedding:8b", 40960, 4096,
            new Uri("http://127.0.0.1:11434/v1/"), null, null);

    private static LongRestQualificationRun ModelRoleRun(LongRestQualificationOptions options) =>
        new(options, PublishedTestProcessIsolation.Create("offline-long-rest-roles"), new LongRestQualificationLog(null, null));

    private static async Task<LongRestRecordedRequest[]> RecordModelRoleTrafficAsync(LongRestQualificationOptions options, string? fault = null)
    {
        int forwarded = 0;

        await using StubUpstream upstream = await StubUpstream.StartAsync(context =>
        {
            Interlocked.Increment(ref forwarded);

            return StubChatResponse(context);
        });

        await using LongRestQualificationProxy proxy = await LongRestQualificationProxy.StartAsync(upstream.Endpoint);

        using HttpClient client = new();

        if (fault == "metadata-only")
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(proxy.Endpoint, "models"));

            response.EnsureSuccessStatusCode();
        }
        else
        {
            foreach (string purpose in new[] { "summary", "adjudication", "embedding", "extra" })
            {
                if ((purpose == "extra" && fault is not ("generic-chat" or "generic-default" or "other-purpose"))
                    || (purpose == "summary" && fault == "omit-summary")
                    || (purpose == "adjudication" && fault == "omit-adjudication"))
                {
                    continue;
                }

                string model = purpose == "embedding" ? options.EmbeddingModel : options.ChatModel;

                if (fault == purpose + "-model")
                {
                    model = "wrong:tag";
                }
                else if ((purpose == "summary" && fault == "summary-default") || (purpose == "extra" && fault == "generic-default"))
                {
                    model = options.DefaultModel;
                }

                string? credential = purpose == "embedding"
                    ? LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder
                    : LongRestQualificationConfiguration.ChatCredentialPlaceholder;

                if (fault == purpose + "-credential")
                {
                    credential = purpose == "embedding"
                        ? LongRestQualificationConfiguration.ChatCredentialPlaceholder
                        : LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder;
                }
                else if (purpose == "summary" && fault is "unknown-credential" or "missing-credential")
                {
                    credential = fault == "unknown-credential" ? "unknown-synthetic-test-key" : null;
                }

                string route = purpose == "embedding" ? "embeddings" : purpose == "extra" && fault == "other-purpose" ? "unclassified" : "chat/completions";

                JsonObject body = new() { ["model"] = model };

                if (purpose == "embedding")
                {
                    body["input"] = "Synthetic embedding fixture";
                }
                else
                {
                    body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Synthetic text fixture" });

                    if (purpose != "extra")
                    {
                        body["response_format"] = new JsonObject
                        {
                            ["type"] = "json_schema",
                            ["json_schema"] = new JsonObject
                            {
                                ["name"] = purpose == "summary" ? LongRestPromptLayout.SummarySchemaName : LongRestPromptLayout.AdjudicationSchemaName,
                            },
                        };
                    }
                }

                using HttpRequestMessage request = new(HttpMethod.Post, new Uri(proxy.Endpoint, route))
                {
                    Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                };

                if (credential is not null)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
                }

                using HttpResponseMessage response = await client.SendAsync(request);

                response.EnsureSuccessStatusCode();
            }
        }

        await WaitUntilAsync(() => proxy.All.All(static request => request.CompletedAt is not null));

        LongRestRecordedRequest[] traffic = [.. proxy.All];

        Assert.Equal(traffic.Length, forwarded);
        Assert.All(traffic, static request =>
        {
            Assert.Null(request.Error);
            Assert.Equal(200, request.StatusCode);
        });

        return traffic;
    }
}
