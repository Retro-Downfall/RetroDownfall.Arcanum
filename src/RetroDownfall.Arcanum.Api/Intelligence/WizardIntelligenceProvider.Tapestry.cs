using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class WizardIntelligenceProvider
{
    private static string MaterializedTextHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static HashSet<int> AcceptCurrentTextAttachments(ContextMaterializationLedger ledger,
        IReadOnlyList<AttachedFileDto> files, Guid? sessionId)
    {
        HashSet<int> accepted = [];

        for (int index = 0; index < files.Count; index++)
        {
            AttachedFileDto file = files[index];

            int bytes = Encoding.UTF8.GetByteCount(file.Content ?? string.Empty);

            string hash = MaterializedTextHash(file.Content ?? string.Empty);

            var entry = ledger.Accept(new(sessionId, ContextMaterializationSourceKind.CurrentTurnAttachment,
                $"current:{index.ToString(CultureInfo.InvariantCulture)}", hash, null,
                ContextMaterializationRange.Whole, ContextMaterializationOrigin.Explicit,
                file.RelativePath, hash, EstimateMaterializedTokens(bytes), bytes, ContextMaterializationTrust.UntrustedData), true);

            if (entry.Accepted && ledger.TryMarkInjected(entry.Identity, 0)) accepted.Add(index);
        }

        return accepted;
    }

    private ContextMaterializationLedger CreatePreviewMaterializationLedger(PingRequest request,
        SessionContextPinMaterialization pins, AttachmentMemoryGateAmbient.TurnScope attachmentState)
    {
        AttachmentEmbeddingSettings limits = settings.Value.ResolveEmbeddings().Attachments;

        ContextMaterializationLedger ledger = new(request.SessionId, new(
            ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedChunks(limits.MaxRetrievedChunks),
            ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedAttachments(limits.MaxRetrievedAttachments),
            ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedBytes(limits.MaxRetrievedBytes),
            ArcanumSettingClamps.EmbeddingsAttachmentMaxRetrievedTokens(limits.MaxRetrievedTokens)))
        { AttachmentMemory = attachmentState };

        _ = AcceptCurrentTextAttachments(ledger, request.AttachedFiles ?? [], request.SessionId);

        foreach (ContextPinMaterializedItem pin in pins.Items ?? [])
        {
            var entry = ledger.Accept(new(request.SessionId, ContextMaterializationSourceKind.ExplicitContextPin,
                Guid.TryParse(pin.SourceId, out Guid id) ? id.ToString("N") : pin.SourceId,
                pin.VersionOrdinal?.ToString(CultureInfo.InvariantCulture) ?? pin.ContentHash, pin.VersionOrdinal,
                ContextMaterializationRange.Whole, ContextMaterializationOrigin.Explicit, pin.SourceLabel, pin.ContentHash,
                EstimateMaterializedTokens(pin.MaterializedBytes), pin.MaterializedBytes, ContextMaterializationTrust.UntrustedData), true);

            if (entry.Accepted)
            {
                _ = ledger.TryMarkInjected(entry.Identity, 0);

                if (pin.Kind == RetroDownfall.Arcanum.Core.Storage.SessionContextPinKind.File) ledger.RegisterExplicitWorkspaceSource(pin.SourceId);
            }
        }

        return ledger;
    }

    private ITapestryRetrievalObservation? CreateTapestryObservation(TapestryInvocationSelection? selection,
        Embedding<float>? query, TapestryRetrievedNode[]? retrieved, TapestryContextNode[]? rendered)
    {
        if (selection is null || rendered is not { Length: > 0 } || retrieved is null
            || query?.AdditionalProperties is null
            || !query.AdditionalProperties.TryGetValue(EmbeddingProvenance.MetadataKey, out object? value)
            || value is not EmbeddingProvenance provenance) return null;

        TapestryEmbeddingSettings tapestry = settings.Value.ResolveEmbeddings().Tapestry;

        int target = ArcanumSettingClamps.EmbeddingsTapestryTargetChildrenPerSummary(tapestry.TargetChildrenPerSummary);

        TapestryRetrievalIdentity identity = new(provenance.Identity, query.Vector.Length,
            new(ArcanumSettingClamps.EmbeddingsTapestryMaxTreeDepth(tapestry.MaxTreeDepth), target,
                Math.Max(target, ArcanumSettingClamps.EmbeddingsTapestryMaxChildrenPerSummary(tapestry.MaxChildrenPerSummary)),
                ArcanumSettingClamps.EmbeddingsTapestryMaxClustersPerLayer(tapestry.MaxClustersPerLayer),
                ArcanumSettingClamps.EmbeddingsTapestryMaxSummaryTokens(tapestry.MaxSummaryTokens)));

        HashSet<string> hashes = new(rendered.Select(static node => node.ContentHash), StringComparer.Ordinal);

        return new TapestryRetrievalObservation(selection, identity, retrieved.Where(node => hashes.Contains(node.ContentHash))
            .Select(static node => new TapestryScopeGeneration(new(node.ScopeKind, node.ScopeId), node.GenerationId)));
    }
}
