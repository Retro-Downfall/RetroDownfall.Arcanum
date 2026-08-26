# The Annals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Saga and Lexicon memories an append-only, bitemporal claim substrate with typed assertion origin, guarded current pointers, and bounded structurally-acyclic dependency edges, populated by a conservative upgrade sweep and kept current by live write-through.

**Architecture:** Four new Core-tier tables (`annal_claims`, `annal_versions`, `annal_heads`, `annal_dependencies`) reached through Core schema version 3. A claim binds to the exact durable row that carries its content; versions are immutable statements about that claim; the head is the guarded current pointer. One shared `AnnalsClaimWriter` is the only producer, used by the Saga store, the Lexicon service, and the version-3 backfill, so no two producers can disagree about what a revision means. Nothing reads a head to decide what a turn recalls — this slice changes no retrieval and no prompt bytes.

**Tech Stack:** .NET 10, Native AOT, raw SQL over SQLCipher through the declarative `Data/Schema/**` tree, xunit.

**Spec:** `docs/superpowers/specs/2026-08-26-issue-105-annals-bitemporal-claims-design.md`

## Global Constraints

- Raw SQL only, through the declarative schema tree. One object per file for head objects, one **statement** per file for transition statements. No EF entity, no numbered migration, no compiled-model regeneration.
- Native AOT: no reflection-based `JsonSerializer`, no anonymous DTOs. Config POCOs use `{ get; set; }` and never `init`.
- C# house style: one blank line after each line of code (not around braces or control statements); file-scoped namespaces; positional records for DTOs; primary constructors for DI.
- Core's published version-2 source-definition fingerprint, captured before any schema file was edited and **not recomputable afterwards**: `CEFA40F472EB4815F13B257327F8FA78C00B6F671C78DCAB89E4A38B40646F2C`
- Transition statement text must be the head file's statement text **verbatim**, comments included. A fresh version-3 install and one evolved from version 2 must normalize to the same `sqlite_master` text.
- Deletion order is always **heads, then versions, then claims**. `annal_dependencies` and the `PredecessorVersionId` self-reference cascade; the head-to-version and version-to-claim references deliberately do not.
- Documentation under `docs/` names capabilities and never issues. `grep -nE "#[0-9]{2,4}\b" docs/*.md` must return hits only in `Arcanum.OATH.md`. Files under `docs/superpowers/` are exempt.
- No artificial line wrapping in documentation: one logical block is one physical line.
- Verification gates: `dotnet build RetroDownfall.Arcanum.slnx --no-incremental` must produce zero errors and zero warnings (incremental builds hide analyzer warnings). Clear `tests/**/TestResults` before a full test run or the run can hang.
- **Merge constraint:** Tasks 2 through 7 land on one branch and merge as a unit. Task 2 records version 3; an installation upgraded at an intermediate commit would never re-run the sweep, so no intermediate commit may be released on its own.

---

### Task 1: Core Annals contracts

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Annals/AnnalEnums.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Annals/AnnalLimits.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Annals/AnnalContentDigest.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Annals/AnnalClaimVersion.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Annals/IAnnalsStore.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalContentDigestTests.cs`

**Interfaces:**
- Consumes: `RetroDownfall.Arcanum.Core.Weave.SagaMemoryScopeKind`, `RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity`.
- Produces: `AnnalSubjectStore`, `AnnalOperation`, `AnnalOrigin`, `AnnalDependencyRelation`, `AnnalLimits.MaxDependenciesPerVersion`, `AnnalContentDigest.ForSagaMemory(string)`, `AnnalContentDigest.ForLexiconEntry(string, string)`, `AnnalClaimVersion`, `IAnnalsStore`.

- [ ] **Step 1: Write the failing digest tests**

Create `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalContentDigestTests.cs`:

```csharp
using RetroDownfall.Arcanum.Core.Annals;

namespace RetroDownfall.Arcanum.Tests.Annals;

public sealed class AnnalContentDigestTests
{

    [Fact]
    public void A_saga_digest_is_thirty_two_bytes_and_stable_for_the_same_content()
    {

        byte[] first = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        byte[] second = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

        Assert.Equal(32, first.Length);

        Assert.Equal(first, second);

    }

    [Fact]
    public void Different_saga_content_digests_differently()
    {

        Assert.NotEqual(
            AnnalContentDigest.ForSagaMemory("one conclusion"),
            AnnalContentDigest.ForSagaMemory("another conclusion"));

    }

    /// <summary>
    /// The separator is the whole point. Without it a type ending in text a fact set begins with would
    /// hash identically to a different pair, and two distinct Lexicon states would share one binding.
    /// </summary>
    [Fact]
    public void A_lexicon_digest_separates_the_type_from_the_fact_set()
    {

        Assert.NotEqual(
            AnnalContentDigest.ForLexiconEntry("Person", "alpha"),
            AnnalContentDigest.ForLexiconEntry("PersonAlpha", string.Empty));

    }

    [Fact]
    public void A_lexicon_digest_is_stable_for_the_same_type_and_fact_set()
    {

        Assert.Equal(
            AnnalContentDigest.ForLexiconEntry("Project", "ships on Friday\nwritten in C#"),
            AnnalContentDigest.ForLexiconEntry("Project", "ships on Friday\nwritten in C#"));

    }

}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~AnnalContentDigestTests"`
Expected: FAIL to compile — `AnnalContentDigest` does not exist.

- [ ] **Step 3: Write the enums**

Create `src/RetroDownfall.Arcanum.Core/Annals/AnnalEnums.cs`:

```csharp
namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// Which durable store holds the row a claim is about.
/// </summary>
/// <remarks>
/// Every code is written literally because it is persisted on <c>annal_claims</c> and
/// <c>annal_heads</c>. Renumbering a member would repoint an existing claim at another store, and a
/// store-scoped erasure would then miss rows it promised to remove.
/// </remarks>
public enum AnnalSubjectStore
{

    Saga = 1,

    Lexicon = 2,

}

/// <summary>
/// What one version does to the claim it belongs to.
/// </summary>
/// <remarks>
/// <see cref="Retire"/> is declared and constrained now although nothing writes one yet, so the
/// curation surfaces that will produce it inherit a shape they cannot contradict. A retirement is a
/// tombstone: it binds to no content, which the table enforces.
/// </remarks>
public enum AnnalOperation
{

    Assert = 1,

    Correct = 2,

    Retire = 3,

}

/// <summary>
/// Who asserted a claim version.
/// </summary>
/// <remarks>
/// The distinction between <see cref="OperatorStated"/> and the two agent origins is the one curation
/// and trust need: "the operator said this" and "a model inferred this from a transcript" are not the
/// same warrant, and a surface that could not tell them apart could not ask the right question.
///
/// <para><see cref="SystemBackfilled"/> is separate from all three because a backfilled version is
/// evidence of an upgrade rather than of an assertion. Nobody attested it, so it names no Session.</para>
/// </remarks>
public enum AnnalOrigin
{

    /// <summary>The operator said it.</summary>
    OperatorStated = 1,

    /// <summary>A model wrote it through a tool call it chose to make.</summary>
    AgentAsserted = 2,

    /// <summary>Headless extraction inferred it from a finished transcript. No one chose to state it.</summary>
    AgentExtracted = 3,

    /// <summary>An upgrade classified a row written before the Annals existed.</summary>
    SystemBackfilled = 4,

}

/// <summary>
/// What one dependency edge asserts about the version it points at.
/// </summary>
public enum AnnalDependencyRelation
{

    /// <summary>The dependent version replaces the version it names.</summary>
    Supersedes = 1,

    /// <summary>The dependent version was derived from the version it names.</summary>
    DerivedFrom = 2,

    /// <summary>The dependent version independently agrees with the version it names.</summary>
    Corroborates = 3,

}
```

- [ ] **Step 4: Write the limits**

Create `src/RetroDownfall.Arcanum.Core/Annals/AnnalLimits.cs`:

```csharp
namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// The bounds the Annals schema enforces, restated here so a caller can refuse before the database
/// does.
/// </summary>
/// <remarks>
/// The database is the authority, not this type. A bound that lived only in a writer could be
/// bypassed by any other writer, which is why <c>annal_dependencies</c> carries the same ceiling as a
/// <c>CHECK</c> on its ordinal. These constants exist so a caller can produce a useful message rather
/// than a constraint abort, and any change here is a change to the schema file as well.
/// </remarks>
public static class AnnalLimits
{

    /// <summary>Matches <c>CHECK (Ordinal BETWEEN 1 AND 16)</c> on <c>annal_dependencies</c>.</summary>
    public const int MaxDependenciesPerVersion = 16;

}
```

- [ ] **Step 5: Write the digest**

Create `src/RetroDownfall.Arcanum.Core/Annals/AnnalContentDigest.cs`:

