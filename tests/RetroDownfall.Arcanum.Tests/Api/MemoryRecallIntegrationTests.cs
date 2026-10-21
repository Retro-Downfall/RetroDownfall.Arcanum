using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Packaging;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class MemoryRecallIntegrationTests
{
    [Fact]
    public async Task Native_recall_preserves_exact_provenance_protected_tool_pair_and_provider_continuation()
    {
        await using var provider = await PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.StartAsync();

        const string credential = "ARCANUM_RECALL_HTTP_TEST_KEY";

        string? previous = global::System.Environment.GetEnvironmentVariable(credential);

        global::System.Environment.SetEnvironmentVariable(credential, PublishedArcanumSessionSmokeTests.ProviderKey);

        try
        {
            await using var factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true, configure: settings =>
            {
                settings.DefaultModel = PublishedArcanumSessionSmokeTests.ExpectedModel;

                settings.Providers = [new ProviderSettings
                {
                    Name = "published-smoke", Type = AiProviderKind.OpenAICompatible, Endpoint = provider.Endpoint.AbsoluteUri,
                    CredentialEnvironmentVariable = credential,
                    Models = [new ModelEntry(PublishedArcanumSessionSmokeTests.ExpectedModel, SupportsTools: true)], ContextWindowLimit = 16384
                }];

                settings.Features = new FeatureSettings
                {
                    Apprentices = false, Lexicon = false, ArchiveSearch = false, Metrics = false, Scrying = false,
                    Attachments = false, Reasoning = false, WorkspaceChecks = false, Annals = false, Covenant = true,
                    AgentMemoryRecall = true
                };

                PublishedArcanumSessionSmokeTests.ConfigureEvaluationPricing(settings);
            });

            var originals = factory.ServiceOverrides;

            factory.ServiceOverrides = services =>
            {
                originals!(services);

                services.RemoveAll<IArcanumIntelligenceProvider>();

                services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
            };

            using var client = factory.CreateAuthenticatedClient();

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            Guid sessionId = await PublishedArcanumSessionSmokeTests.ExerciseMemoryRecallAsync(client, provider, 0, deadline.Token);

            using var scope = factory.Services.CreateScope();

            var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(deadline.Token);

            Guid generation = scope.ServiceProvider.GetRequiredService<ICovenantRuntimeGenerationProvider>().Current.Availability.DatasetGeneration!.Value;

            await using var command = connection.CreateCommand();

            command.CommandText = """
                SELECT label.SensitivityCode, label.ExactGenerationIds, label.ArtifactContentDigest, label.ArtifactRevision
                FROM artifact_sensitivity label
                WHERE label.ArtifactKindCode = $kind
                  AND lower(replace(label.SessionId, '-', '')) = $session
                """;

            command.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.AssistantEntry);

            command.Parameters.AddWithValue("$session", sessionId.ToString("N"));

            int labels = 0;

            await using (var rows = await command.ExecuteReaderAsync(deadline.Token))
            {
                while (await rows.ReadAsync(deadline.Token))
                {
                    labels++;

                    Assert.Equal((int)ContentSensitivity.CovenantDerived, rows.GetInt32(0));

                    AssertGeneration((byte[])rows[1], generation);

                    Assert.Equal(32, ((byte[])rows[2]).Length);

                    Assert.True(rows.GetInt64(3) > 0);
                }
            }

            Assert.Equal(3, labels);

            command.Parameters.Clear();

            command.CommandText = """
                SELECT SubjectId, SensitivityCode, ExactGenerationIds
                FROM external_disclosure_receipts
                WHERE SubjectKind = $kind AND EffectCategoryCode = $effect
                ORDER BY DisclosedAtUtc, SubjectOrdinal
                """;

            command.Parameters.AddWithValue("$kind", (int)CovenantDisclosureSubjectKind.Turn);

            command.Parameters.AddWithValue("$effect", (int)CovenantDisclosureEffectCategory.ProviderDispatch);

            List<string> subjects = [];

            await using (var rows = await command.ExecuteReaderAsync(deadline.Token))
            {
                while (await rows.ReadAsync(deadline.Token))
                {
                    subjects.Add(rows.GetString(0));

                    Assert.Equal((int)ContentSensitivity.CovenantDerived, rows.GetInt32(1));

                    AssertGeneration((byte[])rows[2], generation);
                }
            }

            Assert.Equal(2, subjects.Count);

            Assert.Equal(subjects[0], subjects[1]);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(credential, previous);
        }
    }

    private static void AssertGeneration(byte[] bytes, Guid generation)
    {
        Assert.Equal(0, bytes.Length % 16);

        Assert.Contains(Enumerable.Range(0, bytes.Length / 16),
            index => new Guid(bytes.AsSpan(index * 16, 16), bigEndian: true) == generation);
    }
}
