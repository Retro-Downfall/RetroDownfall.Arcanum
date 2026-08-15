# Covenant Native SQLite and Schema Tiers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the ambient SQLCipher bundle with a verified hermetic runtime and install the Grimoire core, Covenant canonical, and Covenant accelerator schemas as independently validated transaction tiers.

**Architecture:** A checked-in native-asset project supplies exactly one verified SQLCipher library for each shipping RID and one process-wide provider initializer selects it before any SQLite use. One connection initializer applies SQLCipher and SQLite policy plus closed authorization functions to every connection. The declarative schema catalog gains independent family and transaction-tier dimensions, closed manifests, transactional data initializers, and process-level health so core failure blocks startup, canonical failure isolates Covenant, and accelerator failure degrades only inspection search.

**Tech Stack:** .NET 10, C# 14, Native AOT, MSBuild `buildTransitive`, SQLitePCLRaw provider APIs, Microsoft.Data.Sqlite, SQLCipher 4.17.0 Community on SQLite 3.53.3, statically linked OpenSSL 3.5.7, SQLite FTS5, Bash, PowerShell, xUnit.

## Delivered scope and approved deviations (issue #80, 2026-08-15)

Tasks 1 through 7 and Task 17 of this plan were implemented as GitHub issue #80. Three deviations
from the text below were approved by the operator during implementation and are the authority where
they conflict:

1. **Shipping runtime identifiers are `osx-arm64`, `win-x64`, and `win-arm64`** — three, not the five
   listed below. Linux was dropped entirely: `linux-x64` and `linux-arm64` are no longer shipping
   RIDs, are removed from `verify-aot-il-warnings.sh` and the CI matrix, and `osx-x64` is also out of
   the set. `ci.yml`'s build/test and AOT lanes moved from `ubuntu-latest` to `macos-14`, because a
   runner whose RID has no native asset now fails the build by design.
2. **Each manifest asset record carries a closed `status` of `verified` or `pending`.** A verified
   record requires a checked-in binary whose hash matches; a pending record requires the binary to be
   absent. This exists because only `osx-arm64` can be built on the implementation host; the two
   Windows assets are produced by `.github/workflows/verify-native-sqlcipher.yml` on
   `windows-latest`. A pending RID still hard-fails the build (`ARCSQLC002`) — there is no fallback.
3. **Windows RIDs are built by `scripts/build-native-sqlcipher.ps1`**, not the Bash script, which
   refuses them rather than cross-building an approximation. The compatibility fixture is generated
   from the runtime Arcanum actually shipped before this change (`SQLitePCLRaw.lib.e_sqlcipher`
   2.1.11, SQLite 3.39.2) rather than a 4.5.2 container image, because that is the database an
   upgrading operator actually has.

Task 6's connection **owner, factory, lease, and artifact-inventory** types were not implemented: they
exist to drain and re-point connections for the backup, restore, reset, and erasure flows that Plan 04
owns, and are not required by issue #80's acceptance criteria. The central initializer, the closed
connection modes, and the twelve default-denied authorization functions — the parts criterion 5 does
require — are implemented and tested.

## Delivered scope and approved deviations (issue #81, 2026-08-15)

Tasks 8 through 16 of this plan were implemented as GitHub issue #81. The deviations below were
approved by the operator during implementation and are the authority where they conflict with the
task text.

1. **No database migration or upgrade machinery was built.** The operator confirmed there is no
   installed base and no Grimoire anywhere to upgrade, including their own. `SupportedPreCovenantCoreManifest`
   (Task 14 Step 5) and the legacy-upgrade arm of the installer (Task 15 Step 6) are therefore not
   implemented, along with their classification tests `Exact_supported_pre_Covenant_catalog_upgrades_in_one_core_transaction`,
   `Missing_extra_drifted_partial_or_mixed_legacy_catalog_is_refused_before_DDL`, and
   `Legacy_core_without_feature_metadata_is_upgraded_in_the_core_transaction`. An allowlist of a
   catalog no database has is dead code that still has to be maintained. The fail-closed refusals
   that *do* apply to a database this build itself wrote are implemented in full:
   `IncompatibleNewerVersion`, `SourceDefinitionMismatch`, `MetadataMissing`, and
   `InstalledCatalogDrift`. This also resolves the plan's unnamed "Wave 0 recorded base commit",
   which no longer needs to be chosen.
2. **The sealed restore-staging capability is deferred to Plan 04.** Task 11's
   `restored_managed_file_authority_tombstones` table and its disjoint tombstone-backed trigger
   branches are implemented, but the single-take sealed capability that makes
   `arcanum_restore_staging_managed_authority_sanitization_authorized()` return 1 is not:
   `CovenantSqliteConnectionInitializer.Authorize` still rejects that kind, `AuthorizeCore` still has
   no external caller, and building the capability is in no Task 8-16 file list. Plan 04 owns restore
   behavior and builds it there. Until then the predicate can only return 0 in production and the
   staging branch is unreachable outside tests.
3. **`InstallCoreOnlyAsync` takes no embedding dimension, and `GrimoireSchemaCatalog.Resolve` accepts
   a null one.** The plan keeps `int embeddingDimensions` on `InstallAsync` but omits it from
   `InstallCoreOnlyAsync` while routing both through the same helper. No shipped `.sql` file uses
   `{{EmbeddingDimensions}}`, so the parameter now feeds only the post-commit dimension-mismatch
   diagnostic. `Resolve` therefore takes `int?` and fails closed on a templated object when no width
   was supplied, rather than installing at a guessed width.
4. **The CLI acquires the installation lock only if it is free.** Task 16 Step 4 says CLI
   initialization owns one scoped acquisition through readiness. Taken literally that regresses daily
   use: `ArcanumMaintenanceLock` is `FileShare.None` and the hosted service holds it for the host's
   whole lifetime, so `arcanum ask` and `arcanum run` would begin failing whenever the host is up,
   where today they coexist through WAL plus `busy_timeout`. The CLI now tries the lock; holding it
   means sole ownership and a full bootstrap under it, and failing to get it means a live host
   already bootstrapped authority, so the CLI proceeds without it. The plan's real invariant - that
   no startup path nests a second acquisition - is preserved.
5. **`GrimoireDbReadiness` is unchanged.** Task 16 lists it as Modify but no step describes a change,
   and the readiness contract for downstream hosted services is deliberately unaffected: canonical
   and accelerator results publish *before* `MarkReady()`, so an optional-tier failure must not delay
   readiness.
6. **Codes the specification names but never numbers were pinned here.**
   `CovenantFtsRebuildState` (`Idle=1`, `FullRebuildRequired=2`, `Rebuilding=3`) is declared in
   `Core/Covenant/CovenantCanonicalStateCodes.cs` so Plan 02 cannot disagree with it. The canonical
   tables' exact column lists were designed against the specification's prose bullets, which supply
   no column names, affinities, or index manifests. `SessionCampaignBindingKind` was **not** added:
   `Core/TheForge/SessionCampaignBinding.cs` already declares it with the required values.

One pre-existing defect found and fixed in passing: `GrimoireDatabaseBootstrapper.InstallSchemaAsync`
resolved `WeaveIndexAvailability` with `GetRequiredService` inside a `try` that also resolved
embedding settings. `AddArcanumGrimoireForCli` never registered that type, so every CLI bootstrap
threw before the dimension assignment and silently installed at the default embedding width instead
of the configured one, with the exception swallowed.

Plan 02 still owns the canonical repository, mutation kernel, FTS synchronization, search compiler,
`SearchAsync`, fallback, and base rebuild. Plan 04 still owns authenticated cursor and API
integration, the long-running rebuild adapter and recovery handler, and lifecycle, backup, restore,
reset, and erasure behavior.

## Global Constraints

- The approved source of truth is [`2026-08-13-covenant-design.md`](../specs/2026-08-13-covenant-design.md). If a plan step and the specification differ, stop that slice and resolve the plan before changing production code.
- Observe the expected failing test before each production change. Record the focused command and failure reason in the task notes or commit message draft.
- Preserve `Cli -> Api -> Infrastructure -> Core`. Core cannot reference provider, EF, SQLite, ASP.NET, or CLI types.
- Use source-generated JSON everywhere. Native provenance and recovery manifests use closed schemas and reject unknown fields or versions.
- Never use reflection-based serialization, dynamic schema discovery, numbered EF migrations, ambient authority, or SQL string interpolation.
- Schema resources are DDL-only, one object per file. Indexes remain co-located with their owning table resource.
- The existing core schema remains one startup-blocking transaction. Covenant canonical schema version 1 and Covenant accelerator schema version 1 each use a separate transaction and separate health result.
- `grimoire_feature_schemas` records family, transaction tier, integer version, source-definition fingerprint, installed-catalog fingerprint, installation timestamp, and health metadata.
- One `ICovenantSqliteConnectionInitializer` initializes every EF, bootstrap, backup, restore, reset, reinitialize, worker, fixture, benchmark, and direct SQLCipher connection. Delete, cascade, and accelerator authorization functions always begin false.
- Build SQLCipher from tag `v4.17.0`, tag object `f9788efa8ac4dfed75c03e4756b1666a1d0845da`, and commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`, based on SQLite `3.53.3`.
- Statically link OpenSSL `3.5.7`, from the OpenSSL 3.5 LTS line, with hidden symbols and no ambient Homebrew, system OpenSSL, or undeclared runtime dependency.
- Pin exact runtime values `sqlite_version() = "3.53.3"` and `PRAGMA cipher_version = "4.17.0 community"`; pin the accepted artifact's keyed `cipher_provider`, `cipher_provider_version`, and `cipher_status` values in the native manifest.
- Compile with `SQLITE_HAS_CODEC`, `SQLCIPHER_CRYPTO_OPENSSL`, `SQLITE_EXTRA_INIT=sqlcipher_extra_init`, `SQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown`, `SQLITE_THREADSAFE=1`, `SQLITE_TEMP_STORE=2`, `SQLITE_ENABLE_COLUMN_METADATA`, `SQLITE_ENABLE_FTS3`, `SQLITE_ENABLE_FTS3_PARENTHESIS`, `SQLITE_ENABLE_FTS4`, `SQLITE_ENABLE_FTS5`, `SQLITE_ENABLE_MATH_FUNCTIONS`, `SQLITE_ENABLE_RTREE`, `SQLITE_ENABLE_SNAPSHOT`, `SQLITE_OMIT_LOAD_EXTENSION`, and the pinned SQLCipher 4 compatibility defaults.
- Ship exactly `runtimes/osx-arm64/native/libe_sqlcipher.dylib`, `runtimes/osx-x64/native/libe_sqlcipher.dylib`, `runtimes/linux-arm64/native/libe_sqlcipher.so`, `runtimes/linux-x64/native/libe_sqlcipher.so`, and `runtimes/win-x64/native/e_sqlcipher.dll`.
- Keep one checked-in source manifest with source, archive, patch, license, toolchain/container, flag, runtime-value, binary, and SBOM hashes. SQLCipher archive SHA-256 and OpenSSL upstream signature and SHA-256 are mandatory.
- Remove `SQLitePCLRaw.bundle_e_sqlcipher`, retain an explicitly pinned AOT-safe `SQLitePCLRaw.provider.e_sqlcipher`, and permit no system-library or search-path fallback.
- Remove the relative sqlite-vec dynamic probe. Managed cosine remains the only vector fallback until a separately reviewed static accelerator exists.
- Keep the disabled and unrelated-product paths operational when Covenant canonical installation fails. A canonical failure must never join the startup-blocking core transaction.
- This plan owns native delivery, provider and connection initialization, catalog and manifest mechanics, all schema primitives, tier health, and schema failure isolation. Plan 02 owns the canonical repository, mutation, owner cleanup, accelerator synchronization, the sole search compiler and `SearchAsync` implementation, canonical fallback, and the base rebuild algorithm. Plan 04 owns authenticated cursor and API integration, the long-running rebuild adapter and recovery handler, and lifecycle, backup, restore, reset, and erasure behavior.
- Preserve exact absent-Covenant behavior. Existing non-Covenant raw SQL and schema identities remain compatible unless a task explicitly changes their catalog representation.
- Do not commit intermediate red states. One final branch commit is permitted only after all required verification is green.

---

## File and interface map

The implementation establishes these boundaries before higher plans consume them:

- `RetroDownfall.Arcanum.NativeSqlCipher` owns source provenance, reproducible binaries, RID selection, and package/build delivery. It contains no application policy.
- `ISqliteNativeRuntime.Initialize()` selects and freezes the bundled provider exactly once. `SqliteNativeRuntimeValidator.ValidateAsync(...)` proves codec and FTS behavior before the Grimoire opens.
- `ICovenantSqliteConnectionInitializer.InitializeAsync(...)` applies connection policy. `Authorize(...)` returns a scoped, non-serializable handle used by later repository and lifecycle plans.
- `GrimoireSchemaCatalog` owns ordered source resources and source fingerprints. `GrimoireSchemaManifestInspector` owns installed-catalog and index-shape validation.
- `GrimoireSchemaInstaller.InstallAsync(...)` owns three transactions and invokes `IGrimoireSchemaDataInitializer` inside the corresponding transaction.
- `CovenantAvailability` publishes immutable core, canonical, and accelerator schema-health snapshots. It contains no CRUD or search implementation.

### Task 1: Pin native provenance and create the asset project

**Files:**

- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/RetroDownfall.Arcanum.NativeSqlCipher.csproj`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/LICENSES/sqlcipher-LICENSE.txt`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/LICENSES/openssl-LICENSE.txt`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/sbom/sqlcipher.spdx.json`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/sbom/sqlcipher.cdx.json`
- Create: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/NativeSqlCipherTestPaths.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/NativeSqlCipherAssetContractTests.cs`
- Modify: `RetroDownfall.Arcanum.slnx`

**Interfaces:**

- Consumes: Approved SQLCipher and OpenSSL identities from the design specification.
- Produces: Manifest schema version `1`, the exact five-RID asset inventory, and an MSBuild project that later tasks populate and consume.

- [ ] **Step 1: Write the failing provenance contract test**

Add `Native_manifest_pins_approved_sources_and_shipping_rids` and `Native_project_is_the_only_declared_asset_source`. Parse with `JsonDocument`, read only named properties, and assert the exact values below:

```csharp
[Fact]
public void Native_manifest_pins_approved_sources_and_shipping_rids()
{

    using JsonDocument document = JsonDocument.Parse(
        File.ReadAllText(NativeSqlCipherTestPaths.Manifest));

    JsonElement root = document.RootElement;

    Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

    Assert.Equal("v4.17.0", root.GetProperty("sqlcipher").GetProperty("tag").GetString());

    Assert.Equal(
        "f9788efa8ac4dfed75c03e4756b1666a1d0845da",
        root.GetProperty("sqlcipher").GetProperty("tagObject").GetString());

    Assert.Equal(
        "810db22f575ee7cf94ea96a3e91622b5fcece3dc",
        root.GetProperty("sqlcipher").GetProperty("commit").GetString());

    Assert.Equal("3.53.3", root.GetProperty("sqliteVersion").GetString());

    Assert.Equal("3.5.7", root.GetProperty("openssl").GetProperty("version").GetString());

    string[] expectedRids = ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-x64"];

    string[] actualRids =
    [
        .. root.GetProperty("assets")
            .EnumerateArray()
            .Select(static asset => asset.GetProperty("rid").GetString()!)
            .Order(StringComparer.Ordinal),
    ];

    Assert.Equal(expectedRids, actualRids);

}
```

`NativeSqlCipherTestPaths.RepositoryRoot()` must walk parents from `AppContext.BaseDirectory` until it finds both `RetroDownfall.Arcanum.slnx` and `src`, then fail with a bounded diagnostic if no root is found.

- [ ] **Step 2: Run the focused test and witness the missing manifest**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipherAssetContractTests.Native_manifest_pins_approved_sources_and_shipping_rids"
```

Expected: FAIL because `src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json` does not exist.

- [ ] **Step 3: Verify upstream identities and record immutable hashes**

Fetch the immutable SQLCipher commit archive and the OpenSSL 3.5.7 release plus its upstream signature into a fresh `mktemp -d` directory. Run these exact checks before adding the manifest:

```bash
git ls-remote https://github.com/sqlcipher/sqlcipher.git refs/tags/v4.17.0 refs/tags/v4.17.0^{}
git verify-tag v4.17.0
git rev-parse v4.17.0^{tag}
git rev-parse v4.17.0^{}
shasum -a 256 sqlcipher-810db22f575ee7cf94ea96a3e91622b5fcece3dc.tar.gz
gpg --verify openssl-3.5.7.tar.gz.asc openssl-3.5.7.tar.gz
shasum -a 256 openssl-3.5.7.tar.gz
```

Expected: the tag object and peeled commit equal the approved values; GPG succeeds; both archives produce one lowercase 64-character SHA-256. Record those observed hashes verbatim. If an upstream SQLCipher signature becomes available, verify and record it. Do not create a substitute signature.

- [ ] **Step 4: Add the minimal asset project and closed manifest**

Create a packable project with `IsPackable=true`, `IncludeBuildOutput=false`, deterministic build enabled, package ID `RetroDownfall.Arcanum.NativeSqlCipher`, and no package reference. The manifest must have these closed top-level properties:

```text
schemaVersion
sqlcipher
sqliteVersion
openssl
compileOptions
compatibilityDefaults
runtimePragmas
toolchains
patches
licenses
assets
sboms
```

Store the immutable commit URL containing the full SQLCipher commit, the two observed archive hashes, the OpenSSL signer fingerprint, exact source-license hashes, the complete approved compile-option set, and one asset record per exact RID/path. Each asset record contains RID, relative path, SHA-256, output filename, compiler identity, linker identity, container or runner image digest, and dynamic dependency allowlist.

Add the project to `RetroDownfall.Arcanum.slnx`. Keep binaries absent until Task 3, so this task establishes provenance and shape only.

- [ ] **Step 5: Run the focused contract tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipherAssetContractTests"
```

Expected: PASS for provenance, closed property set, exact source identities, exact RID/path inventory, and asset-project uniqueness. Tests that require generated binaries remain excluded by their distinct method-name filter until Task 3.

- [ ] **Step 6: Refactor the manifest reader in tests**

Extract `ReadManifest()` and `AssertSha256(JsonElement, string)` helpers. Reject missing values, uppercase digests, non-64-character digests, duplicate RIDs, duplicate output names within a RID, and absolute paths. Re-run the same filter and expect PASS.

### Task 2: Build and verify SQLCipher reproducibly

**Files:**

- Create: `scripts/build-native-sqlcipher.sh`
- Create: `scripts/verify-native-sqlcipher.sh`
- Modify: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/NativeSqlCipherAssetContractTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Packaging/ReleasePipelineTests.cs`

**Interfaces:**

- Consumes: `native-source-manifest.json` schema version 1 and the immutable source hashes from Task 1.
- Produces: `build-native-sqlcipher.sh --rid <RID> --output <directory>` and `verify-native-sqlcipher.sh --manifest-only|--rid <RID>|--all`.

- [ ] **Step 1: Write failing script-contract tests**

Add these tests:

```csharp
[Fact]
public void Native_build_script_uses_only_manifest_pinned_sources_and_flags()
{

    string script = File.ReadAllText(NativeSqlCipherTestPaths.BuildScript);

    Assert.Contains("SOURCE_DATE_EPOCH", script, StringComparison.Ordinal);

    Assert.Contains("SQLITE_OMIT_LOAD_EXTENSION", script, StringComparison.Ordinal);

    Assert.Contains("SQLCIPHER_CRYPTO_OPENSSL", script, StringComparison.Ordinal);

    Assert.DoesNotContain("brew --prefix openssl", script, StringComparison.Ordinal);

    Assert.DoesNotContain("/usr/lib/libssl", script, StringComparison.Ordinal);

}

[Fact]
public void Native_verifier_has_manifest_only_rid_and_all_modes()
{

    string script = File.ReadAllText(NativeSqlCipherTestPaths.VerifyScript);

    Assert.Contains("--manifest-only", script, StringComparison.Ordinal);

    Assert.Contains("--rid", script, StringComparison.Ordinal);

    Assert.Contains("--all", script, StringComparison.Ordinal);

}
```

Extend `ReleasePipelineTests` with `Native_scripts_never_accept_unverified_downloads`, asserting every `curl` result is checked against the manifest before extraction and OpenSSL verification uses `gpg --verify`.

- [ ] **Step 2: Run the tests and witness missing scripts**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipher|FullyQualifiedName~ReleasePipelineTests.Native_scripts"
```

Expected: FAIL because both scripts are absent.

- [ ] **Step 3: Implement manifest-only verification**

Write `verify-native-sqlcipher.sh` with `set -euo pipefail`. Resolve the repository from the script directory, use `jq -e` to validate schema version, sources, exact RIDs, unique paths, 64-character hashes, license hashes, SBOM hashes, compile options, and runtime values. Reject symlinks and any path escaping `src/RetroDownfall.Arcanum.NativeSqlCipher`.

The manifest-only mode must execute without network access:

```bash
./scripts/verify-native-sqlcipher.sh --manifest-only
```

Expected: exit zero after Task 1's manifest and license/SBOM files validate.

- [ ] **Step 4: Implement one reproducible build pipeline**

Write `build-native-sqlcipher.sh` with a closed RID switch for the five shipping RIDs. The script must:

1. Create its work area through `mktemp -d` and clean it through a trap.
2. Download only the manifest URLs, verify hashes before extraction, and verify the OpenSSL signature.
3. Prove the SQLCipher tag-object-to-commit relationship against Git metadata.
4. Build OpenSSL static, position-independent, and with hidden exported symbols.
5. Build the SQLCipher shared library with every compile definition in Global Constraints.
6. Add stack protection, fortification where supported, non-executable memory, ASLR/PIE, RELRO/NOW or Windows equivalents, deterministic archives, path maps, linker timestamp suppression, and `SOURCE_DATE_EPOCH` derived from the pinned commit.
7. Strip only after linking and emit the exact platform filename into the caller-provided output directory.
8. Emit a build attestation containing compiler, linker, image, input, flag, and output hashes.

Use array arguments for compilers and linkers. Do not assemble flags into an `eval` string.

- [ ] **Step 5: Implement binary verification and clean-rebuild comparison**

For each selected RID, `verify-native-sqlcipher.sh` must verify the checked-in asset hash, filename, file format, declared dynamic dependency allowlist, hidden OpenSSL symbols, required compile options, absence of `load_extension`, and SPDX/CycloneDX hashes. `--all` dispatches each RID to its pinned native runner or container, rebuilds twice from clean directories, and compares byte hashes before signing.

The host-compatible RID additionally invokes SQLCipher's upstream testfixture. Unsupported cross-host execution must fail `--all`; it cannot silently skip a RID.

- [ ] **Step 6: Run the script contracts and provenance-only gate**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipher|FullyQualifiedName~ReleasePipelineTests.Native_scripts"
./scripts/verify-native-sqlcipher.sh --manifest-only
```

Expected: both commands exit zero.

- [ ] **Step 7: Refactor shared shell validation**

Keep `require_cmd`, `read_manifest_value`, `verify_sha256`, `verify_relative_path`, and `rid_output_name` as single-purpose functions. Run `shellcheck scripts/build-native-sqlcipher.sh scripts/verify-native-sqlcipher.sh` and the focused tests. Expected: zero shellcheck findings and all tests PASS.

### Task 3: Check in five native assets and deliver exactly one per RID

**Files:**

- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/build/RetroDownfall.Arcanum.NativeSqlCipher.targets`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/buildTransitive/RetroDownfall.Arcanum.NativeSqlCipher.targets`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/osx-arm64/native/libe_sqlcipher.dylib`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/osx-x64/native/libe_sqlcipher.dylib`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/linux-arm64/native/libe_sqlcipher.so`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/linux-x64/native/libe_sqlcipher.so`
- Create: `src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-x64/native/e_sqlcipher.dll`
- Modify: `src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json`
- Modify: `src/RetroDownfall.Arcanum.NativeSqlCipher/RetroDownfall.Arcanum.NativeSqlCipher.csproj`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/packages.lock.json`
- Create: `src/RetroDownfall.Arcanum.Cli/packages.lock.json`
- Modify: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/NativeSqlCipherAssetContractTests.cs`

**Interfaces:**

- Consumes: Task 2 build and verification commands.
- Produces: `ArcanumSqlCipherNativeAsset` MSBuild items, exactly one `NativeCopyLocalItems` item, and exactly one `ResolvedFileToPublish` item for a concrete shipping RID.

- [ ] **Step 1: Add failing delivery tests**

Add `Every_shipping_rid_has_one_verified_native_asset`, `Infrastructure_removes_bundle_and_pins_provider`, and `Native_targets_fail_zero_or_multiple_rid_matches`. The package assertion is exact:

```csharp
XDocument project = XDocument.Load(NativeSqlCipherTestPaths.InfrastructureProject);

Assert.Empty(
    project.Descendants("PackageReference")
        .Where(static item => string.Equals(
            item.Attribute("Include")?.Value,
            "SQLitePCLRaw.bundle_e_sqlcipher",
            StringComparison.Ordinal)));

XElement provider = Assert.Single(
    project.Descendants("PackageReference")
        .Where(static item => string.Equals(
            item.Attribute("Include")?.Value,
            "SQLitePCLRaw.provider.e_sqlcipher",
            StringComparison.Ordinal)));

Assert.False(string.IsNullOrWhiteSpace(provider.Attribute("Version")?.Value));
```

- [ ] **Step 2: Run the delivery test and witness absent binaries or old bundle**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipherAssetContractTests.Every_shipping_rid|FullyQualifiedName~NativeSqlCipherAssetContractTests.Infrastructure_removes_bundle"
```

Expected: FAIL because the RID assets are absent and Infrastructure still references `SQLitePCLRaw.bundle_e_sqlcipher`.

- [ ] **Step 3: Produce and verify the unsigned assets**

Run `scripts/build-native-sqlcipher.sh` for each exact RID on its pinned runner. Run each RID twice and require byte-identical hashes. Copy the verified unsigned output into its exact `runtimes/<RID>/native` path with `apply_patch` or the repository's binary-safe patch mechanism, then update only the manifest's observed output, toolchain, and SBOM hashes. Run:

```bash
./scripts/verify-native-sqlcipher.sh --all
```

Expected: exit zero for provenance, reproducibility, testfixture, dynamic dependencies, hashes, and SBOMs on all five RIDs.

- [ ] **Step 4: Implement exact-RID MSBuild delivery**

Both build targets must map only the current `RuntimeIdentifier`. The repository `build` target and packed `buildTransitive` target expose the same contract:

```xml
<ItemGroup Condition="'$(RuntimeIdentifier)' != ''">
  <ArcanumSqlCipherNativeAsset Include="$(MSBuildThisFileDirectory)..\runtimes\$(RuntimeIdentifier)\native\libe_sqlcipher.dylib"
                                Condition="$([System.String]::Copy('$(RuntimeIdentifier)').StartsWith('osx-'))" />
  <ArcanumSqlCipherNativeAsset Include="$(MSBuildThisFileDirectory)..\runtimes\$(RuntimeIdentifier)\native\libe_sqlcipher.so"
                                Condition="$([System.String]::Copy('$(RuntimeIdentifier)').StartsWith('linux-'))" />
  <ArcanumSqlCipherNativeAsset Include="$(MSBuildThisFileDirectory)..\runtimes\$(RuntimeIdentifier)\native\e_sqlcipher.dll"
                                Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
</ItemGroup>

<Target Name="ResolveArcanumNativeSqlCipher"
        BeforeTargets="CopyFilesToOutputDirectory;ComputeFilesToPublish"
        Condition="'$(RuntimeIdentifier)' != ''">
  <Error Condition="'@(ArcanumSqlCipherNativeAsset)' == ''"
         Text="No hermetic SQLCipher asset exists for RuntimeIdentifier '$(RuntimeIdentifier)'." />
  <Error Condition="'@(ArcanumSqlCipherNativeAsset->Count())' != '1'"
         Text="Exactly one hermetic SQLCipher asset is required for RuntimeIdentifier '$(RuntimeIdentifier)'." />
  <ItemGroup>
    <NativeCopyLocalItems Include="@(ArcanumSqlCipherNativeAsset)" />
    <ResolvedFileToPublish Include="@(ArcanumSqlCipherNativeAsset)">
      <RelativePath>%(Filename)%(Extension)</RelativePath>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </ResolvedFileToPublish>
  </ItemGroup>
</Target>
```

The project packs runtime assets under `runtimes/<RID>/native`, both target files under their conventional folders, the source manifest, licenses, and SBOMs. Infrastructure imports the repository build target, references the asset project, removes the bundle package, and pins `SQLitePCLRaw.provider.e_sqlcipher` to version `2.1.11`.

- [ ] **Step 5: Generate and enforce package locks**

Set `RestorePackagesWithLockFile=true` in the shipping CLI and Infrastructure projects. Generate lock files using the approved SDK, inspect the `SQLitePCLRaw.provider.e_sqlcipher` SHA-512 entry, and commit the exact generated files. CI and release restore use `--locked-mode`.

- [ ] **Step 6: Run exact-RID item and output tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipherAssetContractTests"
dotnet build src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj -r "$(dotnet --info | awk '/RID:/{print $2; exit}')"
```

Expected: tests PASS; the build output contains one platform-named `e_sqlcipher` library whose SHA-256 matches the manifest, with no second SQLCipher library.

- [ ] **Step 7: Refactor the build and packed target into one source**

Keep `buildTransitive/RetroDownfall.Arcanum.NativeSqlCipher.targets` as the source and make the repository build target import it through a stable relative path. Re-run the Task 3 commands and expect PASS.

### Task 4: Select and freeze the SQLCipher provider once

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/ISqliteNativeRuntime.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntime.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/SqliteNativeRuntimeTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

**Interfaces:**

- Consumes: The single native library and pinned `SQLitePCLRaw.provider.e_sqlcipher` from Task 3.
- Produces: `ISqliteNativeRuntime.Initialize()` and process singleton `SqliteNativeRuntime.Instance`.

- [ ] **Step 1: Write failing initialization tests**

Add `Initialize_selects_e_sqlcipher_and_is_idempotent` and `Initialize_freezes_provider_against_replacement`:

```csharp
[Fact]
public void Initialize_selects_e_sqlcipher_and_is_idempotent()
{

    SqliteNativeRuntime.Instance.Initialize();

    SqliteNativeRuntime.Instance.Initialize();

    Assert.Equal("3.53.3", raw.sqlite3_libversion().utf8_to_string());

    Assert.ThrowsAny<Exception>(() => raw.SetProvider(new SQLite3Provider_e_sqlcipher()));

}
```

Use the provider replacement supported by the test project without introducing another SQLite native asset. The assertion must prove `raw.FreezeProvider(true)` prevents replacement.

- [ ] **Step 2: Run the focused test and witness missing types**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SqliteNativeRuntimeTests"
```

Expected: FAIL to compile because `ISqliteNativeRuntime` and `SqliteNativeRuntime` do not exist.

- [ ] **Step 3: Add the smallest process-wide initializer**

Use `Lazy<T>` with execution-and-publication semantics so no caller can observe a half-installed provider:

```csharp
internal interface ISqliteNativeRuntime
{

    void Initialize();

}

internal sealed class SqliteNativeRuntime : ISqliteNativeRuntime
{

    public static SqliteNativeRuntime Instance { get; } = new();

    private static readonly Lazy<bool> Initialized =
        new(InitializeCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public void Initialize() => _ = Initialized.Value;

    private static bool InitializeCore()
    {

        raw.SetProvider(new SQLite3Provider_e_sqlcipher());

        raw.FreezeProvider(true);

        return true;

    }

}
```

Call `SqliteNativeRuntime.Instance.Initialize()` at the start of `ArcanumDbContextFactory.CreateDbContext`. Register the same singleton as `ISqliteNativeRuntime` for runtime composition.

- [ ] **Step 4: Run the initialization tests**

Run the Task 4 filter. Expected: PASS, with the exact runtime library version and a frozen provider.

- [ ] **Step 5: Refactor all initialization diagnostics to content-free errors**

Wrap provider-load failures in `SqliteNativeRuntimeUnavailableException` whose public message names only the RID and expected asset filename. Do not include search paths, user paths, or loader environment. Add `Initialize_failure_does_not_search_or_disclose_paths`, re-run the filter, and expect PASS.

### Task 5: Prove codec, FTS5, and old-database compatibility at runtime

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeManifest.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidator.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteNativeRuntimeValidationResult.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/SqlCipherCompatibilityTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/TestData/SqlCipher/sqlcipher-4.5.2-sqlite-3.39.2.db`
- Create: `tests/RetroDownfall.Arcanum.Tests/TestData/SqlCipher/sqlcipher-4.5.2-sqlite-3.39.2.json`
- Create: `scripts/build-sqlcipher-compatibility-fixture.sh`
- Modify: `tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj`

**Interfaces:**

- Consumes: `ISqliteNativeRuntime.Initialize()` and exact accepted runtime values from the native source manifest.
- Produces: `SqliteNativeRuntimeValidator.ValidateAsync(string scratchDirectory, CancellationToken)` returning `SqliteNativeRuntimeValidationResult` with no secret or path fields.

- [ ] **Step 1: Write failing runtime smoke tests**

Add these exact test methods:

```text
ValidateAsync_accepts_the_pinned_runtime_and_codec
ValidateAsync_rejects_wrong_key_and_tampered_pages
ValidateAsync_proves_FTS5_secure_delete_and_rank_one_integrity
ValidateAsync_proves_load_extension_is_unauthorized
Pinned_runtime_opens_and_mutates_the_4_5_2_compatibility_fixture
New_runtime_database_reopens_with_the_retained_old_runtime_job_without_Covenant_FTS
```

The first test asserts:

```csharp
SqliteNativeRuntimeValidationResult result = await validator.ValidateAsync(
    scratchDirectory,
    CancellationToken.None);

Assert.True(result.IsValid, result.ErrorCode);

Assert.Equal("3.53.3", result.SqliteVersion);

Assert.Equal("4.17.0 community", result.CipherVersion);

Assert.True(result.CodecRoundTripPassed);

Assert.True(result.FtsSecureDeletePassed);

Assert.True(result.LoadExtensionBlocked);
```

- [ ] **Step 2: Run the focused test and witness missing validator types**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SqlCipherCompatibilityTests"
```

Expected: FAIL to compile because the validator and result do not exist.

- [ ] **Step 3: Add a closed runtime-manifest reader**

Embed `native-source-manifest.json` into Infrastructure through the native project target. `SqliteNativeRuntimeManifest.Load()` reads it with `JsonDocument`, requires schema version 1 and the current RID, and returns only these values:

```csharp
internal sealed record SqliteNativeRuntimeManifest(
    string SqliteVersion,
    string CipherVersion,
    string CipherProvider,
    string CipherProviderVersion,
    string CipherStatus,
    IReadOnlySet<string> CompileOptions,
    string AssetSha256);
```

Reject unknown schema versions, a missing current RID, duplicate compile options, and a binary hash that does not match the loaded file. The error carries a stable code such as `Grimoire.NativeRuntimeManifestInvalid`, with no raw manifest content.

- [ ] **Step 4: Implement the encrypted scratch proof**

`ValidateAsync` must perform this sequence in one owned temporary directory:

1. Initialize the provider and generate a random 256-bit key.
2. Create an encrypted database with pooling disabled.
3. Read exact `sqlite_version()`, `cipher_version`, keyed `cipher_provider`, `cipher_provider_version`, and `cipher_status` values.
4. Read `PRAGMA compile_options` and compare the complete security-relevant set with the manifest.
5. Create a sentinel table, insert one sentinel, checkpoint, close every handle, and reopen with the same key.
6. Verify the sentinel and require every row from `PRAGMA cipher_integrity_check` to equal `ok`.
7. Open with a different key and require the first page access to fail.
8. Create an external-content FTS5 fixture, enable `secure-delete` with rank 1, delete a token, and require rank-1 integrity plus absence of the deleted token.
9. Execute `SELECT load_extension('forbidden');` and require SQLite authorization failure.
10. Close handles and delete the main file plus WAL, SHM, and journal sidecars.

On any mismatch return a closed error code and mark Grimoire unavailable. Do not search another library or continue into database bootstrap.

- [ ] **Step 5: Generate and pin the old-runtime compatibility fixture**

`build-sqlcipher-compatibility-fixture.sh` must run in a pinned container containing the current `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 runtime, create an encrypted fixture with the repository's current page size and KDF settings, exercise WAL and rekey, checkpoint and close it, then emit a content-free JSON sidecar with runtime versions, page/KDF settings, sentinel digest, database SHA-256, and container digest. The script refuses to overwrite a fixture whose recorded source identity differs.

The fixture is copied as test data. Tests copy it to a temporary directory before mutation and never alter the checked-in file.

- [ ] **Step 6: Run runtime and compatibility tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SqlCipherCompatibilityTests"
```

Expected: PASS for correct-key reopen, wrong-key rejection, tamper rejection, WAL recovery, rekey, FTS secure-delete, load-extension refusal, and 4.5.2 compatibility.

- [ ] **Step 7: Refactor scratch cleanup and failure normalization**

Extract one `SqliteScratchDatabase` owner that records only application-created paths and deletes its main file and known sidecars on disposal. Add `ValidateAsync_removes_every_scratch_artifact_after_failure`; re-run the focused suite and expect PASS.

### Task 6: Centralize all SQLite connection policy and authorization

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantSqliteConnectionInitializer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionInitializer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionMode.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteAuthorizationKind.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteAuthorizationScope.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantSqliteConnectionOwner.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionOwner.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionLease.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionClass.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ICovenantSqliteConnectionFactory.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSqliteConnectionFactory.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantInitializedConnectionLease.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDatabaseArtifactInventory.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSqliteConnectionInitializerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSqliteConnectionOwnerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantSqliteConnectionFactoryTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantDatabaseArtifactInventoryTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqliteConnectionPragmas.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/SqlitePragmaConnectionInterceptor.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/SqlitePragmaConnectionInterceptorTests.cs`

**Interfaces:**

- Consumes: `ISqliteNativeRuntime.Initialize()` from Task 4.
- Produces the central handle owner, bounded sidecar and staging inventory, and:

```csharp
internal interface ICovenantSqliteConnectionInitializer
{

    ValueTask InitializeAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken);

    CovenantSqliteAuthorizationScope Authorize(
        SqliteConnection connection,
        CovenantSqliteAuthorizationKind kind);

}

internal interface ICovenantSqliteConnectionFactory
{

    ValueTask<CovenantInitializedConnectionLease> OpenAsync(
        SqliteConnectionStringBuilder connection,
        CovenantSqliteConnectionMode mode,
        CovenantSqliteConnectionClass connectionClass,
        CancellationToken cancellationToken);

}
```

- [ ] **Step 1: Write failing pragma and authorization tests**

Add these methods:

```text
InitializeAsync_applies_foreign_keys_busy_timeout_secure_delete_and_cipher_policy
InitializeAsync_read_only_mode_does_not_attempt_journal_mode_change
Every_authorization_function_begins_false
Authorize_enables_only_the_requested_function_until_disposed
Nested_authorization_scopes_restore_false_after_the_last_dispose
Authorization_scope_cannot_be_serialized_or_used_on_another_connection
General_authorize_rejects_restore_staging_sanitization
Interceptor_delegates_to_the_central_initializer
Owner_rejects_new_open_after_close_begins_and_drains_exact_registered_handles
Owner_delayed_release_cannot_remove_a_reused_connection_registration
Owner_exclusive_lease_clears_pools_only_after_every_direct_and_EF_handle_closes
Artifact_inventory_returns_only_main_wal_shm_journal_and_registered_staging_files
Factory_acquires_owner_before_open_and_initializes_before_return
Factory_open_failure_disposes_connection_and_exact_registration
Factory_dispose_closes_connection_before_owner_release
Factory_rejects_open_after_exclusive_close_begins
```

The default-state assertion queries all closed function names:

```csharp
string[] functions =
[
    "arcanum_session_binding_write_authorized",
    "arcanum_session_retention_authorized",
    "arcanum_owner_cleanup_authorized",
    "arcanum_artifact_replacement_authorized",
    "arcanum_sensitivity_purge_authorized",
      "arcanum_covenant_family_maintenance_authorized",
      "arcanum_accelerator_sync_authorized",
    "arcanum_protected_session_transfer_authorized",
    "arcanum_turn_capacity_mutation_authorized",
    "arcanum_campaign_path_marker_intent_mutation_authorized",
    "arcanum_managed_file_intent_mutation_authorized",
    "arcanum_restore_staging_managed_authority_sanitization_authorized",
];

foreach (string function in functions)
{

    Assert.Equal(0L, await ScalarLongAsync(connection, $"SELECT {function}();"));

}
```

The names above are fixed internal SQL identifiers, never input. Production commands remain parameterized for all variable values.
`Authorize_enables_only_the_requested_function_until_disposed` covers ordinary codes 0 through 10. `General_authorize_rejects_restore_staging_sanitization` owns code 11 until Task 15 adds and tests the sealed candidate-only borrower.

- [ ] **Step 2: Run the focused test and witness missing initializer types**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSqliteConnectionInitializerTests|FullyQualifiedName~CovenantSqliteConnectionOwnerTests|FullyQualifiedName~CovenantSqliteConnectionFactoryTests|FullyQualifiedName~CovenantDatabaseArtifactInventoryTests|FullyQualifiedName~SqlitePragmaConnectionInterceptorTests"
```

Expected: FAIL to compile because the central initializer and authorization types do not exist.

- [ ] **Step 3: Define closed modes and authorization kinds**

Use these exact values:

```csharp
internal enum CovenantSqliteConnectionMode
{