```csharp
using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// The 32-byte binding between a claim version and the exact bytes it was written about.
/// </summary>
/// <remarks>
/// A binding rather than a copy. It proves which content a version describes without being able to
/// reconstruct it, which is what lets an operator erase a memory without leaving a record that still
/// carries what they asked to remove.
/// </remarks>
public static class AnnalContentDigest
{

    /// <summary>
    /// Separates a Lexicon entry's type from its fact set. Without it a type ending in text that the
    /// fact set begins with would hash identically to a different pair, and two distinct states of one
    /// entry would share a binding.
    /// </summary>
    private const char FieldSeparator = '\u001F';

    public static byte[] ForSagaMemory(string content)
    {

        ArgumentNullException.ThrowIfNull(content);

        return SHA256.HashData(Encoding.UTF8.GetBytes(content));

    }

    /// <param name="factsText">
    /// The newline-joined projection <c>lexicon_entries.FactsText</c> stores, not the JSON. The
    /// projection is what changes when a fact is appended, and it is what the full-text index already
    /// agrees is the entry's content.
    /// </param>
    public static byte[] ForLexiconEntry(string type, string factsText)
    {

        ArgumentNullException.ThrowIfNull(type);

        ArgumentNullException.ThrowIfNull(factsText);

        return SHA256.HashData(Encoding.UTF8.GetBytes($"{type}{FieldSeparator}{factsText}"));

    }

}
```

- [ ] **Step 6: Run the digest tests to verify they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~AnnalContentDigestTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Write the read projection and port**

Create `src/RetroDownfall.Arcanum.Core/Annals/AnnalClaimVersion.cs`:

```csharp
using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// One immutable statement of one claim, as a reader sees it.
/// </summary>
/// <remarks>
/// <paramref name="RecordedUntilUtc"/> is <b>derived</b>, never stored: a version's transaction time
/// ends at the moment its successor was recorded, and is open when it has no successor. Storing it
/// would need an update to a row the guard trigger forbids updating, and would be a second
/// measurement of a quantity the successor's own timestamp already states.
///
/// <para><paramref name="ValidToUtc"/> is stored, because a validity end is a fact the version states
/// about the world rather than a consequence of a later write.</para>
/// </remarks>
public sealed record AnnalClaimVersion(
    string VersionId,
    string ClaimId,
    long Sequence,
    int Revision,
    AnnalOperation Operation,
    AnnalOrigin Origin,
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    ContentSensitivity Sensitivity,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ValidToUtc,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? RecordedUntilUtc,
    string? PredecessorVersionId);

/// <summary>One dependency edge, as a reader sees it.</summary>
public sealed record AnnalDependencyEdge(
    string DependentVersionId,
    string DependencyVersionId,
    AnnalDependencyRelation Relation,
    int Ordinal);

/// <summary>A claim's identity and its current pointer.</summary>
public sealed record AnnalClaimHead(
    string ClaimId,
    AnnalSubjectStore SubjectStore,
    string SubjectId,
    string CurrentVersionId,
    int CurrentRevision,
    AnnalOperation CurrentOperation,
    DateTimeOffset UpdatedAtUtc);
```

Create `src/RetroDownfall.Arcanum.Core/Annals/IAnnalsStore.cs`:

```csharp
namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// Read access to the Annals. Deliberately read-only: every write goes through the store that owns
/// the subject row, inside that store's own transaction, so a claim can never commit without the
/// memory it describes.
/// </summary>
public interface IAnnalsStore
{

    /// <summary>The claim for one durable row, or <see langword="null"/> when it has none.</summary>
    /// <remarks>
    /// A row with no claim is a first-class state, not an error: it is what a memory written while the
    /// Annals was disabled looks like, and what every row looks like before the upgrade sweep drains.
    /// </remarks>
    Task<AnnalClaimHead?> GetClaimAsync(
        AnnalSubjectStore subjectStore,
        string subjectId,
        CancellationToken cancellationToken);

    /// <summary>Every version of one claim, oldest revision first.</summary>
    Task<IReadOnlyList<AnnalClaimVersion>> GetVersionsAsync(
        string claimId,
        CancellationToken cancellationToken);

    /// <summary>One version's dependency edges, in ordinal order.</summary>
    Task<IReadOnlyList<AnnalDependencyEdge>> GetDependenciesAsync(
        string versionId,
        CancellationToken cancellationToken);

}
```

- [ ] **Step 8: Build and confirm zero warnings**

Run: `dotnet build RetroDownfall.Arcanum.slnx --no-incremental`
Expected: 0 errors, 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Annals tests/RetroDownfall.Arcanum.Tests/Annals
git commit -m "feat(annals): add the claim, origin, and content-binding contracts"
```

---

### Task 2: The Annals schema and the version-3 upgrade

This is one task rather than three because a reviewer could not sensibly approve "version 3 schema with no sweep" while rejecting the sweep. The deliverable is a single sentence: an installation at version 2 upgrades to version 3 and every existing Saga memory and Lexicon entry has a claim.

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_claims.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_versions.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_heads.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_dependencies.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_claims_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_versions_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_dependencies_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_heads_validate_update.sql`
- Create: nineteen files under `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V3/` (listed in Step 6)
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/MemoryAnnalsBackfill.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTwoFixture.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/MemoryAnnalsEvolutionTests.cs`
- Test: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/AnnalsSchemaInvariantTests.cs`

**Interfaces:**
- Consumes: Task 1's `AnnalSubjectStore`, `AnnalOperation`, `AnnalOrigin`, `AnnalDependencyRelation`, `AnnalContentDigest`.
- Produces: `AnnalsClaimWriter.AppendAssertAsync`, `AnnalsClaimWriter.AppendCorrectionAsync`, `AnnalsClaimWriter.DeleteClaimsForSubjectAsync`, `AnnalsClaimWriter.DeleteClaimsForStoreAsync`, `CoreSchemaVersionTwoFixture.Fingerprint`, `CoreSchemaVersionTwoFixture.ChainSet()`.

- [ ] **Step 1: Write the version-two fixture and the failing pin test**

Create `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTwoFixture.cs`:

```csharp
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// The Core head tree as it stood at schema version 2, reconstructed so an upgrade can be driven from a
/// real version-2 installation rather than from a hand-written metadata row.
/// </summary>
/// <remarks>
/// Version 3 only <i>adds</i> objects and edits none, so the reconstruction is the shipped Core tree
/// with every Annals object removed. That is also what keeps it honest: the fingerprint this list
/// produces is compared against the pin the shipped chain carries for version 2, so a reconstruction
/// that drifted fails here rather than quietly certifying the wrong pin.
///
/// <para>A later version step that <i>edits</i> a Core object has to freeze that object's version-2 text
/// here, exactly as <see cref="CoreSchemaVersionOneFixture"/> freezes two objects, or the reconstruction
/// stops describing version 2 and the pin assertion says so.</para>
/// </remarks>
internal static class CoreSchemaVersionTwoFixture
{

    /// <summary>The prefix every object version 3 introduced shares.</summary>
    private const string AnnalsObjectPrefix = "annal_";

    /// <summary>Every Core object as version 2 declared it.</summary>
    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects.Where(
            static definition => !definition.Name.StartsWith(AnnalsObjectPrefix, StringComparison.Ordinal)),
    ];

    /// <summary>The fingerprint the version-2 tree published, computed from the reconstruction above.</summary>
    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    /// <summary>
    /// An installable version-2 chain set: the reconstructed Core tree at version 2 with no step, and
    /// the shipped chains for both Covenant tiers.
    /// </summary>
    /// <remarks>
    /// Installing this and then handing the same installer <see cref="GrimoireSchemaVersionChains.Default"/>
    /// is the whole of an upgrade as a caller reaches it. Nothing writes a metadata row or a journal row
    /// to describe the older installation, so no assertion rests on a state a test invented.
    /// </remarks>
    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 2,
                    Fingerprint,
                    Objects),
                Objects,
                []),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

}
```

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/MemoryAnnalsEvolutionTests.cs` with the pin test only for now:

```csharp
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The upgrade that gives every existing durable memory a claim, driven the way a host reaches it:
/// install version 2, then hand the same installer the shipped chain and let the shipped driver drain
/// the sweep.
/// </summary>
public sealed class MemoryAnnalsEvolutionTests
{

    /// <summary>
    /// The pin is a literal captured before the version-2 tree was edited. A wrong pin means every
    /// version-2 installation refuses the upgrade with <c>SourceDefinitionMismatch</c>, so it must fail
    /// here instead.
    /// </summary>
    [Fact]
    public void The_shipped_chain_pins_the_fingerprint_the_version_two_tree_published()
    {

        GrimoireSchemaVersionChain core =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(CoreSchemaVersionTwoFixture.Fingerprint, core.SourceDefinitionFingerprintFor(2));

    }

}
```

- [ ] **Step 2: Run the pin test to verify it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~MemoryAnnalsEvolutionTests"`
Expected: FAIL — `SourceDefinitionFingerprintFor(2)` returns the head manifest's fingerprint for version 2 (Core is still at head version 2), which is the *current* tree including nothing new; once the schema files land in Step 3 this becomes a genuine mismatch. Confirm the test runs and reports a comparison failure rather than a setup error.

