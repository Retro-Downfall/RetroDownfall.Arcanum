# Saga Curation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the operator read, correct, retire, reinstate, and pin one auto-extracted Saga memory, with every correction and retirement appending an immutable Annals version and every retirement leaving keyed evidence that stops extraction re-adding what was removed.

**Architecture:** Two nullable timestamp columns on `saga_memories` carry lifecycle, because lifecycle is a property of that row. Two new add-only tables carry the keyed suppression evidence and its installation key, because that evidence has to outlive the row it describes. A new `SagaCurationService` owns the embed-then-write orchestration; `SagaMemoryStore` owns the transactional primitives; `AnnalsClaimWriter` gains the retirement its enum already declared. Retirement takes a memory out of retrieval by deleting its embedding rows, so exclusion is structural rather than a predicate four call sites have to agree about.

**Tech Stack:** .NET 10, C# with Native AOT discipline, xUnit, raw `DbCommand` SQL over SQLCipher-encrypted SQLite, System.CommandLine 2.0.10, `System.Text.Json` source generation.

**Spec:** [`docs/superpowers/specs/2026-08-26-issue-97-saga-version-operations-design.md`](../specs/2026-08-26-issue-97-saga-version-operations-design.md)

## Global Constraints

- **The Core tier's version-3 source fingerprint is `2CC5BB384111470F86668C4928B54306C7B8F7DCFDBBB152DF9F7C0CF162CC2F`.** It was read out of the head tree before any object file was touched and cannot be recomputed once one is. Copy it verbatim into `GrimoireSchemaVersionChains.SourcePins` under `(GrimoireSchemaTransactionTier.Core, 4)`.
- **No new `AnnalOperation` member.** `annal_versions.OperationCode` bakes `CHECK (OperationCode IN (1, 2, 3))` into a shipped table SQLite cannot `ALTER`, referenced by three composite foreign keys and guarded by an append-only trigger.
- **`ALTER TABLE ... ADD COLUMN` splices `, <column-def>` in front of the stored declaration's closing parenthesis, verbatim.** The head file `Tables/saga_memories.sql` must be written in that exact layout or every evolved installation reports `DefinitionDrift` and every fresh one passes.
- **`ALTER` cannot add a `CHECK`.** No invariant over the new columns may be a table constraint; the writers own them.
- **Raw SQL only** for these tables — no EF entity, no numbered migration, no compiled-model regeneration. One object per file, `CREATE ... IF NOT EXISTS`, one statement per transition file.
- **Native AOT:** no reflection-based serialization, no dynamic type loading. Every wire type is registered in `ArcanumJsonContext`.
- **Documentation under `docs/` names capabilities, never issues.** After every doc edit, `grep -nE "#[0-9]{2,4}\b" docs/*.md` must return hits only in `Arcanum.OATH.md`. This does not apply to `docs/superpowers/`, which is working material rather than a source of truth.
- **No artificial line wrapping in `docs/*.md`:** one logical block is one physical line.
- **Blank-line style:** this codebase puts a blank line after every opening brace of a method or type body and before every closing brace, and between consecutive members. Match the file you are editing.
- **Test isolation:** any suite touching `ArcanumPaths`, configuration, sessions, MCP, or file roots sets both host environments to `Testing` and points `ARCANUM_TEST_HOME` at a uniquely owned temporary root before the first path access.
- **Run tests with:** `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "<expr>"`. Filters match the **class or method name**, never the file name.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/saga_retirement_suppressions.sql` | The keyed retirement evidence and the Campaign index cleanup reads. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/saga_suppression_key.sql` | One row holding the installation's suppression key. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V4/010_saga_memories_retired_at.sql` | Version-4 step: the retirement column. |
| `.../Transitions/V4/020_saga_memories_pinned_at.sql` | Version-4 step: the pin column. |
| `.../Transitions/V4/030_saga_retirement_suppressions.sql` | Version-4 step: the evidence table. |
| `.../Transitions/V4/031_saga_retirement_suppressions_campaign_index.sql` | Version-4 step: its Campaign index. |
| `.../Transitions/V4/040_saga_suppression_key.sql` | Version-4 step: the key table. |
| `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs` | The lifecycle, eligibility, detail, and outcome shapes every surface shares. |
| `src/RetroDownfall.Arcanum.Core/Weave/ISagaCurationService.cs` | The operator-facing port: show, correct, retire, reinstate, pin. |
| `src/RetroDownfall.Arcanum.Core/Weave/SagaSuppressionDigest.cs` | The keyed digest, as a pure function. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaSuppressionKeyStore.cs` | Reads or creates the installation key inside a caller's transaction. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs` | The store's transactional curation primitives. |
| `src/RetroDownfall.Arcanum.Infrastructure/Weave/SagaCurationService.cs` | Embed-then-write orchestration and refusal mapping. |
| `src/RetroDownfall.Arcanum.Api/Tower/SagaCurationEndpoints.cs` | The six `/api/memory/saga/*` routes. |
| `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.SagaCuration.cs` | The six `arcanum memory saga` handlers. |
| `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.SagaCuration.cs` | The typed client for those six routes. |
| `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionThreeFixture.cs` | The Core head tree as version 3 declared it. |
| `tests/RetroDownfall.Arcanum.Tests/Data/Schema/SagaCurationEvolutionTests.cs` | Fresh-versus-evolved agreement for version 4. |
| `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs` | The store primitives. |
| `tests/RetroDownfall.Arcanum.Tests/Data/SagaSuppressionTests.cs` | The digest, the key, and the insert chokepoint. |
| `tests/RetroDownfall.Arcanum.Tests/Weave/SagaCurationServiceTests.cs` | Embed-first ordering and refusal mapping. |
| `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs` | The routes, entered as a client reaches them. |
| `tests/RetroDownfall.Arcanum.Tests/Cli/MemorySagaCurationCommandTests.cs` | The registered CLI verbs. |

**Modified**

| File | Change |
|---|---|
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/saga_memories.sql` | Two columns appended in `ALTER`'s own layout. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs` | `CoreSchemaVersion = 4` and the version-3 pin. |
| `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs` | Insert returns an outcome; five curation primitives added. |
| `src/RetroDownfall.Arcanum.Core/Weave/SagaMemoryDto.cs` | Two lifecycle timestamps. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs` | Suppression check at the insert chokepoint; the new columns projected. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs` | `AppendRetirementAsync`. |
| `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs` | Handles a suppressed write without failing the page. |
| `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs` | `DataRetentionSagaCurationInventory`, carried on the plan. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs` | Pin honored in planning and in execution. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs` | Both memory-reset arms clear the new tables. |
| `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs` | Factory reset clears the new tables. |
| `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreDatabaseWorker.cs` | The new tables travel with a backup. |
| `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs` | The new Saga refusal codes. |
| `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs` | The new wire types. |
| `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs` | Maps the curation endpoints. |
| `src/RetroDownfall.Arcanum.Infrastructure/ServiceCollectionExtensions.cs` | Registers `ISagaCurationService`. |
| `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs` | `arcanum memory saga`. |
| `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs` | Becomes `partial`. |
| `docs/Arcanum.DESIGN.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Command.Reference.md`, `docs/Arcanum.CommandMap.json`, `README.md` | Documentation. |

---

### Task 1: Core schema version 4

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/saga_memories.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/saga_retirement_suppressions.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/saga_suppression_key.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Transitions/V4/010_saga_memories_retired_at.sql`
- Create: `.../Transitions/V4/020_saga_memories_pinned_at.sql`
- Create: `.../Transitions/V4/030_saga_retirement_suppressions.sql`
- Create: `.../Transitions/V4/031_saga_retirement_suppressions_campaign_index.sql`
- Create: `.../Transitions/V4/040_saga_suppression_key.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaVersionChains.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionThreeFixture.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/SagaCurationEvolutionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaTransitionResourceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaVersionChainTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `saga_memories.RetiredAtUtc` and `saga_memories.PinnedAtUtc` (both `TEXT NULL`, round-trip `"o"`-format UTC); table `saga_retirement_suppressions(SuppressionDigest BLOB PK, ScopeKindCode INTEGER, CampaignId TEXT NULL, RetiredAtUtc TEXT)`; table `saga_suppression_key(KeyId INTEGER PK CHECK (KeyId = 1), KeyMaterial BLOB, CreatedAtUtc TEXT)`; `GrimoireSchemaVersionChains.CoreSchemaVersion == 4`; `CoreSchemaVersionThreeFixture.Objects`, `.Fingerprint`, `.ChainSet()`.

- [ ] **Step 1: Read the two existing patterns before writing anything**

Read `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionTwoFixture.cs` in full, and `tests/RetroDownfall.Arcanum.Tests/Data/Schema/MemoryAnnalsEvolutionTests.cs`. Task 1 is the same shape as the version-3 step: a fixture that reconstructs the previous head tree, a chain set built from it, and a test that installs the old version, evolves it, and compares the result against a fresh install. Copy that shape rather than inventing one.

- [ ] **Step 2: Write the failing evolution test**

Create `tests/RetroDownfall.Arcanum.Tests/Data/Schema/SagaCurationEvolutionTests.cs`. Model it on `MemoryAnnalsEvolutionTests`: install `CoreSchemaVersionThreeFixture.ChainSet()` into a temporary encrypted database, then hand the same installer `GrimoireSchemaVersionChains.Default`, then assert three things.

