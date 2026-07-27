using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// RAG Phase 4 — <c>read_saga</c>: semantic search over Saga (long-term associative memory). Read-only
/// by design — there is no <c>scribe_saga</c> or <c>delete_saga</c> counterpart, so the model cannot
/// poison its own memory. Gated by <c>Arcanum:Features:Saga</c> plus valid
/// <c>Arcanum:Integrations:Embeddings</c> provider/model facts (see
/// <see cref="ArcanumInternalToolServer._sagaEnabled"/>, checked both at <c>tools/list</c>
/// advertising and <c>tools/call</c> dispatch).
/// </summary>
internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteReadSagaAsync(JsonElement arguments, CancellationToken cancellationToken)
    {

        ReadSagaParams? args;

        try
        {

            args = JsonSerializer.Deserialize(arguments, _json.ReadSagaParams);

        }
        catch (JsonException ex)
        {

            _logger?.LogError(ex, "read_saga argument deserialization failed.");

            return ToolError("Invalid arguments for read_saga.");

        }

        if (args is null || string.IsNullOrWhiteSpace(args.Query))
        {

            return ToolError("read_saga requires a non-empty 'query'.");

        }

        string query = args.Query.Trim();

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IWeaveService weaveService = scope.ServiceProvider.GetRequiredService<IWeaveService>();

            if (!weaveService.IsAvailable)
            {

                return ToolError("The embedding provider is unavailable; Saga cannot be searched right now.");

            }

            Result<Embedding<float>> embedResult = await weaveService.EmbedAsync(query, cancellationToken).ConfigureAwait(false);

            if (embedResult.IsFailure)
            {

                return ToolError("Failed to embed the query for Saga search; see server logs for detail.");

            }

            IOptionsMonitor<ArcanumSettings> options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

            EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

            int limit = ArcanumSettingClamps.EmbeddingsMaxResults(args.Limit ?? embeddings.MaxResults);

            float similarityThreshold = ArcanumSettingClamps.EmbeddingsSimilarityThreshold(embeddings.SimilarityThreshold);

            IDivinationService divinationService = scope.ServiceProvider.GetRequiredService<IDivinationService>();

            Result<DivinationResult[]> searchResult = await divinationService
                .SearchAsync(
                    "saga_memory_embeddings_vec",
                    "MemoryId",
                    "Embedding",
                    embedResult.Value,
                    limit,
                    similarityThreshold,
                    cancellationToken)
                .ConfigureAwait(false);

            if (searchResult.IsFailure)
            {

                return ToolError("Saga search failed; see server logs for detail.");

            }

            if (searchResult.Value.Length == 0)
            {

                return new McpToolsCallResultWire
                {
                    Content = [new McpToolContentTextWire { Text = "No Saga memories matched that query." }],
                    IsError = false,
                };

            }

            ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

            IReadOnlyDictionary<string, SagaMemoryDto> byId = await store
                .GetByIdsAsync([.. searchResult.Value.Select(static hit => hit.Id)], cancellationToken)
                .ConfigureAwait(false);

            StringBuilder sb = new();

            foreach (DivinationResult hit in searchResult.Value)
            {

                if (!byId.TryGetValue(hit.Id, out SagaMemoryDto? memory))
                {

                    continue;

                }

                sb.Append("- ");

                sb.Append(memory.Content);

                sb.Append(" (similarity: ");

                sb.Append(hit.Similarity.ToString("F2", CultureInfo.InvariantCulture));

                sb.Append(", formed ");

                sb.Append(memory.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                sb.AppendLine(")");

            }

            string text = sb.Length == 0 ? "No Saga memories matched that query." : sb.ToString().TrimEnd();

            return new McpToolsCallResultWire
            {
                Content = [new McpToolContentTextWire { Text = text }],
                IsError = false,
            };

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            _logger?.LogError(ex, "read_saga failed for query {Query}.", query);

            return ToolError("An internal error occurred during tool execution.");

        }

    }

}