- [ ] **Step 3: Write the four table files**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_claims.sql`:

```sql
-- A claim is the identity a durable assertion keeps across every correction. It binds to the exact row
-- that carries its content, and that binding lives here rather than on each version. A Lexicon
-- correction rewrites one row in place, so every revision of that claim names the same
-- lexicon_entries.Id; a per-version binding with a unique index over it would refuse the second
-- revision, and without the index two claims could quietly own one row.
CREATE TABLE IF NOT EXISTS annal_claims (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    SubjectId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

-- One durable row has at most one claim. This is also what makes the upgrade sweep idempotent: a batch
-- selects rows for which no claim exists, so the corpus shrinks by exactly the work that committed and
-- a re-run after a lost commit selects the same rows rather than claiming them twice.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_subject
ON annal_claims(SubjectStoreCode, SubjectId);

-- The candidate key annal_heads carries a composite foreign key to. It proves a head's store is the
-- store its own claim belongs to, which no single-column reference to ClaimId could.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_store_candidate
ON annal_claims(ClaimId, SubjectStoreCode);
```

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_versions.sql`:

```sql
-- One immutable statement of one claim: who asserted it, whose memory it is, how sensitive it is, when
-- it was true, and when Arcanum came to hold it.
--
-- Sequence is an INTEGER PRIMARY KEY, which is SQLite's rowid alias, so the engine allocates it inside
-- the insert statement. An explicit MAX(Sequence) + 1 would race under the deferred transaction the
-- Saga insert path opens, and the resulting unique-constraint abort is not a SQLITE_BUSY and would
-- therefore not be retried. The allocation order is what annal_dependencies uses to make a cycle
-- unrepresentable.
--
-- Transaction time has only one column. A version's belief ends at the RecordedAtUtc of the version
-- whose PredecessorVersionId names it, and is open when none does. Storing that end would need an
-- update to a row annal_versions_guard_update forbids updating, and would be a second measurement of a
-- quantity the successor's own timestamp already states. Valid time keeps both ends, because a validity
-- end is a fact the version states about the world rather than a consequence of a later write.
CREATE TABLE IF NOT EXISTS annal_versions (
    Sequence INTEGER PRIMARY KEY,
    VersionId TEXT NOT NULL,
    ClaimId TEXT NOT NULL REFERENCES annal_claims(ClaimId),
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2, 3)),
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3, 4)),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL,
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    ContentHash BLOB NULL CHECK (ContentHash IS NULL OR length(ContentHash) = 32),
    ValidFromUtc TEXT NOT NULL,
    ValidToUtc TEXT NULL,
    RecordedAtUtc TEXT NOT NULL,
    PredecessorVersionId TEXT NULL REFERENCES annal_versions(VersionId) ON DELETE CASCADE,
    SourceSessionId TEXT NULL,
    -- A Campaign-scoped version names its Campaign; no other kind borrows one. The two unresolved kinds
    -- are deliberately reachable here, because a version that copies an unresolved subject's scope must
    -- be able to say so rather than rounding it up to installation-global.
    CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL)),
    -- A retirement is a tombstone and binds to no content. Letting one carry a hash would leave a record
    -- of exactly the bytes the retirement was meant to stop standing behind.
    CHECK ((OperationCode = 3 AND ContentHash IS NULL) OR (OperationCode <> 3 AND ContentHash IS NOT NULL)),
    -- Revision one begins a claim and has no predecessor; every later revision links to exactly one.
    CHECK ((Revision = 1 AND PredecessorVersionId IS NULL) OR (Revision > 1 AND PredecessorVersionId IS NOT NULL)),
    -- Both columns are round-trip "o"-format UTC text, which orders lexicographically, so this compares
    -- instants rather than coincidences of formatting.
    CHECK (ValidToUtc IS NULL OR ValidToUtc >= ValidFromUtc),
    -- A version nobody attested cannot name a Session as its source.
    CHECK (OriginCode <> 4 OR SourceSessionId IS NULL)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_version
ON annal_versions(VersionId);

-- The candidate key annal_dependencies carries both of its composite foreign keys to. Binding an edge's
-- recorded sequence to the version it names is what stops the ordering check from being told a lie.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_sequence_candidate
ON annal_versions(VersionId, Sequence);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_claim_revision
ON annal_versions(ClaimId, Revision);

-- The candidate key annal_heads carries a composite foreign key to. A plain reference to VersionId would
-- let a head adopt a version belonging to another claim, or one whose revision and operation disagree
-- with the head's own columns.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_head_candidate
ON annal_versions(VersionId, ClaimId, Revision, OperationCode);

CREATE INDEX IF NOT EXISTS idx_annal_versions_claim_recorded
ON annal_versions(ClaimId, RecordedAtUtc);
```

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_heads.sql`:

```sql
-- The guarded current pointer. A head is meant to move; what it must never do is move backwards or
-- change what it is a pointer to, which annal_heads_validate_update enforces.
CREATE TABLE IF NOT EXISTS annal_heads (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    CurrentVersionId TEXT NOT NULL,
    CurrentRevision INTEGER NOT NULL CHECK (CurrentRevision > 0),
    CurrentOperationCode INTEGER NOT NULL CHECK (CurrentOperationCode IN (1, 2, 3)),
    UpdatedAtUtc TEXT NOT NULL,
    -- Both references are composite, and that is the point. A plain reference to VersionId would let a
    -- head adopt a version belonging to another claim; a plain reference to ClaimId would let a head
    -- claim a store its claim does not belong to, and a store-scoped erasure would then miss it.
    FOREIGN KEY (CurrentVersionId, ClaimId, CurrentRevision, CurrentOperationCode)
        REFERENCES annal_versions(VersionId, ClaimId, Revision, OperationCode),
    FOREIGN KEY (ClaimId, SubjectStoreCode) REFERENCES annal_claims(ClaimId, SubjectStoreCode)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_heads_current_version
ON annal_heads(CurrentVersionId);

-- A store-scoped erasure reads this to find the heads it must release before the versions may go.
CREATE INDEX IF NOT EXISTS idx_annal_heads_store
ON annal_heads(SubjectStoreCode);
```

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/annal_dependencies.sql`:

```sql
-- Bounded, deterministic, cycle-safe edges that target exact retained versions.
--
-- Cycle safety is structural rather than procedural. Each edge carries both endpoints' allocation
-- sequences, each bound to its version by a composite foreign key so neither can be misstated, and the
-- ordering check refuses any edge that does not point strictly backwards. A cycle needs at least one
-- edge that does not, so this table cannot hold one. There is no traversal, no recursive query, and no
-- detector to get wrong -- and no way for a future writer to bypass the rule by taking another code
-- path, because the rule is in the database.
--
-- Sequence reuse after a deletion does not weaken that. Edges cascade away with the version they name,
-- so at every instant every live edge satisfies the strict ordering, which is a directed acyclic graph
-- by construction.
CREATE TABLE IF NOT EXISTS annal_dependencies (
    DependentVersionId TEXT NOT NULL,
    DependentSequence INTEGER NOT NULL,
    DependencyVersionId TEXT NOT NULL,
    DependencySequence INTEGER NOT NULL,
    RelationCode INTEGER NOT NULL CHECK (RelationCode IN (1, 2, 3)),
    -- The ceiling lives here rather than in a writer, so the seventeenth edge is refused whatever
    -- produced it. AnnalLimits.MaxDependenciesPerVersion restates it and is not the authority.
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (DependentVersionId, DependencyVersionId),
    FOREIGN KEY (DependentVersionId, DependentSequence)
        REFERENCES annal_versions(VersionId, Sequence) ON DELETE CASCADE,
    FOREIGN KEY (DependencyVersionId, DependencySequence)
        REFERENCES annal_versions(VersionId, Sequence) ON DELETE CASCADE,
    CHECK (DependencySequence < DependentSequence)
);

-- One stable total order over a version's edges, so two readers of one claim see the same dependency
-- list in the same order. The unique constraint is also the bound: sixteen legal ordinals, one row each.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_dependencies_dependent_ordinal
ON annal_dependencies(DependentVersionId, Ordinal);

CREATE INDEX IF NOT EXISTS idx_annal_dependencies_dependency
ON annal_dependencies(DependencyVersionId);
```

- [ ] **Step 4: Write the four trigger files**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_claims_guard_update.sql`:

```sql
-- A claim is an identity, and an identity that could be edited would let one claim's history be
-- reattributed to another durable row after the fact.
CREATE TRIGGER IF NOT EXISTS annal_claims_guard_update
BEFORE UPDATE ON annal_claims
BEGIN
    SELECT RAISE(ABORT, 'annal_claims is append-only; existing rows cannot be updated.');
END;
```

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_versions_guard_update.sql`:

```sql
-- A version is the immutable record of one assertion. Its content binding says which bytes it was
-- written about and its timestamps say when it was believed; editing a row in place would leave that
-- evidence describing something else. A correction is written as the next revision.
CREATE TRIGGER IF NOT EXISTS annal_versions_guard_update
BEFORE UPDATE ON annal_versions
BEGIN
    SELECT RAISE(ABORT, 'annal_versions is append-only; a correction is the next revision.');
END;
```

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_dependencies_guard_update.sql`:

```sql
-- An edge is part of the version that owns it. Repointing one in place would move a dependency without
-- moving the revision that asserted it, and the ordering check would then be validating a claim nobody
-- made.
CREATE TRIGGER IF NOT EXISTS annal_dependencies_guard_update
BEFORE UPDATE ON annal_dependencies
BEGIN
    SELECT RAISE(ABORT, 'annal_dependencies is append-only; existing edges cannot be repointed.');
END;
```

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/annal_heads_validate_update.sql`:

```sql
-- The one table here that is meant to change, and the only three ways it must not. A head that could
-- move backwards would make a superseded version current again; one that could change its claim or its
-- store would silently relabel a memory's whole history.
CREATE TRIGGER IF NOT EXISTS annal_heads_validate_update
BEFORE UPDATE ON annal_heads
WHEN NEW.CurrentRevision <= OLD.CurrentRevision
    OR NEW.ClaimId <> OLD.ClaimId
    OR NEW.SubjectStoreCode <> OLD.SubjectStoreCode
BEGIN
    SELECT RAISE(ABORT, 'annal_heads may only advance to a higher revision of the same claim.');
END;
```

- [ ] **Step 5: Verify the head tree installs and the schema invariants hold**

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/AnnalsSchemaInvariantTests.cs`. These tests write directly to the tables rather than through `AnnalsClaimWriter`, deliberately: the claim under test is that the **schema** refuses these things, so a test that could only reach them through the writer would prove nothing about a future writer.

```csharp
using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The invariants the Annals delegates to SQLite rather than to a writer.
/// </summary>
/// <remarks>
/// Every case here writes directly to the tables. That is the point: the assertion is that the schema
/// refuses these things whatever produced them, and a case that could only be reached through
/// <c>AnnalsClaimWriter</c> would prove only that one writer behaves.
/// </remarks>
public sealed class AnnalsSchemaInvariantTests
{

    static AnnalsSchemaInvariantTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task An_edge_that_does_not_point_strictly_backwards_is_refused()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string first, long firstSequence) = await scratch.SeedVersionAsync("claim-a", revision: 1);

        (string second, long secondSequence) = await scratch.SeedVersionAsync("claim-b", revision: 1);

        // Backwards is legal.
        await scratch.SeedEdgeAsync(second, secondSequence, first, firstSequence);

        // Forwards is not, and neither is a self-edge, which the ordering check also excludes.
        SqliteException forwards = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(first, firstSequence, second, secondSequence));

        Assert.Contains("CHECK constraint failed", forwards.Message, StringComparison.Ordinal);

        SqliteException self = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(first, firstSequence, first, firstSequence));

        Assert.Contains("CHECK constraint failed", self.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_seventeenth_edge_on_one_version_is_refused()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        List<(string VersionId, long Sequence)> targets = [];

        for (int index = 0; index < 17; index++)
        {

            targets.Add(await scratch.SeedVersionAsync($"claim-{index}", revision: 1));

        }

        (string dependent, long dependentSequence) = await scratch.SeedVersionAsync("claim-dependent", revision: 1);

        for (int index = 0; index < 16; index++)
        {

            await scratch.SeedEdgeAsync(
                dependent,
                dependentSequence,
                targets[index].VersionId,
                targets[index].Sequence,
                ordinal: index + 1);

        }

        SqliteException overflow = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(
                dependent,
                dependentSequence,
                targets[16].VersionId,
                targets[16].Sequence,
                ordinal: 17));

        Assert.Contains("CHECK constraint failed", overflow.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Claims_versions_and_edges_all_refuse_an_update()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string version, long sequence) = await scratch.SeedVersionAsync("claim-a", revision: 1);

        (string other, long otherSequence) = await scratch.SeedVersionAsync("claim-b", revision: 1);

        await scratch.SeedEdgeAsync(other, otherSequence, version, sequence);

        Assert.Contains(
            "append-only",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.ExecuteAsync("UPDATE annal_claims SET SubjectId = 'moved';"))).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "append-only",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.ExecuteAsync("UPDATE annal_versions SET RecordedAtUtc = '2030-01-01T00:00:00.0000000+00:00';"))).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "append-only",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.ExecuteAsync("UPDATE annal_dependencies SET RelationCode = 2;"))).Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_head_may_advance_but_never_retreat_or_change_claim()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string first, _) = await scratch.SeedVersionAsync("claim-a", revision: 1);

        (string second, _) = await scratch.SeedVersionAsync("claim-a", revision: 2, predecessorVersionId: first);

        await scratch.SeedHeadAsync("claim-a", first, revision: 1);

        await scratch.AdvanceHeadAsync("claim-a", second, revision: 2);

        Assert.Contains(
            "may only advance",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.AdvanceHeadAsync("claim-a", first, revision: 1))).Message,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A retirement binds to no content, and a non-retirement must bind to some. Both halves matter:
    /// without the second, a claim could be asserted about nothing at all.
    /// </summary>
    [Fact]
    public async Task A_retirement_carries_no_content_hash_and_an_assertion_must()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedVersionAsync("claim-a", revision: 1, operationCode: 3, withContentHash: true));

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedVersionAsync("claim-b", revision: 1, operationCode: 1, withContentHash: false));

    }

}
```

The `AnnalsScratch` helper belongs in the same file, below the test class. It opens an `EvolutionScratchDatabase`, installs `GrimoireSchemaVersionChains.Default` through `GrimoireSchemaTestInstaller.InstallAsync`, and exposes:

- `SeedVersionAsync(string claimId, int revision, string? predecessorVersionId = null, int operationCode = 1, bool withContentHash = true)` — inserts the `annal_claims` row when it does not exist (subject id `$"subject-{claimId}"`, store code 1), inserts an `annal_versions` row with `OriginCode = 4`, `ScopeKindCode = 1`, `CampaignId` null, `SensitivityCode = 0`, `ContentHash` a 32-byte array when requested, `ValidFromUtc` and `RecordedAtUtc` a fixed `"o"`-format timestamp, `SourceSessionId` null; returns the generated `VersionId` and the `Sequence` SQLite allocated (read back with `SELECT Sequence FROM annal_versions WHERE VersionId = $id`).
- `SeedEdgeAsync(string dependentVersionId, long dependentSequence, string dependencyVersionId, long dependencySequence, int ordinal = 1)`.
- `SeedHeadAsync(string claimId, string versionId, int revision)` and `AdvanceHeadAsync(...)` (an `UPDATE annal_heads SET CurrentVersionId = ..., CurrentRevision = ..., UpdatedAtUtc = ... WHERE ClaimId = ...`).
- `ExecuteAsync(string sql)`.

Every insert must run with foreign keys enforced. Open the connection through `EvolutionScratchDatabase.OpenAsync` and execute `PRAGMA foreign_keys=ON;` immediately after opening, so a composite-key violation surfaces as a test failure rather than passing silently.

- [ ] **Step 6: Write the nineteen version-3 transition statements**

Under `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V3/`, one statement per file, each carrying the corresponding head-file statement **verbatim including its comments**:

| File | Statement copied from |
|---|---|
| `010_annal_claims.sql` | `Tables/annal_claims.sql`, the `CREATE TABLE` |
| `011_annal_claims_subject_index.sql` | `ux_annal_claims_subject` |
| `012_annal_claims_store_candidate_index.sql` | `ux_annal_claims_store_candidate` |
| `020_annal_versions.sql` | `Tables/annal_versions.sql`, the `CREATE TABLE` |
| `021_annal_versions_version_index.sql` | `ux_annal_versions_version` |
| `022_annal_versions_sequence_candidate_index.sql` | `ux_annal_versions_sequence_candidate` |
| `023_annal_versions_claim_revision_index.sql` | `ux_annal_versions_claim_revision` |
| `024_annal_versions_head_candidate_index.sql` | `ux_annal_versions_head_candidate` |
| `025_annal_versions_claim_recorded_index.sql` | `idx_annal_versions_claim_recorded` |
| `030_annal_heads.sql` | `Tables/annal_heads.sql`, the `CREATE TABLE` |
| `031_annal_heads_current_version_index.sql` | `ux_annal_heads_current_version` |
| `032_annal_heads_store_index.sql` | `idx_annal_heads_store` |
| `040_annal_dependencies.sql` | `Tables/annal_dependencies.sql`, the `CREATE TABLE` |
| `041_annal_dependencies_dependent_ordinal_index.sql` | `ux_annal_dependencies_dependent_ordinal` |
| `042_annal_dependencies_dependency_index.sql` | `idx_annal_dependencies_dependency` |
| `050_annal_claims_guard_update.sql` | `Triggers/annal_claims_guard_update.sql` |
| `060_annal_versions_guard_update.sql` | `Triggers/annal_versions_guard_update.sql` |
| `070_annal_dependencies_guard_update.sql` | `Triggers/annal_dependencies_guard_update.sql` |
| `080_annal_heads_validate_update.sql` | `Triggers/annal_heads_validate_update.sql` |

Copy the text; do not retype it. A single character of difference between a transition statement and its head file makes an evolved installation report `DefinitionDrift` while every fresh one stays green, which is the hardest shape of that failure to reproduce because a developer's own database is always fresh.

- [ ] **Step 7: Write the claim writer**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs`. It is `internal static`, takes the caller's live `DbConnection` and `DbTransaction`, and is the single implementation the Saga store, the Lexicon service, and the backfill all share — three copies would be three ideas of what a revision means, and a claim written by the live path would eventually disagree with one written by the sweep.