```csharp
[SkippableFact]
public async Task Evolving_a_version_three_installation_reaches_the_shipped_version_four_tree()
{

    // Arrange: a real version-3 installation, built by the installer rather than by a seeded row.
    await using SchemaEvolutionHarness harness = await SchemaEvolutionHarness
        .InstallAsync(CoreSchemaVersionThreeFixture.ChainSet())
        .ConfigureAwait(false);

    // Act: the upgrade, exactly as a caller reaches it.
    await harness.InstallAsync(GrimoireSchemaVersionChains.Default).ConfigureAwait(false);

    // Assert: the evolved tree and a fresh version-4 tree normalize to the same text.
    Assert.Equal(
        await SchemaEvolutionHarness.FreshDefinitionsAsync().ConfigureAwait(false),
        await harness.DefinitionsAsync().ConfigureAwait(false));

}

[SkippableFact]
public async Task Version_four_adds_the_two_lifecycle_columns_and_leaves_every_row_active()
{

    await using SchemaEvolutionHarness harness = await SchemaEvolutionHarness
        .InstallAsync(CoreSchemaVersionThreeFixture.ChainSet())
        .ConfigureAwait(false);

    await harness.ExecuteAsync(
        """
        INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
        VALUES ('m-1', 'a memory written before curation existed', '2026-01-01T00:00:00.0000000+00:00', 1)
        """).ConfigureAwait(false);

    await harness.InstallAsync(GrimoireSchemaVersionChains.Default).ConfigureAwait(false);

    Assert.Null(await harness.ScalarAsync("SELECT RetiredAtUtc FROM saga_memories WHERE Id = 'm-1'").ConfigureAwait(false));

    Assert.Null(await harness.ScalarAsync("SELECT PinnedAtUtc FROM saga_memories WHERE Id = 'm-1'").ConfigureAwait(false));

}

[Fact]
public void Version_three_reconstruction_matches_the_pinned_fingerprint()
{

    // The pin is unrecoverable once a head object changes, so it is proved here rather than against
    // every operator's version-3 installation.
    Assert.Equal(
        "2CC5BB384111470F86668C4928B54306C7B8F7DCFDBBB152DF9F7C0CF162CC2F",
        CoreSchemaVersionThreeFixture.Fingerprint);

}
```

If `MemoryAnnalsEvolutionTests` does not use a helper named `SchemaEvolutionHarness`, use whatever helper it does use and keep the three assertions above. Do not invent a new harness.

- [ ] **Step 3: Run it and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationEvolutionTests"`
Expected: FAIL — `CoreSchemaVersionThreeFixture` does not exist.

- [ ] **Step 4: Write the version-3 fixture**

Create `tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionThreeFixture.cs`, modelled on `CoreSchemaVersionTwoFixture`. Version 4 both **adds** objects and **edits** one, so the reconstruction has to do both: drop the two new tables from the shipped list, and substitute `saga_memories.sql`'s frozen version-3 text for the shipped one.

Freeze the version-3 text by copying `Tables/saga_memories.sql` **before Step 5 edits it** into a `private const string` in the fixture. Its final table line at version 3 is exactly:

```
, ScopeKindCode INTEGER NOT NULL DEFAULT 0, CampaignId TEXT);
```

- [ ] **Step 5: Extend the head files**

In `Tables/saga_memories.sql`, extend the existing spliced line and nothing else. The comment block at the top already explains why the layout is what it is; add the two new columns to the sentence naming which columns it covers.

```sql
, ScopeKindCode INTEGER NOT NULL DEFAULT 0, CampaignId TEXT, RetiredAtUtc TEXT, PinnedAtUtc TEXT);
```

Create `Tables/saga_retirement_suppressions.sql`:

```sql
-- Content-free, keyed evidence that one memory was retired, kept so the next extraction pass cannot
-- re-add what the operator just removed. It deliberately names no memory: the row has to outlive the
-- row it describes, because an operator who retires a memory and then deletes it must not thereby
-- re-enable the extraction they rejected.
--
-- The digest is an HMAC rather than a bare hash for two reasons, both narrow and both stated in full.
-- annal_versions.ContentHash is already a bare SHA-256 of the same bytes, so an unkeyed digest here
-- would be that identical value and the two tables would join into one confirmation oracle rather
-- than none. And deleting the single saga_suppression_key row makes every digest here permanently
-- useless for confirming a guess about content that has since been erased, which one row cannot do
-- for an unkeyed hash.
--
-- The scope columns restate what the digest was computed over. That is not a second measurement of
-- the digest: it is the only way a Campaign deletion can find its own suppressions, and a suppression
-- that outlived the Campaign identity it applied to would suppress extraction for an owner that no
-- longer exists with nothing left to remove it.
CREATE TABLE IF NOT EXISTS saga_retirement_suppressions (
    SuppressionDigest BLOB NOT NULL PRIMARY KEY CHECK (length(SuppressionDigest) = 32),
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
    CampaignId TEXT NULL,
    RetiredAtUtc TEXT NOT NULL,
    CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL))
);

-- Campaign cleanup reads this to remove a deleted Campaign's suppressions in the transaction that
-- removes the Campaign.
CREATE INDEX IF NOT EXISTS idx_saga_retirement_suppressions_campaign
ON saga_retirement_suppressions(ScopeKindCode, CampaignId);
```

Create `Tables/saga_suppression_key.sql`:

```sql
-- One row, by CHECK rather than by convention. The key is generated lazily inside the first
-- retirement's own transaction, so an installation that never retires anything never holds one.
CREATE TABLE IF NOT EXISTS saga_suppression_key (
    KeyId INTEGER NOT NULL PRIMARY KEY CHECK (KeyId = 1),
    KeyMaterial BLOB NOT NULL CHECK (length(KeyMaterial) = 32),
    CreatedAtUtc TEXT NOT NULL
);
```

- [ ] **Step 6: Write the five transition statements**

`Transitions/V4/010_saga_memories_retired_at.sql`:

```sql
-- The column definition text below is copied verbatim into the stored CREATE TABLE statement, so it
-- has to read character for character like the tail of Tables/saga_memories.sql.
ALTER TABLE saga_memories ADD COLUMN RetiredAtUtc TEXT;
```

`Transitions/V4/020_saga_memories_pinned_at.sql`:

```sql
-- Same rule as the column beside it: this text lands in the stored declaration unchanged.
ALTER TABLE saga_memories ADD COLUMN PinnedAtUtc TEXT;
```

`Transitions/V4/030_saga_retirement_suppressions.sql` and `040_saga_suppression_key.sql` carry the `CREATE TABLE` statements from Step 5 **character for character**, without the index. `031_saga_retirement_suppressions_campaign_index.sql` carries the index statement alone. A transition file that differs from its head file's statement by even a comment makes an evolved installation disagree with a fresh one.

- [ ] **Step 7: Declare the version and its pin**

In `GrimoireSchemaVersionChains.cs`, set `CoreSchemaVersion = 4`, extend that constant's remarks with a sentence naming what version 4 added, and add the pin:

```csharp
// Read out of the Core head tree immediately before saga_memories.sql gained its lifecycle columns
// and the two suppression objects were added. Nothing can recompute it either.
// CoreSchemaVersionThreeFixture reconstructs that tree and a test hashes it, so a wrong value here
// fails there rather than against every operator's version-3 installation.
[(GrimoireSchemaTransactionTier.Core, 4)] =
    "2CC5BB384111470F86668C4928B54306C7B8F7DCFDBBB152DF9F7C0CF162CC2F",
```

Add no entry to `Backfills`. The columns are nullable and both tables start empty, so the step needs no sweep to be correct — and unlike version 3, there is no existing row a sweep could usefully classify.

- [ ] **Step 8: Run the evolution test**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationEvolutionTests"`
Expected: PASS, all three.

- [ ] **Step 9: Extend the two closed inventories this trips**

`GrimoireSchemaTransitionResourceTests` pins every transition statement by name in install order — add the five new ones. `GrimoireSchemaVersionChainTests` pins the Core head version literal — change it to 4. Both fail on the new files rather than on a wrong result, so a green targeted run says nothing about them.

- [ ] **Step 10: Run the whole schema suite**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Data.Schema"`
Expected: PASS. `GrimoireSchemaInstallerTests`, `GrimoireSchemaManifestTests`, and `CoreSchemaVersionTwoFixture`'s own pin test all read the Core tree and are the ones most likely to catch a layout mistake.

- [ ] **Step 11: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Schema tests/RetroDownfall.Arcanum.Tests/Fixtures/CoreSchemaVersionThreeFixture.cs tests/RetroDownfall.Arcanum.Tests/Data/Schema
git commit -m "feat(saga): install the curation substrate as Core schema version 4"
```

---

### Task 2: The suppression digest and its key

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Weave/SagaSuppressionDigest.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaSuppressionKeyStore.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/SagaSuppressionTests.cs`

**Interfaces:**
- Consumes: `saga_suppression_key` from Task 1; `SagaMemoryScopeKind` from `Core/Weave`.
- Produces: `SagaSuppressionDigest.Compute(ReadOnlySpan<byte> key, SagaMemoryScopeKind scopeKind, string? campaignId, string content) -> byte[]` (32 bytes); `SagaSuppressionKeyStore.ReadOrCreateAsync(DbConnection, DbTransaction?, DateTimeOffset, CancellationToken) -> Task<byte[]>`; `SagaSuppressionKeyStore.ReadAsync(DbConnection, DbTransaction?, CancellationToken) -> Task<byte[]?>`.

- [ ] **Step 1: Write the failing digest tests**

Create `tests/RetroDownfall.Arcanum.Tests/Data/SagaSuppressionTests.cs`:

```csharp
public sealed class SagaSuppressionDigestTests
{

    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();

    [Fact]
    public void The_same_content_in_the_same_scope_produces_the_same_digest()
    {

        Assert.Equal(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"));

    }

    [Fact]
    public void The_same_content_in_a_different_Campaign_produces_a_different_digest()
    {

        // A rejection made inside one piece of work does not govern another the operator never had an
        // opinion about.
        string first = "11111111-1111-1111-1111-111111111111";

        string second = "22222222-2222-2222-2222-222222222222";

        Assert.NotEqual(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, first, "the operator prefers tabs"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, second, "the operator prefers tabs"));

    }

    [Fact]
    public void The_digest_is_not_the_content_hash_the_Annals_already_stores()
    {

        // Domain separation is the whole reason this is keyed: an unkeyed digest would be the identical
        // value annal_versions.ContentHash holds, and the two tables would join into one oracle.
        Assert.NotEqual(
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"));

    }

    [Fact]
    public void A_field_boundary_cannot_be_forged_by_content_that_looks_like_the_next_field()
    {

        // Without a separator the pair ("ab", "c") and ("a", "bc") would hash identically.
        Assert.NotEqual(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, "11111111-1111-1111-1111-111111111111", "x"),
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Campaign, "11111111-1111-1111-1111-1111111111", "11x"));

    }

    [Fact]
    public void A_different_key_produces_a_different_digest()
    {

        byte[] other = new byte[32];

        other[0] = 0xFF;

        Assert.NotEqual(
            SagaSuppressionDigest.Compute(Key, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"),
            SagaSuppressionDigest.Compute(other, SagaMemoryScopeKind.Global, null, "the operator prefers tabs"));

    }

}
```

- [ ] **Step 2: Run and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaSuppressionDigestTests"`
Expected: FAIL — `SagaSuppressionDigest` does not exist.