    ReadWrite = 0,

    ReadOnly = 1,

    ExclusiveMaintenance = 2,

}

internal enum CovenantSqliteAuthorizationKind
{

    SessionBindingWrite = 0,

    SessionRetention = 1,

    OwnerCleanup = 2,

    ArtifactReplacement = 3,

    SensitivityRetentionPurge = 4,

    CovenantFamilyMaintenance = 5,

      AcceleratorSynchronization = 6,

      ProtectedSessionTransfer = 7,

      TurnCapacityMutation = 8,

      CampaignPathMarkerIntentMutation = 9,

      ManagedFileIntentMutation = 10,

      RestoreStagingManagedAuthoritySanitization = 11,

}
```

`CovenantSqliteAuthorizationScope` is a sealed `IDisposable` class with no public constructor, no serializable fields, and a connection-identity check. Use per-kind nesting counters so an inner scope cannot disable an outer authorization. The general `Authorize` entry point rejects `RestoreStagingManagedAuthoritySanitization`. Only Task 15's separate internal overload accepts that code, and only with the sealed authenticated restore-staging connection capability plus its same-object active run identity for the exact unpublished candidate connection. The overload receives no raw connection and can obtain the scope only through that capability's producer-only initializer-bound authorization operation.

- [ ] **Step 4: Implement initialization and false-by-default functions**

`InitializeAsync` must:

1. Call `ISqliteNativeRuntime.Initialize()`.
2. Require an open `SqliteConnection`.
3. Register all twelve scalar functions over connection-local authorization state.
4. Apply `busy_timeout=5000`, `foreign_keys=ON`, `secure_delete=ON`, and the pinned SQLCipher policy.
5. Apply `journal_mode=WAL` and `synchronous=NORMAL` only for `ReadWrite`.
6. Apply the exclusive locking policy only for `ExclusiveMaintenance`.
7. Read back `foreign_keys`, `secure_delete`, `cipher_version`, and busy timeout, then fail closed on mismatch.

Keep `SqliteConnectionPragmas` as a compatibility facade that delegates to the singleton initializer until Task 7 migrates all callers. Change `SqlitePragmaConnectionInterceptor` from a static instance to an injected singleton so it delegates to the same initialized service in sync and async callbacks.

Every logical open first acquires a registration from `ICovenantSqliteConnectionOwner` for the exact database-file identity and connection class, then attaches the opened connection to that exact registration. Exclusive close atomically rejects new registration, signals cancellable owners, drains every EF, pooled, direct, worker, backup, and maintenance lease, calls `SqliteConnection.ClearAllPools()` only after drain, and returns an exclusive lease. Release compares the registration object and monotonic identity, so delayed cleanup cannot remove a later registration for the same connection object.

`CovenantSqliteConnectionFactory.OpenAsync` clones the supplied builder, resolves the exact database-file identity, acquires its owner registration before constructing or opening the connection, opens it, runs `InitializeAsync`, attaches the same connection to the registration, and returns a sealed `CovenantInitializedConnectionLease`. Initialization or open failure closes and disposes the connection before releasing that exact registration. Lease disposal closes and disposes before owner release. The returned `SqliteConnection` cannot be detached from its lease. Long-lived single-writer services may retain one warm lease, but may not retain a bare connection.

`CovenantDatabaseArtifactInventory` accepts one resolved Grimoire database path plus bounded staging identities already recorded in recovery journals. It returns only the main file and exact sibling `-wal`, `-shm`, rollback-journal, registered temporary, staged replacement, and old replacement files. It never expands a glob, follows a link, scans a parent directory, or accepts a caller path.

- [ ] **Step 5: Run the central-initializer tests**

Run the Task 6 command. Expected: PASS, including read-only mode and nested authorization behavior.

- [ ] **Step 6: Refactor connection state ownership**

Store authorization state in a `ConditionalWeakTable<SqliteConnection, CovenantSqliteConnectionState>` and ownership in explicit reference registrations. Disposal must be idempotent, authorization on a closed or different connection must throw, and reopening a pooled logical connection must begin with all counters at zero and a new owner registration. Add `Reopened_connection_resets_every_authorization`; re-run the filter and expect PASS.

### Task 7: Route every SQLite path through the native runtime and connection initializer

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextOptionsConfigurator.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/ArcanumDbContextFactory.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreDatabaseWorker.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupArchiveCodec.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupInventoryPlanner.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Diagnostics/GrimoireDiagnostics.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetExistingGrimoire.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Weave/SqliteVecExtensionLoader.cs`
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Accelerators/entry_embeddings_vec.sql`
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Accelerators/saga_memory_embeddings_vec.sql`
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Accelerators/session_attachment_embeddings_vec.sql`
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Accelerators/tapestry_node_embeddings_vec.sql`
- Delete: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Accelerators/workspace_file_embeddings_vec.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Weave/WeaveIndexAvailability.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/ArcanumWebApplicationFactory.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupDatabaseSnapshotterTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreServiceTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupSessionImporterTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/SqliteConnectionInitializationInventoryTests.cs`

**Interfaces:**

- Consumes: Task 4 provider initializer plus Task 6 connection factory, initializer, owner, and artifact inventory.
- Produces: one audited ownership and initialization path for every SQLCipher connection and permanent managed vector fallback.

- [ ] **Step 1: Write the failing source-inventory test**

Add `Every_direct_sqlcipher_connection_routes_through_the_central_initializer`, `Every_EF_and_direct_open_acquires_and_releases_the_connection_owner`, `No_Batteries_initialization_remains`, and `No_dynamic_extension_loader_or_vec0_resource_remains`. Inventory every production `.cs` file containing `SqliteConnection`, every EF options builder using SQLite, and every schema resource containing `vec0`. Fail with sorted relative paths.

The assertions include:

```csharp
Assert.DoesNotContain(
    productionSources,
    static source => source.Text.Contains("Batteries_V2.Init", StringComparison.Ordinal));

Assert.DoesNotContain(
    productionSources,
    static source => source.Text.Contains("EnableExtensions", StringComparison.Ordinal)
        || source.Text.Contains("LoadExtension", StringComparison.Ordinal));
```

Allow the runtime validator's intentional `load_extension()` refusal probe by exact file and exact SQL literal.

- [ ] **Step 2: Run the inventory and witness all current bypasses**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SqliteConnectionInitializationInventoryTests"
```

Expected: FAIL naming the current bootstrap, factory, backup, reset, diagnostics, fixture, and dynamic-extension paths.

- [ ] **Step 3: Replace every `Batteries_V2.Init()` call**

Call `ISqliteNativeRuntime.Initialize()` before constructing the first connection. Static design-time and compatibility entry points call `SqliteNativeRuntime.Instance.Initialize()`. DI-owned runtime paths receive `ISqliteNativeRuntime` through their existing constructor or service scope.

Remove `using SQLitePCL;` where it becomes unused. Test fixtures call the same production initializer rather than package helpers.

- [ ] **Step 4: Initialize every opened connection**

Immediately after `Open` or `OpenAsync`, call the central initializer with the correct closed mode. Required mappings are:

| Path | Mode |
|---|---|
| EF interceptor and ordinary repository connection | `ReadWrite` |
| bootstrap install, rekey, existing-file probe, shutdown checkpoint | `ReadWrite` |
| backup snapshot source and inventory | `ReadOnly` where SQLite permits snapshot semantics, otherwise `ReadWrite` with no mutation authorization |
| extracted archive validation and staged restore schema convergence | `ReadWrite` |
| reset, factory erase, and installation-reset maintenance handle | `ExclusiveMaintenance` |
| diagnostics integrity connection | `ReadOnly` |
| testhost, fixture template, copied fixture, and benchmark fixture | matching production mode |

No caller receives an authorization handle merely because it uses maintenance mode. Later plans acquire the narrow handle around the exact transaction that needs it.

Route every direct open through `ICovenantSqliteConnectionFactory` and retain its returned lease through the complete use. Extend `SqlitePragmaConnectionInterceptor` with exact owner registration at opening, attachment and initialization at opened, failed-open cleanup, and close release, including pooled reuse. Backup, restore, reset, index, cleanup, benchmark, diagnostics, and test fixtures use the same factory or EF interceptor path. No component may call `SqliteConnection.ClearAllPools()` directly outside the owner's exclusive lease.

- [ ] **Step 5: Remove dynamic sqlite-vec loading and DDL**

Delete the loader and five `vec0` resources. Remove `TryInstallAcceleratorsAsync`'s sqlite-vec branch from `GrimoireSchemaInstaller`; Tasks 13 and 15 replace it with tiered Covenant FTS installation. Keep BLOB companion tables and managed cosine search. Update `WeaveIndexAvailability` so its default and published state remain managed-only without probing a relative library.

- [ ] **Step 6: Run initialization inventory and affected tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~SqliteConnectionInitializationInventoryTests|FullyQualifiedName~SqlitePragmaConnectionInterceptorTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~BackupDatabaseSnapshotterTests"
```

Expected: PASS. Every fresh connection reports authorization functions false, and existing backup/bootstrap behavior remains green.

- [ ] **Step 7: Remove component-local connection-opening helpers**

Replace component-local `OpenInitializedAsync` helpers with the Task 6 factory. Keep caller-owned EF connections on the interceptor path and reject any direct caller that constructs, opens, initializes, or registers a connection in separate steps. Re-run the Task 7 command and expect PASS.

### Task 8: Model schema family, transaction tier, and ordered catalogs

**Files:**

- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaObject.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaCatalog.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaCatalogTests.cs`

**Interfaces:**

- Consumes: Existing embedded resource glob and one-object-per-file convention.
- Produces: `GrimoireSchemaFamily`, `GrimoireSchemaTransactionTier`, extended `GrimoireSchemaObject`, `CoreObjects`, `CovenantCanonicalObjects`, `CovenantAcceleratorObjects`, `CanonicalSchemaFingerprint`, and `CovenantCanonicalSchemaFingerprint`.

- [ ] **Step 1: Replace two-tier catalog assumptions with failing tests**

Add these methods:

```text
Catalog_exposes_core_covenant_canonical_and_covenant_accelerator_in_order
Covenant_resources_are_never_in_CoreObjects
Nested_capability_path_encodes_family_tier_and_category
Unknown_family_tier_or_category_fails_closed
Combined_and_covenant_canonical_source_fingerprints_are_stable_and_distinct
```

At this slice, the two optional catalogs may still be empty. Prove the properties and path parser exist, that core resources never enter them, and that a synthetic nested resource maps correctly. Tasks 12 and 13 add the nonempty catalog assertions with the real resources.

Use exact assertions:

```csharp
Assert.All(
    GrimoireSchemaCatalog.CoreObjects,
    static item => Assert.Equal(GrimoireSchemaTransactionTier.Core, item.TransactionTier));

Assert.All(
    GrimoireSchemaCatalog.CovenantCanonicalObjects,
    static item => Assert.Equal(
        GrimoireSchemaTransactionTier.CovenantCanonical,
        item.TransactionTier));

Assert.DoesNotContain(
    GrimoireSchemaCatalog.CoreObjects,
    static item => item.Family == GrimoireSchemaFamily.Covenant);
```

- [ ] **Step 2: Run catalog tests and witness missing tiers**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaCatalogTests"
```

Expected: FAIL to compile because the new dimensions and catalogs do not exist.

- [ ] **Step 3: Define the closed catalog types**

Use these exact declarations:

```csharp
internal enum GrimoireSchemaFamily
{

    Core = 0,

    Covenant = 1,

}

internal enum GrimoireSchemaTransactionTier
{

    Core = 0,

    CovenantCanonical = 1,

