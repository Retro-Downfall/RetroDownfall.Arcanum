using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact normalized Core version-12 tree before erasure evidence was added.</summary>
/// <remarks>
/// Version 13 adds the <c>memory_erasure_</c> objects, appends one index to
/// <c>disclosure_subject_state</c>, and corrects four head comments. The normalized fingerprint this
/// version published ignores comments, but the fixtures for versions 1 to 5 hash raw bytes and inherit
/// every object they do not freeze through this one, so each edited table is frozen here byte for byte.
/// <c>lexicon_fts</c> needs no copy, because <see cref="CoreSchemaVersionTenFixture"/> already freezes it.
/// </remarks>
internal static class CoreSchemaVersionTwelveFixture
{
    internal const string PublishedFingerprint =
        "616E371CA834F78D84C484E4918C4124F8399686B17E1E8D497557303C08063B";

    // Version 13 appends the unfolded-tail index and corrects the leading comment.
    private const string DisclosureSubjectStateSql =
        """
        -- One row per disclosure subject: a logical turn, or a durable operation such as an encrypted
        -- backup. It owns ordinal allocation, the overall counts, and the rolling chain, so exactly one
        -- writer advances them and a receipt can never be counted twice or skipped. Compaction folds detail
        -- into aggregates but is forbidden from touching anything here except the folded watermark.
        CREATE TABLE IF NOT EXISTS disclosure_subject_state (
            OriginInstallationId TEXT NOT NULL CHECK (length(OriginInstallationId) > 0),
            -- CovenantDisclosureSubjectKind: Turn = 1, Operation = 2.
            SubjectKind INTEGER NOT NULL CHECK (SubjectKind IN (1, 2)),
            SubjectId TEXT NOT NULL CHECK (length(SubjectId) > 0),
            -- Open = 1, Orphaned = 2, Completed = 3, Abandoned = 4. Closed because an unknown lifecycle
            -- value would let a subject dispatch after it was supposed to be sealed.
            LifecycleCode INTEGER NOT NULL CHECK (LifecycleCode IN (1, 2, 3, 4)),
            -- Which boot created the subject. A prior-boot Open subject is not this process's to resume, so
            -- startup can tell an adoptable orphan from a live turn without guessing.
            CreatorBootId TEXT NOT NULL CHECK (length(CreatorBootId) > 0),
            LastHeartbeatAtUtc TEXT NOT NULL,
            ClosedAtUtc TEXT NULL,
            ProviderAttemptCount INTEGER NOT NULL CHECK (ProviderAttemptCount >= 0),
            ExternalEffectCount INTEGER NOT NULL CHECK (ExternalEffectCount >= 0),
            LastAllocatedOrdinal INTEGER NOT NULL CHECK (LastAllocatedOrdinal >= 0),
            LastFoldedOrdinal INTEGER NOT NULL CHECK (LastFoldedOrdinal >= 0),
            -- Order-sensitive: it commits to the exact sequence of folded and appended receipts, so a
            -- removed or reordered receipt cannot be hidden by rewriting a count.
            DisclosureChainDigest BLOB NOT NULL CHECK (length(DisclosureChainDigest) = 32),
            PRIMARY KEY (OriginInstallationId, SubjectKind, SubjectId),
            -- A live subject has no close time and a terminal one always does. Without this a reader cannot
            -- tell an abandoned subject from an open one that happened to record a timestamp.
            CHECK (
                (LifecycleCode IN (1, 2) AND ClosedAtUtc IS NULL)
                OR (LifecycleCode IN (3, 4) AND ClosedAtUtc IS NOT NULL)
            ),
            -- Folding can never run ahead of allocation. A folded watermark past the allocated one would
            -- silently skip receipts that have not been written yet.
            CHECK (LastFoldedOrdinal <= LastAllocatedOrdinal)
        );

        -- Startup sweeps prior-boot subjects, and the heartbeat monitor scans live ones.
        CREATE INDEX IF NOT EXISTS idx_disclosure_subject_state_lifecycle_heartbeat
            ON disclosure_subject_state(LifecycleCode, LastHeartbeatAtUtc);

        -- Compaction picks terminal subjects whose tail still has unfolded receipts.
        CREATE INDEX IF NOT EXISTS idx_disclosure_subject_state_lifecycle_folded
            ON disclosure_subject_state(LifecycleCode, LastFoldedOrdinal);

        """;