- [ ] **Step 3: Write the digest**

Create `src/RetroDownfall.Arcanum.Core/Weave/SagaSuppressionDigest.cs`:

```csharp
using System.Globalization;

using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// The 32-byte keyed binding between a retirement and the content-and-scope it refuses to see again.
/// </summary>
/// <remarks>
/// Keyed rather than hashed, for two narrow reasons and no broader claim. The Annals already stores a
/// bare SHA-256 of the same bytes, so an unkeyed digest would be that identical value and the two
/// tables would join into one confirmation oracle rather than none. And destroying the single key row
/// makes every surviving digest permanently useless for confirming a guess about content that has
/// since been erased, which one row cannot do for an unkeyed hash. The Grimoire is encrypted at rest,
/// so this is not what keeps the content from someone who can read the file.
///
/// <para>The scope is part of the preimage because a rejection made inside one Campaign is not an
/// opinion about another. That is the same reasoning Campaign-scoped retrieval applies to what a turn
/// may recall.</para>
/// </remarks>
public static class SagaSuppressionDigest
{

    /// <summary>
    /// Separates the preimage's fields. A unit separator cannot occur in a scope code or a Campaign
    /// identity, and the content field is last, so no value can move a field boundary.
    /// </summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>Domain separation from every other keyed value this installation derives.</summary>
    private const string Domain = "arcanum/saga/retirement-suppression/v1";

    /// <summary>The binding for one retired memory's content, under its own ownership.</summary>
    public static byte[] Compute(
        ReadOnlySpan<byte> key,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        string content)
    {

        ArgumentNullException.ThrowIfNull(content);

        string preimage = string.Create(
            CultureInfo.InvariantCulture,
            $"{Domain}{FieldSeparator}{(int)scopeKind}{FieldSeparator}{campaignId ?? string.Empty}{FieldSeparator}{content}");

        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(preimage));

    }

}
```

- [ ] **Step 4: Run and confirm the digest tests pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaSuppressionDigestTests"`
Expected: PASS, all five.

- [ ] **Step 5: Write the failing key-store test**

Append to the same file a second class. Reach the database through whatever encrypted-Grimoire fixture `SagaMemoryStoreTests` already uses — read that file first and reuse its fixture rather than opening a connection by hand.

```csharp
public sealed class SagaSuppressionKeyStoreTests
{

    [SkippableFact]
    public async Task The_key_is_created_once_and_read_back_unchanged()
    {

        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

        Assert.Null(await SagaSuppressionKeyStore
            .ReadAsync(harness.Connection, null, TestContext.Current.CancellationToken)
            .ConfigureAwait(false));

        byte[] first = await SagaSuppressionKeyStore
            .ReadOrCreateAsync(harness.Connection, null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        byte[] second = await SagaSuppressionKeyStore
            .ReadOrCreateAsync(harness.Connection, null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        Assert.Equal(32, first.Length);

        Assert.Equal(first, second);

    }

}
```

- [ ] **Step 6: Run and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaSuppressionKeyStoreTests"`
Expected: FAIL — `SagaSuppressionKeyStore` does not exist.

- [ ] **Step 7: Write the key store**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaSuppressionKeyStore.cs`. It takes the caller's connection and transaction rather than opening its own, exactly as `AnnalsClaimWriter` does and for the same reason: the key has to commit or roll back with the retirement that needed it.

```csharp
internal static class SagaSuppressionKeyStore
{

    private const string TimestampFormat = "o";

    /// <summary>The installation's suppression key, or <see langword="null"/> when nothing has been retired.</summary>
    internal static async Task<byte[]?> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken) { /* SELECT KeyMaterial FROM saga_suppression_key WHERE KeyId = 1 */ }

    /// <summary>
    /// The installation's suppression key, generating it inside the caller's transaction when this is
    /// the first retirement.
    /// </summary>
    /// <remarks>
    /// The insert is <c>INSERT OR IGNORE</c> followed by a read rather than a check-then-insert: two
    /// retirements racing on separate connections would both see no key and both try to write one, and
    /// the loser of that race has to end up holding the winner's key rather than an abort.
    /// </remarks>
    internal static async Task<byte[]> ReadOrCreateAsync(
        DbConnection connection,
        DbTransaction? transaction,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken) { /* INSERT OR IGNORE ... RandomNumberGenerator.GetBytes(32) ... then ReadAsync */ }

}
```

Implement both bodies fully; the comments above are the reasoning, not a stub.

- [ ] **Step 8: Run and confirm both classes pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaSuppression"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Weave/SagaSuppressionDigest.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaSuppressionKeyStore.cs tests/RetroDownfall.Arcanum.Tests/Data/SagaSuppressionTests.cs
git commit -m "feat(saga): give retirement a keyed, content-free binding of its own"
```

---

### Task 3: The curation contracts and the detail read

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/SagaMemoryDto.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs`

**Interfaces:**
- Consumes: Task 1's columns.
- Produces:

```csharp
public enum SagaRetrievalEligibility { Eligible = 1, Retired = 2, OwnershipUnresolved = 3, EmbeddingMissing = 4 }

public sealed record SagaMemoryLifecycle(DateTimeOffset? RetiredAtUtc, DateTimeOffset? PinnedAtUtc);

public sealed record SagaMemoryCurationRow(SagaMemoryDto Memory, SagaMemoryLifecycle Lifecycle, bool HasEmbedding);

public sealed record SagaMemoryDetail(
    SagaMemoryDto Memory,
    SagaMemoryLifecycle Lifecycle,
    SagaRetrievalEligibility Eligibility,
    AnnalClaimHead? Claim,
    IReadOnlyList<AnnalClaimVersion> History);

public enum SagaCurationOutcomeKind { Applied = 1, NotFound = 2, StaleContent = 3, AlreadyRetired = 4, NotRetired = 5, Unchanged = 6 }

public sealed record SagaCurationOutcome(SagaCurationOutcomeKind Kind, SagaMemoryLifecycle? Lifecycle);

public enum SagaMemoryWriteOutcome { Written = 1, Suppressed = 2 }
```

plus `ISagaMemoryStore.ReadCurationRowAsync(string id, CancellationToken) -> Task<SagaMemoryCurationRow?>` and `SagaMemoryDto` gaining `DateTimeOffset? RetiredAtUtc = null, DateTimeOffset? PinnedAtUtc = null` as trailing optional parameters.

- [ ] **Step 1: Write the failing test**

Create `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs`. Write the memory through the store's own `InsertAsync` — never by inserting a row — so the precondition is reached the way production reaches it.

```csharp
[SkippableFact]
public async Task A_freshly_written_memory_reads_back_active_unpinned_and_embedded()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1",
        "the operator prefers tabs",
        DateTimeOffset.UtcNow,
        sessionId: null,
        tags: null,
        source: null,
        harness.Embedding(),
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaMemoryCurationRow? row = await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.NotNull(row);

    Assert.Null(row.Lifecycle.RetiredAtUtc);

    Assert.Null(row.Lifecycle.PinnedAtUtc);

    Assert.True(row.HasEmbedding);

}

[SkippableFact]
public async Task An_unknown_identity_reads_back_as_nothing_rather_than_as_an_error()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    Assert.Null(await harness.Store
        .ReadCurationRowAsync("m-absent", TestContext.Current.CancellationToken)
        .ConfigureAwait(false));

}
```

`SagaStoreHarness` does not exist yet — build it in this task beside the tests, giving it an encrypted temporary Grimoire, a real `SagaMemoryStore`, an `Embedding()` helper returning a vector of the configured dimension, and `Connection` for the Task 2 tests. Model it on the fixture `SagaMemoryStoreTests` already uses; if that file has a usable fixture, use it instead and delete this paragraph's work.

- [ ] **Step 2: Run and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationStoreTests"`
Expected: FAIL — `ReadCurationRowAsync` is not defined.

- [ ] **Step 3: Write the contracts**

Create `src/RetroDownfall.Arcanum.Core/Weave/SagaCurationContracts.cs` with the shapes from **Interfaces** above. Give `SagaRetrievalEligibility` a remarks block saying what each member means and, in particular, that `OwnershipUnresolved` is a memory whose owning Session's binding never resolved — retrievable in no scope at all, which is a different thing from retired and a different thing from broken.

- [ ] **Step 4: Extend the DTO and the port**

Add `DateTimeOffset? RetiredAtUtc = null` and `DateTimeOffset? PinnedAtUtc = null` as the last two parameters of `SagaMemoryDto`, after `ScopeCampaignId`. They go last because every existing positional construction has to keep compiling.

Add to `ISagaMemoryStore`:

```csharp
/// <summary>
/// One memory's row, its curation lifecycle, and whether it still has an embedding, read together.
/// </summary>
/// <remarks>
/// One read rather than three. A caller that asked for the row, then the lifecycle, then the embedding
/// would be describing three instants as though they were one, and the detail view exists to say what
/// is true now.
/// </remarks>
Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken);
```

- [ ] **Step 5: Implement it**

Create `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs`, make `SagaMemoryStore` `partial`, and implement `ReadCurationRowAsync` as one `LEFT JOIN` against `saga_memory_embeddings`. Extend `SagaMemoryStore.ReadMemory` to project the two new columns so `ListAsync` and `GetByIdsAsync` report lifecycle too, and add the columns to those two queries' `SELECT` lists.

- [ ] **Step 6: Run and confirm it passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationStoreTests|FullyQualifiedName~SagaMemoryStoreTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Weave src/RetroDownfall.Arcanum.Infrastructure/Data tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs
git commit -m "feat(saga): read one memory's lifecycle and retrieval state together"
```

---

### Task 4: The Annals learns to retire

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs`

**Interfaces:**
- Consumes: `AnnalOperation.Retire`, already declared and constrained.
- Produces:

```csharp
internal static Task<bool> AppendRetirementAsync(
    DbConnection connection,
    DbTransaction? transaction,
    AnnalSubjectStore subjectStore,
    string subjectId,
    AnnalOrigin origin,
    SagaMemoryScopeKind scopeKind,
    string? campaignId,
    ContentSensitivity sensitivity,
    DateTimeOffset validFrom,
    DateTimeOffset recordedAt,
    Guid? sourceSessionId,
    CancellationToken cancellationToken);
```

Note there is **no** `contentHash` parameter. A retirement is a tombstone that binds to no content, which `annal_versions` enforces; a parameter that could only ever be null would invite a caller to pass one.

- [ ] **Step 1: Write the failing test**