Required members:

```csharp
internal static Task<bool> AppendAssertAsync(
    DbConnection connection,
    DbTransaction transaction,
    AnnalSubjectStore subjectStore,
    string subjectId,
    AnnalOrigin origin,
    SagaMemoryScopeKind scopeKind,
    string? campaignId,
    ContentSensitivity sensitivity,
    byte[] contentHash,
    DateTimeOffset validFrom,
    DateTimeOffset recordedAt,
    Guid? sourceSessionId,
    CancellationToken cancellationToken);
```

Returns `false` without writing when a claim already exists for `(subjectStore, subjectId)`, which is what makes the sweep idempotent. Otherwise inserts the claim, inserts revision 1 with `OperationCode = 1`, inserts the head, and returns `true`.

```csharp
internal static Task<bool> AppendCorrectionAsync(
    DbConnection connection,
    DbTransaction transaction,
    AnnalSubjectStore subjectStore,
    string subjectId,
    AnnalOrigin origin,
    SagaMemoryScopeKind scopeKind,
    string? campaignId,
    ContentSensitivity sensitivity,
    byte[] contentHash,
    DateTimeOffset validFrom,
    DateTimeOffset recordedAt,
    Guid? sourceSessionId,
    CancellationToken cancellationToken);
```

Reads the claim and its head version. When there is no claim it delegates to `AppendAssertAsync`. When the head version's `ContentHash` equals `contentHash` it returns `false` and writes nothing — that comparison is what stops a repeated `scribe_lexicon` call restating a known fact from appending a revision that records no change. Otherwise it inserts revision `head.CurrentRevision + 1` with `OperationCode = 2` and `PredecessorVersionId = head.CurrentVersionId`, inserts one `annal_dependencies` row at ordinal 1 with `RelationCode = 1`, updates the head, and returns `true`.