    // Version 13 corrects only the leading comment. The raw version-1 to version-5 fixtures inherit
    // this object through this one, so the comment edit would move their hashes without this copy.
    private const string ExternalDisclosureStateSql =
        """
        -- The installation-wide joined disclosure state, one row per destination class and revocability.
        -- Subject aggregates join into it once, on terminal folding, using checked addition, Boolean OR,
        -- timestamp maximum, and Bloom OR. Those operations only behave as a semilattice if the identity
        -- element has exactly one encoding, which is what the shape check below pins down: an empty state
        -- that could also be written as a lower bound would make "nothing was ever disclosed" and "at least
        -- zero things were disclosed" indistinguishable, and a join would then quietly preserve the weaker
        -- claim forever.
        CREATE TABLE IF NOT EXISTS external_disclosure_state (
            -- CovenantEgressDestination, one through eight.
            DestinationCode INTEGER NOT NULL CHECK (DestinationCode IN (1, 2, 3, 4, 5, 6, 7, 8)),
            -- CovenantDisclosureRevocability: LocallyRevocable = 1, Nonrevocable = 2.
            RevocabilityCode INTEGER NOT NULL CHECK (RevocabilityCode IN (1, 2)),
            -- CovenantDisclosureCountKind: Exact = 1, LowerBound = 2.
            CountKindCode INTEGER NOT NULL CHECK (CountKindCode IN (1, 2)),
            EverOccurred INTEGER NOT NULL CHECK (EverOccurred IN (0, 1)),
            JoinedCount INTEGER NOT NULL CHECK (JoinedCount >= 0),
            -- Numeric, not the ISO-8601 text every other timestamp uses, because this column is joined by
            -- unsigned maximum and needs a zero identity element that no valid instant can collide with.
            MaxDisclosedAtUtcTicks INTEGER NOT NULL CHECK (MaxDisclosedAtUtcTicks >= 0),
            -- 256 bits of diagnostic evidence, merged by bitwise OR. It never authorizes anything.
            EvidenceBloom BLOB NOT NULL CHECK (length(EvidenceBloom) = 32),
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (DestinationCode, RevocabilityCode),
            -- The empty state has exactly one encoding and it is always Exact. Every nonempty state is
            -- positive in all four components at once, so an EverOccurred bit can never be paired with a
            -- zero count, an absent instant, or an empty Bloom that would make the disclosure look
            -- unevidenced.
            CHECK (
                (EverOccurred = 0
                    AND CountKindCode = 1
                    AND JoinedCount = 0
                    AND MaxDisclosedAtUtcTicks = 0
                    AND EvidenceBloom = zeroblob(32))
                OR (EverOccurred = 1
                    AND JoinedCount >= 1
                    AND MaxDisclosedAtUtcTicks > 0
                    AND EvidenceBloom <> zeroblob(32))
            )
        );

        """;

    // Version 13 corrects only the leading comment, for the same reason as the copy above.
    private const string DisclosureSubjectAggregatesSql =
        """
        -- The folded form of a subject's receipts, one row per destination class and revocability. The key
        -- space is closed at eight destinations by two revocabilities, so a subject can never hold more than
        -- sixteen aggregate rows no matter how many receipts folded into it. That bound is what lets
        -- compaction reclaim an unbounded tail without an unbounded index.
        CREATE TABLE IF NOT EXISTS disclosure_subject_aggregates (
            OriginInstallationId TEXT NOT NULL CHECK (length(OriginInstallationId) > 0),
            -- CovenantDisclosureSubjectKind: Turn = 1, Operation = 2.
            SubjectKind INTEGER NOT NULL CHECK (SubjectKind IN (1, 2)),
            SubjectId TEXT NOT NULL CHECK (length(SubjectId) > 0),
            -- CovenantEgressDestination, one through eight.
            DestinationCode INTEGER NOT NULL CHECK (DestinationCode IN (1, 2, 3, 4, 5, 6, 7, 8)),
            -- CovenantDisclosureRevocability: LocallyRevocable = 1, Nonrevocable = 2.
            RevocabilityCode INTEGER NOT NULL CHECK (RevocabilityCode IN (1, 2)),
            -- CovenantDisclosureCountKind: Exact = 1, LowerBound = 2. Once a fold loses exactness it can
            -- never be regained, so the kind travels with the count rather than being inferred later.
            CountKindCode INTEGER NOT NULL CHECK (CountKindCode IN (1, 2)),
            FoldedCount INTEGER NOT NULL CHECK (FoldedCount >= 1),
            EverOccurred INTEGER NOT NULL CHECK (EverOccurred IN (0, 1)),
            -- Numeric, not the ISO-8601 text every other timestamp uses, because this column is joined by
            -- unsigned maximum and needs a zero identity element that no valid instant can collide with.
            MaxDisclosedAtUtcTicks INTEGER NOT NULL CHECK (MaxDisclosedAtUtcTicks > 0),
            -- 256 bits of diagnostic evidence. It never authorizes replay, read, or erasure; a set bit is a
            -- hint that a receipt digest was folded here, nothing more.
            EvidenceBloom BLOB NOT NULL CHECK (length(EvidenceBloom) = 32),
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (OriginInstallationId, SubjectKind, SubjectId, DestinationCode, RevocabilityCode),
            -- An aggregate row exists only because at least one receipt folded into it. The empty shape
            -- belongs to external_disclosure_state, which must be able to say "nothing happened here"; a
            -- subject aggregate that said the same thing would just be a row that should not exist.
            CHECK (EverOccurred = 1)
        );

        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => !definition.Name.StartsWith("memory_erasure_", StringComparison.Ordinal))
            .Select(static definition => definition.Name switch
            {
                "disclosure_subject_state" => definition with { Sql = DisclosureSubjectStateSql.ReplaceLineEndings("\n") },

                "external_disclosure_state" => definition with { Sql = ExternalDisclosureStateSql.ReplaceLineEndings("\n") },

                "disclosure_subject_aggregates" => definition with { Sql = DisclosureSubjectAggregatesSql.ReplaceLineEndings("\n") },

                _ => definition,
            }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 12,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 12),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