    CovenantAccelerator = 2,

}

internal sealed record GrimoireSchemaObject(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    GrimoireSchemaCategory Category,
    string Name,
    string ResourcePath,
    string Sql);
```

Remove `Accelerators` from `GrimoireSchemaCategory`; its values remain `Tables`, `FullTextSearch`, `Triggers`, and `Views` in installation order.

- [ ] **Step 4: Parse both established and capability resource paths**

Map existing `Data.Schema.<Category>.<Name>.sql` resources to `Core/Core`. Map only these nested shapes:

```text
Data.Schema.Capabilities.Covenant.Canonical.<Category>.<Name>.sql
Data.Schema.Capabilities.Covenant.Accelerator.<Category>.<Name>.sql
```

Reject any other family, tier, missing category, extra segment, duplicate `(tier, category, name)`, or name whose SQL declaration differs from the resource filename. Sort by transaction tier, category, and ordinal name.

- [ ] **Step 5: Compute two source fingerprints**

Frame each source hash with family, tier, category, resource path, name, and exact unresolved SQL. `CanonicalSchemaFingerprint` covers all resources for fixture invalidation. `CovenantCanonicalSchemaFingerprint` covers only Covenant canonical resources for capability identity. Both are uppercase 64-character SHA-256 values to preserve the current fixture format.

- [ ] **Step 6: Run and refactor catalog tests**

Run the Task 8 filter. Expected: PASS. Then extract one `ParseResourcePath` method returning a closed result and one `ComputeSourceFingerprint` method accepting an ordered sequence. Re-run and expect PASS.

### Task 9: Add core feature metadata, binding, registry, authority, and path schema

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/grimoire_feature_schemas.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_campaign_bindings.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_campaign_binding_resolution_receipts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_registry_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/covenant_authority_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_identities.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_marker_intents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/campaign_path_operation_receipts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_campaign_bindings_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_campaign_bindings_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_campaign_bindings_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_registry_state_campaign_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_registry_state_campaign_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_campaign_binding_resolution_receipts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_campaign_binding_resolution_receipts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_marker_intents_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_marker_intents_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_marker_intents_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_operation_receipts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/campaign_path_operation_receipts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/IGrimoireSchemaDataInitializer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInitializationContext.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CoreGrimoireSchemaDataInitializer.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Security/CovenantAuthoritySnapshot.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Security/ICovenantAuthoritySnapshotProvider.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthorityBootstrapper.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthorityBootstrapPreparation.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Security/CovenantAuthoritySnapshotProvider.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/ArcanumMaintenanceLock.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCoreIdentitySchemaTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Security/CovenantAuthorityBootstrapperTests.cs`

**Interfaces:**

- Consumes: Core catalog parsing from Task 8, connection authorization from Task 6, `ISecretStore`, and one caller-held `ArcanumMaintenanceLock` lease for the installation.
- Produces: always-present identity tables, the one-time startup master-material lease source, `ICovenantAuthoritySnapshotProvider`, `CovenantAuthorityBootstrapper.PrepareUnderInstallationLockAsync(...)`, and `CoreGrimoireSchemaDataInitializer.InitializeAsync(...)` for Task 15 and Plan 03.

- [ ] **Step 1: Write failing core identity tests**

Add `Core_schema_creates_feature_binding_registry_authority_and_path_tables`, `Session_binding_checks_enforce_all_three_closed_shapes`, `Session_binding_mutation_requires_exact_authorization`, `Campaign_insert_and_delete_advance_registry_epoch`, `Registry_epoch_overflow_fails_the_Campaign_transaction`, `Core_initializer_backfills_legacy_sessions_without_laundering_deleted_Campaigns`, `Path_marker_intent_kind_maps_to_exact_optional_exclusive_owner_and_effect_digest`, `Path_marker_intent_phases_have_exact_codes_and_kind_specific_graphs`, `Campaign_delete_orphan_pending_requires_commit_reopen_and_composite_finalization`, `No_other_marker_kind_can_enter_an_orphan_phase`, `Path_marker_intent_insert_update_and_delete_require_exact_authorization`, `Path_marker_intent_owner_effect_scope_and_evidence_are_immutable`, `Path_marker_temporary_identity_is_null_at_Prepared_and_filled_once_at_TempCreated`, `Path_marker_reopened_target_records_exact_opened_identity_or_proven_absence_once`, `Path_marker_identity_evidence_cannot_be_rewritten_skipped_or_fabricated`, `Path_marker_intent_update_requires_exact_prior_revision_and_rejects_phase_skips`, `Path_marker_intent_nonterminal_delete_is_rejected`, `One_restore_or_full_reset_owner_can_journal_distinct_intents_for_multiple_Campaigns`, `Duplicate_owner_Campaign_and_kind_replays_one_intent`, `Full_reset_marker_intent_has_no_in_process_gate_owner_and_blocks_ordinary_recovery`, `Authority_bootstrap_requires_the_caller_held_installation_lock`, `Authority_bootstrap_never_reacquires_or_disposes_the_caller_lock`, `Authority_bootstrap_rejects_a_disposed_or_wrong_installation_lock`, `Authority_bootstrap_seeds_version_one_and_advances_on_key_change`, `Authority_bootstrap_rotate_back_never_reuses_version_or_epoch`, `Authority_state_has_closed_clean_pending_and_host_tools_tainted_shapes`, `Pending_host_tools_taint_requires_random_transition_identity_and_taint_time_master_version`, `Later_key_rotation_preserves_taint_time_master_version`, `Startup_master_material_lease_is_single_take_and_zeroized`, and `Raw_Campaign_identifiers_use_uppercase_D_text`.

The backfill test seeds one legacy Session with a live Campaign and one Session with null `CampaignId`. After two initializer calls it requires `Campaign` for the first and `LegacyUnresolved` for the second. A null legacy value never becomes `GlobalOnly` without a durable creation fact.

- [ ] **Step 2: Run the red test**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCoreIdentitySchemaTests|FullyQualifiedName~CovenantAuthorityBootstrapperTests"
```

Expected: FAIL because the new core tables are absent.

- [ ] **Step 3: Add the metadata and identity DDL**

Use this exact metadata shape:

```sql
CREATE TABLE IF NOT EXISTS grimoire_feature_schemas (
    FamilyCode INTEGER NOT NULL,
    TransactionTierCode INTEGER NOT NULL,
    SchemaVersion INTEGER NOT NULL CHECK (SchemaVersion > 0),
    SourceDefinitionFingerprint TEXT NOT NULL CHECK (length(SourceDefinitionFingerprint) = 64),
    InstalledCatalogFingerprint TEXT NOT NULL CHECK (length(InstalledCatalogFingerprint) = 71),
    InstalledAtUtc TEXT NOT NULL,
    HealthCode INTEGER NOT NULL,
    HealthDetailCode TEXT NULL,
    PRIMARY KEY (FamilyCode, TransactionTierCode)
);

CREATE INDEX IF NOT EXISTS idx_grimoire_feature_schemas_health
    ON grimoire_feature_schemas (HealthCode, FamilyCode, TransactionTierCode);
```

The other table contracts are:

| Table | Required shape |
|---|---|
| `session_campaign_bindings` | PK and Session cascade on `SessionId`; closed GlobalOnly, Campaign, or LegacyUnresolved kind; nullable Campaign shape check; historical Campaign has no FK |
| `session_campaign_binding_resolution_receipts` | operation PK; unique Session; request/prior digests; positive authority epoch; closed terminal result |
| `campaign_registry_state` | key fixed to 1; positive signed epoch |
| `covenant_authority_state` | singleton installation identity; positive authority and recovery-envelope epochs; unsigned current master version in checked signed storage; 32-byte current and optional taint fingerprints; closed host-tools state `Clean=1`, `PendingHostToolsTaint=2`, or `HostToolsTainted=3`; nullable positive taint-time master version and uppercase-D random transition ID present exactly for Pending or Tainted |
| `campaign_path_identities` | Campaign PK/FK; positive policy and revision; bounded display path/depth; unique 32-byte opaque physical identity |
| `campaign_path_marker_intents` | random intent PK distinct from owner operation; immutable owner operation ID; unique active historical Campaign identity; unique owner-operation, Campaign, and intent-kind tuple; closed marker-intent kind; nullable exclusive-owner operation code with exact kind-dependent shape; immutable 32-byte owner effect digest; encrypted bounded marker payload; marker digest; nullable apply-request digest with exact kind-dependent shape; bounded temporary basename; nullable one-time `TemporaryPhysicalIdentityDigest`; target path; prior revision; nullable one-time `TargetObservationCode` and `ReopenedTargetPhysicalIdentityDigest`; closed phase |
| `campaign_path_operation_receipts` | PathMutation owner-operation PK only; historical Campaign without FK; apply-request/effect digests; closed result; optional resulting revision; per-Campaign replay index |

`campaign_path_marker_intents` uses the immutable kind codes `PathMutation=1`, `CampaignDelete=2`, `RestoreCleanup=3`, and `FullInstallationResetCleanup=4`. Each row has its own random nonempty `IntentId` primary key plus a nonempty immutable `OwnerOperationId`. The unique `(OwnerOperationId, CampaignId, IntentKind)` tuple makes a replay idempotent while allowing one restore or full-reset owner to journal one row for every Campaign. The nullable exclusive-owner operation code is respectively required and equal to `CampaignPathMutation`, `CampaignDelete`, or `BackupRestore` for kinds 1 through 3. It is null only for kind 4, whose authority is the stopped-host installation lock plus the authenticated full-reset journal rather than the in-process Covenant gate. Every kind stores the immutable 32-byte owner effect digest. `ApplyRequestDigest` is required only for `PathMutation`, where it is the exact receipt-first token-independent request digest, and is null for the other three kinds. Cleanup recovery authenticates its owner and per-marker evidence from the deletion, restore, or full-reset journal and never substitutes the effect digest into the request-digest column. Normal pre-readiness Campaign recovery may resume only kinds 1 and 2 through `ResumeCampaignExclusiveAsync`. Kind 3 remains a child of the one global `BackupRestore` owner and is processed only by restore recovery after `ResumeExclusiveAsync`; a Campaign-scoped resume is invalid. Kind 4 blocks ordinary readiness and can be resumed only by the exact full-installation-reset operation and attestation journal. `campaign_path_operation_receipts` is written only by the single-Campaign `PathMutation` arm. Delete, restore, and full-reset cleanup use their owning deletion or lifecycle journal and the per-marker terminal intent; they do not insert a public path-operation receipt.

The immutable marker-intent phase codes are `Prepared=1`, `TempCreated=2`, `TempWritten=3`, `TempFsynced=4`, `RenamedNoReplace=5`, `ParentFsynced=6`, `TargetReopenedOrAbsent=7`, `CodecOrAbsenceVerified=8`, `DatabaseStateCommitted=9`, `SensitiveMaterialDestroyed=10`, `ReopenPending=11`, `Completed=12`, `Compensated=13`, `ManualBlocker=14`, `OrphanReopenPending=15`, and `Orphaned=16`. `PathMutation` follows the complete 1 through 11 sequence before gate disposition. `CampaignDelete` and `RestoreCleanup` follow `Prepared -> TargetReopenedOrAbsent -> CodecOrAbsenceVerified -> DatabaseStateCommitted -> SensitiveMaterialDestroyed -> ReopenPending`; an already-absent target is evidence recorded by the same phase codes. A same-handle and exact-byte verified compensation may advance only PathMutation or RestoreCleanup from a legal nonterminal phase no later than `CodecOrAbsenceVerified` to `ReopenPending(RollbackAndReopen)`. CampaignDelete has no rollback or `Compensated` arm after its core owner deletion commits; exact deletion or already-absent evidence reaches `ReopenPending(CommitAndReopen)`. A successful gate-owned effect records `CommitAndReopen`. Uncertainty remains at its last proven pre-`ReopenPending` phase and calls `KeepClosed`; a genuinely nonfinalizable manual condition may advance to `ManualBlocker`.

The nullable `TemporaryPhysicalIdentityDigest` is null at `Prepared`. Only an authorized `Prepared -> TempCreated` transition may set it, exactly once, to the 32-byte identity observed from the same newly opened temporary-file handle. Every subsequent PathMutation temporary-file phase requires that exact value and cannot rewrite or clear it. It remains null for a PathMutation compensated before temporary creation and for the three cleanup kinds, which never create a temporary marker. The nullable `TargetObservationCode` has the closed values `Opened=1` and `Absent=2`. It and `ReopenedTargetPhysicalIdentityDigest` are both null before `TargetReopenedOrAbsent`. The authorized transition into that phase fills the observation exactly once from the retained root capability: `Opened` requires one exact 32-byte reopened-target identity, while `Absent` requires a null identity. Later phases preserve both values. For a successful PathMutation, the reopened target must be `Opened` and its identity must equal `TemporaryPhysicalIdentityDigest`; a missing or different target cannot advance as a successful effect. These content-free physical-evidence fields remain after sensitive material destruction so recovery can prove the exact same-handle rename, compare-delete, or absence without reconstructing authority from a path.

`CampaignDelete` has one additional orphan arm because core owner deletion cannot remain blocked by an unavailable, mismatched, or no-longer-owned workspace marker. Only `Prepared` or `TargetReopenedOrAbsent` may advance to this arm. One authorized CAS securely clears the encrypted payload and temporary-name capability, preserves the immutable owner and content-free evidence digests, advances to `OrphanReopenPending`, and stores `CommitAndReopen`. `ReopenPending`, `OrphanReopenPending`, every terminal phase, every PathMutation or RestoreCleanup phase, and a CampaignDelete phase after verified database cleanup cannot enter or reclassify into the orphan arm. No other kind may enter either orphan phase. Before either normal `ReopenPending` or `OrphanReopenPending` is returned for disposition, the same coordinator advances the parent `owner_deletion_operation_intents` row from `OwnerDeleted` to `MarkerCleanupTerminal`. Only the composite deletion finalizer, after successful matching disposition, advances the child to `Completed` or `Orphaned` and the parent from `MarkerCleanupTerminal` to `Completed`; failed disposition or finalizer retains both pending child and `MarkerCleanupTerminal` parent. `Orphaned` is a visible content-free terminal orphan record that does not hold Campaign admission closed and remains until Task 7's explicitly confirmed no-follow takeover consumes that exact evidence. It never authorizes deletion of the mismatched file.

Only the one-shot post-disposition finalizer may advance `ReopenPending -> Completed` after successful `CommitAndReopen` or `ReopenPending -> Compensated` after successful `RollbackAndReopen`. Failed disposition, finalizer failure, uncertainty, and `ManualBlocker` retain the row and durable owner. `FullInstallationResetCleanup` has no gate disposition. Under the exact held installation lock, authenticated reset journal, and marker-intent SQL authorization, it follows the cleanup sequence through `SensitiveMaterialDestroyed -> Completed`, or advances to `ManualBlocker` for a typed orphan. `Completed`, `Compensated`, and `Orphaned` are terminal and cannot change except that authenticated takeover may retain away the exact `Orphaned` evidence after committing its replacement intent.

- [ ] **Step 4: Add guarded triggers and the initializer**

Binding insert and one-time LegacyUnresolved resolution require `arcanum_session_binding_write_authorized()`. Binding delete requires Session retention or owner cleanup. Campaign insert and delete increment the registry and abort at `9223372036854775807`. Resolution and path-operation receipts reject update and require owner cleanup or family maintenance for delete.

Every marker-intent insert and phase update requires the connection-local `arcanum_campaign_path_marker_intent_mutation_authorized()` function from Task 6. The Task 7 path lifecycle, Task 15 restore lifecycle, and Task 17 full-reset lifecycle borrow that authorization only on their caller-owned live transaction connection. Intent ID, owner operation ID, Campaign ID, kind, optional exclusive-operation code, owner effect digest, request digest, marker and evidence digests, target display digest, and prior revision are immutable from insert. `TemporaryPhysicalIdentityDigest`, `TargetObservationCode`, and `ReopenedTargetPhysicalIdentityDigest` are nullable at insert and have only the authorized one-time fills and phase shapes specified above; after their first nonnull evidence transition they are immutable. Encrypted marker payload and temporary-name capability are immutable and nonnull before `SensitiveMaterialDestroyed` or the CampaignDelete-only `OrphanReopenPending` transition; either exact transition securely clears both once while retaining their immutable digests and all content-free physical evidence. They remain null afterward. `PendingDispositionCode` is null outside `ReopenPending` and `OrphanReopenPending`. PathMutation and RestoreCleanup may store exact `CommitAndReopen` or `RollbackAndReopen` at `ReopenPending`; CampaignDelete requires exactly `CommitAndReopen` at either pending phase and cannot reach `Compensated`. CampaignDelete also requires exactly `CommitAndReopen` at `OrphanReopenPending`. The code is immutable afterward. Kind 4 always requires null. The update trigger requires an exact prior `PhaseRevision`, increments it once, validates the phase-specific physical-evidence shape, and accepts only the closed phase edges listed above. Delete requires the same marker-intent authorization plus `OwnerCleanup` or `CovenantFamilyMaintenance`, and succeeds only for `Completed` or `Compensated` after the required public receipt or owning deletion, restore, or reset journal is terminal. `Orphaned` cannot use ordinary retention deletion; only a successfully committed authenticated takeover may remove that exact evidence. Direct insert, owner or effect substitution, a premature or repeated physical-evidence fill, an opened/absent shape mismatch, a successful PathMutation whose two recorded identities differ, any other payload or temp mutation, phase skip, stale revision, CampaignDelete rollback or compensation, orphan reclassification from any unlisted source, terminal rewrite, pre-disposition deletion, and `ManualBlocker` deletion abort the transaction.

Use these exact initializer signatures:

```csharp
internal sealed record GrimoireSchemaInitializationContext(
    string InstallationIdentity,
    long AuthorityEpoch,
    uint MasterKeyVersion,
    byte[] MasterKeyFingerprint,
    long RecoveryEnvelopeEpoch,
    DateTimeOffset InstalledAtUtc);

internal interface IGrimoireSchemaDataInitializer
{

    GrimoireSchemaTransactionTier TransactionTier { get; }

    Task InitializeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken);

}
```

`CoreGrimoireSchemaDataInitializer` acquires `SessionBindingWrite`, backfills missing bindings, seeds registry state, and seeds or verifies authority state. A new installation starts `Clean` with no transition ID, taint fingerprint, or taint-time master version. `PendingHostToolsTaint` and `HostToolsTainted` require one nonempty random transition ID, taint fingerprint, and positive taint-time master version that remains immutable across later API-key rotation. No startup initializer, API-key rotation, or ordinary reinitialize may convert either state to `Clean`. Every variable value is a parameter. An existing malformed or mismatched authority row fails closed.

Use this exact lock-borrowing entrypoint:

```csharp
internal sealed class CovenantAuthorityBootstrapper
{
    public Task<CovenantAuthorityBootstrapPreparation> PrepareUnderInstallationLockAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken);
}
```

The hosted-service or CLI bootstrap caller acquires the one `ArcanumMaintenanceLock` for the exact guarded installation before any OS-marker, secret, or database work and passes that same live object through the startup gate, authority bootstrap, and core installation. `ArcanumMaintenanceLock.AssertHeldFor(string guardedDirectory)` validates the live undisposed lock and its canonical lock-file identity without probing or acquiring another lock. `PrepareUnderInstallationLockAsync` calls that validation, borrows the lock for the duration of the preparation, and never calls `TryAcquire`, `IsHeld`, `Dispose`, or another lock-taking helper. `CovenantAuthorityBootstrapPreparation` is nonserializable and async-disposable. It owns the resulting `GrimoireSchemaInitializationContext` plus the bounded single-take startup master-material lease, but never owns the caller's installation lock. Plan 03's pre-service startup gate consumes this same method after its OS-marker precheck, so no startup path can nest lock acquisition.

While the caller continues holding that lock, the bootstrapper reads and validates the existing master API key once and holds it only in a zeroizable bounded startup buffer. It computes `SHA-256(UTF8("Arcanum.Covenant.MasterKeyFingerprint.v1\0") || keyBytes)`. Inside the same core install transaction that owns DDL and data initialization, seed version and epochs at one or compare the encrypted stored fingerprint, then advance the unsigned master version, authority epoch, and recovery-envelope epoch on change. Rotate-back advances again. Publish the immutable non-secret `CovenantAuthoritySnapshot`, including the closed host-tools state and transition identity, only after commit. Expose the raw startup buffer through one internal single-take lease for Plan 03's envelope derivation, then zero it. Failure clears the buffer and publishes no authority. A Pending or Tainted snapshot is published only to the pre-service host-tools startup gate and never to a clean Covenant key, pool, or prompt service.

- [ ] **Step 5: Run the green test**

Run the Task 9 command. Expected: PASS for shapes, authorization, overflow, uppercase GUIDs, idempotency, and conservative backfill.

- [ ] **Step 6: Refactor**

Extract provider-neutral parameter creation through the repository's existing command pattern. Keep DDL and trigger resources one object per file. Re-run and expect PASS.

### Task 10: Add core sensitivity, finalization, turn-claim, and capacity schema

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/artifact_sensitivity.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_sensitivity_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_summary_artifacts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_summary_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_title_artifacts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_title_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/assistant_entry_finalizations.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/assistant_entry_erasure_receipts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_turn_claims.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/assistant_finalization_capacity_reservations.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_turn_quota_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/installation_turn_quota_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/session_turn_maintenance_steps.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/artifact_sensitivity_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/artifact_sensitivity_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_summary_artifacts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_summary_artifacts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_title_artifacts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_title_artifacts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_entry_finalizations_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_entry_finalizations_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_entry_finalizations_validate_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_entry_erasure_receipts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_entry_erasure_receipts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_claims_validate_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_claims_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_claims_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_finalization_capacity_reservations_validate_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_finalization_capacity_reservations_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/assistant_finalization_capacity_reservations_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/Sessions_turn_quota_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_quota_state_validate_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_quota_state_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_quota_state_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/installation_turn_quota_state_validate_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/installation_turn_quota_state_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_maintenance_steps_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/session_turn_maintenance_steps_guard_delete.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CoreGrimoireSchemaDataInitializer.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCoreArtifactSchemaTests.cs`

**Interfaces:**

- Consumes: Task 6 authorization functions and Task 9 Session ownership.
- Produces: Content-free sensitivity, one-shot publication, durable future-guard reservation, and exact claim/finalization capacity primitives for Plans 02 through 04.

- [ ] **Step 1: Write failing artifact and capacity tests**

Add `Artifact_sensitivity_enforces_exact_or_BloomOverflow_generation_shapes`, `Artifact_sensitivity_requires_purge_authorization`, `Summary_and_title_state_reference_matching_immutable_artifacts`, `Core_initializer_backfills_legacy_summary_title_and_sensitivity_state_idempotently`, `Assistant_finalization_is_one_shot_and_accepts_empty_committed_content`, `Erasure_receipt_and_live_artifact_are_mutually_exclusive`, `Turn_claim_allows_one_active_claim_per_Session`, `Turn_claim_separates_immutable_input_and_mutable_expected_projection_revisions`, `New_Session_receives_zeroed_turn_quota_state_in_its_creation_transaction`, `Only_exact_missing_zero_quota_owner_insertion_is_structurally_unauthorized`, `Pending_claim_reserves_one_future_guard_slot_atomically`, `Assistant_begin_consumes_the_exact_reservation_without_changing_total_guard_capacity`, `Never_begun_terminal_claim_releases_the_exact_reservation`, `Reservation_replay_cannot_double_reserve_consume_or_release`, `Direct_internal_import_and_fork_guards_use_unreserved_capacity`, `Claim_and_guard_limits_are_independent_per_Session_and_installation`, `Turn_capacity_rows_reject_every_mutation_without_exact_authorization`, `Turn_capacity_authorization_starts_false_and_is_transaction_scoped`, `Quota_guard_multi_statement_transitions_hold_one_turn_capacity_authorization`, `Session_retention_decrements_installation_turn_counters_exactly`, and `Maintenance_steps_allow_only_four_closed_step_codes`.