Add to `tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs`:

```csharp
[SkippableFact]
public async Task A_retirement_appends_a_tombstone_that_supersedes_the_version_it_ends()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true).ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    bool appended = await AnnalsClaimWriter.AppendRetirementAsync(
        harness.Connection,
        null,
        AnnalSubjectStore.Saga,
        "m-1",
        AnnalOrigin.OperatorStated,
        SagaMemoryScopeKind.Global,
        null,
        ContentSensitivity.None,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.True(appended);

    AnnalClaimHead? head = await harness.Annals
        .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.NotNull(head);

    Assert.Equal(AnnalOperation.Retire, head.CurrentOperation);

    Assert.Equal(2, head.CurrentRevision);

    IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
        .GetVersionsAsync(head.ClaimId, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    // The tombstone binds to nothing, and it names the version it ended.
    AnnalClaimVersion tombstone = history[^1];

    Assert.Equal(AnnalOperation.Retire, tombstone.Operation);

    Assert.Equal(history[0].VersionId, tombstone.PredecessorVersionId);

    IReadOnlyList<AnnalDependencyEdge> edges = await harness.Annals
        .GetDependenciesAsync(tombstone.VersionId, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(AnnalDependencyRelation.Supersedes, Assert.Single(edges).Relation);

}

[SkippableFact]
public async Task Retiring_a_claim_less_memory_records_who_asserted_it_before_who_ended_it()
{

    // A memory written while the Annals was disabled has no claim. Opening one at the retirement with
    // the operator as its author would rewrite history: extraction asserted this memory, and the
    // operator only ended it.
    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false).ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Null(await harness.Annals
        .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", TestContext.Current.CancellationToken)
        .ConfigureAwait(false));

    _ = await AnnalsClaimWriter.AppendAssertAsync(
        harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
        AnnalOrigin.AgentExtracted, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    _ = await AnnalsClaimWriter.AppendRetirementAsync(
        harness.Connection, null, AnnalSubjectStore.Saga, "m-1",
        AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    AnnalClaimHead head = (await harness.Annals
        .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", TestContext.Current.CancellationToken)
        .ConfigureAwait(false))!;

    IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
        .GetVersionsAsync(head.ClaimId, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(AnnalOrigin.AgentExtracted, history[0].Origin);

    Assert.Equal(AnnalOrigin.OperatorStated, history[1].Origin);

}
```

- [ ] **Step 2: Run and confirm it fails**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaAnnalsWriteThroughTests"`
Expected: FAIL — `AppendRetirementAsync` is not defined.

- [ ] **Step 3: Implement `AppendRetirementAsync`**

In `AnnalsClaimWriter.cs`, beside `AppendCorrectionAsync`. It reads the head; returns `false` without writing when there is none (a retirement of an unclaimed row records nothing, and opening a claim here is the caller's decision, not this method's); inserts a version at `head.CurrentRevision + 1` with `AnnalOperation.Retire` and a null content hash; writes one `Supersedes` edge at ordinal 1 from the new version to the head's; and moves the head. It returns `false` when the head is already a retirement, because retiring twice records no change.

- [ ] **Step 4: Run and confirm it passes**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaAnnalsWriteThroughTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs tests/RetroDownfall.Arcanum.Tests/Annals/SagaAnnalsWriteThroughTests.cs
git commit -m "feat(annals): write the retirement the operation vocabulary already declared"
```

---

### Task 5: Retirement and reinstatement in the store

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces:

```csharp
Task<SagaCurationOutcome> RetireAsync(
    string id, byte[] expectedContentDigest, DateTimeOffset retiredAt, CancellationToken cancellationToken);

Task<SagaCurationOutcome> ReinstateAsync(
    string id, byte[] expectedContentDigest, float[] embedding, DateTimeOffset reinstatedAt, CancellationToken cancellationToken);
```

- [ ] **Step 1: Write the failing tests**

Add to `SagaCurationStoreTests`:

```csharp
[SkippableFact]
public async Task Retirement_removes_both_embedding_rows_and_leaves_the_memory_inspectable()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.RetireAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

    SagaMemoryCurationRow row = (await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false))!;

    // The memory is still there to read. What is gone is the only thing retrieval can reach it by.
    Assert.Equal("the operator prefers tabs", row.Memory.Content);

    Assert.NotNull(row.Lifecycle.RetiredAtUtc);

    Assert.False(row.HasEmbedding);

    Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings", "MemoryId = 'm-1'").ConfigureAwait(false));

}

[SkippableFact]
public async Task Retirement_refuses_content_the_caller_did_not_read()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.RetireAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("something else entirely"),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.StaleContent, outcome.Kind);

    Assert.True((await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false))!.HasEmbedding);

}

[SkippableFact]
public async Task Retiring_twice_is_refused_rather_than_recorded_twice()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

    _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    SagaCurationOutcome second = await harness.Store
        .RetireAsync("m-1", digest, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.AlreadyRetired, second.Kind);

}

[SkippableFact]
public async Task Reinstatement_restores_the_embedding_and_releases_the_suppression()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

    _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(1, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

    SagaCurationOutcome outcome = await harness.Store.ReinstateAsync(
        "m-1", digest, harness.Embedding(), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

    SagaMemoryCurationRow row = (await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false))!;

    Assert.Null(row.Lifecycle.RetiredAtUtc);

    Assert.True(row.HasEmbedding);

    Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

}

[SkippableFact]
public async Task Reinstating_a_live_memory_is_refused()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.ReinstateAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        harness.Embedding(),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.NotRetired, outcome.Kind);

}
```

Add `CountAsync(string table, string predicate)` to `SagaStoreHarness`.

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationStoreTests"`
Expected: FAIL — `RetireAsync` is not defined.

- [ ] **Step 3: Implement both primitives**

In `SagaMemoryStore.Curation.cs`, both inside `SqliteBusyRetry.ExecuteAsync` with a fresh transaction per attempt, mirroring `InsertCoreAsync`'s comment about why the transaction is created inside the delegate.

`RetireAsync`, in order, inside one transaction:

1. `SELECT Content, CreatedAt, SessionId, ScopeKindCode, CampaignId, RetiredAtUtc FROM saga_memories WHERE Id = @id` — `NotFound` when absent, `AlreadyRetired` when `RetiredAtUtc` is not null;
2. compare `AnnalContentDigest.ForSagaMemory(content)` with `expectedContentDigest` using `CryptographicOperations.FixedTimeEquals` — `StaleContent` on mismatch;
3. `UPDATE saga_memories SET RetiredAtUtc = @retiredAt WHERE Id = @id`;
4. `DELETE FROM saga_memory_embeddings WHERE MemoryId = @id`, and the vec0 mirror when `availability.IsVecAvailable`;
5. `SagaSuppressionKeyStore.ReadOrCreateAsync`, then `SagaSuppressionDigest.Compute`, then `INSERT OR IGNORE INTO saga_retirement_suppressions ...` — `OR IGNORE` because two memories with identical content in one scope produce one digest, and the second retirement must not abort;
6. `AnnalsClaimWriter.AppendAssertAsync(... AnnalOrigin.AgentExtracted ..., contentHash: current digest, validFrom: CreatedAt, recordedAt: CreatedAt ...)` — a no-op when a claim exists, and the honest opening when one does not;
7. `AnnalsClaimWriter.AppendRetirementAsync(... AnnalOrigin.OperatorStated ...)`.

Steps 6 and 7 are **ungated**: they run whatever `Arcanum:Features:Annals` says, because the record that the operator ended this memory is evidence rather than retrieval. Put that sentence in a comment beside them, mirroring the ungated-deletion comment already in `DeleteAsync`.

`ReinstateAsync` is the mirror: `NotFound`, `NotRetired` when `RetiredAtUtc` is null, `StaleContent` on mismatch; then clear `RetiredAtUtc`, insert the embedding BLOB and the vec0 mirror, delete the suppression row for that digest, and append an `AnnalOperation.Correct` version binding the content again through `AppendCorrectionAsync` with `AnnalOrigin.OperatorStated`.

The suppression delete recomputes the digest from the key rather than remembering it; if `SagaSuppressionKeyStore.ReadAsync` returns null there is nothing to release and the delete is skipped.

- [ ] **Step 4: Run and confirm they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationStoreTests"`
Expected: PASS.

- [ ] **Step 5: Prove exclusion is structural, not incidental**

Add one test to `tests/RetroDownfall.Arcanum.Tests/Data/SagaCampaignScopedRetrievalTests.cs` that writes two memories, retires one, runs a Campaign-scoped search that would rank both, and asserts only the survivor comes back. That file already has the harness for a real scoped search — reuse it.

- [ ] **Step 6: Run the retrieval suite**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCampaignScopedRetrievalTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs tests/RetroDownfall.Arcanum.Tests/Data
git commit -m "feat(saga): retire a memory out of retrieval without losing it"
```

---

### Task 6: Suppression enforced at the insert chokepoint

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SagaExtractionService.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/SagaSuppressionTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 2 and 5.
- Produces: both `ISagaMemoryStore.InsertAsync` overloads return `Task<SagaMemoryWriteOutcome>` instead of `Task`.

- [ ] **Step 1: Write the failing test**

Add to `SagaSuppressionTests`:

```csharp
[SkippableFact]
public async Task A_retired_memory_is_not_written_again_by_the_path_that_wrote_it()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    _ = await harness.Store.RetireAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
        "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaMemoryWriteOutcome.Suppressed, outcome);

    Assert.Equal(0, await harness.CountAsync("saga_memories", "Id = 'm-2'").ConfigureAwait(false));

}