```csharp
internal static Task DeleteClaimsForSubjectAsync(
    DbConnection connection,
    DbTransaction transaction,
    AnnalSubjectStore subjectStore,
    string subjectId,
    CancellationToken cancellationToken);

internal static Task DeleteClaimsForStoreAsync(
    DbConnection connection,
    DbTransaction transaction,
    AnnalSubjectStore subjectStore,
    CancellationToken cancellationToken);
```

Both issue three statements in the fixed order — heads, versions, claims — because SQLite enforces an immediate foreign key as each row is deleted rather than at the end of the statement. Edges and predecessor chains fall away by cascade. The subject-scoped form:

```sql
DELETE FROM annal_heads
WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = $store AND SubjectId = $subject);

DELETE FROM annal_versions
WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = $store AND SubjectId = $subject);

DELETE FROM annal_claims WHERE SubjectStoreCode = $store AND SubjectId = $subject;
```

The store-scoped form is the same three statements with the `SubjectId` predicate dropped and `annal_heads` filtered on its own `SubjectStoreCode` column.

Timestamps are written with `ToString("o", CultureInfo.InvariantCulture)`, matching every other timestamp in the Grimoire and the lexicographic ordering the validity check depends on. `VersionId` and `ClaimId` are `Guid.NewGuid().ToString()`. `Sequence` is never supplied — SQLite allocates it, and the writer reads it back with `SELECT Sequence FROM annal_versions WHERE VersionId = $id` when it needs it for an edge.

- [ ] **Step 8: Write the backfill**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/MemoryAnnalsBackfill.cs`, modelled on `SagaMemoryCampaignScopeBackfill.cs` in the same folder.

- `Name => "memory-annals-claims"`.
- `MaxRowsPerBatch => 200`.
- Each batch reads one bounded page of Saga memories with no claim, then, if room remains, one bounded page of Lexicon entries with no claim. **The whole read completes before the first write**, because the selecting query filters on the absence of rows in the table the write inserts into, and writing to a table an open cursor is still filtering against is the case SQLite leaves undefined.

Saga page:

```sql
SELECT memory.Id, memory.Content, memory.CreatedAt, memory.ScopeKindCode, memory.CampaignId
FROM saga_memories AS memory
WHERE NOT EXISTS (
    SELECT 1 FROM annal_claims AS claim
    WHERE claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id)
ORDER BY memory.Id
LIMIT $limit;
```

Lexicon page:

```sql
SELECT entry.Id, entry.Type, entry.FactsText, entry.UpdatedAt, entry.ScopeCampaignId
FROM lexicon_entries AS entry
WHERE NOT EXISTS (
    SELECT 1 FROM annal_claims AS claim
    WHERE claim.SubjectStoreCode = 2 AND claim.SubjectId = entry.Id)
ORDER BY entry.Id
LIMIT $limit;
```

Each row is then appended through `AnnalsClaimWriter.AppendAssertAsync` with `origin: AnnalOrigin.SystemBackfilled`, `sourceSessionId: null`, `sensitivity: ContentSensitivity.None`, and:

- Saga: `scopeKind` and `campaignId` **copied verbatim from the row**, `contentHash: AnnalContentDigest.ForSagaMemory(content)`, `validFrom` and `recordedAt` both the row's `CreatedAt`.
- Lexicon: `scopeKind` is `Global` when `ScopeCampaignId` is the empty string and `Campaign` otherwise (with that value as `campaignId`), `contentHash: AnnalContentDigest.ForLexiconEntry(type, factsText)`, `validFrom` and `recordedAt` both the row's `UpdatedAt`.

Copying the Saga scope verbatim is the acceptance criterion, not an implementation detail: a row at `Unclassified` stays `Unclassified` and a row at `LegacyUnresolved` stays `LegacyUnresolved`. Neither becomes `Global`, because an installation-global claim is retrievable inside every Campaign and a memory whose ownership was never resolved has no authority to become one.

Return `new GrimoireSchemaBackfillBatch(NextCursor: null, RowsProcessed: written, IsComplete: written == 0)`. There is no cursor: the predicate is its own position, so the corpus shrinks by exactly the work that commits and a crash before commit re-selects the same rows.

- [ ] **Step 9: Wire the version-3 step onto the chain**

In `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`:

- Change `CoreSchemaVersion` to `3` and update its `<remarks>` to describe what version 3 gave the durable schema.
- Add to `SourcePins`:

```csharp
            // Read out of the Core head tree immediately before the Annals objects were added. Nothing
            // can recompute it: the tree that produced it no longer exists. CoreSchemaVersionTwoFixture
            // reconstructs that tree by removing the Annals objects from the shipped list and a test
            // hashes it, so a wrong value here fails there rather than against every operator's
            // version-2 installation.
            [(GrimoireSchemaTransactionTier.Core, 3)] =
                "CEFA40F472EB4815F13B257327F8FA78C00B6F671C78DCAB89E4A38B40646F2C",
```

- Add to `Backfills`:

```csharp
            // Version 3's objects are all new, so the step's DDL needs no sweep to be correct. The sweep
            // is what makes it useful: without it the Annals would hold nothing but claims written after
            // the upgrade, and every memory an installation already had would be unexplained.
            [(GrimoireSchemaTransactionTier.Core, 3)] = new MemoryAnnalsBackfill(),
```

- [ ] **Step 10: Run the pin test to verify it now passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~MemoryAnnalsEvolutionTests"`
Expected: PASS. If it fails, the pin is wrong or a Core `.sql` file outside the Annals was edited — do not "fix" it by pasting the computed value; find out which object moved.

- [ ] **Step 11: Add the upgrade-journey tests**

Extend `MemoryAnnalsEvolutionTests` with a `MemoryAnnalsUpgradeHarness` modelled directly on `CampaignScopeUpgradeHarness` in `SagaMemoryCampaignScopeEvolutionTests.cs` — same `EvolutionScratchDatabase`, same `GrimoireSchemaTestInstaller`, same `GrimoireSchemaTransitionCoordinator`, same `FixedCoreConnectionSource` — but starting from `CoreSchemaVersionTwoFixture.ChainSet()` instead of `CoreSchemaVersionOneFixture.ChainSet()`.

Because the harness starts at version 2, it seeds version-2 rows directly, which is legitimate: a version-2 `saga_memories` row with `ScopeKindCode = 0` is a state no writer in this binary can produce, and everything the tests **assert** — the claim each row receives — is produced by production code from that state.

Cases:

```csharp
    [Fact]
    public async Task Every_existing_saga_memory_and_lexicon_entry_receives_exactly_one_claim()

    [Fact]
    public async Task A_backfilled_claim_records_that_nobody_attested_it()
        // origin is SystemBackfilled and SourceSessionId is null

    [Fact]
    public async Task A_backfilled_version_is_recorded_at_the_memorys_own_timestamp_and_not_the_sweeps()

    [Fact]
    public async Task An_unclassified_saga_memory_is_never_laundered_into_global_authority()
        // seeded ScopeKindCode 0 -> version ScopeKindCode 0

    [Fact]
    public async Task A_legacy_unresolved_saga_memory_is_never_laundered_into_global_authority()
        // seeded ScopeKindCode 3 -> version ScopeKindCode 3

    [Fact]
    public async Task A_campaign_scoped_saga_memory_keeps_its_campaign()

    [Fact]
    public async Task A_global_lexicon_entry_is_claimed_global_and_a_campaign_scoped_one_names_its_campaign()

    [Fact]
    public async Task The_backfill_is_idempotent_and_safe_to_interrupt()
        // seed 60 memories and 60 entries, drive UpgradeInOnePassAtATimeAsync,
        // assert one claim each, then assert a further pass reports Advanced == false
        // and the claim count is unchanged

    [Fact]
    public async Task An_interrupted_upgrade_leaves_the_tier_below_head_until_the_sweep_drains()
```

- [ ] **Step 12: Run the full schema and evolution suites**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema"`
Expected: PASS, including the pre-existing `SagaMemoryCampaignScopeEvolutionTests` and `GrimoireSchemaEvolutionInstallerTests`, which must be unaffected.

- [ ] **Step 13: Build clean and commit**