- [ ] **Step 2: Run the red test**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCoreArtifactSchemaTests"
```

Expected: FAIL because the sensitivity, claim, reservation, quota-state, and maintenance tables are absent.

- [ ] **Step 3: Add sensitivity and current-state DDL**

`artifact_sensitivity` has unique `(ArtifactKind, ArtifactId)`, a closed CovenantDerived code, exact mode with 1 to 8 sorted generation IDs or BloomOverflow with exactly 32 bytes, 32-byte content/sensitivity/label digests, and Campaign/Session/turn owner indexes. Summary and title artifact rows are immutable. Their current-state rows use matching composite FKs and monotonic revisions. `session_sensitivity_state` is one conservative aggregate row per Session.

Inside the same core install transaction, `CoreGrimoireSchemaDataInitializer` inserts a sensitivity-None state row for every existing Session. It snapshots each nonnull legacy `Sessions.Summary` and `Sessions.Title` into one deterministic imported artifact plus current-state revision without changing the legacy bytes or watermark. Repeat installation returns the same IDs and rows. Any conflicting partial backfill rolls back the whole core tier.

- [ ] **Step 4: Add finalization and claim DDL**

`assistant_entry_finalizations` uses historical assistant Entry ID as PK without an Entry FK, Session retention cascade, closed Committed/Discarded/CommittedImported/CommittedForked outcome, and checked sensitivity/request/receipt digests. `assistant_entry_erasure_receipts` uses the assistant ID as PK and binds the guard digest, reason, operation, and timestamp.

`session_turn_claims` stores origin installation/client ID uniqueness, Session and immutable binding, request/dependency digests, closed state, bounded terminal payload, lease/checkpoint fields, immutable pre-request history and input-sensitivity revisions, a separately mutable expected-current sensitivity revision, the exact future-finalization reservation identity, and a partial unique index permitting at most one PendingMaintenance or Begun claim per Session. Only the guarded maintenance transaction may advance expected-current sensitivity revision by compare-and-swap while preserving the original input evidence. `session_turn_maintenance_steps` has PK `(ClaimId, StepCode)`, four closed step codes, monotonic checkpoint transitions, and immutable committed output identity/digests.

`assistant_finalization_capacity_reservations` is the durable capacity ledger keyed by one random reservation identity. It binds one Session, one future assistant Entry identity, closed origin `PublicClaim=1`, `Internal=2`, `Imported=3`, or `Forked=4`, optional unique claim identity present exactly for `PublicClaim`, and closed state `Reserved=1`, `Consumed=2`, or `Released=3`. A public claim inserts one `Reserved` row in the same immediate transaction that consumes claim capacity and writes `PendingMaintenance`. Assistant begin may transition only that exact row from `Reserved` to `Consumed` in the same transaction that inserts the placeholder. A never-begun terminal claim may transition only `Reserved` to `Released`. Internal begin and imported or forked guard creation insert an already `Consumed` row in the same transaction as their guarded placeholder or finalization rows. `Consumed` and `Released` are terminal. The reservation identity, Session, assistant identity, origin, and optional claim never change. A matching consumed row is required before an `assistant_entry_finalizations` insert, so a retry cannot create an uncounted guard.

`session_turn_quota_state` has exactly one row per Session and stores checked nonnegative `ClaimCount`, `ReservedFinalizationCount`, and `ConsumedFinalizationCount`. `installation_turn_quota_state` is a singleton with the same three checked counters. The published claim limit applies independently to `ClaimCount`; the published guard limit applies to the checked sum of reserved and consumed finalization counts. The exact maxima are 16,384 claims and 16,384 guard slots per Session, and 1,048,576 claims and 1,048,576 guard slots installation-wide. Reserving a public claim atomically increments claim and reserved counts. Consuming its reservation moves one count from reserved to consumed without changing total guard capacity. Releasing a never-begun reservation decrements only reserved capacity. Direct internal, imported, and forked guards increment only consumed capacity. No ordinary terminal claim or guard releases lifetime capacity. Whole-Session retention removes its claims, reservations, and guards and decrements the installation counters by the exact locked per-Session values in the same authorized transaction. Counter overflow, underflow, wrong-session identity, duplicate reservation, wrong prior state, or either published limit aborts before a claim, placeholder, finalization, disclosure subject, or side effect can commit.

- [ ] **Step 5: Add guarded mutation triggers**

Immutable artifact, sensitivity, finalization, and erasure rows reject updates. Deletes require the exact ArtifactReplacement, SensitivityRetentionPurge, SessionRetention, OwnerCleanup, or CovenantFamilyMaintenance authorization appropriate to the row. Checks prevent a terminal claim from retaining executor authority. Every reservation insert, reservation state update, nonzero quota insert, quota counter update, or ordinary delete of `assistant_finalization_capacity_reservations`, `session_turn_quota_state`, or `installation_turn_quota_state` requires `arcanum_turn_capacity_mutation_authorized() = 1` in addition to its exact local shape and prior-state checks. The only unauthenticated structural exception is insertion of an exactly zeroed missing quota-owner row by `CoreGrimoireSchemaDataInitializer` or the `Sessions_turn_quota_state` parent-creation trigger. That exception cannot reserve, consume, release, increment, decrement, replace, or delete capacity, and a duplicate or nonzero insert still aborts. Plan 02 `CovenantQuotaGuard` borrows one `TurnCapacityMutation` authorization scope from the exact caller-owned transaction connection before its first reservation or counter statement, holds it across the complete multi-table reserve, consume, release, direct-allocation, or compare-and-swap sequence, and disposes it before returning without committing or retrying the transaction. This lets the triggers distinguish the narrow guard from direct SQL while the transaction passes through otherwise unavoidable intermediate counter states. The scope begins false on every connection and authorizes no artifact, claim, placeholder, finalization, retention, or family-maintenance write.

Session-retention cleanup remains separate. `ReleaseSessionCapacityAsync` first uses `TurnCapacityMutation` to compare and decrement the installation counters from the locked Session row. The parent delete and cascade then require the existing `SessionRetention` or `OwnerCleanup` authorization; they do not leave `TurnCapacityMutation` enabled. Reservation and quota triggers reject direct counter edits, skipped reservation states, a mismatched authorization phase, and any transition outside these two closed paths. The initializer seeds the installation singleton and one zeroed Session quota row for every existing Session in the same idempotent core transaction. `Sessions_turn_quota_state` creates the matching zeroed row for each later Session inside its parent creation transaction, so no Session creator can omit the counter owner.

- [ ] **Step 6: Run the green test**

Run the Task 10 command. Expected: PASS for malformed aggregate rejection, one-shot guards, active-claim uniqueness, exact reservation transitions, independent claim and guard ceilings, retention decrements, step bounds, and guarded deletion.

- [ ] **Step 7: Refactor**

Extract test helpers for unauthorized and authorized mutation attempts. Keep production triggers separate. Re-run and expect PASS.

### Task 11: Add core deletion journal, disclosure, and managed-work schema

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/owner_deletion_events.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/owner_deletion_operation_intents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/capability_cleanup_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/external_disclosure_receipts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/disclosure_subject_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/disclosure_subject_aggregates.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/external_disclosure_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/local_erasure_work_items.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/managed_file_write_intents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/restored_managed_file_authority_tombstones.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/protected_session_transfer_intents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/protected_session_transfer_blobs.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/covenant_schema_repair_intents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Tables/long_running_operation_request_identities.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/Campaigns_owner_deletion_event.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/Sessions_owner_deletion_event.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/owner_deletion_operation_intents_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/owner_deletion_operation_intents_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/owner_deletion_operation_intents_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/owner_deletion_events_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/owner_deletion_events_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/external_disclosure_receipts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/external_disclosure_receipts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/disclosure_subject_state_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/disclosure_subject_aggregates_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/external_disclosure_state_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/local_erasure_work_items_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/local_erasure_work_items_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/local_erasure_work_items_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/managed_file_write_intents_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/managed_file_write_intents_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/managed_file_write_intents_guard_update.sql`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/artifact_sensitivity_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/restored_managed_file_authority_tombstones_guard_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/restored_managed_file_authority_tombstones_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/restored_managed_file_authority_tombstones_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/protected_session_transfer_intents_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/protected_session_transfer_intents_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/protected_session_transfer_blobs_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/protected_session_transfer_blobs_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/covenant_schema_repair_intents_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/covenant_schema_repair_intents_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Triggers/long_running_operation_request_identities_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileRecoveryStateContracts.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CoreGrimoireSchemaDataInitializer.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCoreCleanupSchemaTests.cs`

**Interfaces:**

- Consumes: Core initializer and authorizations from Tasks 6 and 9.
- Produces: Monotonic core owner events and cleanup cursors, without an optional-tier dependency in Campaign or Session deletion.

- [ ] **Step 1: Write failing cleanup tests**

Add `Campaign_and_Session_deletion_append_monotonic_owner_events`, `Campaign_deletion_intent_persists_and_copies_the_exact_exclusive_owner_into_the_event`, `Owner_deletion_intent_insert_update_and_delete_require_owner_cleanup_authorization`, `Unauthorized_owner_deletion_intent_CAS_cannot_forge_marker_cleanup_terminal_or_completed`, `Direct_unmanaged_Session_deletion_keeps_the_owner_event_without_fabricating_an_exclusive_owner`, `Core_owner_deletion_succeeds_when_all_Covenant_objects_are_absent`, `Cleanup_state_tracks_owner_sequences_independently`, `Disclosure_receipts_bind_origin_subject_effect_and_allocated_ordinals`, `Disclosure_effect_identity_is_unique_within_origin_subject`, `Disclosure_subject_state_binds_lifecycle_boot_heartbeat_close_counts_ordinals_fold_and_chain`, `Disclosure_aggregates_enforce_Exact_or_LowerBound_and_256_bit_Bloom`, `Local_erasure_item_binds_durable_location_ownership_hash_label_operation_state_and_revision`, `Local_erasure_state_codes_evidence_shapes_and_CAS_graph_are_exact`, `Local_erasure_completion_removes_label_and_terminalizes_source_ownership_atomically`, `Managed_write_intent_binds_artifact_pending_label_root_revision_parent_target_temp_hash_and_sensitivity`, `Managed_write_pending_label_blob_phase_shape_and_secure_clear_edges_are_exact`, `Managed_write_created_child_identity_is_null_at_Prepared_filled_once_at_TempCreated_and_immutable`, `Managed_write_adoption_guard_requires_final_and_created_identity_equality`, `Managed_write_final_ownership_is_null_before_adoption_set_once_and_terminally_required`, `Managed_write_phase_codes_shapes_and_CAS_graph_are_exact`, `Managed_write_adopted_ownership_cannot_be_retained_away_before_erasure`, `Managed_write_erasure_edge_rejects_managed_mutation_and_requires_matching_local_erasure_completion`, `Restore_staging_tombstone_codes_and_content_free_shape_are_exact`, `Restore_staging_trigger_graph_has_one_tombstone_backed_nonterminal_delete_branch`, `Restore_staging_trigger_graph_requires_dedicated_predicate_and_exact_label_disposition`, `Restore_staging_trigger_graph_requires_source_tombstone_before_linked_local_tombstone`, `Restore_staging_trigger_graph_pins_source_local_work_label_source_order`, `Restore_staging_trigger_graph_rejects_other_authorization_combinations`, `Protected_session_transfer_intent_binds_operation_effect_source_destination_scope_session_blob_manifest_and_phase`, `Protected_session_transfer_parent_updates_require_exact_authorization_and_revision`, `Protected_session_transfer_blob_rows_are_complete_bounded_and_recoverable_without_source_lease`, `Protected_session_transfer_blob_phase_graph_is_monotonic_and_restartable_at_every_boundary`, `Schema_repair_intent_persists_the_exact_exclusive_owner_before_catalog_mutation`, `Schema_repair_intent_phase_graph_is_monotonic_and_guarded`, `Schema_repair_reopen_pending_requires_successful_disposition_and_post_disposition_finalizer`, `Requested_operation_identity_is_one_to_one_immutable_and_cascades_with_operation`, and `Core_initializer_seeds_cleanup_and_disclosure_state_once`.

- [ ] **Step 2: Run the red test**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCoreCleanupSchemaTests"
```

Expected: FAIL because owner-journal and disclosure objects are absent.

- [ ] **Step 3: Add owner journal and cleanup DDL**

`owner_deletion_events` has a positive monotonic sequence, closed Campaign or Session kind, historical uppercase owner ID, nullable operation ID plus nullable 32-byte exclusive effect digest present or absent together, and timestamp, indexed by `(OwnerKind, Sequence)` and `(OwnerKind, OwnerId, Sequence)`. `owner_deletion_operation_intents` is an always-present core table keyed by nonempty operation ID with one unique active historical owner kind and ID, exact `CampaignDelete` operation code, immutable 32-byte effect digest, phase `Prepared=1`, `OwnerDeleted=2`, `MarkerCleanupTerminal=3`, or `Completed=4`, CAS revision, and timestamps. Managed Campaign deletion inserts `Prepared` before deleting the Campaign in the same immediate transaction. The core Campaign delete trigger finds the exact unique intent, copies its operation ID and effect digest into the monotonic event, and advances the intent to `OwnerDeleted`. A Campaign deletion with no exact prepared intent is rejected once the managed deletion contract is installed. Session deletion remains failure-isolated and appends an event with both optional owner fields null unless a later closed Session-deletion protocol explicitly prepares an intent. No trigger invents an effect digest. Insert, update, and delete of `owner_deletion_operation_intents`, including trigger-driven `Prepared -> OwnerDeleted`, require the existing false-by-default `arcanum_owner_cleanup_authorized()` connection scope on the caller's live transaction connection. Updates also require exact prior revision and only the monotonic graph; deletion requires `Completed` plus bounded owner-journal retention. Task 7 deletion, startup reconciliation, and the composite post-disposition finalizer each borrow that same authorization only around their own immediate transaction and never carry it across transactions. An operation ID, effect digest, or revision alone grants no mutation authority. Core Campaign and Session delete triggers otherwise append only to the core event table. `capability_cleanup_state` stores independently applied Campaign and Session sequences per capability family.

- [ ] **Step 4: Add disclosure and managed-work DDL**

Disclosure receipts bind origin installation identity, closed `Turn` or `Operation` subject kind, subject ID, closed effect-category code, stable frozen effect-identity digest, checked subject ordinal, checked category physical-attempt ordinal, destination and revocability codes, opaque destination digest, sensitivity and bounded generation aggregate, optional Ward/admission/backup evidence digests, and timestamp. The primary identity is `(OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal)`. Unique indexes enforce `(OriginInstallationId, SubjectKind, SubjectId, EffectIdentityDigest)` and `(OriginInstallationId, SubjectKind, SubjectId, EffectCategoryCode, CategoryPhysicalAttemptOrdinal)`, so an exact effect is idempotent while distinct physical attempts cannot reuse an ordinal. No raw content, key, path, or search text is stored. `disclosure_subject_state` stores the same origin and subject identity, closed `Open=1`, `Orphaned=2`, `Completed=3`, or `Abandoned=4` lifecycle, creator boot identity, last heartbeat, nullable close timestamp, checked provider-attempt and external-effect counts, last allocated subject ordinal, last folded ordinal, and the 32-byte rolling disclosure-chain digest. Open or Orphaned rows require no close timestamp; terminal rows require one. Last-folded cannot exceed last-allocated. Subject state owns allocation and the overall count and chain exactly once. Subject and global aggregate tables enforce positive counts, Exact or LowerBound state, per-destination and revocability counts, 32-byte evidence Bloom, chain digest, and maximum timestamp.

`managed_file_write_intents` is both the crash journal and the durable ownership catalog for a managed workspace file. It is keyed by the random write operation ID and stores immutable stable effect identity, nonempty artifact ID, nonempty sensitivity-label ID, exact sensitivity-label digest, nullable encrypted `PendingArtifactSensitivityLabel`, encrypted `ManagedFileWriteDurableLocationEvidence`, expected full content hash and checked length, nullable one-time 32-byte `CreatedChildPhysicalIdentityDigest`, nullable encrypted `FinalOwnershipEvidence`, phase, CAS revision, retry count, and timestamps. The write evidence embeds the canonical Campaign root identity digest, positive path revision, bounded normalized relative parent segments, same-handle parent physical-identity digest, bounded target leaf, and one distinct bounded random temporary leaf under that exact parent. The pending label is a complete immutable Plan 02 `ArtifactSensitivityLabel` projection containing every field required to insert the exact `artifact_sensitivity` row after restart, including kind, artifact and owner identities, Campaign, Session and turn ownership, provenance, revision, content, sensitivity, producing-evidence and label digests, and timestamp. Its artifact ID, label ID and label digest equal the indexed columns, and its content digest and revision equal the write request. It is required and byte-for-byte immutable from `Prepared` through `ParentFsynced`. The exact transition to `AdoptedAndLabeled` inserts the label from this projection and securely clears the encrypted projection in the same transaction. A transition to `Cleaned` or `ManualNonrevocable` also securely clears it in that terminalizing transaction. `AdoptedAndLabeled`, `Cleaned`, `ManualNonrevocable`, and `Erased` require it null and retain only the content-free label ID and digest. `FinalOwnershipEvidence` is the exact Plan 03 `ManagedFileOwnershipEvidence` containing the reopened final-file physical identity, full content hash, and checked length. No persisted evidence stores a live handle or a capability for a nonexistent target.

The immutable write-intent phase codes are `Prepared=1`, `TempCreated=2`, `TempWritten=3`, `TempFsynced=4`, `RenamedNoReplace=5`, `ParentFsynced=6`, `AdoptedAndLabeled=7`, `Cleaned=8`, `ManualNonrevocable=9`, and `Erased=10`. Normal creation advances one code at a time through `ParentFsynced`. `CreatedChildPhysicalIdentityDigest` is null at `Prepared`. The authorized `Prepared -> TempCreated` transition alone fills it exactly once from the same newly created and still-open temporary-file handle. Every phase originating at `TempCreated` or later requires and preserves that exact digest, including `Cleaned` or `ManualNonrevocable`. A `Prepared` row for which recovery proves both temporary and target children absent may advance directly to `Cleaned` with the digest null. A `Prepared` row with either child present cannot prove that the child was created by this operation because the create-to-CAS boundary was not journaled; it advances to `ManualNonrevocable` with the digest null and performs no filesystem effect. These are the only terminal null shapes.

