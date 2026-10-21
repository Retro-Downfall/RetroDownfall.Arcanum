# Schema source fingerprint compatibility probe (#224)

The probe is complete before any schema or fingerprint edit. Installed databases do record source-definition fingerprints; these are compatibility identities, not values computed only inside tests. This source-level probe does not open the operator's encrypted database.

`GrimoireSchemaInstaller.WriteMetadataAsync` stores the manifest's `SourceDefinitionFingerprint` in `grimoire_feature_schemas`. Its evolution path also writes the target fingerprint to `grimoire_schema_transitions` through `GrimoireSchemaTransitionJournal`. `GrimoireSchemaEvolutionPlanner` requires the recorded source version to match its published fingerprint, and a resumed journal to match the target head. Recomputing historical pins with a different algorithm would therefore refuse installed databases with `SourceDefinitionMismatch` or invalidate an interrupted transition. The healthy-catalog erasure guard independently checks this identity too.

## Compatibility design already delivered

Commit `349d4de308783344202672a6113abcfdeba05daf` adopted normalized SQL for Core at schema version 6. It preserved the exact raw fingerprint of the departing version 5. `GrimoireSchemaCatalog.ComputeSourceFingerprint` now hashes `GrimoireSqlNormalizer.Normalize(definition.Sql)` with the existing object identity framing. Comment edits and changes to whitespace runs do not move Core's source identity; quoted strings and identifiers remain byte-for-byte significant. This is the existing normalizer's contract, not a complete SQL semantic equivalence algorithm.

Historical fixtures must reconstruct the value their version actually published:

| Tier and versions | Fingerprint algorithm |
| --- | --- |
| Core 1–5 | Raw unresolved SQL |
| Core 6–19 | Normalized unresolved SQL |
| Core 20 (current) | Normalized unresolved SQL |
| Covenant canonical 1–6 | Raw unresolved SQL |
| Covenant accelerator 1 | Raw unresolved SQL |

The conditional mechanical rehash proposed by #224 is unsafe and is not performed. Every historical value is instead recomputed from its existing fixture with its original algorithm and checked against the published chain. The current head is computed from its current catalog, rather than pinned as a second authority.

## Why the version-4 SQL remains frozen

`CoreSchemaVersionFourFixture.Fingerprint` already computes its value from reconstructed `Objects`; it does not return a substitute fingerprint literal. The fixture retains historical SQL, including the old `saga_memories` comment, because version 4 published a raw hash. Deleting that frozen text would make the reconstruction stop recognizing a database that recorded the published version-4 identity. The production departure pin is also retained for that compatibility check. This retention satisfies #224's explicit alternative to removing the frozen version-4 literal.

## Scope

#224 is schema tooling, a sibling of memory curation under #73. Core already implements the requested comment-insensitive algorithm through the versioned compatibility design above. This change records the missing probe and retention decision, verifies all historical reconstructions, and adds a regression through the real installer for comment-only and whitespace-only Core source changes. A negative control restores raw Core hashing temporarily to prove that this regression detects the original refusal, then restores the existing normalized implementation.

The Covenant tiers remain on their published raw algorithms. Changing them requires a separately declared compatibility transition. A version bump with an arbitrary no-op is not sufficient: the installer deliberately rejects older metadata whose physical catalog already matches the head as `MixedCatalogVersions`. Inventing schema objects or weakening that admission rule is unnecessary to close the conditional #224 contract. The combined `CanonicalSchemaFingerprint` remains raw too: it keys cached test databases whose Covenant metadata still depends on raw SQL, so a Covenant comment edit must invalidate that cache.

## Additional local qualification repairs

Local qualification exposed composition and fixture defects in addition to the schema work. Recall tool and response-lifetime constructors silently defaulted the authority provider to null, contrary to the dependency contract. They now require that registered provider and reject null at construction; the Wizard resolves it as required. A provider whose current authority is absent still refuses protected recall. Two construction tests failed before this change, then all 96 affected recall and architecture cases passed.

The MCP bootstrap fixture read the operator's global AlwaysOn configuration. An actual server could advance the generation after a test captured it, and an explicit reload could time out while starting that server. The fixture now uses the existing isolated test-home scope and process-environment collection, restores the environment after manager disposal, and waits for the projection's partition barrier instead of sleeping. Both reproduced failures passed, followed by all 1,234 MCP cases with three platform skips. No manager behavior or timeout was changed.