Run: `dotnet build RetroDownfall.Arcanum.slnx --no-incremental` — 0 errors, 0 warnings.

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data tests/RetroDownfall.Arcanum.Tests/Data/Schema tests/RetroDownfall.Arcanum.Tests/Fixtures
git commit -m "feat(annals): install the claim substrate as Core schema version 3"
```

---

### Task 3: The feature gate and Saga write-through

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Configuration/PublicConfigurationSettings.cs` (the `FeatureSettings` record, after `CampaignScopedMemory`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:120-210` (`InsertCoreAsync`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs`

**Interfaces:**
- Consumes: `AnnalsClaimWriter.AppendAssertAsync`, `AnnalContentDigest.ForSagaMemory`, `AnnalOrigin.AgentExtracted`.
- Produces: `FeatureSettings.Annals`.

- [ ] **Step 1: Write the failing write-through tests**

Create `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs`. Enter through `ISagaMemoryStore` resolved from the same DI container the host builds, never by constructing `SagaMemoryStore` directly — the store is registered `AddScoped<ISagaMemoryStore, SagaMemoryStore>()` in `ServiceCollectionExtensions.cs:1194`, and the interface is what every production caller holds.

```csharp
    [Fact]
    public async Task An_inserted_memory_receives_a_claim_asserting_the_content_that_was_stored()
        // gate on; insert through ISagaMemoryStore; assert exactly one claim, one version at
        // revision 1 with OperationCode 1, one head, OriginCode 3 (AgentExtracted), and a
        // ContentHash equal to AnnalContentDigest.ForSagaMemory(content)

    [Fact]
    public async Task An_inserted_memorys_claim_carries_the_scope_the_store_derived()
        // a Campaign-bound Session's memory yields ScopeKindCode 2 and that Campaign,
        // proving the claim reuses the classifier's answer rather than re-deriving one

    [Fact]
    public async Task With_the_gate_off_an_inserted_memory_receives_no_claim_and_is_stored_unchanged()

    [Fact]
    public async Task A_claim_and_its_memory_commit_together()
        // insert two memories, assert claim count equals memory count
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaAnnalsWriteThroughTests"`
Expected: FAIL — no claim rows are written.

- [ ] **Step 3: Add the feature key**

In `FeatureSettings`, after `CampaignScopedMemory`:

```csharp
    /// <summary>
    /// Whether durable memory records what it claimed, when that was true, and when Arcanum came to
    /// hold it. Default <c>false</c>, and the default is the contract: with it unset, nothing is
    /// appended and every store behaves exactly as it does today.
    /// </summary>
    /// <remarks>
    /// The schema installs either way, because schema evolution is not optional and the upgrade sweep
    /// runs with it. What this governs is whether new writes append a claim. A memory written while
    /// this is off carries no claim and receives none retroactively — the sweep runs once, when the
    /// version step runs, and nothing re-runs it.
    ///
    /// <para>A <c>{ get; set; }</c> property, like every other key in this record: the configuration
    /// binding generator silently skips <c>init</c>-only properties, which would leave the feature
    /// permanently off while <c>arcanum.json</c> said otherwise.</para>
    /// </remarks>
    public bool Annals { get; set; }
```

- [ ] **Step 4: Append the claim inside the Saga insert transaction**

In `SagaMemoryStore.InsertCoreAsync`, after the `saga_memories` insert and the optional provenance insert, and before the embedding writes:

```csharp
                if (options.CurrentValue.Features.Annals)
                {

                    // Inside the memory's own transaction, and reusing the scope the classifier just
                    // derived rather than deriving a second one. Two derivations of one authority
                    // eventually disagree, and the disagreement would land on what a turn may recall.
                    _ = await AnnalsClaimWriter.AppendAssertAsync(
                        connection,
                        transaction,
                        AnnalSubjectStore.Saga,
                        id,
                        AnnalOrigin.AgentExtracted,
                        scopeKind,
                        scopeCampaignId,
                        ContentSensitivity.None,
                        AnnalContentDigest.ForSagaMemory(content),
                        createdAt,
                        createdAt,
                        sessionId,
                        cancellationToken).ConfigureAwait(false);

                }
```

`AgentExtracted` is the only honest origin here: Saga has no operator write path and no `scribe_saga` tool, so every row is a headless extraction's inference from a finished transcript.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaAnnalsWriteThroughTests"`
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Configuration src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs tests/RetroDownfall.Arcanum.Tests/Annals
git commit -m "feat(annals): claim every Saga memory as it is written"
```

---

### Task 4: Lexicon write-through

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:28-31` (constructor) and `:120-145` (the insert/update fork in `UpsertCoreAsync`)
- Test: `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs`

**Interfaces:**
- Consumes: `AnnalsClaimWriter.AppendAssertAsync`, `AnnalsClaimWriter.AppendCorrectionAsync`, `AnnalContentDigest.ForLexiconEntry`, `AnnalOrigin.AgentAsserted`, `FeatureSettings.Annals`.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Create `tests/RetroDownfall.Arcanum.Tests/Annals/LexiconAnnalsWriteThroughTests.cs`, entering through `ILexiconService` resolved from DI:

```csharp
    [Fact]
    public async Task A_first_upsert_asserts_revision_one()
        // OperationCode 1, OriginCode 2 (AgentAsserted), head at revision 1, no edges

    [Fact]
    public async Task A_second_upsert_with_new_facts_appends_a_correction_that_supersedes_revision_one()
        // revision 2, OperationCode 2, PredecessorVersionId is revision 1's VersionId,
        // exactly one edge at ordinal 1 with RelationCode 1, head advanced to revision 2

    [Fact]
    public async Task Revision_one_is_unchanged_after_a_correction()
        // read revision 1's whole row before and after the second upsert and compare every column

    [Fact]
    public async Task Re_scribing_an_identical_fact_set_appends_no_revision_and_does_not_move_the_head()

    [Fact]
    public async Task A_campaign_scoped_entry_is_claimed_to_that_campaign_and_a_global_one_is_claimed_global()

    [Fact]
    public async Task With_the_gate_off_an_upsert_appends_nothing()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~LexiconAnnalsWriteThroughTests"`
Expected: FAIL — no claim rows are written.

- [ ] **Step 3: Give the service the settings it needs**

Change the primary constructor to:

```csharp
internal sealed class LexiconService(
    ArcanumDbContext db,
    ILogger<LexiconService> logger,
    IOptionsMonitor<ArcanumSettings> options,
    ICovenantLabeledArtifactGuard? labeledArtifactGuard = null) : ILexiconService
```

The optional parameter must stay last, so the new one goes before it. `IOptionsMonitor<ArcanumSettings>` is already registered; no DI change is needed at `ServiceCollectionExtensions.cs:1198`.

- [ ] **Step 4: Append inside the existing BEGIN IMMEDIATE**

In `UpsertCoreAsync`, replace the insert/update fork so the Annals append happens in the same serialized section, after the row is written and before `ReplaceFactProvenanceAsync`:

```csharp
                        if (existing is null)
                        {
                            await InsertAsync(connection, id, trimmedName, normalized, scope.Key, resolvedType, factsJson, factsText, now, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await UpdateAsync(connection, id, trimmedName, normalized, scope.Key, resolvedType, factsJson, factsText, now, cancellationToken).ConfigureAwait(false);
                        }

                        if (options.CurrentValue.Features.Annals)
                        {

                            // One call for both arms. The writer decides between an assertion and a
                            // correction from the claim it finds, so a first write and a later one
                            // cannot disagree about which this is, and a merge that added no fact
                            // appends nothing at all.
                            _ = await AnnalsClaimWriter.AppendCorrectionAsync(
                                connection,
                                transaction: null,
                                AnnalSubjectStore.Lexicon,
                                id.ToString(),
                                AnnalOrigin.AgentAsserted,
                                scope.CampaignId is null ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
                                scope.CampaignId?.ToString(),
                                ContentSensitivity.None,
                                AnnalContentDigest.ForLexiconEntry(resolvedType, factsText),
                                now,
                                now,
                                sourceSessionId: null,
                                cancellationToken).ConfigureAwait(false);

                        }
```

`LexiconService` drives its transaction with raw `BEGIN IMMEDIATE` / `COMMIT` text rather than a `DbTransaction` object, so it has no transaction to pass. Give `AnnalsClaimWriter` a nullable `DbTransaction` parameter and assign `command.Transaction` only when it is non-null; the commands run on the same connection and are therefore inside the same open transaction either way. Add a remark on the parameter saying exactly that, because a null transaction normally reads as "no transaction" and here it means the opposite.

`AgentAsserted` rather than `AgentExtracted`: a Lexicon write is a tool call a model chose to make, not something taken from a transcript behind its back.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~LexiconAnnalsWriteThroughTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Run the whole Lexicon suite for regressions**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Lexicon"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Lexicon src/RetroDownfall.Arcanum.Infrastructure/Data/Annals tests/RetroDownfall.Arcanum.Tests/Annals
git commit -m "feat(annals): assert and correct a Lexicon claim on every write"
```

---

### Task 5: Erasure and the data lifecycle

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs` (`RetentionDataClass`, `DataRetentionSettingsCatalog.ResolveRule`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:432-600` (`DeleteAsync`, `DeleteAllAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:179-260` (`DeleteByNameAsync`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs:31-120` (`FactoryPlanTables`, `FactoryDeletionTables`)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:255-278` (inventory)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs:2272-2395` (Saga and Lexicon candidate derived counts and their delete executors)
- Test: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs`

**Interfaces:**
- Consumes: `AnnalsClaimWriter.DeleteClaimsForSubjectAsync`, `AnnalsClaimWriter.DeleteClaimsForStoreAsync`.
- Produces: `RetentionDataClass.Annals`.

- [ ] **Step 1: Write the failing erasure tests**

Create `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsErasureTests.cs`:

```csharp
    [Fact]
    public async Task Deleting_one_saga_memory_removes_its_claim_and_leaves_every_other_claim_standing()

    [Fact]
    public async Task Deleting_one_lexicon_entity_removes_its_claim_and_every_revision_of_it()

    [Fact]
    public async Task Resetting_the_saga_store_leaves_no_saga_claim_and_leaves_lexicon_claims_untouched()
        // through IDataRetentionService with MemoryResetScope.Saga

    [Fact]
    public async Task Resetting_the_lexicon_store_leaves_no_lexicon_claim_and_leaves_saga_claims_untouched()

    [Fact]
    public async Task A_factory_reset_leaves_all_four_annals_tables_empty()

    /// <summary>
    /// Disabling the feature must not strand rows. A claim written while it was on has to be removable
    /// after it is off, or the operator would be left with records no surface can reach and no reset can
    /// clear.
    /// </summary>
    [Fact]
    public async Task A_claim_written_while_the_gate_was_on_is_deleted_after_the_gate_is_turned_off()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~AnnalsErasureTests"`
Expected: FAIL — claim rows survive their subjects.

- [ ] **Step 3: Add the retention class**

In `RetentionDataClass`, after `Covenant = 28`:

```csharp
    /// <summary>The Annals. Inventoried, never aged out on its own timer.</summary>
    Annals = 29,
```

In `ResolveRule`, beside the `Covenant` arm:

```csharp
            // Explicit, not a fall-through. A claim's lifecycle is its subject's: a rule that could age
            // one out from under a live memory would leave that memory unexplained.
            RetentionDataClass.Annals => null,
```

- [ ] **Step 4: Delete claims with their subjects**

In `SagaMemoryStore.DeleteAsync`, after the `saga_memories` delete succeeds and inside the same transaction, call `AnnalsClaimWriter.DeleteClaimsForSubjectAsync(connection, transaction, AnnalSubjectStore.Saga, id, cancellationToken)`. In `DeleteAllAsync`, call `DeleteClaimsForStoreAsync(..., AnnalSubjectStore.Saga, ...)`. In `LexiconService.DeleteByNameAsync`, call `DeleteClaimsForSubjectAsync(..., AnnalSubjectStore.Lexicon, entryId.ToString(), ...)` in the same statement batch that removes the entry.

None of these is gated. A claim written while the Annals was enabled must be removable after it is disabled.

- [ ] **Step 5: Join the factory reset plans**

In `FactoryPlanTables`, after the Lexicon rows:

```csharp
        new("annal_claims", RetentionDataClass.Annals, FactoryRecordKind.Derived),
        new("annal_versions", RetentionDataClass.Annals, FactoryRecordKind.Derived),
        new("annal_heads", RetentionDataClass.Annals, FactoryRecordKind.Derived),
        new("annal_dependencies", RetentionDataClass.Annals, FactoryRecordKind.Derived),
```

In `FactoryDeletionTables`, **before** the `lexicon_entries` and `saga_memories` rows and in this exact order, because the head-to-version and version-to-claim references carry no cascade:

```csharp
        new("annal_dependencies", RetentionDataClass.Annals, FactoryRecordKind.Derived),
        new("annal_heads", RetentionDataClass.Annals, FactoryRecordKind.Derived),
        new("annal_versions", RetentionDataClass.Annals, FactoryRecordKind.Derived),
        new("annal_claims", RetentionDataClass.Annals, FactoryRecordKind.Derived),
```

- [ ] **Step 6: Join the retention inventory**

In `DataRetentionService.BuildStatusAsync`, after the Lexicon composite:

```csharp
        await AddCompositeDatabaseStatusAsync(
            items,
            RetentionDataClass.Annals,
            [
                "annal_claims",
                "annal_versions",
                "annal_heads",
                "annal_dependencies",
            ],
            "Bitemporal claim identities, immutable versions, current pointers, and dependency edges over Saga and Lexicon rows. Removed with the memory each claim describes, never aged out on their own.",
            retention,
            cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 7: Count and remove claims in pruning**

In `AddSagaCandidatesAsync`, add the claim rows to the `derived` count for each candidate:

```csharp
            derived += await CountTableAsync(
                "annal_claims",
                "SubjectStoreCode = 1 AND SubjectId = @id",
                cancellationToken,
                ("@id", id)).ConfigureAwait(false);
```

Do the same in `AddLexiconCandidatesAsync` with `SubjectStoreCode = 2`. Then find the executors that actually delete a Saga or Lexicon candidate (search for `SagaCandidatePrefix` and `LexiconCandidatePrefix` in `DataRetentionService.Pruning.cs`) and call `AnnalsClaimWriter.DeleteClaimsForSubjectAsync` in the same transaction, before the subject row is removed. A rehearsal that under-reports what it will delete is a rehearsal an operator cannot rely on.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~AnnalsErasureTests"`
Expected: PASS, 6 tests.

- [ ] **Step 9: Run the whole data-lifecycle suite for regressions**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetention|FullyQualifiedName~InstallationReset"`
Expected: PASS. Any test asserting a complete `RetentionDataClass` inventory needs the new member added, not removed.

- [ ] **Step 10: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/DataLifecycle src/RetroDownfall.Arcanum.Infrastructure/Data src/RetroDownfall.Arcanum.Infrastructure/Lexicon tests/RetroDownfall.Arcanum.Tests/Annals
git commit -m "feat(annals): remove a claim with the memory it describes"
```

---

### Task 6: The read port

**Files:**
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:1194-1200`
- Test: `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs`

**Interfaces:**
- Consumes: `IAnnalsStore`, `AnnalClaimHead`, `AnnalClaimVersion`, `AnnalDependencyEdge`.
- Produces: `AnnalsStore` registered as `IAnnalsStore`.

- [ ] **Step 1: Write the failing tests**

Create `tests/RetroDownfall.Arcanum.Tests/Annals/AnnalsStoreTests.cs`. Every case reaches its starting state by writing through `ILexiconService` or `ISagaMemoryStore` with the gate on — nothing seeds a claim row directly, because a test that seeds the state it asserts can never discover that production cannot produce it.

```csharp
    [Fact]
    public async Task A_claim_is_readable_by_the_subject_row_it_describes()

    [Fact]
    public async Task A_row_with_no_claim_reads_as_null_rather_than_failing()
        // gate off, insert, then read: null. This is a first-class state, not an error.

    [Fact]
    public async Task Versions_come_back_oldest_revision_first()

    [Fact]
    public async Task A_versions_transaction_time_ends_where_its_successor_was_recorded()
        // revision 1's RecordedUntilUtc equals revision 2's RecordedAtUtc;
        // revision 2's RecordedUntilUtc is null

    [Fact]
    public async Task A_corrections_supersedes_edge_is_readable_in_ordinal_order()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~AnnalsStoreTests"`
Expected: FAIL to resolve `IAnnalsStore`.

- [ ] **Step 3: Implement the store**

Create `AnnalsStore` as `internal sealed class AnnalsStore(ArcanumDbContext db) : IAnnalsStore`, reusing the scoped connection through `DbCommand` exactly as `SagaMemoryStore` does, wrapped in `SqliteBusyRetry.ExecuteAsync`.

`GetVersionsAsync` computes `RecordedUntilUtc` in SQL with a correlated subquery, so the derivation lives in one place:

```sql
SELECT
    version.VersionId,
    version.ClaimId,
    version.Sequence,
    version.Revision,
    version.OperationCode,
    version.OriginCode,
    version.ScopeKindCode,
    version.CampaignId,
    version.SensitivityCode,
    version.ValidFromUtc,
    version.ValidToUtc,
    version.RecordedAtUtc,
    (SELECT successor.RecordedAtUtc
     FROM annal_versions AS successor
     WHERE successor.PredecessorVersionId = version.VersionId) AS RecordedUntilUtc,
    version.PredecessorVersionId
FROM annal_versions AS version
WHERE version.ClaimId = @claimId
ORDER BY version.Revision
```

The subquery returns at most one row: `ux_annal_versions_claim_revision` plus the revision chain means a version has at most one successor.

- [ ] **Step 4: Register it**

```csharp
        services.AddScoped<IAnnalsStore, AnnalsStore>();
```

beside the Saga and Lexicon registrations.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~AnnalsStoreTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure tests/RetroDownfall.Arcanum.Tests/Annals
git commit -m "feat(annals): add the read port over claim history"
```

---

### Task 7: Documentation, incidental fix, and verification

**Files:**
- Modify: `docs/Arcanum.DESIGN.md` (§5.4.4, §5.4.7, §10.6.1, §21.4, §21.5, new §21.12)
- Modify: `docs/Arcanum.Command.Reference.md` (`arcanum data status` classes)
- Modify: `docs/Compendium.README.md` (`Arcanum:Features:Annals`)
- Modify: `README.md` (metaphor table, durable-memory status)
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaCatalog.cs:129-131` (stale remark)

- [ ] **Step 1: Correct the stale catalog remark**

`GrimoireSchemaCatalog.TransitionStatements` carries "Empty today: no tier has left version 1", which stopped being true when Core reached version 2. Replace that paragraph with a description of the loader's actual state: Core declares steps to versions 2 and 3, both Covenant tiers declare none, and a tier that never left version 1 costs nothing.

- [ ] **Step 2: Write DESIGN §21.12**

Insert after §21.11 and before §22. Cover: what a claim, a version, a head, and an edge are; why the subject binding lives on the claim; the two timelines and why only valid time stores both ends; the four origins and the distinction they exist to draw; structural cycle-safety and why it is a schema check rather than a traversal; the sixteen-edge bound; write-through for both stores and the digest comparison that keeps corrections deterministic; the sweep's conservatism about unresolved scope; the deletion order; and a closing paragraph naming what is deliberately absent — no retrieval change, no deduplication, no operator correction surface, and no `Retire` producer.

Name capabilities, never issues. One logical block per physical line.

- [ ] **Step 3: Update the remaining DESIGN sections**

- §5.4.4: add the four tables to the persistence inventory.
- §5.4.7: add `Annals` to the retention taxonomy as inventoried and never aged out, beside `Covenant`.
- §10.6.1: add a promotion-policy row — a claim inherits its subject's promotion decision and authorizes nothing on its own; a file cannot cause itself to be claimed.
- §21.4: add the degradation row — an Annals write shares its subject's transaction, so a failure fails the memory write the store already reports rather than half-committing, and no turn sees an exception.
- §21.5: add the gap — a memory written while the Annals is disabled carries no claim and receives none retroactively.

- [ ] **Step 4: Update the CLI, config, and README docs**

- `Arcanum.Command.Reference.md`: `arcanum data status` reports the new class. No new verb, so `Arcanum.CommandMap.json` needs no regeneration — confirm with `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "Committed_command_map_matches_the_live_tree"`.
- `Compendium.README.md`: `Arcanum:Features:Annals`, default `false`, with what it governs and what it does not.
- `README.md`: The Annals joins the metaphor table; the durable-memory status paragraph records what landed.

- [ ] **Step 5: Prove the documentation carries no issue references**

Run: `grep -nE "#[0-9]{2,4}\b" docs/*.md`
Expected: hits in `Arcanum.OATH.md` only. Then re-read the new prose for inferred references — "the slice that added this", "still owed by", "arrives with the next" — and remove any.

- [ ] **Step 6: Run the mutation check**

Three production behaviours the acceptance criteria name. Break each in the source, confirm the suite fails, then restore it:

1. Replace the backfill's Saga scope copy with a constant `SagaMemoryScopeKind.Global`. `An_unclassified_saga_memory_is_never_laundered_into_global_authority` and `A_legacy_unresolved_saga_memory_is_never_laundered_into_global_authority` must fail.
2. Delete `CHECK (DependencySequence < DependentSequence)` from `annal_dependencies.sql` **and its transition file**. `An_edge_that_does_not_point_strictly_backwards_is_refused` must fail.
3. Make `AppendCorrectionAsync` skip the content-hash comparison and always append. `Re_scribing_an_identical_fact_set_appends_no_revision_and_does_not_move_the_head` must fail.

A test that survives its mutation proves nothing. Restore the source after each and confirm green again.

- [ ] **Step 7: Full verification**

```bash
rm -rf tests/RetroDownfall.Arcanum.Tests/TestResults tests/RetroDownfall.Compendium.Tests/TestResults
dotnet build RetroDownfall.Arcanum.slnx --no-incremental
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
```

Expected: 0 errors, 0 warnings, all green. The suite has known flaky concurrency tests — roughly one red per full run, a different one each time. Isolate any failure with a targeted re-run before treating it as a regression, and note that a filename-derived filter matches the **class**, not the file, so a mistyped filter can run zero tests and still print `Passed!`.

- [ ] **Step 8: Audit the diff before committing**

Run `git diff long-term-memory` and read every deletion line. Treat any removed condition, filter, bounds check, ordering constraint, `await`, assertion, or `[Fact]` as guilty until proven innocent. Confirm no test or fixture change made a failure disappear by making the test quieter rather than more realistic.

- [ ] **Step 9: Commit**

```bash
git add docs README.md src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaCatalog.cs
git commit -m "docs(annals): document the bitemporal claim substrate"
```

---

## Self-Review

**Spec coverage.** §5.1 → Task 2 Step 3. §5.2 → Task 2 Step 3. §5.3 → Task 2 Step 3. §5.4 → Task 2 Step 3 and the invariant tests in Step 5. §5.5 → Task 2 Step 4. §5.6 → Task 2 Step 7 and Task 5 Step 5. §6 → Task 1 Step 5 and Task 4 Step 4. §7.1 → Task 3. §7.2 → Task 4. §7.3 → Task 5. §8 → Task 2 Steps 6 and 9. §8.1 → Task 2 Step 8. §9.1 → Task 3 Step 3. §9.2 → Task 7 Step 3. §9.3 → Task 7 Step 3. §10 → Task 5. §11 → Tasks 1, 2, and 6. §12 → Task 7. §13 → the test steps of every task. §13.1 → Task 7 Step 6. §14 → Task 7 Step 2's closing paragraph. §15 → Task 7 Step 1.

**Type consistency.** `AnnalSubjectStore`, `AnnalOperation`, `AnnalOrigin`, `AnnalDependencyRelation`, `AnnalLimits.MaxDependenciesPerVersion`, `AnnalContentDigest.ForSagaMemory`, `AnnalContentDigest.ForLexiconEntry`, `AnnalClaimVersion`, `AnnalDependencyEdge`, `AnnalClaimHead`, `IAnnalsStore`, `AnnalsStore`, `AnnalsClaimWriter.AppendAssertAsync`, `AnnalsClaimWriter.AppendCorrectionAsync`, `AnnalsClaimWriter.DeleteClaimsForSubjectAsync`, `AnnalsClaimWriter.DeleteClaimsForStoreAsync`, `MemoryAnnalsBackfill`, `CoreSchemaVersionTwoFixture.Fingerprint`, `CoreSchemaVersionTwoFixture.ChainSet`, `FeatureSettings.Annals`, `RetentionDataClass.Annals` are each spelled the same way in every task that names them.

**Known asymmetry, deliberate.** `AnnalsClaimWriter` takes a nullable `DbTransaction` because `LexiconService` drives its transaction with raw `BEGIN IMMEDIATE` text and has no transaction object to hand over, while `SagaMemoryStore` has one. Task 4 Step 4 requires the parameter to carry a remark saying that null means "already inside one", because the ordinary reading is the opposite.