`FinalOwnershipEvidence` is null in phases 1 through 6. Only the same transaction that inserts the exact `artifact_sensitivity` row from `PendingArtifactSensitivityLabel`, securely clears that projection, and rechecks its indexed identities may advance `ParentFsynced -> AdoptedAndLabeled` and fill `FinalOwnershipEvidence` once from Plan 03's same-handle `VerifyAndAdoptAsync` result. The verifier receives the persisted `CreatedChildPhysicalIdentityDigest` as an independent required expectation. It succeeds only when the reopened target's same-handle physical identity equals that digest and its full hash and length equal the immutable expected content. The returned `FinalOwnershipEvidence.PhysicalIdentityDigest` must equal `CreatedChildPhysicalIdentityDigest`; the update trigger enforces the same equality before adoption and preserves it through `Erased`. A same-content replacement under a new physical identity cannot be adopted by the live writer or restart recovery. Final ownership is required and immutable in `AdoptedAndLabeled` and `Erased`. Recovery from `TempCreated` or `TempWritten` may compare-delete only the same opened temporary child whose physical identity equals `CreatedChildPhysicalIdentityDigest`, using Plan 03's recovery-only primitive and its parent fsync. At `TempFsynced`, a present temporary and absent target may resume rename only after the temporary's same-handle created identity, exact expected full content hash, and length all match. A missing temporary with a present target may recognize a rename-ahead crash and advance to `RenamedNoReplace` only when that target, opened from the same retained parent, has the same exact created identity, hash, and length. A target mismatch, two present children, unknown absence, or any other phase/leaf combination advances to `ManualNonrevocable` without deletion. A proven operation-owned target at `RenamedNoReplace` or `ParentFsynced` is compare-deleted only after the same-handle created identity and exact expected full hash and length all match. Only completion of the matching `local_erasure_work_items` row may advance `AdoptedAndLabeled -> Erased`, in the same transaction that removes the exact label and advances that work item from `DeletionVerified` to `Completed`. `AdoptedAndLabeled` cannot be guard-deleted or retained away while the artifact or label exists. Only `Cleaned`, `ManualNonrevocable`, or `Erased` is retention-terminal.

Insert and every writer or write-recovery phase edge through `AdoptedAndLabeled` require `ManagedFileIntentMutation`, exact prior revision, immutable artifact, effect, location, expected hash and length, and the phase-specific label, created-identity, and final-ownership shape. `ManagedFileIntentMutation` alone cannot authorize `AdoptedAndLabeled -> Erased`. That sole source-ownership exception requires `ManagedFileIntentMutation` false and exactly one of `SensitivityRetentionPurge` or `CovenantFamilyMaintenance` true on the caller-owned transaction, plus the exact matching `DeletionVerified` `local_erasure_work_items` row, exact label removal, and that work item's atomic transition to `Completed`. A skip, regression, repeated evidence fill, evidence clear on an unlisted edge, identity or hash mismatch, label mismatch, wrong or simultaneously enabled authorization combination, unmatched work item, or terminal rewrite aborts.

`local_erasure_work_items` is keyed by random work-item ID and stores the erasure operation ID, source managed-write operation ID and expected source revision, artifact ID, source sensitivity-label ID, encrypted `ManagedFileDurableLocationEvidence` with canonical Campaign root identity, positive path revision, bounded normalized relative parent segments, same-handle parent physical identity, and bounded target leaf, copied expected `ManagedFileOwnershipEvidence`, closed state, nullable deletion-evidence code, checkpoint revision, retry count, and timestamps. Its insert transaction rereads the source write intent and exact label, requires `AdoptedAndLabeled`, and copies the complete location and ownership from that durable producer row. A caller-supplied root, revision, segment, parent identity, leaf, or ownership value is never authoritative.

The immutable local-erasure states are `Prepared=1`, `DeletionVerified=2`, `Completed=3`, and `ManualBlocker=4`. `DeletionEvidenceCode` is null at `Prepared` and `ManualBlocker`; `DeletionVerified` and `Completed` require exactly `AlreadyAbsent=1` or `SameHandleDeletedAndParentFsynced=2`. `Prepared` may advance to `DeletionVerified` only after Plan 03's opener proves absence or its verifier compare-deletes the exact same opened handle and fsyncs the retained parent. It may advance to `ManualBlocker` on an unavailable parent or any ownership, identity, or hash mismatch, leaving the file, source write intent, and label untouched. `DeletionVerified -> Completed` removes the exact label and advances the source write intent `AdoptedAndLabeled -> Erased` in one transaction before marking the work item terminal. Recovery from a crash after unlink but before the evidence CAS observes absence and follows the `AlreadyAbsent` arm. Location, copied ownership, producer identity, artifact, label, and operation are immutable in every state. Insert and each exact-prior-revision transition require the retention-purge or matching family-maintenance authorization on that live transaction. Only `Completed` and `ManualBlocker` are retention-terminal. Neither table stores an unbounded exception. Both evidence values and every leaf are size-capped encrypted bytes.

`restored_managed_file_authority_tombstones` is the only staging sanitation projection for these two tables. Its primary key is `(RestoreOperationId, SourceKind, SourceRowId)`. Every immutable row stores the exact authenticated `BackupRestore` operation ID and effect digest, staged dataset generation, source kind, source row ID, source managed-write operation ID, artifact and sensitivity-label IDs, original phase or state code, closed owner scope with nullable Campaign ID, closed label disposition, one domain-separated 32-byte stripped-authority digest, and timestamp. `ManagedWriteIntent=1` and `LocalErasureWorkItem=2` are exhaustive source kinds. `NoLiveLabel=1` and `ExactLabelRemoved=2` are exhaustive label dispositions. The stripped digest commits to the complete removed authority projection for audit correlation, but a tombstone stores no canonical root identity, path revision, parent segments, parent or child leaf, parent or child physical identity, created-child identity, final ownership, expected hash or length, pending label projection, deletion evidence, serialized durable location, or opener input.

Task 15 inserts every required tombstone and sanitizes the associated label, work item, and write row in one immediate transaction on the unpublished staged connection with `secure_delete=ON`. The dedicated `arcanum_restore_staging_managed_authority_sanitization_authorized()` predicate is true only while the exact sealed restore-staging connection capability is borrowed. A general authorization request, live connection, published candidate, wrong restore owner or effect digest, different staged dataset generation, nested ordinary authorization, or reused capability keeps it false. The transaction has one exact order. It inventories every restored `managed_file_write_intents` row and inserts and validates every `ManagedWriteIntent=1` source tombstone first. It then inventories every `local_erasure_work_items` row and inserts and validates every linked `LocalErasureWorkItem=2` tombstone, guard-deletes the local rows, guard-deletes the exact adopted labels, guard-deletes the managed source rows, and verifies the final count and canonical vector. An `AdoptedAndLabeled` source requires its exact matching `artifact_sensitivity` row and records `ExactLabelRemoved` only when that row is guard-deleted later in the same transaction. Every other managed source phase requires that label absent and records `NoLiveLabel`; an unexpected or mismatched label aborts sanitation and staged validation. A local-erasure tombstone copies its label disposition only from the already-inserted exact linked immutable source tombstone. Secure delete clears every encrypted durable location, pending-label, created-identity, and final-ownership cell with the removed rows. Rollback exposes neither tombstones nor partial clears.

The ordinary writer, recovery, and erasure state graphs remain exactly the codes and edges above. Their insert and update triggers never accept restore-staging authorization. Their delete triggers have one disjoint staging branch requiring that exact immutable tombstone and authorization. The local-tombstone insert trigger additionally requires the already-present source tombstone to match restore operation, effect digest, staged generation, source write operation, artifact, label, owner scope, and label disposition. A local-row delete requires its exact local tombstone. The `artifact_sensitivity` delete trigger requires its exact managed-source tombstone, `ExactLabelRemoved`, and no remaining linked local row. The managed-source delete branch requires its exact source tombstone, no remaining linked local row, and either the proven absent-label `NoLiveLabel` arm or the already-deleted exact-label `ExactLabelRemoved` arm. All existing live retention branches continue to require their prior terminal state and ordinary authorization. Tombstone update is always forbidden. Bounded deletion of a tombstone requires a later authenticated full-installation-reset or ordinary evidence-retention policy and cannot restore any removed authority. A destination opener cannot be resolved while the staged capability is active, and no field retained by a tombstone can be converted to `ManagedFileDurableLocationEvidence`.

`ManagedFileRecoveryStateContracts.cs` is the sole C# owner of those persisted discriminants:

```csharp
internal enum ManagedFileWriteIntentPhase : byte
{
    Prepared = 1,
    TempCreated = 2,
    TempWritten = 3,
    TempFsynced = 4,
    RenamedNoReplace = 5,
    ParentFsynced = 6,
    AdoptedAndLabeled = 7,
    Cleaned = 8,
    ManualNonrevocable = 9,
    Erased = 10
}

internal enum LocalErasureWorkItemState : byte
{
    Prepared = 1,
    DeletionVerified = 2,
    Completed = 3,
    ManualBlocker = 4
}

internal enum LocalErasureDeletionEvidenceCode : byte
{
    AlreadyAbsent = 1,
    SameHandleDeletedAndParentFsynced = 2
}

internal enum RestoredManagedFileAuthoritySourceKind : byte
{
    ManagedWriteIntent = 1,
    LocalErasureWorkItem = 2
}

internal enum RestoredManagedFileLabelDisposition : byte
{
    NoLiveLabel = 1,
    ExactLabelRemoved = 2
}
```

Plan 03 managed-write recovery and Plan 04 erasure and startup recovery consume these exact internal Infrastructure types. They do not define aliases, duplicate codes, or a second transition table. The schema test asserts every literal and exhaustiveness before either later consumer is implemented.

The managed writer and its restart recovery borrow `ManagedFileIntentMutation` only on each caller-owned live transaction connection that inserts the row or advances a writer-owned phase through `AdoptedAndLabeled`. Local-erasure new work borrows `SensitivityRetentionPurge` or `CovenantFamilyMaintenance`, matching its caller authority, for each work-item insert or update and for the atomic label, source-row, and work-item completion transaction. Pre-readiness local-erasure recovery borrows `SensitivityRetentionPurge` only under its caller-held installation lock after authenticating the exact durable row and producer ownership. The `managed_file_write_intents_guard_update` trigger selects authorization by the exact old/new phase edge. Every ordinary edge except `AdoptedAndLabeled -> Erased` requires `arcanum_managed_file_intent_mutation_authorized() = 1` and both erasure predicates false. The erasure edge requires that predicate false and exactly one of `arcanum_sensitivity_purge_authorized()` or `arcanum_covenant_family_maintenance_authorized()` true. It also requires the exact `DeletionVerified` work item to be present and the exact label already removed; the work-item completion guard then requires that source row to be `Erased` before the same transaction can commit the work item as `Completed`. Restore-staging authorization permits no phase update and only the exact tombstone-backed sanitation deletes above. No authorization scope crosses a commit, filesystem effect, retained handle, or recovery callback. Direct SQL, the wrong or simultaneously enabled authorization kind, stale revision, caller-supplied evidence substitution, and a nonterminal delete without the exact staging tombstone branch abort.

`protected_session_transfer_intents` stores one nonempty operation identity, the exact `Arcanum.Covenant.ProtectedSessionTransfer.v1` effect digest, source-evidence and destination-binding digests, closed destination scope kind `Global=1` or `Campaign=2`, nullable historical destination Campaign ID, destination Session ID, a bounded attachment manifest digest and count, encrypted durable destination-root identity, closed `Prepared=1`, `BlobsStaged=2`, `DatabaseCommitted=3`, `ReopenPending=4`, `Completed=5`, or `Abandoned=6` phase, nullable pending-disposition code, CAS revision, and timestamps. The Global scope requires null Campaign ID; Campaign requires one. These fields are the complete persisted `CovenantExclusiveRecoveryOwner` with `ProtectedSessionTransfer` operation code and `ProtectedTransferScope` needed for recovery to call Plan 02 `ResumeProtectedTransferAsync` before touching a blob or row. The guarded parent update trigger requires `ProtectedSessionTransfer` or family-maintenance authorization, exact prior revision, immutable identity and scope fields, and only `Prepared -> BlobsStaged -> DatabaseCommitted -> ReopenPending -> Completed` for a committed transfer or `Prepared|BlobsStaged -> ReopenPending -> Abandoned` after verified precommit cleanup. `PendingDispositionCode` is null before `ReopenPending`, is respectively `CommitAndReopen` or `RollbackAndReopen` there, and is immutable afterward. Only the one-shot post-disposition finalizer performs the final edge after successful matching gate disposition. Failed disposition or finalizer failure retains `ReopenPending` with the complete recovery owner; uncertainty and `KeepClosed` retain `DatabaseCommitted` or the last earlier proven phase until recovery can select a finalizable disposition. Direct deletion is allowed only at `Completed` or `Abandoned` under the same authorization after successful disposition.

`protected_session_transfer_blobs` is a normalized child keyed by `(OperationId, BlobOrdinal)`. Every row stores encrypted durable parent identity, bounded temporary and immutable final leaf, expected full hash and length, a CAS revision, and the closed phase `Prepared=1`, `TempCreated=2`, `TempWritten=3`, `TempFsynced=4`, `RenamedNoReplace=5`, `ParentFsynced=6`, `ReopenedVerified=7`, `Referenced=8`, or `Cleaned=9`. Normal write progression advances one code at a time through `ReopenedVerified`; the destination database transaction advances every verified child to `Referenced` with the parent `DatabaseCommitted` CAS. Recovery before database commit may advance any proven absent or compare-deleted operation-owned blob to `Cleaned`. `ObservedPhysicalIdentity` is null through `ParentFsynced`, required for `ReopenedVerified` and `Referenced`, and optional for `Cleaned` because a crash may precede file creation. No other transition, skip, regression, identity mutation, or update without exact prior revision and transfer authorization is permitted. The parent manifest count and digest cover the exact ordered child preimages. Persist every child before the first filesystem byte. It stores no attachment bytes, source capability, or live handle. Children cascade only after the guarded parent reaches post-disposition `Completed`, or after `Abandoned` when every child is already `Cleaned`; a referenced child can never cascade under `Abandoned`. Crash tests restart before and after every filesystem syscall and before each corresponding phase CAS. This journal lets protected fork and selective import recovery enumerate, verify, and compare-delete every exact operation-owned blob without the source lease after a process crash.

`covenant_schema_repair_intents` is an always-present core journal keyed by one nonempty operation ID. It stores the immutable 32-byte schema-repair effect digest, inspected whole-catalog digest, closed repair action and target-tier codes, nullable 128-bit captured dataset generation, positive authority epoch, phase `Prepared=1`, `CatalogCommitted=2`, `HealthVerified=3`, `ReopenPending=4`, `Completed=5`, or `Abandoned=6`, exact prior CAS revision, bounded last durable error code, and timestamps. Captured dataset generation is null only for the exact absent-canonical-family action and is required for an existing-family or ordinary-index repair. A newly installed dataset generation is recorded separately in the post-commit health evidence before `HealthVerified`; it never rewrites the immutable captured field. The operation ID plus effect digest is the complete persisted `CovenantExclusiveRecoveryOwner` for `SchemaRepair`. The row is inserted and committed before the first repair DDL. The mutation path is `Prepared -> CatalogCommitted -> HealthVerified -> ReopenPending -> Completed`; the proven no-mutation path is `Prepared -> ReopenPending -> Abandoned`. Both paths persist `ReopenPending` before the exact gate disposition. Only the one-shot post-disposition finalizer may perform `ReopenPending -> Completed` after successful `CommitAndReopen` or `ReopenPending -> Abandoned` after successful proven `RollbackAndReopen`. A failed disposition skips that finalizer and leaves `ReopenPending`; successful `KeepClosed` verifies it remains `ReopenPending`. These transitions require `CovenantFamilyMaintenance` authorization. Identity, digests, action, tier, captured generation, and authority epoch are immutable. Recovery cannot infer an owner from catalog state alone. A terminal row is removed only by bounded core operation retention after its final result is no longer replayable.

`long_running_operation_request_identities` is a normalized one-to-one core support table keyed by the existing operation ID, with a unique caller-requested operation ID plus fixed 32-byte apply-request and effect digests. `LongRunningOperationStore` inserts the operation and support row in one transaction. The row is immutable and cascades only with ordinary operation retention, avoiding an unsupported `ALTER TABLE` or numbered migration.

- [ ] **Step 5: Seed state and prove optional failure isolation**

Extend `CoreGrimoireSchemaDataInitializer` to seed cleanup and disclosure singleton rows. Drop every optional `covenant_*` object, delete a Campaign and Session, and require both core transactions and owner events to commit.

- [ ] **Step 6: Run the green test**

Run the Task 11 command. Expected: PASS.

- [ ] **Step 7: Refactor**

Use one test helper for monotonic owner-sequence assertions while retaining separate Campaign and Session triggers. Re-run and expect PASS.

### Task 12: Add Covenant canonical schema primitives

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_entries.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_state.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_versions.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_heads.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_version_attachment_provenance.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_mutation_receipts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_turn_receipts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_turn_receipt_aggregate.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_search_outbox.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Tables/covenant_key_epochs.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_entries_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_entries_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_versions_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_versions_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_version_attachment_provenance_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_version_attachment_provenance_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_mutation_receipts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_mutation_receipts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_turn_receipts_guard_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_turn_receipts_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_heads_validate_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_heads_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_search_outbox_guard_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_key_epochs_guard_overflow.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_heads_key_epoch_insert.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_heads_key_epoch_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_heads_key_epoch_delete.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_state_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Canonical/Triggers/covenant_turn_receipt_aggregate_validate_update.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CovenantCanonicalSchemaDataInitializer.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantCanonicalSchemaTests.cs`

**Interfaces:**

- Consumes: Nested catalogs from Task 8, authorization from Task 6, and initialization context from Task 9.
- Produces: Version-1 canonical DDL and integrity triggers. Plan 02 owns repository and mutation behavior.

- [ ] **Step 1: Write failing canonical tests**

Add `Canonical_catalog_contains_ten_version_one_tables`, `Canonical_tables_reference_only_canonical_tables`, `Global_and_Campaign_keys_use_separate_partial_unique_indexes`, `Version_revision_and_head_projection_have_composite_integrity`, `Global_Proposed_is_rejected`, `Set_and_Retire_content_shapes_are_enforced`, `Immutable_rows_reject_update_and_unauthorized_delete`, `Outbox_is_text_free_and_worker_delete_is_narrow`, and `Canonical_initializer_seeds_one_fresh_state_row`.

- [ ] **Step 2: Run the red test**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantCanonicalSchemaTests"
```

Expected: FAIL because the canonical catalog is empty.

- [ ] **Step 3: Add entry, version, head, and provenance DDL**

`covenant_entries` stores stable ID, scope, nullable Campaign, authored/normalized key, and timestamp, with separate Global and Campaign partial unique indexes. `covenant_versions` stores every approved design field for immutable authored/tombstone content, hashes, compiled cost, policy versions, origin/source evidence, mutation digests, predecessor, provenance aggregate, and timestamp. It has unique `(EntryId, LaneCode, LaneRevision)` plus the composite candidate key referenced by heads.

`covenant_heads` has PK `(EntryId, LaneCode)`, matching composite FK to version entry/lane/revision/operation, validated denormalized projection fields, and unique positive stable search row ID. Provenance uses version/ordinal identity and an internal FK to the immutable version. No optional table has a foreign key into Campaign, Session, Entry, or attachment core tables.

- [ ] **Step 4: Add state, receipt, outbox, and key-epoch DDL**