The environment-isolation checker conservatively follows references across every method on a type. Five packaging contract classes reached the factory's environment-mutating type through shared pure helpers but lacked a serialized collection. They now use `ProcessEnvironment`; the checker is unchanged. All 193 affected isolation and packaging cases passed.

The held-stderr MCP test assumed that a daemonized descendant would escape process containment. Containment could correctly terminate it before output draining, which made the expected incomplete-stderr marker disappear. An independent test-owned process now receives the descriptor through an acknowledged Unix socket handoff. A separate 30-second failure watchdog bounds its lifetime; exit code, incomplete-stderr reporting and exact retrieval of 100,000 stdout characters are still asserted. The repaired scenario passed four consecutive executions.

Backup command construction eagerly reached the marker lifecycle's ordinary database source before the current database key was initialized. A required adapter now defers that source within its original DI scope. Real command, restore-plan and migration composition can reach their ordinary outcomes without initializing the current key; invoking a live marker read still reaches the required source and refuses an uninitialized key. The test container also uses asynchronous disposal for its async-only disclosure writer. All 34 backup-command cases passed.

A separate protected-replacement regression uses two differently encrypted databases, a real destination-only physical Campaign marker and a forbidden ordinary source. Both safety-backup settings failed because restore called the ordinary source during pre-swap marker inventory. The repair gives inventory and post-swap marker reconciliation their separately admitted restore-owned connections, preserving the journal, exclusive-owner, installation-lock and generation checks. Both variants passed after the repair, including completed cleanup in the new generation with a different database secret. A released pre-swap owner refuses before inventory or marker preparation and preserves the original database and physical marker. The acquisition inventory includes all 519 exact call sites; its new classification controls also passed.

The documentation checks also required removing the tracker reference from the owning design prose and restating the timestamp restart invariant without a phrase the existing narration check rejects. The same byte-preservation requirement is retained; all 22 affected documentation cases passed.

## Validation

The negative control temporarily restored raw SQL hashing in `ComputeSourceFingerprint` for both the original and edited manifests. Both unchanged-source reopen controls passed; the comment-only and whitespace-only edited reopens then failed with `SourceDefinitionMismatch`. Restoring the existing normalized implementation made all 30 new cases pass. The production catalog was restored byte for byte before qualification.

| Local verification | Result |
| --- | --- |
| Forced sequential Release rebuild and final solution build | Passed, zero warnings and errors |
| New fingerprint compatibility cases | 30 passed |
| Complete schema namespace | 742 passed |
| Recall and nullable-dependency architecture cases | 96 passed |
| Complete MCP namespace | 1,234 passed, three Windows skips; both repaired cases also passed in freshly rebuilt Release |
| Affected documentation checks | 22 passed |
| Compendium Release tests | 193 passed |
| The Forge Release tests | 742 passed, two Windows skips |
| Ordinary Arcanum Release suite | 27,169 passed, 113 skipped; 29 failures isolated to the repaired cases described above |
| Final affected-case rerun, including protected replacement | 4,154 passed, zero failures, six platform skips |
| Native AOT Ollama vision, restart, recall and timestamp qualification | Requalified final native apphost: one executed pass, zero failures or skips |
| Native AOT Ollama Long Rest qualification | Requalified final native apphost: one executed pass, zero failures or skips; unchanged rerun completed in 13 minutes 54 seconds |

The first final-native Long Rest trial stopped during corpus seeding: the model repeated a `delegate_task` call without either required budget, received the budget refusal, and then reached `Hub.NoProgressDetected`. The advertised schema, runtime validation and returned tool-result history agreed. No child was dispatched. This is the documented probabilistic seeding-model failure; the failed trial produced no success receipt. Its log is retained separately from the unchanged rerun. Tools, provider forwarding, retry counts and the repeated-call guard are unchanged.

The unchanged final-native Long Rest rerun covered the clean sweep and replay, killed-host recovery after the five-minute dead-owner lease, the ordinary default-model turn, and the Mutiny Scenario. Journal settlement, accounting and health checks passed. In the Mutiny Scenario, all 20 possible valid folds completed with zero cross-topic folds; neither the Pinned nor the Retired memory appeared in any provider request. Two invalid model outputs used the harness's explicit operator-retry path, and the final journal settled with no failed work items. The embedding calibration kept all 64 true pairs and excluded all 1,126 other directed pairs on this corpus; this is a measured qualification, not a claim that every embedding model separates every possible topic. The operator explicitly excluded the full CI gate; coverage, cross-platform shipping lanes and hosted-producer analysis were not run as delivery gates. The ordinary local suite excludes the manual performance baseline and the opt-in Long Rest scale probe as well.
