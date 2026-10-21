using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>Reads only the ordinary clean retrieval lanes; Covenant authority remains with the turn plan.</summary>
internal sealed class MemoryRecallCandidateReader(
    ISagaMemoryStore saga,
    ILexiconService lexicon,
    IOptionsMonitor<ArcanumSettings> options) : IMemoryRecallCandidateReader
{
    private static readonly Error Invalid = new("MemoryRecall.InvalidRequest", "The recall request or host binding is invalid.");

    private static readonly Error Unavailable = new("MemoryRecall.SourceUnavailable", "A recall source is unavailable.");

    private static readonly Error Oversized = new("MemoryRecall.CandidateLimit", "A recall candidate exceeds the supported bounds.");

    public async Task<Result<MemoryRecallCandidates>> ReadAsync(MemoryRecallRequest request,
        CanonicalCampaignContext canonical, MemoryScope memoryScope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (MemoryRecallPager.Validate(request).IsFailure)
        {
            return Invalid;
        }

        try
        {
            ArcanumSettings settings = options.CurrentValue;

            if (memoryScope != MemoryScope.Resolve(settings.Features.CampaignScopedMemory, canonical.CampaignId))
            {
                return Invalid;
            }

            List<MemoryRecallFragment> items = new();

            bool candidateLimitReached = false;

            if (request.Scope is MemoryRecallScope.All or MemoryRecallScope.Saga && settings.Features.Saga)
            {
                SagaMemoryCurationRow[] rows = await saga.ListCleanRecallRowsAsync(request.Query, memoryScope,
                    MemoryRecallPager.MaximumCandidatesPerSource, cancellationToken).ConfigureAwait(false);

                if (rows.Length > MemoryRecallPager.MaximumCandidatesPerSource)
                {
                    return Oversized;
                }

                candidateLimitReached |= rows.Length == MemoryRecallPager.MaximumCandidatesPerSource;

                foreach (SagaMemoryCurationRow row in rows)
                {
                    if (row is null || !SagaRetrievalEligibilityClassifier.IsEligible(
                            SagaRetrievalEligibilityClassifier.Classify(row, memoryScope.IsEnforced)))
                    {
                        continue;
                    }

                    SagaMemoryDto memory = row.Memory;

                    if (memoryScope.IsEnforced
                        && memory.ScopeKind != SagaMemoryScopeKind.Global
                        && !(memory.ScopeKind == SagaMemoryScopeKind.Campaign && memory.ScopeCampaignId == memoryScope.CampaignId))
                    {
                        continue;
                    }

                    if (Encoding.UTF8.GetByteCount(memory.Content) > MemoryRecallPager.MaximumCandidateBytes
                        || memory.Source is { Length: > 512 })
                    {
                        return Oversized;
                    }

                    string provenance = $"Saga;createdAt={memory.CreatedAt.ToString("O", CultureInfo.InvariantCulture)};source={memory.Source};sha256={ContentDigest(memory.Content)}";

                    Guid? versionId = null;

                    if (memory.UseCoordinate is { } coordinate)
                    {
                        if (coordinate.ClaimId.Length > 256 || coordinate.VersionId.Length > 256)
                        {
                            return Oversized;
                        }

                        provenance += $";claim={coordinate.ClaimId};version={coordinate.VersionId};digest={coordinate.ContentDigest}";

                        versionId = Guid.TryParse(coordinate.VersionId, out Guid exactVersion) ? exactVersion : null;
                    }

                    if (memory.AttachmentProvenance is { } attachment)
                    {
                        provenance += $";attachment={attachment.AttachmentId:D};attachmentVersion={attachment.Version.ToString(CultureInfo.InvariantCulture)};attachmentHash={attachment.ContentHash};availability={attachment.Availability}";
                    }

                    if (Encoding.UTF8.GetByteCount(provenance) > 2048)
                    {
                        return Oversized;
                    }

                    items.Add(new MemoryRecallFragment(MemoryRecallScope.Saga, memory.Id, memory.Content,
                        null, memory.ScopeCampaignId, versionId, provenance));
                }
            }

            if (request.Scope is MemoryRecallScope.All or MemoryRecallScope.Lexicon
                && settings.ResolveIntelligence().EnableLexiconSystem)
            {
                Result<IReadOnlyList<LexiconEntryDto>> matched = await lexicon.MatchCleanEntitiesAsync(
                    [request.Query], MemoryRecallPager.MaximumCandidatesPerSource,
                    memoryScope.ToLexiconScope(), cancellationToken).ConfigureAwait(false);

                if (matched.IsFailure)
                {
                    return Unavailable;
                }

                if (matched.Value.Count > MemoryRecallPager.MaximumCandidatesPerSource)
                {
                    return Oversized;
                }

                candidateLimitReached |= matched.Value.Count == MemoryRecallPager.MaximumCandidatesPerSource;

                foreach (LexiconEntryDto entry in matched.Value)
                {
                    if (entry.RetiredAtUtc is not null || entry.Eligibility != LexiconRetrievalEligibility.Eligible
                        || entry.ScopeCampaignId is { } owner && owner != memoryScope.ToLexiconScope().CampaignId)
                    {
                        continue;
                    }

                    long bytes = Encoding.UTF8.GetByteCount(entry.Name) + (long)Encoding.UTF8.GetByteCount(entry.Type) + 4;

                    foreach (string fact in entry.Facts)
                    {
                        bytes += Encoding.UTF8.GetByteCount(fact) + 1L;
                    }

                    if (bytes > MemoryRecallPager.MaximumCandidateBytes)
                    {
                        return Oversized;
                    }

                    string content = $"{entry.Name} ({entry.Type})\n{string.Join('\n', entry.Facts)}";

                    string provenance = $"Lexicon;curationGeneration={entry.CurationGeneration.ToString(CultureInfo.InvariantCulture)};updatedAt={entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)};sha256={ContentDigest(content)}";

                    items.Add(new MemoryRecallFragment(MemoryRecallScope.Lexicon, entry.Id.ToString("D"), content,
                        CampaignId: entry.ScopeCampaignId, Provenance: provenance));
                }
            }

            return new MemoryRecallCandidates(items, candidateLimitReached);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable;
        }
    }

    private static string ContentDigest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