`covenant_state` contains the exact dataset, canonical/applied sequence, owner-cleanup sequence, accelerator/key/envelope epoch, master binding, next-search-ID, and rebuild fields from the specification. All increments fail before signed overflow. Mutation receipts have the closed identity/digest/outcome fields and scope/source/result indexes. Turn receipts and the per-Session aggregate enforce the 1,024/65,536 tail shape without storing content. The outbox PK is `(SearchSequence, Ordinal)` and contains stable IDs plus nullable desired version only. Key epochs are positive and keyed by normalized key.

- [ ] **Step 5: Add integrity and authorization triggers**

Updates to entries, versions, provenance, mutation receipts, and exact turn receipts abort. Owner deletion requires owner-cleanup or family-maintenance authorization. Outbox deletion accepts only accelerator synchronization or family maintenance. Head triggers compare every denormalized field with entry/version rows. Checks reject malformed hashes, origins, scopes, Global Proposed rows, predecessor mismatches, and nonpositive or overflowing counters.

- [ ] **Step 6: Add the canonical initializer**

`CovenantCanonicalSchemaDataInitializer` inserts one random 128-bit dataset generation, canonical sequence zero, null applied FTS tuple, current core deletion sequences, epochs 1, Task 9 master version/fingerprint, next search ID 1, and rebuild state FullRebuildRequired. Repeat invocation verifies the existing singleton and changes nothing.

- [ ] **Step 7: Run the green test**

Run the Task 12 command. Expected: PASS for partial indexes, internal FKs, projection integrity, authorization, and initializer idempotency.

- [ ] **Step 8: Refactor**

Extract only test helpers for table info, FKs, index shapes, and trigger failures. Keep each SQL object explicit. Re-run and expect PASS.

### Task 13: Add Covenant FTS5 accelerator schema primitives

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Accelerator/Tables/covenant_search_documents.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Accelerator/FullTextSearch/covenant_fts.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Accelerator/Triggers/covenant_search_documents_ai.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Accelerator/Triggers/covenant_search_documents_au.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/Capabilities/Covenant/Accelerator/Triggers/covenant_search_documents_ad.sql`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CovenantAcceleratorSchemaDataInitializer.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantAcceleratorSchemaTests.cs`

**Interfaces:**

- Consumes: Pinned FTS5 runtime from Task 5 and accelerator catalog from Task 8.
- Produces: Version-1 projection and external-content FTS objects. Plan 02 owns synchronization, the compiler, the sole `SearchAsync` query and fallback path, and the base rebuild algorithm. Plan 04 owns authenticated cursor and HTTP integration plus the long-running rebuild adapter and recovery handler.

- [ ] **Step 1: Write failing accelerator tests**

Add `Accelerator_catalog_contains_documents_FTS_and_three_triggers`, `Covenant_FTS_uses_external_content_and_stable_rowid`, `Covenant_FTS_uses_exact_tokenizer_prefixes_and_unindexed_IDs`, `Insert_update_delete_triggers_leave_no_ghost_tokens`, `Delete_command_carries_every_old_indexed_value`, and `Accelerator_initializer_enables_secure_delete_and_rank_one_integrity`.

- [ ] **Step 2: Run the red test**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAcceleratorSchemaTests"
```

Expected: FAIL because the accelerator catalog is empty.

- [ ] **Step 3: Add projection and FTS DDL**

`covenant_search_documents` uses positive integer `SearchRowId` as PK and stores entry/lane/version identity, scope/Campaign, normalized key, authored content, compiled content, dataset generation, and canonical search sequence. It has no cross-tier FK.

Use this semantic FTS definition, changing quoting only if the Task 5 runtime proves SQLite requires it:

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS covenant_fts USING fts5(
    NormalizedKey,
    AuthoredContent,
    CompiledContent,
    EntryId UNINDEXED,
    LaneCode UNINDEXED,
    VersionId UNINDEXED,
    content='covenant_search_documents',
    content_rowid='SearchRowId',
    tokenize='unicode61 remove_diacritics 2 tokenchars ''._-''',
    prefix='2 3 4 8'
);
```

- [ ] **Step 4: Add exact external-content triggers**

Insert writes row ID plus every indexed/unindexed value. Delete sends the FTS `delete` command with `OLD.SearchRowId` and every old value before projection deletion. Update sends the complete old-value delete before the complete new-value insert.

- [ ] **Step 5: Enable secure delete in the accelerator initializer**

Inside the accelerator install transaction execute:

```sql
INSERT INTO covenant_fts(covenant_fts, rank) VALUES('secure-delete', 1);
INSERT INTO covenant_fts(covenant_fts, rank) VALUES('integrity-check', 1);
```

Read back the FTS configuration and require secure-delete value 1. Unsupported commands or rank-1 integrity failure throw so Task 15 rolls back only this tier.

- [ ] **Step 6: Run the green test**

Run the Task 13 command. Expected: PASS, including real unique-token insert/update/delete probes.

- [ ] **Step 7: Refactor**

Keep one ghost-token test helper and assert stable row IDs throughout. Re-run and expect PASS.

### Task 14: Validate closed installed manifests and index shapes

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifest.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestEntry.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireExpectedIndex.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestBuilder.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestInspector.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTierOwnershipRegistry.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSqlNormalizer.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CovenantAcceleratorSyntheticManifest.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/SupportedPreCovenantCoreManifest.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantSchemaManifestTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaIdentityTests.cs`

**Interfaces:**

- Consumes: Ordered tier catalogs and source fingerprints from Task 8, plus pinned SQLite 3.53.3 FTS shadow DDL from Task 5.
- Produces:

```csharp
internal sealed record GrimoireSchemaManifest(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int Version,
    string SourceDefinitionFingerprint,
    IReadOnlyList<GrimoireSchemaManifestEntry> Entries);

internal sealed class GrimoireSchemaManifestInspector(
    GrimoireSchemaTierOwnershipRegistry ownershipRegistry)
{

    public Task<GrimoireSchemaInspectionResult> InspectAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GrimoireSchemaManifest manifest,
        CancellationToken cancellationToken);

}
```

It also produces `SupportedPreCovenantCoreManifest`, one closed literal manifest of the repository's recorded pre-issue-#74 core catalog. It contains the exact object definitions, explicit and implicit index shapes, existing FTS shadow objects, and one pinned installed-catalog fingerprint captured under the accepted SQLCipher and SQLite runtime. It is an upgrade allowlist, not a manifest synthesized from the database being inspected or from the later issue-#74 Core catalog.

- [ ] **Step 1: Write failing manifest tests**

Add these exact methods:

```text
Manifest_excludes_sqlite_autoindexes_but_validates_primary_and_unique_shapes
Manifest_rejects_unexpected_user_Covenant_index
  Manifest_rejects_unknown_Covenant_object
  Manifest_accepts_objects_owned_by_other_known_tiers_after_all_three_are_installed
  Manifest_rejects_Covenant_prefixed_object_unowned_by_any_tier
Manifest_rejects_missing_or_changed_object
Manifest_rejects_changed_index_columns_order_uniqueness_or_predicate
Accelerator_manifest_owns_all_four_FTS_shadow_tables
Installed_fingerprint_is_stable_across_whitespace_only_sql_normalization
Whole_database_backup_identity_still_includes_every_physical_object
Supported_pre_Covenant_manifest_has_literal_object_index_and_fingerprint_entries
Supported_pre_Covenant_manifest_accepts_only_the_exact_recorded_catalog
Supported_pre_Covenant_manifest_rejects_a_missing_object
Supported_pre_Covenant_manifest_rejects_an_extra_object_or_index
Supported_pre_Covenant_manifest_rejects_definition_or_index_drift
Supported_pre_Covenant_manifest_rejects_partial_issue_74_core_catalogs
Supported_pre_Covenant_manifest_rejects_mixed_legacy_and_issue_74_catalogs
```

The synthetic-entry assertion is exact:

```csharp
Assert.Equal(
    [
        "covenant_fts_config",
        "covenant_fts_data",
        "covenant_fts_docsize",
        "covenant_fts_idx",
    ],
    CovenantAcceleratorSyntheticManifest.Entries
        .Select(static entry => entry.Name)
        .Order(StringComparer.Ordinal));
```

- [ ] **Step 2: Run the manifest tests and witness missing types**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantSchemaManifestTests|FullyQualifiedName~GrimoireSchemaIdentityTests"
```

Expected: FAIL to compile because manifest and inspector types do not exist.

- [ ] **Step 3: Define exact expected-object and index records**

Use closed records whose sequence order participates in the fingerprint:

```csharp
internal enum GrimoireSchemaObjectType
{

    Table = 0,

    VirtualTable = 1,

    Trigger = 2,

    View = 3,

}

internal sealed record GrimoireExpectedIndex(
    string Name,
    bool IsUnique,
    string Origin,
    bool IsPartial,
    IReadOnlyList<GrimoireExpectedIndexColumn> Columns);

internal sealed record GrimoireExpectedIndexColumn(
    int Sequence,
    int ColumnId,
    string? Name,
    bool Descending,
    string Collation,
    bool IsKey);

internal sealed record GrimoireSchemaManifestEntry(
    GrimoireSchemaObjectType Type,
    string Name,
    string TableName,
    string NormalizedSql,
    bool IsSynthetic,
    IReadOnlyList<GrimoireExpectedIndex> Indexes);
```

`Origin` uses SQLite's closed `pk`, `u`, or `c` values. Explicit indexes use their declared name. Autoindexes have no expected generated name and match by exact origin plus `index_xinfo` shape.

- [ ] **Step 4: Build manifests from trusted resource grammar**

`GrimoireSchemaManifestBuilder.Build(...)` accepts only catalog resources. It extracts the single table, virtual table, trigger, or view name from the first DDL declaration, then extracts every co-located explicit index and table-level primary/unique constraint using the repository's quoted-identifier grammar. Reject unsupported expression indexes, unparseable collations, duplicate index names, or a declaration name that differs from the file name.

`GrimoireSqlNormalizer.Normalize(string)` must preserve quoted strings and identifiers byte for byte, normalize CRLF to LF, collapse ASCII whitespace outside quotes, remove the source-only `IF NOT EXISTS` tokens, strip one terminal semicolon, and use invariant ASCII keyword handling. Tests include quotes containing repeated spaces so semantic text cannot be collapsed.

- [ ] **Step 5: Capture and pin SQLite 3.53.3 FTS shadow entries**

Using Task 5's accepted runtime, create the exact `covenant_fts` virtual table from Task 13 in a scratch database, then capture:

```sql
SELECT "type", "name", "tbl_name", COALESCE("sql", '')
FROM sqlite_master
WHERE "name" IN (
    'covenant_fts_data',
    'covenant_fts_idx',
    'covenant_fts_docsize',
    'covenant_fts_config')
ORDER BY "type", "name", "tbl_name";
```

Normalize those four rows and add them as literal synthetic records in `CovenantAcceleratorSyntheticManifest`. Record the same normalized row hashes in `native-source-manifest.json`. A runtime version change must update these values through an explicit native dependency review.

From a pristine worktree at the Wave 0 recorded base commit, install the pre-issue-#74 schema with the same accepted runtime and capture every non-`sqlite_%` object plus every `PRAGMA index_list` and `PRAGMA index_xinfo` shape. Include existing application FTS shadow objects explicitly. Normalize and frame those rows with the same closed grammar used by the current manifests, compute the lowercase `sha256-` installed-catalog fingerprint, and check the resulting literal entries and fingerprint into `SupportedPreCovenantCoreManifest`. The checked-in implementation contains no placeholder, runtime source-catalog builder, wildcard, or accept-current fallback. A base-schema or native-runtime change requires an explicit supported-upgrade manifest review and new literal fingerprint.

- [ ] **Step 6: Inspect catalog rows and index shapes exactly**

`GrimoireSchemaTierOwnershipRegistry` is the complete closed mapping from every trusted core, canonical, accelerator, and synthetic shadow object and explicit index to its single owning tier. It rejects duplicate or missing ownership during manifest construction. `InspectAsync` receives that registry and must:

1. Read only `sqlite_master` rows owned by the manifest, Covenant-prefixed rows, and FTS shadows.
2. Exclude `sqlite_autoindex_*` names from name-based rows.
3. Validate every object owned by the inspected tier, ignore an object owned by another known tier, and reject only a Covenant-prefixed object or explicit user index unowned by the complete registry.
4. Compare normalized type, name, owner, and SQL for every expected and synthetic entry.
5. Query `PRAGMA index_list(<quoted fixed owner>)` and `PRAGMA index_xinfo(<quoted fixed index>)` for exact uniqueness, origin, partial flag, order, collation, and key/auxiliary columns.
6. Frame installed rows and index shapes with field and row separators, then compute `sha256-` plus lowercase hex.

All object and index names originate in the trusted manifests and complete ownership registry. Keep a dedicated identifier-quoting method and never accept an API value. Install all three tiers, then inspect Core, CovenantCanonical, and CovenantAccelerator independently and require all three inspections to succeed without treating another tier's known rows as drift.

Add a separate exact-database inspection mode used only with `SupportedPreCovenantCoreManifest`. It enumerates every non-`sqlite_%` user object and every index shape, compares the complete set with the literal legacy entries, and succeeds only when the pinned installed fingerprint also matches. It returns closed content-free classifications for `ExactSupportedLegacy`, `MissingObject`, `UnexpectedObject`, `DefinitionDrift`, `IndexShapeDrift`, `CurrentCorePresent`, and `MixedCatalog`. Presence of `grimoire_feature_schemas` or any object first introduced by issue #74 can never classify as legacy. A partial current catalog and a legacy catalog mixed with any current-only object fail before DDL.

- [ ] **Step 7: Preserve whole-database backup identity**

Leave `GrimoireSchemaIdentity.ComputeAsync` as the full physical-file identity used by backup verification. Add `ComputeManifestAsync` to the new inspector rather than narrowing the existing backup hash. Update its documentation to distinguish physical backup identity from tier health.

- [ ] **Step 8: Run manifest and identity tests**

Run the Task 14 command. Expected: PASS for drift, unknown objects, synthetic shadows, autoindex shape, whitespace normalization, and unchanged whole-database identity behavior.

- [ ] **Step 9: Refactor manifest failure reporting**

Return closed `GrimoireSchemaInspectionFailure` values `MissingObject`, `UnexpectedObject`, `DefinitionDrift`, `IndexShapeDrift`, `ShadowObjectDrift`, and `CatalogReadFailed`. Include only tier and object name in diagnostics, never SQL text. Re-run the focused suite and expect PASS.

### Task 15: Install three tiers with metadata and transactional data initializers

**Files:**

- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTierHealth.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTierInstallResult.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaDataInitializers.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/GrimoireSchemaInstallerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantSchemaInstallerTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Schema/CovenantSchemaRepairTests.cs`

**Interfaces:**

- Consumes: Catalogs and schema primitives from Tasks 8 through 13 and closed manifest inspection from Task 14.
- Produces:

```csharp
internal sealed record GrimoireSchemaInstallResult(
    GrimoireSchemaTierInstallResult Core,
    GrimoireSchemaTierInstallResult CovenantCanonical,
    GrimoireSchemaTierInstallResult CovenantAccelerator);

internal interface IGrimoireSchemaDataInitializer
{

    GrimoireSchemaTransactionTier TransactionTier { get; }

    Task InitializeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken);

}
```

- [ ] **Step 1: Write failing three-tier installation tests**

Add these exact methods:

```text
Fresh_install_commits_core_canonical_and_accelerator_in_three_transactions
Repeat_install_is_idempotent_and_preserves_fingerprints
Core_failure_rolls_back_core_and_throws
Canonical_failure_rolls_back_only_canonical_and_returns_unavailable
Accelerator_failure_rolls_back_only_accelerator_and_returns_degraded
Initializer_failure_rolls_back_its_DDL_metadata_and_data
Installed_newer_version_is_refused_without_DDL
Same_version_different_source_fingerprint_is_refused
Optional_objects_without_metadata_are_refused
  Metadata_without_expected_objects_is_refused
  Unknown_optional_object_is_refused
  Core_only_install_seeds_authority_without_opening_or_creating_optional_tiers
  Repeat_core_only_install_is_idempotent_and_returns_the_same_identity
  Exact_supported_pre_Covenant_catalog_upgrades_in_one_core_transaction
  Missing_extra_drifted_partial_or_mixed_legacy_catalog_is_refused_before_DDL
```

The canonical-isolation test first installs core, injects a canonical initializer that throws `CovenantCanonicalInitializerFault`, and asserts `Sessions` plus `grimoire_feature_schemas` remain while `covenant_entries` and the canonical metadata row do not exist. The accelerator-isolation test asserts canonical tables and metadata remain committed after the accelerator fault.

- [ ] **Step 2: Run the installer tests and witness two-tier behavior**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~GrimoireSchemaInstallerTests|FullyQualifiedName~CovenantSchemaInstallerTests|FullyQualifiedName~CovenantSchemaRepairTests"
```

Expected: FAIL because the current installer has one core transaction, an obsolete vec0 branch, no metadata table, and no tier results.

- [ ] **Step 3: Bind the three initializers to their exact tiers**

`GrimoireSchemaDataInitializers` requires exactly one initializer for Core, CovenantCanonical, and CovenantAccelerator. It rejects duplicates and omissions before opening a transaction. Use the metadata table from Task 9 and schema version 1 for both Covenant tiers.

- [ ] **Step 4: Define exact tier results**

Use:

```csharp
internal enum GrimoireSchemaTierHealth
{

    Healthy = 0,

    Unavailable = 1,

    IncompatibleNewerVersion = 2,

    SourceDefinitionMismatch = 3,

    InstalledCatalogDrift = 4,

    MetadataMissing = 5,

    DependencyUnavailable = 6,

}

internal sealed record GrimoireSchemaTierInstallResult(
    GrimoireSchemaTransactionTier TransactionTier,
    int SchemaVersion,
    GrimoireSchemaTierHealth Health,
    string SourceDefinitionFingerprint,
    string? InstalledCatalogFingerprint,
    string? DiagnosticCode)
{

    public bool IsHealthy => Health == GrimoireSchemaTierHealth.Healthy;

}
```

`DiagnosticCode` is closed and content-free. It cannot contain exception text or SQL.

- [ ] **Step 5: Replace the installer with explicit tier transactions**

Convert `GrimoireSchemaInstaller` to an injected sealed service. Its public methods are:

```csharp
public Task<GrimoireSchemaInstallResult> InstallAsync(
    SqliteConnection connection,
    int embeddingDimensions,
    GrimoireSchemaInitializationContext context,
    CancellationToken cancellationToken);

public Task<GrimoireSchemaTierInstallResult> InstallCoreOnlyAsync(
    SqliteConnection connection,
    GrimoireSchemaInitializationContext context,
    CancellationToken cancellationToken);
```

`InstallCoreOnlyAsync` calls the same core `InstallTierAsync` helper and `CoreGrimoireSchemaDataInitializer` transaction used by `InstallAsync`, but never inspects, creates, attaches, or initializes Covenant canonical or accelerator objects. It rethrows core failure. A new-install host-tools startup gate may call this method on its one non-pooled core connection, read and join the seeded authority row, close the connection, and only then decide whether optional services may initialize. `InstallAsync` delegates its Core phase to the same helper, then opens the canonical and accelerator boundaries only after the Core result is healthy. There is one Core installation algorithm.