[SkippableFact]
public async Task A_suppression_made_in_one_Campaign_does_not_govern_another()
{

    // Written through two Sessions the classifier resolves to different Campaigns, so the scope on each
    // row is derived exactly as production derives it rather than declared by the test.
    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    Guid first = await harness.SessionBoundToNewCampaignAsync().ConfigureAwait(false);

    Guid second = await harness.SessionBoundToNewCampaignAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        first, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    _ = await harness.Store.RetireAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaMemoryWriteOutcome outcome = await harness.Store.InsertAsync(
        "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
        second, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

}

[SkippableFact]
public async Task Reinstating_the_memory_lets_the_same_conclusion_be_written_again()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

    _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    _ = await harness.Store.ReinstateAsync(
        "m-1", digest, harness.Embedding(), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(
        SagaMemoryWriteOutcome.Written,
        await harness.Store.InsertAsync(
            "m-2", "the operator prefers tabs", DateTimeOffset.UtcNow,
            null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false));

}
```

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaSuppressionTests"`
Expected: FAIL — `InsertAsync` returns `Task`, not `Task<SagaMemoryWriteOutcome>`.

- [ ] **Step 3: Change the port and the chokepoint**

Change both `InsertAsync` overloads on `ISagaMemoryStore` to return `Task<SagaMemoryWriteOutcome>`, keeping the default-interface-method delegation on the provenance overload.

In `InsertCoreAsync`, immediately after the scope classifier resolves and **before** the `saga_memories` insert, read the suppression key with `SagaSuppressionKeyStore.ReadAsync`. When it is null there is nothing suppressed and the write proceeds. Otherwise compute the digest and `SELECT 1 FROM saga_retirement_suppressions WHERE SuppressionDigest = @digest`; on a hit, roll the transaction back and return `SagaMemoryWriteOutcome.Suppressed`.

The check goes here and nowhere else. A courtesy check in the extraction service would be a second statement of the rule, and the second statement is the one that drifts.

- [ ] **Step 4: Teach extraction what a suppression means**

In `SagaExtractionService`, where the store's insert is awaited, capture the outcome. On `Suppressed`, log at information level naming the session and that the operator retired an equivalent conclusion, and **continue the page normally** — the watermark still advances. A deliberate rejection is not a failure; treating it as one would put the same page on the retry ladder forever, and the ladder can never converge because the next attempt is refused identically.

- [ ] **Step 5: Fix every call site the signature change touches**

Run `dotnet build RetroDownfall.Arcanum.slnx` and work through the errors. Callers that do not care about the outcome discard it with `_ =` rather than ignoring it silently.

- [ ] **Step 6: Write the end-to-end extraction test**

Add to `tests/RetroDownfall.Arcanum.Tests/Hosting/SagaExtractionServiceTests.cs` a test that drives the extraction service itself with a stub provider returning one conclusion, lets it write, retires that memory through the store, drives extraction over the same entries again, and asserts the row did not come back **and** that the session's watermark advanced. Assert on the store's own count, not on a helper's return value.

- [ ] **Step 7: Run both suites**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaSuppressionTests|FullyQualifiedName~SagaExtractionServiceTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "feat(saga): stop extraction re-adding what the operator retired"
```

---

### Task 7: Correction and pinning in the store

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/Weave/ISagaMemoryStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/SagaCurationStoreTests.cs`

**Interfaces:**
- Consumes: Tasks 1–6.
- Produces:

```csharp
Task<SagaCurationOutcome> CorrectAsync(
    string id, byte[] expectedContentDigest, string content, float[] embedding, DateTimeOffset correctedAt, CancellationToken cancellationToken);

Task<SagaCurationOutcome> SetPinAsync(
    string id, bool pinned, DateTimeOffset changedAt, CancellationToken cancellationToken);
```

`SetPinAsync` takes **no** content digest. A pin is not a content mutation, and requiring proof of what the text says would make pinning fail after an unrelated correction — friction with no safety behind it.

- [ ] **Step 1: Write the failing tests**

Add to `SagaCurationStoreTests`:

```csharp
[SkippableFact]
public async Task Correction_replaces_the_content_and_the_vector_together()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(seed: 1), TestContext.Current.CancellationToken).ConfigureAwait(false);

    byte[] before = await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        "the operator prefers spaces",
        harness.Embedding(seed: 2),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);

    SagaMemoryCurationRow row = (await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false))!;

    Assert.Equal("the operator prefers spaces", row.Memory.Content);

    // The vector moved with the text. A correction that changed one without the other would leave
    // retrieval surfacing the sentence the operator just rejected.
    Assert.NotEqual(before, await harness.EmbeddingBytesAsync("m-1").ConfigureAwait(false));

}

[SkippableFact]
public async Task Correction_refuses_content_the_caller_did_not_read()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("something else entirely"),
        "the operator prefers spaces",
        harness.Embedding(),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.StaleContent, outcome.Kind);

    Assert.Equal(
        "the operator prefers tabs",
        (await harness.Store.ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken)
            .ConfigureAwait(false))!.Memory.Content);

}

[SkippableFact]
public async Task Correcting_a_retired_memory_is_refused()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    byte[] digest = AnnalContentDigest.ForSagaMemory("the operator prefers tabs");

    _ = await harness.Store.RetireAsync("m-1", digest, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
        "m-1", digest, "the operator prefers spaces", harness.Embedding(),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.AlreadyRetired, outcome.Kind);

}

[SkippableFact]
public async Task Correcting_to_the_text_already_stored_is_refused_rather_than_recorded()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationOutcome outcome = await harness.Store.CorrectAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        "the operator prefers tabs",
        harness.Embedding(),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaCurationOutcomeKind.Unchanged, outcome.Kind);

}

[SkippableFact]
public async Task Correction_records_the_operator_as_the_author_and_extraction_as_the_asserter()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true).ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    _ = await harness.Store.CorrectAsync(
        "m-1",
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        "the operator prefers spaces",
        harness.Embedding(),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    AnnalClaimHead head = (await harness.Annals
        .GetClaimAsync(AnnalSubjectStore.Saga, "m-1", TestContext.Current.CancellationToken)
        .ConfigureAwait(false))!;

    IReadOnlyList<AnnalClaimVersion> history = await harness.Annals
        .GetVersionsAsync(head.ClaimId, TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(AnnalOrigin.AgentExtracted, history[0].Origin);

    Assert.Equal(AnnalOperation.Correct, history[1].Operation);

    Assert.Equal(AnnalOrigin.OperatorStated, history[1].Origin);

}

[SkippableFact]
public async Task Correction_leaves_the_memory_formation_time_and_its_sensitivity_label_alone()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    DateTimeOffset formed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    Guid id = Guid.NewGuid();

    await harness.Store.InsertAsync(
        id.ToString(), "the operator prefers tabs", formed,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    await harness.LabelSensitiveAsync(id).ConfigureAwait(false);

    _ = await harness.Store.CorrectAsync(
        id.ToString(),
        AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        "the operator prefers spaces",
        harness.Embedding(),
        DateTimeOffset.UtcNow,
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaMemoryCurationRow row = (await harness.Store
        .ReadCurationRowAsync(id.ToString(), TestContext.Current.CancellationToken).ConfigureAwait(false))!;

    // The memory was formed then; the Annals records when it was corrected.
    Assert.Equal(formed, row.Memory.CreatedAt);

    // The label stays. Removing it would be the fail-open direction: the operator's own text is not
    // Covenant-derived, but a label that over-reaches is safe and one that under-reaches is not.
    Assert.Equal(1, await harness.CountAsync("artifact_sensitivity", $"ArtifactId = '{id}'").ConfigureAwait(false));

}

[SkippableFact]
public async Task A_pin_is_recorded_and_released_without_touching_the_memory()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(
        SagaCurationOutcomeKind.Applied,
        (await harness.Store.SetPinAsync("m-1", true, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false)).Kind);

    Assert.NotNull((await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false))!.Lifecycle.PinnedAtUtc);

    Assert.Equal(
        SagaCurationOutcomeKind.Applied,
        (await harness.Store.SetPinAsync("m-1", false, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false)).Kind);

    Assert.Null((await harness.Store
        .ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false))!.Lifecycle.PinnedAtUtc);

}

[SkippableFact]
public async Task A_pinned_memory_can_still_be_corrected_and_retired_by_the_operator()
{

    // A pin an operator has to argue with is a pin they stop using. What it binds is the automatic
    // path, because that is the one that acts without being asked.
    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    _ = await harness.Store.SetPinAsync("m-1", true, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    Assert.Equal(
        SagaCurationOutcomeKind.Applied,
        (await harness.Store.CorrectAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
            "the operator prefers spaces",
            harness.Embedding(),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken).ConfigureAwait(false)).Kind);

    Assert.Equal(
        SagaCurationOutcomeKind.Applied,
        (await harness.Store.RetireAsync(
            "m-1",
            AnnalContentDigest.ForSagaMemory("the operator prefers spaces"),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken).ConfigureAwait(false)).Kind);

}
```

Add `EmbeddingBytesAsync(string id)` and `LabelSensitiveAsync(Guid id)` to `SagaStoreHarness`; the label helper writes through whatever production path `CovenantProtectedArtifactErasureKernelTests` uses to label a Saga artifact, never by inserting into `artifact_sensitivity` directly.

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationStoreTests"`
Expected: FAIL — `CorrectAsync` is not defined.

- [ ] **Step 3: Implement both primitives**

`CorrectAsync`, inside one transaction: read the row (`NotFound`, `AlreadyRetired`); fixed-time-compare the digest (`StaleContent`); compare the new content's digest against the current one and return `Unchanged` when they match; `UPDATE saga_memories SET Content = @content`; `UPDATE saga_memory_embeddings SET Embedding = @blob, Dim = @dim`; `INSERT OR REPLACE` the vec0 mirror when available; the ungated `AppendAssertAsync` / `AppendCorrectionAsync` pair from Task 5's step 3.

`SetPinAsync`: `UPDATE saga_memories SET PinnedAtUtc = @value WHERE Id = @id`, `NotFound` on zero rows, `Applied` otherwise. Pinning what is already pinned re-stamps the timestamp and returns `Applied`; there is no history table here for a no-op to pollute.

- [ ] **Step 4: Run and confirm they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(saga): correct one memory in place, and mark one durable"
```

---

### Task 8: The curation service

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Weave/ISagaCurationService.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SagaCurationService.cs`
- Modify: `src/RetroDownfall.Arcanum.Core/Primitives/ErrorCodes.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Weave/SagaCurationServiceTests.cs`

**Interfaces:**
- Consumes: `ISagaMemoryStore`, `IWeaveService`, `IAnnalsStore`.
- Produces:

```csharp
public interface ISagaCurationService
{

    Task<Result<SagaMemoryDetail>> ShowAsync(string id, CancellationToken cancellationToken);

    Task<Result<SagaMemoryDetail>> CorrectAsync(string id, string expectedContentHash, string content, CancellationToken cancellationToken);

    Task<Result<SagaMemoryDetail>> RetireAsync(string id, string expectedContentHash, CancellationToken cancellationToken);

    Task<Result<SagaMemoryDetail>> ReinstateAsync(string id, string expectedContentHash, CancellationToken cancellationToken);

    Task<Result<SagaMemoryDetail>> SetPinAsync(string id, bool pinned, CancellationToken cancellationToken);

}
```

and these `ErrorCodes.Saga` members:

```csharp
public const string StaleContent = "Saga.StaleContent";
public const string AlreadyRetired = "Saga.AlreadyRetired";
public const string NotRetired = "Saga.NotRetired";
public const string Unchanged = "Saga.Unchanged";
public const string EmbeddingUnavailable = "Saga.EmbeddingUnavailable";
```

`expectedContentHash` is the uppercase hex of `AnnalContentDigest.ForSagaMemory`, matching how the Covenant's own correction takes a rendered hash on the wire.

- [ ] **Step 1: Write the failing tests**

Create `tests/RetroDownfall.Arcanum.Tests/Weave/SagaCurationServiceTests.cs` with a hand-written `FakeWeaveService` (no Moq) and a real store over the harness:

```csharp
[SkippableFact]
public async Task Correction_is_refused_before_anything_is_written_when_the_substrate_cannot_embed()
{

    // Refusing is the point. A correction that cannot re-embed would leave the row saying one thing and
    // the vector saying another, so retrieval would keep surfacing the sentence the operator rejected.
    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationService service = new(harness.Store, FakeWeaveService.Unavailable, harness.Annals);

    Result<SagaMemoryDetail> result = await service.CorrectAsync(
        "m-1",
        Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
        "the operator prefers spaces",
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.True(result.IsFailure);

    Assert.Equal(ErrorCodes.Saga.EmbeddingUnavailable, result.Error.Code);

    Assert.Equal(
        "the operator prefers tabs",
        (await harness.Store.ReadCurationRowAsync("m-1", TestContext.Current.CancellationToken)
            .ConfigureAwait(false))!.Memory.Content);

}

[SkippableFact]
public async Task A_retired_memory_reports_retired_rather_than_a_missing_embedding()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    _ = await harness.Store.RetireAsync(
        "m-1", AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

    Result<SagaMemoryDetail> result = await service
        .ShowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaRetrievalEligibility.Retired, result.Value.Eligibility);

}

[SkippableFact]
public async Task A_memory_whose_ownership_never_resolved_reports_that_rather_than_eligible()
{

    // Retrievable in no scope at all is a different answer from retired and a different answer from
    // broken, and the operator has to be able to tell the three apart.
    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync().ConfigureAwait(false);

    Guid orphan = await harness.SessionWithUnresolvedBindingAsync().ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        orphan, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

    Result<SagaMemoryDetail> result = await service
        .ShowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(SagaRetrievalEligibility.OwnershipUnresolved, result.Value.Eligibility);

}

[SkippableFact]
public async Task A_memory_with_no_claim_is_shown_rather_than_refused()
{

    await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: false).ConfigureAwait(false);

    await harness.Store.InsertAsync(
        "m-1", "the operator prefers tabs", DateTimeOffset.UtcNow,
        null, null, null, harness.Embedding(), TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaCurationService service = new(harness.Store, FakeWeaveService.Available, harness.Annals);

    Result<SagaMemoryDetail> result = await service
        .ShowAsync("m-1", TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.True(result.IsSuccess);

    Assert.Null(result.Value.Claim);

    Assert.Empty(result.Value.History);

}
```

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationServiceTests"`
Expected: FAIL — `ISagaCurationService` does not exist.

- [ ] **Step 3: Add the error codes**

Extend `ErrorCodes.Saga` with the five members above, each with an XML doc line saying what the caller did wrong.

- [ ] **Step 4: Write the service**

`SagaCurationService(ISagaMemoryStore store, IWeaveService weave, IAnnalsStore annals)`:

- `ShowAsync` reads the curation row, then the claim and its history when one exists, and composes eligibility in this order: retired first, then ownership (`Unclassified` and `LegacyUnresolved` both map to `OwnershipUnresolved`, read off `SagaMemoryScopeKind` rather than from a literal), then `HasEmbedding`, then `Eligible`. Order matters: a retired memory has no embedding by construction, and reporting that as `EmbeddingMissing` would describe the wrong problem.
- `CorrectAsync` parses the hex hash (`ErrorCodes.Validation` on a malformed one), checks `weave.IsAvailable` and calls `weave.EmbedAsync`, mapping any failure to `Saga.EmbeddingUnavailable`, then calls the store and maps the outcome kinds to error codes. It ends by returning `ShowAsync`'s projection so the caller sees the state it produced.
- `RetireAsync` needs no embedding. `ReinstateAsync` embeds first, exactly as `CorrectAsync` does. `SetPinAsync` neither embeds nor takes a hash.

- [ ] **Step 5: Register it**

In `ServiceCollectionExtensions`, register `ISagaCurationService` with the same lifetime `ISagaMemoryStore` has — read the neighbouring registration rather than assuming.

- [ ] **Step 6: Run and confirm they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationServiceTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(saga): embed before the transaction, and refuse when it cannot"
```

---

### Task 9: Retention honors the pin

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Core/DataLifecycle/DataRetentionContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`

**Interfaces:**
- Consumes: `saga_memories.PinnedAtUtc`.
- Produces: `public sealed record DataRetentionSagaCurationInventory(long PinnedRows, long PinnedRowsExemptFromPlan);` and `DataRetentionPlan` gaining a trailing `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DataRetentionSagaCurationInventory? SagaCuration = null`.

- [ ] **Step 1: Write the failing tests**

Add to `DataRetentionServiceTests`, reusing whatever harness that file already uses to write an aged Saga memory:

```csharp
[SkippableFact]
public async Task A_pinned_memory_is_not_a_pruning_candidate_and_the_plan_says_why()
{

    await using RetentionHarness harness = await RetentionHarness.CreateAsync().ConfigureAwait(false);

    string keep = await harness.WriteAgedSagaMemoryAsync("pinned, and old").ConfigureAwait(false);

    string prune = await harness.WriteAgedSagaMemoryAsync("unpinned, and old").ConfigureAwait(false);

    _ = await harness.Store.SetPinAsync(keep, true, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    DataRetentionPlan plan = await harness.PlanAsync().ConfigureAwait(false);

    Assert.DoesNotContain("saga:" + keep, plan.CandidateIds);

    Assert.Contains("saga:" + prune, plan.CandidateIds);

    // A dry-run that silently omitted the exempted rows would tell an operator their rule reaches
    // further than it does.
    Assert.NotNull(plan.SagaCuration);

    Assert.Equal(1, plan.SagaCuration.PinnedRows);

    Assert.Equal(1, plan.SagaCuration.PinnedRowsExemptFromPlan);

}

[SkippableFact]
public async Task A_pin_taken_after_the_plan_is_honored_when_the_plan_is_applied()
{

    // A plan is a measurement. A measurement that authorizes a later delete has to be re-proved at the
    // moment it is used, or the pin loses exactly the race it exists to win.
    await using RetentionHarness harness = await RetentionHarness.CreateAsync().ConfigureAwait(false);

    string id = await harness.WriteAgedSagaMemoryAsync("old, pinned between plan and apply").ConfigureAwait(false);

    DataRetentionPlan plan = await harness.PlanAsync().ConfigureAwait(false);

    Assert.Contains("saga:" + id, plan.CandidateIds);

    _ = await harness.Store.SetPinAsync(id, true, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(false);

    _ = await harness.ApplyAsync(plan).ConfigureAwait(false);

    Assert.NotNull(await harness.Store
        .ReadCurationRowAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(false));

}
```

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetentionServiceTests"`
Expected: FAIL — the pinned memory is a candidate, and `SagaCuration` does not exist.

- [ ] **Step 3: Add the inventory contract**

Add `DataRetentionSagaCurationInventory` and the optional plan property. Give the record a remarks block saying that `PinnedRowsExemptFromPlan` counts the pinned rows this plan's own cutoff would otherwise have selected, and that there is no consolidation sweep and no decay pass in the installation for a pin to exempt a memory from — the state is durable and the sweeps that arrive inherit it.

- [ ] **Step 4: Honor the pin in planning**

In `AddSagaCandidatesAsync`, change the predicate to `julianday(CreatedAt) <= julianday(@cutoff) AND PinnedAtUtc IS NULL`, and count both `PinnedAtUtc IS NOT NULL` and `PinnedAtUtc IS NOT NULL AND julianday(CreatedAt) <= julianday(@cutoff)` for the inventory. Carry the inventory out to wherever `DataRetentionPlan` is constructed.

- [ ] **Step 5: Honor the pin in execution**

`DataRetentionService.Pruning.cs` reaches a Saga candidate in three places: the boundary resolver, the bounded-value arms around lines 4945 and 5325, and `DeleteSagaCandidateAsync`. Add `AND PinnedAtUtc IS NULL` to the delete's own `WHERE`, so a pinned row survives whichever arm reaches it, and treat a zero-row delete as "already gone" rather than as an error, matching how that method already handles a candidate that disappeared.

- [ ] **Step 6: Run the retention suite**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetention"`
Expected: PASS. `DataRetentionInventoryAccuracyTests` is a closed inventory over table counts and may need the two new tables added.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(saga): let a pin survive the sweep that would have taken it"
```

---

### Task 10: The new tables join every lifecycle that owns Saga data

**Files:**
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreDatabaseWorker.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionCampaignScopedResetTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's two tables.
- Produces: nothing new; this task closes inventories.

- [ ] **Step 1: Write the failing tests**

```csharp
[SkippableFact]
public async Task Resetting_Saga_clears_its_suppressions_and_the_key_that_made_them()
{

    // Clearing the digests alone would leave a key nothing can use; clearing the key alone would leave
    // rows that can never match again while still looking like evidence.
    await using RetentionHarness harness = await RetentionHarness.CreateAsync().ConfigureAwait(false);

    string id = await harness.WriteSagaMemoryAsync("the operator prefers tabs").ConfigureAwait(false);

    _ = await harness.Store.RetireAsync(
        id, AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(false);

    await harness.ResetMemoryAsync(scope: "saga").ConfigureAwait(false);

    Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

    Assert.Equal(0, await harness.CountAsync("saga_suppression_key", "1 = 1").ConfigureAwait(false));

}

[SkippableFact]
public async Task Deleting_a_Campaign_removes_the_suppressions_that_applied_to_it()
{

    // A suppression names a scope rather than a memory, so no memory deletion reaches it, and one left
    // behind would suppress extraction for an owner that no longer exists.
    await using RetentionHarness harness = await RetentionHarness.CreateAsync().ConfigureAwait(false);

    Guid campaign = await harness.NewCampaignAsync().ConfigureAwait(false);

    string id = await harness.WriteSagaMemoryInCampaignAsync(campaign, "the operator prefers tabs").ConfigureAwait(false);

    _ = await harness.Store.RetireAsync(
        id, AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(1, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

    await harness.DeleteCampaignAsync(campaign).ConfigureAwait(false);

    Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

}

[SkippableFact]
public async Task A_factory_reset_leaves_neither_table_behind()
{

    await using RetentionHarness harness = await RetentionHarness.CreateAsync().ConfigureAwait(false);

    string id = await harness.WriteSagaMemoryAsync("the operator prefers tabs").ConfigureAwait(false);

    _ = await harness.Store.RetireAsync(
        id, AnnalContentDigest.ForSagaMemory("the operator prefers tabs"),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(false);

    await harness.FactoryResetAsync().ConfigureAwait(false);

    Assert.Equal(0, await harness.CountAsync("saga_retirement_suppressions", "1 = 1").ConfigureAwait(false));

    Assert.Equal(0, await harness.CountAsync("saga_suppression_key", "1 = 1").ConfigureAwait(false));

}
```

Put the Campaign one in `DataRetentionCampaignScopedResetTests` and the other two in `DataRetentionServiceTests`, using each file's existing harness.

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetention"`
Expected: FAIL — the rows survive.

- [ ] **Step 3: Extend every list that already names a Saga table**

Find them with `grep -rn "saga_memory_embeddings" src --exclude-dir=bin --exclude-dir=obj`. Add the two new tables everywhere the Saga family is enumerated: both memory-reset arms and the Campaign-scoped arm in `DataRetentionService.cs`, the table list in `DataRetentionService.FactoryReset.cs`, and the backup worker's table list. The Campaign arm deletes `WHERE ScopeKindCode = 2 AND CampaignId = @campaignId` and leaves Global suppressions standing.

`--scope lexicon` must not touch either table; assert that if the existing suite has a scope-isolation test to extend.

- [ ] **Step 4: Run the lifecycle suites**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~DataRetention|FullyQualifiedName~Backup"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(saga): keep curation state inside backup, reset, and Campaign cleanup"
```

---

### Task 11: The six routes

**Files:**
- Create: `src/RetroDownfall.Arcanum.Api/Tower/SagaCurationEndpoints.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Api/Serialization/ArcanumJsonContext.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs`

**Interfaces:**
- Consumes: `ISagaCurationService`.
- Produces:

```csharp
public sealed record SagaCorrectRequest(string ExpectedContentHash, string Content);
public sealed record SagaRetireRequest(string ExpectedContentHash);
public sealed record SagaReinstateRequest(string ExpectedContentHash);
```

and routes `GET /api/memory/saga/{id}`, `POST /api/memory/saga/{id}/correct`, `/retire`, `/reinstate`, `/pin`, `/unpin`, each returning `ApiResponse<SagaMemoryDetail>`.

- [ ] **Step 1: Write the failing tests**

Create `tests/RetroDownfall.Arcanum.Tests/Api/Tower/SagaCurationEndpointTests.cs` using `ArcanumWebApplicationFactory` the way `SagaEndpointTests` does. Every test enters through the mapped route.

```csharp
[SkippableFact]
public async Task An_operator_corrects_a_memory_and_a_later_search_returns_the_corrected_text()
{

    // The acceptance criterion end to end: the corrected text is what retrieval reflects, not the text
    // the operator rejected.
    await using ArcanumWebApplicationFactory factory = await ArcanumWebApplicationFactory
        .CreateAsync().ConfigureAwait(false);

    string id = await factory.WriteSagaMemoryAsync("the operator prefers tabs").ConfigureAwait(false);

    HttpResponseMessage corrected = await factory.Client.PostAsJsonAsync(
        $"/api/memory/saga/{id}/correct",
        new SagaCorrectRequest(
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs")),
            "the operator prefers spaces"),
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

    SagaMemoryDto[] hits = await factory.DivineSagaAsync("indentation").ConfigureAwait(false);

    Assert.Equal("the operator prefers spaces", Assert.Single(hits).Content);

}

[SkippableFact]
public async Task A_retired_memory_stops_reaching_retrieval_and_stays_visible_marked_retired()
{

    await using ArcanumWebApplicationFactory factory = await ArcanumWebApplicationFactory
        .CreateAsync().ConfigureAwait(false);

    string id = await factory.WriteSagaMemoryAsync("the operator prefers tabs").ConfigureAwait(false);

    HttpResponseMessage retired = await factory.Client.PostAsJsonAsync(
        $"/api/memory/saga/{id}/retire",
        new SagaRetireRequest(Convert.ToHexString(AnnalContentDigest.ForSagaMemory("the operator prefers tabs"))),
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

    Assert.Empty(await factory.DivineSagaAsync("indentation").ConfigureAwait(false));

    SagaMemoryDto listed = Assert.Single(await factory.ListSagaAsync().ConfigureAwait(false));

    Assert.NotNull(listed.RetiredAtUtc);

}

[SkippableFact]
public async Task A_correction_naming_content_the_caller_never_read_is_refused_with_its_own_code()
{

    await using ArcanumWebApplicationFactory factory = await ArcanumWebApplicationFactory
        .CreateAsync().ConfigureAwait(false);

    string id = await factory.WriteSagaMemoryAsync("the operator prefers tabs").ConfigureAwait(false);

    HttpResponseMessage response = await factory.Client.PostAsJsonAsync(
        $"/api/memory/saga/{id}/correct",
        new SagaCorrectRequest(
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory("something else entirely")),
            "the operator prefers spaces"),
        TestContext.Current.CancellationToken).ConfigureAwait(false);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

    Assert.Contains(ErrorCodes.Saga.StaleContent, await response.Content
        .ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false), StringComparison.Ordinal);

}

[SkippableFact]
public async Task Every_curation_route_refuses_an_unauthenticated_caller()
{

    await using ArcanumWebApplicationFactory factory = await ArcanumWebApplicationFactory
        .CreateAsync().ConfigureAwait(false);

    using HttpClient anonymous = factory.CreateUnauthenticatedClient();

    foreach (string route in new[] { "correct", "retire", "reinstate", "pin", "unpin" })
    {

        HttpResponseMessage response = await anonymous.PostAsync(
            $"/api/memory/saga/m-1/{route}",
            content: null,
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    Assert.Equal(
        HttpStatusCode.Unauthorized,
        (await anonymous.GetAsync("/api/memory/saga/m-1", TestContext.Current.CancellationToken)
            .ConfigureAwait(false)).StatusCode);

}

[SkippableFact]
public async Task A_pinned_memory_reports_its_pin_on_the_detail_route()
{

    await using ArcanumWebApplicationFactory factory = await ArcanumWebApplicationFactory
        .CreateAsync().ConfigureAwait(false);

    string id = await factory.WriteSagaMemoryAsync("the operator prefers tabs").ConfigureAwait(false);

    _ = await factory.Client.PostAsync(
        $"/api/memory/saga/{id}/pin", content: null, TestContext.Current.CancellationToken).ConfigureAwait(false);

    SagaMemoryDetail detail = await factory.ShowSagaAsync(id).ConfigureAwait(false);

    Assert.NotNull(detail.Lifecycle.PinnedAtUtc);

    Assert.Equal(SagaRetrievalEligibility.Eligible, detail.Eligibility);

}
```

`WriteSagaMemoryAsync`, `DivineSagaAsync`, `ListSagaAsync`, and `ShowSagaAsync` are helpers on the factory that go through `/api/saga`, `POST /api/saga/divine`, and the new detail route — never through the store — so the test proves the routes, not the store a second time. `SagaEndpointTests` may already have some of these; reuse rather than duplicate.

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationEndpointTests"`
Expected: FAIL — 404 on every route.

- [ ] **Step 3: Write the endpoints**

Create `SagaCurationEndpoints.cs` modelled on `MemoryEndpoints`. Give the file a class-level remarks block saying that these six verbs each name exactly one store, that they sit beside the Covenant's and the Lexicon's rather than under `/api/saga`, and that none of them appears on `/v1`.

Map the `SagaCurationOutcomeKind` refusals through `ArcanumErrorMapper` — `StaleContent`, `AlreadyRetired`, `NotRetired`, and `Unchanged` are all `409 Conflict`; `NotFound` is `404`; `EmbeddingUnavailable` follows whatever `ErrorCodes.Embeddings.ProviderUnavailable` already maps to. Add the new codes to the mapper if it enumerates them.

- [ ] **Step 4: Register the routes and the wire types**

Add `apiGroup.MapSagaCurationEndpoints();` in `ApiBootstrapper` beside `MapMemoryEndpoints()`. Add `SagaMemoryDetail`, `SagaMemoryLifecycle`, `SagaCorrectRequest`, `SagaRetireRequest`, `SagaReinstateRequest`, `ApiResponse<SagaMemoryDetail>`, and the `AnnalClaimHead` / `AnnalClaimVersion` shapes the detail carries to `ArcanumJsonContext`.

- [ ] **Step 5: Run and confirm they pass**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SagaCurationEndpointTests|FullyQualifiedName~SagaEndpointTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(saga): route the six curation verbs, each naming one store"
```

---

### Task 12: The CLI

**Files:**
- Create: `src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.SagaCuration.cs`
- Create: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.SagaCuration.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Memory.cs`
- Modify: `docs/Arcanum.CommandMap.json`
- Create: `tests/RetroDownfall.Arcanum.Tests/Cli/MemorySagaCurationCommandTests.cs`

**Interfaces:**
- Consumes: Task 11's routes.
- Produces: `arcanum memory saga show | correct | retire | reinstate | pin | unpin <id>`.

- [ ] **Step 1: Write the failing tests**

Create `tests/RetroDownfall.Arcanum.Tests/Cli/MemorySagaCurationCommandTests.cs`, modelled on `CovenantCommandTests`, which drives the **registered command tree** rather than the handler class. At minimum:

```csharp
[Fact]
public async Task Correct_reads_the_replacement_text_from_piped_standard_input()
{

    // The same reason the Covenant's own write verbs take content this way: a memory's replacement
    // text must not land in shell history or in the process list of a shared machine.
    CliHarness harness = CliHarness.WithStandardInput("the operator prefers spaces");

    int exit = await harness
        .RunAsync("memory", "saga", "correct", "m-1", "--expected-content-hash", "AB12", "--yes")
        .ConfigureAwait(false);

    Assert.Equal(0, exit);

    Assert.Equal("the operator prefers spaces", harness.LastCorrectRequest!.Content);

}

[Fact]
public async Task Retire_asks_before_it_acts_and_does_nothing_when_the_operator_declines()
{

    CliHarness harness = CliHarness.DecliningConfirmation();

    int exit = await harness
        .RunAsync("memory", "saga", "retire", "m-1", "--expected-content-hash", "AB12")
        .ConfigureAwait(false);

    Assert.Equal(0, exit);

    Assert.Null(harness.LastRetireRequest);

}

[Fact]
public async Task Show_renders_the_lifecycle_and_the_eligibility_reason()
{

    CliHarness harness = CliHarness.Returning(new SagaMemoryDetail(
        new SagaMemoryDto("m-1", "the operator prefers tabs", DateTimeOffset.UtcNow, null, null, null),
        new SagaMemoryLifecycle(DateTimeOffset.UtcNow, null),
        SagaRetrievalEligibility.Retired,
        Claim: null,
        History: []));

    _ = await harness.RunAsync("memory", "saga", "show", "m-1").ConfigureAwait(false);

    Assert.Contains("Retired", harness.Output, StringComparison.Ordinal);

}

[Fact]
public async Task Every_curation_verb_is_registered_under_memory_saga()
{

    foreach (string verb in new[] { "show", "correct", "retire", "reinstate", "pin", "unpin" })
    {

        Assert.NotNull(CliCommandTree.Resolve("memory", "saga", verb));

    }

}
```

Use whatever harness `CovenantCommandTests` uses; do not build a new one if one exists.

- [ ] **Step 2: Run and confirm they fail**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~MemorySagaCurationCommandTests"`
Expected: FAIL — the verbs are not registered.

- [ ] **Step 3: Write the typed client**

Create `ArcanumApiClient.SagaCuration.cs` mirroring `ArcanumApiClient.Covenant.cs`: one method per route, source-generated serialization, no anonymous types.

- [ ] **Step 4: Write the handlers**

Make `MemoryCommands` `sealed partial class` and create `MemoryCommands.SagaCuration.cs` with `SagaShow`, `SagaCorrect`, `SagaRetire`, `SagaReinstate`, `SagaPin`, and `SagaUnpin`. Correction reads its text from `--file` or piped standard input using the same reader `CovenantCommands.ReadContentAsync` uses — extract it to a shared helper rather than copying it, and say in the XML doc why content arrives this way.

Every mutating verb confirms through `IConfirmationPrompt` unless `--yes` is passed, and every result names Saga and states that no other store was touched.

- [ ] **Step 5: Register the tree**

In `CliCommandTree.Memory.cs`, add a `BuildMemorySagaCuration(sp)` builder and `memory.Add(...)` it beside `lexicon`. Descriptions:

- `show` — "Show one Saga memory's provenance, lifecycle, and retrieval eligibility."
- `correct` — "Replace the text of one Saga memory, naming the exact content being corrected."
- `retire` — "Take one Saga memory out of retrieval, keeping it inspectable."
- `reinstate` — "Put a retired Saga memory back into retrieval."
- `pin` — "Mark one Saga memory durable, so retention will not prune it."
- `unpin` — "Release a pin, so retention may prune this memory again."

- [ ] **Step 6: Regenerate the committed command map**

Run: `ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "Committed_command_map_matches_the_live_tree"`
Then run it again **without** the environment variable and confirm it passes.

- [ ] **Step 7: Run the CLI suite**

Run: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~Cli"`
Expected: PASS. `CliCompletionResolver` and any help-topic coverage test are closed inventories over the tree and may need the new verbs.

- [ ] **Step 8: Commit**

```bash
git add src tests docs/Arcanum.CommandMap.json
git commit -m "feat(saga): give the operator the curation verbs at the command line"
```

---

### Task 13: Documentation

**Files:**
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.API.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `README.md`
- Modify: `docs/Arcanum.OATH.md` and `docs/ArcanumOATH.Human.md` if either states a promise this changes

**Interfaces:**
- Consumes: everything.
- Produces: documentation.

- [ ] **Step 1: Rewrite the Annals' absent list**

`docs/Arcanum.DESIGN.md` §21.12's "What is deliberately absent" currently asserts that there is no operator correction, retirement, or pinning surface, and that the retirement operation is declared with nothing writing one. Both stop being true. Rewrite those sentences — do not append a contradicting paragraph — and keep the claims that are still true: nothing reads a head to decide what a turn recalls, there is still no deduplication or decay, the Covenant is still not claimed here, and the Tapestry still is not.

- [ ] **Step 2: Add the new design section**

Add §21.13, "Curating what extraction remembered", after §21.12 and before §22, with subsections covering: why correction is a rewrite plus an immutable claim rather than a version table; why retirement deletes the embedding instead of filtering; what the keyed suppression buys and what it does not; why a reinstatement is a restatement rather than a fourth operation; what a pin binds and what it has nothing to bind today; and what is deliberately absent — hard erasure with resurrection suppression, review queues, bulk actions, and acting on a search hit.

- [ ] **Step 3: Update the surrounding sections**

§21.9's **Surfaces** paragraph gains the curation family and the sentence distinguishing it from `arcanum saga`. §10.6.2 gains the Saga curation verbs beside the Covenant's and the Lexicon's. §5.4.7's taxonomy gains the two new tables and states that they are cleared by the Saga memory-reset scope and by Campaign cleanup. §5.4.5's schema-version narrative gains Core version 4.

- [ ] **Step 4: Update the reference docs**

`Arcanum.API.md` gains the six routes with their request shapes and refusal codes. `Arcanum.Command.Reference.md` gains the `arcanum memory saga` family. `README.md` gains a status sentence and updated DESIGN anchors — README is one of only two files permitted to mention an issue.

- [ ] **Step 5: Check the two documentation rules**

Run: `grep -nE "#[0-9]{2,4}\b" docs/*.md`
Expected: hits only in `docs/Arcanum.OATH.md`.

Then read every paragraph you added looking for an inferred tracker reference — "the slice that added this", "still owed by", "arrives with the next" — and rewrite any into a plain statement of what the system is or is not.

Then confirm no line you added wraps artificially: one logical block is one physical line.

- [ ] **Step 6: Commit**

```bash
git add docs README.md
git commit -m "docs(saga): document curating what extraction remembered"
```

---

### Task 14: Verification before completion

- [ ] **Step 1: Clean the accumulated state that makes a full run hang**

```bash
find . -type d -name TestResults -not -path "*/node_modules/*" -exec rm -rf {} +
```

- [ ] **Step 2: Build with zero warnings**

```bash
dotnet build RetroDownfall.Arcanum.slnx --no-incremental -warnaserror
```

Expected: zero errors, zero warnings. `--no-incremental` is required — an incremental build skips analyzers on unchanged projects and hides exactly the warnings this gate exists to catch.

- [ ] **Step 3: Run every suite**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
```

A single red test in a full Arcanum run, different each time, is the known concurrency flake. Re-run that test alone before treating it as a regression.

- [ ] **Step 4: Break five behaviours and confirm the suite notices**

Green tests are weak evidence on their own. One at a time, make the change, run the suite, confirm it goes red, and revert:

1. Make the content-digest comparison in `RetireAsync` always return equal. Expected red: `Retirement_refuses_content_the_caller_did_not_read`.
2. Delete the `saga_memory_embeddings` delete from `RetireAsync`. Expected red: the structural-exclusion test in `SagaCampaignScopedRetrievalTests` and the route-level retirement test.
3. Make the suppression lookup in `InsertCoreAsync` always miss. Expected red: `A_retired_memory_is_not_written_again_by_the_path_that_wrote_it` and the end-to-end extraction test.
4. Drop `AND PinnedAtUtc IS NULL` from the planning predicate. Expected red: `A_pinned_memory_is_not_a_pruning_candidate_and_the_plan_says_why`.
5. Drop the same guard from the delete. Expected red: `A_pin_taken_after_the_plan_is_honored_when_the_plan_is_applied`.

If any of the five stays green, the test proving it is not proving it. Fix the test, not the mutation.

- [ ] **Step 5: Confirm every branch a test proves is reachable from production**

For each of the five above, name the production call site that reaches it. In particular, confirm that the suppression check is reached by `SagaExtractionService`'s real write path and not only by a test calling the store, and that `ISagaCurationService` is resolved by the mapped route rather than only constructed in a test.

- [ ] **Step 6: Review the whole diff before merging**

```bash
git diff long-term-memory...HEAD --stat
git diff long-term-memory...HEAD
```

Read it. Confirm nothing landed that no task asked for.

---

## Self-Review

**Spec coverage.** §5.1 → Task 1. §5.2, §5.3, §5.4 → Task 1. §6 correction → Tasks 7 and 8. §7 retirement → Tasks 4 and 5. §8 suppression → Tasks 2 and 6. §9 reinstatement → Task 5. §10 pin → Tasks 7 and 9. §11 detail → Tasks 3 and 8. §12 surfaces → Tasks 11 and 12. §13 lifecycle → Task 10. §14 testing → every task's own steps plus Task 14. §15 closed inventories → Tasks 1, 9, 10, and 12. No spec section is unclaimed.

**Type consistency.** `SagaCurationOutcomeKind` members are `Applied`, `NotFound`, `StaleContent`, `AlreadyRetired`, `NotRetired`, `Unchanged` in Tasks 3, 5, 7, and 11. `SagaRetrievalEligibility` members are `Eligible`, `Retired`, `OwnershipUnresolved`, `EmbeddingMissing` in Tasks 3, 8, 11, and 12. `SagaMemoryWriteOutcome` is `Written` / `Suppressed` in Tasks 3 and 6. `SagaSuppressionDigest.Compute` takes `(key, scopeKind, campaignId, content)` in Tasks 2, 5, and 6. `AppendRetirementAsync` carries no content hash in Tasks 4 and 5. `SetPinAsync` takes no digest in Tasks 7, 9, and 11.

**Known judgment calls left to the implementer.** Three test harnesses (`SagaStoreHarness`, `RetentionHarness`, `CliHarness`) are named as if they exist; each task says to reuse the neighbouring suite's existing fixture and only build one when none is there. That is deliberate — inventing a fourth harness beside three working ones is the more likely mistake than not finding them.
