using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed record LongRestChatRequest(string SystemPrompt, string UserPrompt, string SchemaJson, string SchemaName, string SchemaDescription, int MaxOutputTokens);

/// <summary>Code-owned exact chat rendering and distinct single-input embedding binding.</summary>
public static class LongRestProviderCallFreezer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<LongRestChatRequest> RenderChat(LongRestProviderPayload payload)
    {
        if (payload is null || payload.Kind is not (LongRestProviderPayloadKind.Summary or LongRestProviderPayloadKind.Adjudication))
        {
            return Invalid();
        }

        StringBuilder user = new();

        string system;

        string schema;

        string name;

        string description;

        if (payload.Kind == LongRestProviderPayloadKind.Summary)
        {
            system = LongRestPromptLayout.SummarySystemPromptFor(payload.SummaryBoundBytes);

            schema = LongRestPromptLayout.SummarySchema;

            name = LongRestPromptLayout.SummarySchemaName;

            description = LongRestPromptLayout.SummarySchemaDescription;

            int ordinal = 0;

            foreach (LongRestHierarchyChild child in payload.Fold!.Children)
            {
                ordinal++;

                user.Append(LongRestPromptLayout.SummaryObservation(ordinal, child.Content));
            }
        }
        else
        {
            system = LongRestPromptLayout.AdjudicationSystemPrompt;

            schema = LongRestPromptLayout.DecisionSchema;

            name = LongRestPromptLayout.AdjudicationSchemaName;

            description = LongRestPromptLayout.AdjudicationSchemaDescription;

            int memory = 0;

            foreach (LongRestSourceLeaf input in LongRestPromptLayout.CanonicalAdjudicationOrder(payload.Inputs))
            {
                memory++;

                user.Append(LongRestPromptLayout.AdjudicationMemory(memory, input.Content ?? string.Empty));
            }
        }

        LongRestChatRequest request = new(system, user.ToString(), schema, name, description, LongRestPromptLayout.ReservedAnswerTokens);

        try
        {
            long bytes = LongRestPromptLayout.RenderedBytes(request.SystemPrompt, request.UserPrompt, request.SchemaJson, request.SchemaName, request.SchemaDescription);

            return bytes <= payload.InputBudgetBytes ? request : Invalid();
        }
        catch (EncoderFallbackException)
        {
            return Invalid();
        }
    }

    /// <summary>
    /// Whether the request for <paramref name="payload"/> asks <paramref name="model"/> to answer without reasoning. Only an adjudication does, and
    /// only for a model whose entry declares reasoning: the answer is a few short member verdicts under a closed schema, a reasoning model given the
    /// fixed answer reservation can spend it on thought and be cut off before the verdicts (an invalid paid answer), and what the verdicts must
    /// carry is the restated fact of each memory, not a chain of thought. A summary keeps the model's own default. The control is the typed
    /// Microsoft.Extensions.AI effort, applied by <see cref="ReasoningChatOptionsAdapter"/>, and the digest binds it, so a model that declares
    /// reasoning and one that does not are different requests.
    /// </summary>
    public static bool AdjudicationReasoningOff(LongRestProviderPayloadKind kind, ProviderSettings provider, string model) =>
        kind == LongRestProviderPayloadKind.Adjudication && provider is not null && model is not null
        && provider.Models.Any(entry => string.Equals(entry.Name, model, StringComparison.Ordinal) && entry.Reasoning is not null);

    public static Result<CovenantDigest> PayloadDigest(LongRestProviderPayload payload, ProviderSettings provider, string model, int dimensions = 0)
    {
        if (payload.Kind == LongRestProviderPayloadKind.Embedding)
        {
            return LongRestEmbeddingPayloadHash.Create(provider.Name, model, dimensions, payload.EmbeddingText!);
        }

        Result<LongRestChatRequest> rendered = RenderChat(payload);

        if (rendered.IsFailure)
        {
            return rendered.Error;
        }

        try
        {
            if (!Identity(provider.Name) || !Identity(model))
            {
                return Invalid();
            }

            LongRestChatRequest request = rendered.Value;

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData("Arcanum.LongRest.ChatPayload.v2\0"u8);

            Integer(2);

            Integer((int)payload.Kind);

            Text(provider.Name);

            Text(model);

            Text(request.SystemPrompt);

            Text(request.UserPrompt);

            Text(request.SchemaJson);

            Text(request.SchemaName);

            Text(request.SchemaDescription);

            Integer(request.MaxOutputTokens);

            Integer(0); // Closed tool count.

            Integer(AdjudicationReasoningOff(payload.Kind, provider, model) ? 1 : 0); // Reasoning effort: 0 is the model's own, 1 is None.

            return new CovenantDigest(hash.GetHashAndReset());

            void Integer(int value)
            {
                Span<byte> bytes = stackalloc byte[4];

                BinaryPrimitives.WriteInt32BigEndian(bytes, value);

                hash.AppendData(bytes);
            }

            void Text(string value)
            {
                byte[] bytes = StrictUtf8.GetBytes(value);

                Integer(bytes.Length);

                hash.AppendData(bytes);
            }
        }
        catch (EncoderFallbackException)
        {
            return Invalid();
        }
    }

    private static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) && StrictUtf8.GetByteCount(value) <= 1024;

    private static Error Invalid() => new(ErrorCodes.LongRest.InvalidRequest, "The exact Long Rest provider input is invalid or exceeds its UTF-8 bound.");
}