For each tier, under `SqliteBusyRetry`:

1. Inspect existing metadata and owned catalog before executing DDL.
2. Fail that tier for newer version, same-version source mismatch, metadata loss, unknown objects, or drift.
3. Begin a SQLite transaction.
4. Execute its ordered DDL resources.
5. Invoke exactly one matching data initializer inside that transaction.
6. Inspect the installed manifest through the same transaction.
7. Insert or compare the tier's metadata row.
8. Commit.

Core failure is rethrown and aborts startup. Catch a canonical non-cancellation exception only at the canonical boundary and return unavailable. Attempt accelerator only when canonical is healthy; catch accelerator non-cancellation exceptions at its boundary and return degraded. Cancellation always propagates.

- [ ] **Step 6: Handle the core metadata bootstrap safely**

Before `InstallCoreOnlyAsync` or the Core phase of `InstallAsync` executes any DDL against a database without current Core metadata, classify the complete catalog through Task 14 `SupportedPreCovenantCoreManifest`. An empty database follows the new-install arm. Only `ExactSupportedLegacy` follows the legacy-upgrade arm. That arm begins one Core transaction, executes the complete current Core DDL, invokes `CoreGrimoireSchemaDataInitializer` for authority seeding and every legacy backfill, inspects the resulting current Core manifest inside the same transaction, writes its current metadata row, and commits once. It never accepts an object or index by name alone and never recomputes the legacy allowlist from the inspected database.

`MissingObject`, `UnexpectedObject`, `DefinitionDrift`, `IndexShapeDrift`, `CurrentCorePresent` without valid current metadata, and `MixedCatalog` all fail before DDL or data mutation. A current Core metadata row follows only the ordinary current-manifest path. Optional Covenant objects without their metadata row remain a fail-closed condition. Add `Legacy_core_without_feature_metadata_is_upgraded_in_the_core_transaction` and the exact classification tests from Step 1, then expect them to PASS.

- [ ] **Step 7: Run three-tier installer tests**

Run the Task 15 command. Expected: PASS for fresh, repeat, rollback, version, metadata, unknown-object, and drift cases.

- [ ] **Step 8: Refactor tier orchestration**

Extract `InstallTierAsync(GrimoireSchemaManifest, IGrimoireSchemaDataInitializer, ...)`, have both public methods delegate to it for Core, and keep exactly one `try/catch` boundary per optional tier in `InstallAsync`. Re-run the installer tests and expect PASS.

### Task 16: Publish tier health through bootstrap and schema convergence callers

**Files:**

- Create: `src/RetroDownfall.Arcanum.Core/Covenant/ICovenantAvailability.cs`
- Create: `src/RetroDownfall.Arcanum.Core/Covenant/CovenantAvailabilitySnapshot.cs`
- Create: `src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantAvailability.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireCliInitialization.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDbReadiness.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreDatabaseWorker.cs`
- Modify: `src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Hosting/GrimoireDatabaseBootstrapperTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Backup/BackupRestoreDatabaseWorkerTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireFixture.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireFixtureConcurrencyTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/Data/Covenant/CovenantAvailabilityTests.cs`

**Interfaces:**

- Consumes: `GrimoireSchemaInstallResult` from Task 15.
- Produces:

```csharp
public interface ICovenantAvailability
{

    CovenantAvailabilitySnapshot Current { get; }

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCapabilityState>))]
public enum CovenantCapabilityState : byte
{
    Unavailable = 1,
    Degraded = 2,
    Healthy = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantFtsSynchronizationState>))]
public enum CovenantFtsSynchronizationState : byte
{
    Unavailable = 1,
    Dirty = 2,
    Synchronized = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantHealthTransition>))]
public enum CovenantHealthTransition : byte
{
    Bootstrap = 1,
    SchemaRepair = 2,
    CanonicalMutation = 3,
    OwnerCleanup = 4,
    AcceleratorSynchronization = 5,
    AcceleratorRebuild = 6,
    Reset = 7,
    Restore = 8,
    FamilyReinitialize = 9,
    FeatureConfiguration = 10
}

public sealed record CovenantAvailabilitySnapshot(
    long Generation,
    bool FeatureEnabled,
    CovenantCapabilityState Canonical,
    int? CanonicalSchemaVersion,
    string? CanonicalInstalledFingerprint,
    CovenantCapabilityState Accelerator,
    int? AcceleratorSchemaVersion,
    string? AcceleratorInstalledFingerprint,
    Guid? DatasetGeneration,
    long CanonicalSequence,
    long CoreCampaignDeletionSequence,
    Guid? AppliedDatasetGeneration,
    long? AppliedSequence,
    long? AppliedCampaignDeletionSequence,
    ulong AcceleratorEpoch,
    CovenantFtsSynchronizationState FtsSynchronization,
    bool RebuildRequired,
    CovenantHealthTransition LastHealthTransition,
    string? CanonicalDiagnosticCode,
    string? AcceleratorDiagnosticCode);
```

- [ ] **Step 1: Write failing availability and bootstrap tests**

Add `Availability_enum_codes_are_immutable`, `Hosted_bootstrap_acquires_one_installation_lock_and_passes_the_same_lease`, `Cli_bootstrap_acquires_one_installation_lock_and_passes_the_same_lease`, `Bootstrap_never_reacquires_the_lock_inside_authority_preparation`, `Core_schema_failure_still_blocks_startup`, `Canonical_schema_failure_keeps_Grimoire_ready_and_marks_Covenant_unavailable`, `Accelerator_schema_failure_keeps_canonical_healthy_and_marks_search_degraded`, `Availability_generation_advances_on_each_published_install_result`, `Availability_snapshot_carries_every_hot_gate_cursor_and_status_field`, `Feature_publication_advances_generation_without_schema_reinstall`, `Projection_publication_updates_applied_tuple_epoch_and_rebuild_state_atomically`, `Backup_schema_convergence_returns_all_three_tier_results`, and `Fixture_fingerprint_tracks_combined_and_Covenant_canonical_sources`.

Pin every literal independently of enum declaration order:

```csharp
[Fact]
public void Availability_enum_codes_are_immutable()
{
    Assert.Equal((byte)1, (byte)CovenantCapabilityState.Unavailable);
    Assert.Equal((byte)2, (byte)CovenantCapabilityState.Degraded);
    Assert.Equal((byte)3, (byte)CovenantCapabilityState.Healthy);

    Assert.Equal((byte)1, (byte)CovenantFtsSynchronizationState.Unavailable);
    Assert.Equal((byte)2, (byte)CovenantFtsSynchronizationState.Dirty);
    Assert.Equal((byte)3, (byte)CovenantFtsSynchronizationState.Synchronized);

    Assert.Equal((byte)1, (byte)CovenantHealthTransition.Bootstrap);
    Assert.Equal((byte)2, (byte)CovenantHealthTransition.SchemaRepair);
    Assert.Equal((byte)3, (byte)CovenantHealthTransition.CanonicalMutation);
    Assert.Equal((byte)4, (byte)CovenantHealthTransition.OwnerCleanup);
    Assert.Equal((byte)5, (byte)CovenantHealthTransition.AcceleratorSynchronization);
    Assert.Equal((byte)6, (byte)CovenantHealthTransition.AcceleratorRebuild);
    Assert.Equal((byte)7, (byte)CovenantHealthTransition.Reset);
    Assert.Equal((byte)8, (byte)CovenantHealthTransition.Restore);
    Assert.Equal((byte)9, (byte)CovenantHealthTransition.FamilyReinitialize);
    Assert.Equal((byte)10, (byte)CovenantHealthTransition.FeatureConfiguration);
}
```

- [ ] **Step 2: Run the red tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~CovenantAvailabilityTests|FullyQualifiedName~GrimoireDatabaseBootstrapperTests|FullyQualifiedName~BackupRestoreDatabaseWorkerTests|FullyQualifiedName~GrimoireFixtureConcurrencyTests"
```

Expected: FAIL because bootstrap publishes only vector availability and callers do not carry three tier results.

- [ ] **Step 3: Add the Core availability contract and Infrastructure publisher**

Implement the three exact `byte` enums above in `CovenantAvailabilitySnapshot.cs`; no member uses zero and no alias is permitted. Their exact `StringOnlyJsonStringEnumConverter<TEnum>` attributes reject numeric JSON when Plan 04 exposes them. `CovenantCapabilityState.Healthy` means that the tier is authoritative for its owned work, `Degraded` means that the tier remains diagnosable but cannot serve every owned operation, and `Unavailable` means that callers cannot use the tier. `CovenantFtsSynchronizationState.Synchronized` requires the complete applied dataset/search/Campaign-deletion tuple and rank-1 integrity proof, `Dirty` selects canonical fallback and rebuild guidance, and `Unavailable` means no usable accelerator exists.

`CovenantHealthTransition` records the publisher that produced the complete snapshot. `Bootstrap` owns initial catalog and state publication; `SchemaRepair` owns repair convergence; `CanonicalMutation` owns ordinary committed writes; `OwnerCleanup` owns committed Campaign or Session cleanup; `AcceleratorSynchronization` owns outbox application; `AcceleratorRebuild` owns rebuild start, progress-state, and completion publication; `Reset` owns Covenant reset and healthy-catalog factory erasure; `Restore` owns restored-generation publication; `FamilyReinitialize` owns family replacement; and `FeatureConfiguration` owns the live feature switch. A caller cannot substitute another category or a free-form reason.

`CovenantAvailability` publishes schema results, live feature changes, canonical state changes, and accelerator applied-state changes by swapping one complete immutable snapshot with `Interlocked.Exchange` and incrementing a positive generation. Schema publication copies only the installed schema versions and normalized installed-manifest fingerprints. Canonical publication copies dataset generation, canonical and core Campaign-deletion sequences, and rebuild state. Accelerator publication copies the applied dataset/sequence/deletion tuple, accelerator epoch, FTS synchronization, and rebuild state. Feature publication changes only `FeatureEnabled` plus generation. No reader observes mixed fields and no hot-path read performs database or configuration I/O. Diagnostics remain closed and content-free; no SQL, path, exception text, object DDL, or secret-derived fingerprint enters this snapshot.

Bootstrap publishes the initial schema and state snapshot before readiness opens. Mutation, owner-cleanup, outbox, rebuild, reset, restore, reinitialize, and feature-configuration tasks must call the matching publication method only after their transaction commits and while their affected operation gate is still exclusive. The final provider and tool gates compare the captured availability generation plus the exact dataset and Campaign facts from this snapshot.

- [ ] **Step 4: Integrate bootstrap failure domains**

The hosted-service and CLI callers each acquire one `ArcanumMaintenanceLock` before authority or database bootstrap, retain that same object through the complete bootstrap, and pass it to Task 9 `PrepareUnderInstallationLockAsync`. The hosted service keeps its existing lifetime ownership. CLI initialization owns one scoped acquisition through readiness and releases it afterward. Neither caller nor a nested service probes or reacquires the same lock. Plan 03 later inserts its OS-marker precheck ahead of the borrowed authority preparation while preserving this single-owner path.

While that caller-held lock remains live, initialize and validate the native runtime, open through the central initializer, resolve the injected `GrimoireSchemaInstaller`, and call its three-tier `InstallAsync`. Let core exceptions fail readiness and startup. Publish canonical and accelerator results before marking Grimoire ready. Set `WeaveIndexAvailability` permanently to managed-only independent of Covenant accelerator health.

`GrimoireDatabaseHostedService` and CLI initialization continue waiting on core readiness. Covenant-aware callers in later plans consult `ICovenantAvailability` separately.

- [ ] **Step 5: Carry tier results through schema convergence only**

Change `BackupRestoreDatabaseWorker.MigrateAsync` to return the complete `GrimoireSchemaInstallResult` after initializing its staged connection and converging all three catalogs. Preserve its existing schema-only responsibility in this plan. Plan 04 adds staged identity reconciliation, restoration authority, FTS dirtying, and replacement behavior.

- [ ] **Step 6: Update fixture invalidation**

Keep the combined `CanonicalSchemaFingerprint` as the template-database invalidation key and write the Covenant canonical fingerprint beside it for capability tests. Fixture creation uses the real hermetic SQLCipher runtime and central initializer. It must not fall back to plain `:memory:` SQLite for Covenant schema acceptance tests.

- [ ] **Step 7: Run the green tests**

Run the Task 16 command. Expected: PASS. Core failure blocks, canonical failure isolates all Covenant canonical paths, and accelerator failure degrades only inspection search.

- [ ] **Step 8: Refactor bootstrap diagnostics**

Keep one mapping from `GrimoireSchemaTierHealth` to public capability state and content-free code. Remove obsolete vector-install branches and logger text. Re-run and expect PASS.

### Task 17: Gate native delivery in packaging, CI, testhost, self-contained, and AOT output

**Files:**

- Modify: `scripts/packaging/linux/package-linux.sh`
- Modify: `scripts/packaging/windows/package-windows.ps1`
- Modify: `scripts/packaging/macos/build-arcanum.sh`
- Modify: `scripts/packaging/macos/common.sh`
- Modify: `scripts/verify-aot-il-warnings.sh`
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/private-beta-release.yml`
- Modify: `.github/workflows/build-windows-x64.yml`
- Modify: `.github/workflows/release-macos-arm64.yml`
- Create: `.github/workflows/verify-native-sqlcipher.yml`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Packaging/ReleasePipelineTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Packaging/ContinuousIntegrationWorkflowTests.cs`
- Modify: `tests/RetroDownfall.Arcanum.Tests/Fixtures/GrimoireFixtureConcurrencyTests.cs`
- Create: `tests/RetroDownfall.Arcanum.Tests/NativeSqlCipher/NativeSqlCipherPublishTests.cs`

**Interfaces:**

- Consumes: Verified assets, MSBuild target, provider smoke, and scripts from Tasks 1 through 5.
- Produces: A five-RID native gate and packaging assertions that fail before an archive can omit or substitute SQLCipher.

- [ ] **Step 1: Write failing publish and workflow tests**

Add `Build_output_contains_exactly_one_manifest_matching_native_asset`, `Testhost_loads_the_hermetic_provider`, `Self_contained_publish_copies_the_exact_platform_filename`, `Native_AOT_publish_loads_encrypts_and_reopens`, `Packaging_requires_the_exact_SQLCipher_sidecar`, `Native_CI_matrix_contains_all_five_shipping_RIDs`, `Release_restore_uses_locked_mode`, and `AOT_default_RIDs_include_linux_arm64`.

- [ ] **Step 2: Run the red contract tests**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipherPublishTests|FullyQualifiedName~ReleasePipelineTests|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~GrimoireFixtureConcurrencyTests.Ci_has_packaged_sqlcipher_native_asset"
```

Expected: FAIL because release workflows do not run the five-RID native verifier, `linux-arm64` is absent from the AOT default list, and packaging still assumes the old bundle.

- [ ] **Step 3: Update package-stage assertions**

Linux requires exactly one `libe_sqlcipher.so`; Windows requires exactly one `e_sqlcipher.dll`; macOS requires exactly one `libe_sqlcipher.dylib`. Compare its hash with the RID record in `native-source-manifest.json` before signing or archive creation. Reject any second SQLCipher filename or undeclared OpenSSL dependency. Continue requiring `libonigwrap` where the existing regex runtime needs it, because it is unrelated to SQLCipher delivery.

macOS signs the verified dylib with the staged tree and notarizes the result. Windows Authenticode signs and verifies the checked-in DLL with the staged tree. Signing never changes the checked-in unsigned manifest hash; package evidence records the post-signing hash separately.

- [ ] **Step 4: Add the five-RID native CI matrix**

Run `scripts/verify-native-sqlcipher.sh --rid <RID>` on native runners for `osx-arm64`, `osx-x64`, `linux-arm64`, `linux-x64`, and `win-x64`. Each job verifies provenance, rebuild identity, upstream testfixture, dynamic dependencies, runtime pragmas, compatibility fixture, testhost, self-contained publish, and host-runnable AOT smoke before release packaging can depend on it.

All restore commands used by release jobs pass `--locked-mode`. Cache keys include the native manifest hash and package lock hashes.

- [ ] **Step 5: Expand AOT verification to all shipping RIDs**

Change `DEFAULT_RIDS` and usage text in `verify-aot-il-warnings.sh` to:

```bash
DEFAULT_RIDS=(osx-arm64 osx-x64 linux-arm64 linux-x64 win-x64)
```

The host-OS skip policy remains for local runs. The native CI matrix uses strict native runners so every RID executes.

- [ ] **Step 6: Run focused publish tests and host native verification**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipherPublishTests|FullyQualifiedName~ReleasePipelineTests|FullyQualifiedName~ContinuousIntegrationWorkflowTests|FullyQualifiedName~GrimoireFixtureConcurrencyTests.Ci_has_packaged_sqlcipher_native_asset"
./scripts/verify-native-sqlcipher.sh --rid "$(dotnet --info | awk '/RID:/{print $2; exit}')"
```

Expected: PASS and exit zero. The host build, testhost, self-contained publish, and AOT smoke all load the manifest-matching asset.

- [ ] **Step 7: Refactor workflow duplication**

Use one reusable workflow or composite action for the native verifier while retaining explicit five-RID callers. Keep release signing credentials in existing protected jobs. Re-run the contract tests and expect PASS.

## Plan 01 completion gate

Run from the repository root:

```bash
dotnet build RetroDownfall.Arcanum.slnx
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --filter "FullyQualifiedName~NativeSqlCipher|FullyQualifiedName~CovenantSchema|FullyQualifiedName~CovenantCore|FullyQualifiedName~GrimoireSchema|FullyQualifiedName~SqliteConnectionInitialization|FullyQualifiedName~SqlitePragma|FullyQualifiedName~GrimoireDatabaseBootstrapper"
./scripts/verify-native-sqlcipher.sh --rid "$(dotnet --info | awk '/RID:/{print $2; exit}')"
./scripts/verify-aot-il-warnings.sh
git diff --check
```

Expected: every command exits zero; no host-runnable native or schema test is skipped unexpectedly; the host asset matches provenance and rebuild evidence; first-party IL/AOT warnings remain within the repository gate; core, canonical, and accelerator installation results have their specified failure isolation. The merge-blocking native workflow separately runs strict jobs for all five RIDs. A local host-OS skip is reported and is never accepted as evidence for another RID.

Before handing off to Plan 02, verify these produced contracts match exactly: `ISqliteNativeRuntime.Initialize()`, `ICovenantSqliteConnectionInitializer.InitializeAsync(...)`, `Authorize(...)`, `GrimoireSchemaCatalog.CoreObjects`, `CovenantCanonicalObjects`, `CovenantAcceleratorObjects`, `CovenantCanonicalSchemaFingerprint`, `GrimoireSchemaManifestInspector.InspectAsync(...)`, `GrimoireSchemaInstaller.InstallAsync(...)`, `GrimoireSchemaInstallResult`, and `ICovenantAvailability.Current`.

Do not add canonical repository, mutation-kernel, FTS synchronization, search compiler, `SearchAsync`, fallback, base rebuild, authenticated cursor or API integration, long-running rebuild recovery, backup reconciliation, reset, restore, or erasure behavior in this plan. Plan 02 owns the persistence and search algorithms. Plan 04 owns the public and recoverable-lifecycle adapters against the tested schema and initialization contracts above.
