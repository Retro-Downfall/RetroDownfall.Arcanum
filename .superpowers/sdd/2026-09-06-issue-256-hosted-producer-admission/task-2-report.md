# Task 2 report: Establish the Bidirectional Inventory RED

## Implementation and scope

Added two test-only files:

- `tests/RetroDownfall.Arcanum.Tests/Support/HostedGrimoireProducerInventory.cs`: exact inventory records and enums, semantic hosted registration discovery, production compilation loader, source/DI call graph, closed provider/filesystem vocabularies, connection-route joins, site identities, admission/lifetime inspection, independent validator, and the 23-service/non-hosted catalog skeleton.
- `tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests.cs`: registration and mutation fixtures, all 74 listed vocabulary members including permission helpers, traversal/identity/disposal/frontier fixtures, runtime descriptor comparison, and the two real-tree umbrellas.

No production file, plan, spec, or Task 1 file was edited. No subagents or reviewers were dispatched. The scanner, GREEN fixtures, skeleton, and expected umbrella RED travel in the same commit.

Registration binding compares framework original method definitions and uses the bound generic implementation argument. The reset-aware helper is checked for one exact `RetroDownfall.Arcanum.Infrastructure.Hosting.InstallationResetRecoveryAwareHostedService<TService>` registration; its open registration is excluded and closed calls unwrap once. Factory fixtures include both GetRequiredService forms, direct construction, a method group, a typed Func local, and static extension syntax.

The production loader creates Infrastructure, Api, and Cli source compilations with the current runtime references, project global usings, generated assembly metadata, and preceding source compilation references. Including Infrastructure's generated InternalsVisibleTo metadata was necessary for Api's internal helper call to bind.

Runtime composition contains 24 IHostedService descriptors: the required 23 application registrations and exactly one `Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService` from `Microsoft.AspNetCore.DataProtection`. The test names and requires that exact framework descriptor, then requires the total to equal the source-discovered application count plus one. It does not exclude arbitrary factories or unknown descriptor types.

## Incremental RED evidence

Commands used the Release test project, `--no-restore --disable-build-servers -m:1`, and focused FullyQualifiedName filters. Each row records the pre-implementation behavior failure, not a compiler/infrastructure failure.

| Increment | Focused fixtures | Observed RED | Evidence |
| --- | --- | --- | --- |
| 1 | RegistrationUsesBoundImplementationType; UnsupportedRegistrationFailsClosed | 12 failed, 0 passed | Empty discovered registrations and absent unsupported-shape diagnostics; `/private/tmp/task2-red1.log` |
| 2 | ResetAwareHelperIsUnwrappedOnceAndItsBodyIsValidated | 2 failed, 0 passed | Expected Worker registration, received empty list; `/private/tmp/task2-red2.log` |
| 3 | ValidatorRejectsIndependentMutation | 12 failed, 0 passed | Exact requested diagnostics absent from the stub validator; `/private/tmp/task2-red3.log` |
| 4 | TraversalClassifiesBoundSites; SharedHelper; InterfaceCallFollows; EndpointInvokedSensitive; DeclaredMissingRoot; TraversalFailsClosed | 16 new fixtures failed | Missing sites/root identities/diagnostics. This broad filter also selected 24 existing unrelated passing tests; `/private/tmp/task2-red4.log` |
| 5 | EveryClosedVocabularyMemberIsDiscovered | 74 failed, 0 passed | Each expected symbol/kind absent from empty site discovery; `/private/tmp/task2-red5.log` |
| 6 | TwoCallsInOneMember; PublicationIncludes; MarkedConnectionRouteIsJoined; IncompleteBlobPublication; ConcreteProviderCall | 4 failed, 1 passed | Duplicate physical calls collapsed to one; writer lifetime, route join, and publication endpoint checks absent. Concrete interface-slot normalization already passed; `/private/tmp/task2-red6.log` |
| 7 | ProductionRegistrationsMatchRuntime; ProductionIncludesApiAndCli | 2 failed, 0 passed | Source loader returned no compilations/registrations; `/private/tmp/task2-red7.log` |
| 8 | OrdinaryScopeRequires; OnlyGuardedBoundEffect; OverloadedStartIsExternal; TwoBranchesCalling | 4 failed, 2 passed | Missing work/frontier diagnostic, absent guarded marker, overloaded Start misidentified as lifecycle, collapsed branch context; `/private/tmp/task2-red8.log` |
| 9 | NameofIsNot; EndpointThroughSingleton; DeclaredExternalRootIsResolved | 1 failed, 2 passed | `nameof(Worker)` incorrectly emitted HOSTED_CALL_TARGET_UNRESOLVED; `/private/tmp/task2-red9.log` |
| 10 | OrdinaryEffectsMustBeInsideTheRetainedGroup | 2 failed, 1 passed | Effects before admission and after a using statement's scope were incorrectly accepted; `/private/tmp/task2-red10.log` |
| 11 | SensitiveReadPropertySetter; WrongNamespaceWrapper | 2 failed, 0 passed | FileInfo setter incorrectly disappeared and same-name wrapper in the wrong namespace was accepted; `/private/tmp/task2-red11.log` |
| 12 | OrdinaryEffectsMustBeInsideTheRetainedGroup, explicit-disposal mutation | 1 failed, 3 passed | Explicit `held.Dispose()` did not terminate lexical retention evidence; `/private/tmp/task2-red12.log` |

The first attempted test execution compiled but VSTest could not bind its local communication socket in the sandbox. It was rerun with the necessary test-runner permission; that infrastructure abort is not counted as RED evidence. A char/string EndsWith compile typo was fixed before the first GREEN validator run. The later FileInfo setter fixture exposed that LastWriteTimeUtc is declared on FileSystemInfo; the scanner now normalizes the effective FileInfo receiver before classifying getter/setter use.

## Final GREEN fixture verification

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~HostedGrimoireProducerInventoryTests&FullyQualifiedName!~EveryApplicationHostedServiceHasExactlyOneEntry&FullyQualifiedName!~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce'
```

Result: **138 passed, 0 failed, 0 skipped, 138 total**. Test duration: 8 seconds. Full local log: `/private/tmp/task2-fixtures-final.log`.

Earlier GREEN checkpoints were 26 registration/validator tests, 116 tests after closed-vocabulary/traversal implementation, 129 after the first production/frontier checks, 132 after external-root/nameof checks, 135 after retained effect-group checks, and 137 before the explicit-disposal mutation.

## Separate expected umbrella RED

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~HostedGrimoireProducerInventoryTests.EveryApplicationHostedServiceHasExactlyOneEntry|FullyQualifiedName~HostedGrimoireProducerInventoryTests.EveryDiscoveredProducerSiteIsCataloguedExactlyOnce'
```

`EveryApplicationHostedServiceHasExactlyOneEntry` passes. `EveryDiscoveredProducerSiteIsCataloguedExactlyOnce` remains intentionally RED. The failure is an Assert.DoesNotContain matching a real `HOSTED_SITE_WORK_FRONTIER_MISSING`, beginning with A2ASendingLeaseRenewer's scope creation.

Final separate umbrella result: **1 passed, 1 failed, 0 skipped, 2 total**, duration 25 seconds. Final diagnostic inventory:

| Code | Count |
| --- | ---: |
| HOSTED_SITE_WORK_FRONTIER_MISSING | 102 |
| HOSTED_SITE_EFFECT_FRONTIER_MISSING | 69 |
| HOSTED_SITE_UNCATALOGUED | 761 |
| HOSTED_SITE_UNCLASSIFIED | 805 |
| HOSTED_CALL_TARGET_UNRESOLVED | 228 |
| HOSTED_AGGREGATE_PROOF_MISSING | 2 |
| HOSTED_EXTERNAL_OPERATION_UNCATALOGUED | 21 |

Representative unprotected sites include:

- A2ASendingLeaseRenewer.ExecuteAsync → RenewHeldAsync → CreateAsyncScope.
- CovenantMaintenanceHostedService.ExecuteAsync → maintenance sweep → CreateAsyncScope.
- GrimoireSchemaTransitionHostedService.ExecuteAsync → CreateAsyncScope.
- DataRetentionSweepHostedService.ExecuteAsync → CreateAsyncScope / IDataRetentionService.ApplyAsync.
- ApprenticeService.ExecuteAsync → RunApprenticeAsync → CreateAsyncScope / ExecutePromptAsync / StreamPromptAsync.
- Loremaster.ExecuteAsync → summarization scope / ExecutePromptAsync.
- SagaExtractionService.ExecuteAsync → scope / ExecutePromptAsync / IWeaveService.EmbedAsync.
- TapestryWeavingService.ExecuteAsync → scope / TapestryWeaver.WeaveAsync.
- UnseenServantService.ExecuteAsync → scopes / IDaemonRunner.RunScheduledAsync.
- WorkspaceIndexingService.ExecuteAsync → IWorkspaceFileWatcherFactory.Create and indexing collaborators.

The umbrella writes grouped diagnostics and up to 30 exact identities per code. Full local output is `/private/tmp/task2-umbrella-red.log`. These failures are preserved for the producer changes and Task 14's final exact site/authority/frontier catalog; they are not suppressed or treated as a blocker.

## Compile and combined-class verification

```bash
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
```

Result: **Build succeeded, 0 warnings, 0 errors**, exit code 0; elapsed 0.75 seconds. This compiles the test project and its Core, Secrets, Infrastructure, Api, Api.DevHost, and Cli dependencies.

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~HostedGrimoireProducerInventoryTests"
```

Combined-class result: **139 passed, 1 failed, 0 skipped, 140 total**, duration 19 seconds, exit code 1 as expected. The only failure is `EveryDiscoveredProducerSiteIsCataloguedExactlyOnce`; the 138 fixtures and service-registration umbrella pass. Diagnostic counts match the separate umbrella run above. Full log: `/private/tmp/task2-combined-final.log`.

## Self-review and concerns

- All 23 application registrations are independently specified in the tests; the catalog does not generate its service list from discovered registrations.
- Separate mutation assertions name exact failure codes, so another error cannot satisfy the requested negative test.
- Site identity includes root, exact contextual operation/call-site identity, source-relative path, type, member, site kind, and normalized callee. Two lifecycle roots and two branch calls through one helper remain distinct.
- Site/property/invocation discovery is semantic. Bound concrete provider implementations normalize to interface slots. Interface targets use exact singleton/scoped/transient bindings; unknown sensitive targets are diagnostics.
- Admission markers alone do not imply lifetime protection. Source checks require a rejecting guard, retained using/await-using group ownership at the effect, and no earlier explicit disposal through the admitted value or local retained alias.
- The ordinary catalog's site lists remain empty by design. The lifecycle/Backup entries are a skeleton, not a claim that the production tree is protected. Task 14 still owns exact mixed MCP/LRO branch evidence, final external roots, nonordinary proofs, and all site/frontier entries.
- The graph intentionally exposes many existing sensitive members outside the closed vocabulary and unresolved targets. The loader includes authored sources and generated assembly/global-using files, but does not run source generators in Roslyn; generated-only overload information can therefore contribute unresolved-target diagnostics. Such diagnostics remain visible rather than being converted to protection evidence.
- Retention inspection recognizes the repository's explicit guarded using patterns. It is not a general whole-program alias/control-flow proof engine. The production aggregate and lifecycle proof work is still required; passing fixture tests does not establish that those production paths are safe.
- Source span offsets in exact operation identities intentionally make moved/changed call sites stale, requiring deliberate final catalog review.
- No globally GREEN full suite was run or claimed at this stage.

## Review corrections (after 82290e9a)

This section supersedes the initial implementation's limitations above. Only the two Task 2 test/support files and this report changed. No production, project dependency, Task 1, design/spec, or plan files changed; no agents or reviewers were dispatched.

### Implemented corrections

1. Mixed-authority roots now execute an exact selector: owning source file/type/member plus normalized callee, occurrence, and source span (`::call:callee#occurrence@start:length`). Inserted/moved calls fail `HOSTED_ROOT_UNRESOLVED`; overlapping selectors and whole-member mixed claims fail `HOSTED_ROOT_OVERLAP`. Unselected lifecycle sites remain independently discovered. Selected calls include their exact retained caller-local frontier evidence so discovery can round-trip through validation. The LRO background member has one declared ordinary root, and calls into separately declared authority members are boundaries rather than duplicate roots.
2. Guard recognition handles the Saga shape where the failed arm records deferral without returning and retained lease/effect ownership lives in the successful `else`. Positive guards protect only the successful then-branch. Negative guards without an else require a terminating failure path. Failed branches do not acquire authority.
3. Cycle detection is a recursion-path push/pop with `finally`, not permanent visitation. Every call/getter/disposal edge retains its own source context; deep siblings, diamonds, and cycles are covered. Resolved member bindings are cached, not traversal outcomes, so an unsafe second call cannot disappear behind a previously admitted one.
4. Accessors, expression-bodied and unqualified getters, inherited FileInfo properties, and nested declaring types retain exact semantic identities. One checked site recorder applies frontier checks to invocations, properties, and synthetic disposal. One shared policy requires a work lease for filesystem reads, but an effect group only for provider calls/filesystem mutations. Repeatable admitted reads remain effect-free as required by the approved design. Uncalled local functions/delegates are not executed lexically; Task.Run/Parallel.ForEachAsync callbacks have explicit joins. Ambiguous sensitive interface-slot normalization fails closed.
5. Work/effect instance identities are carried at each site and through every call edge, with exact matching in the independent validator. Blob CreateWriterAsync, CompleteAsync, explicit disposal, and implicit disposal must remain in one continuous group. Both local writers and the production-shaped Batch factory/constructor/field carrier are checked. Field-backed support requires exact constructor assignments, per-field completion/disposal, and the same caller-owned group across factory, writer methods, and implicit carrier disposal. The actual EncryptedBlobWriter inherits Stream.DisposeAsync; that call is normalized from its bound receiver and recorded as a publication effect. Unsupported ownership shapes remain diagnostics, never prose-based exemptions.
6. DI factories follow their returned value/control flow, not nested constructor dependencies: conversions, direct construction, GetRequiredService, conditional/switch arms, block returns, authored helpers/method groups, typed locals and assignments. The implementation must be one concrete assignable type. Multiple or unsupported results leave the call target unresolved. A constructor dependency cannot become the registered implementation.
7. The exact six aggregate contracts have executable bound-source traversal evidence, including contextual call edges and nonempty sensitive-site evidence. Missing, empty, unrelated/drifted bindings fail `HOSTED_AGGREGATE_PROOF_MISSING`; opaque prose is not accepted as aggregate evidence. Production semantic compilations now use evaluated MSBuild Compile/ReferencePath/Analyzer/AdditionalFiles/editor-config inputs, exact defines/language options, and actual SDK source generators. Generated trees supply binding but are not invented authored producer roots. Every generated JsonSerializerContext.Default binds and all three complete compilations have zero errors. No Workspaces package or project-file expansion was needed.

The validator also rejects sensitive sites under EffectFree authority, sites assigned to the wrong root, and an unrelated operation-wide frontier label. The original scope fixture was updated to contain the now-required exact retained lease identity, rather than weakening the check to accept a label.

### Review RED → GREEN evidence

Each behavioral regression below was run and its assertion failure inspected before its scanner/validator change. The command prefix was `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter`; filters were the named fixture(s), combined with `|` for a coherent group. All logs are in `/private/tmp/`.

| Increment / filter | Observed RED | Subsequent GREEN evidence |
| --- | --- | --- |
| ExactCallSelectorsDoNotCrossScanMixedAuthorityBranches | 2 failures, cross-scanned startup/runtime effects; `task2-review-red1.log` | 2 passed; `task2-review-green1.log` |
| SuccessfulElseRetainsBothWorkAndEffectOwnership | 1 failure; `task2-review-red2.log` | included in 3 passed; `task2-review-green2.log` |
| DeepSiblingCallAfterGroupDisposalIsDiscoveredAndRejected | 1 failure, one leaf instead of two; `task2-review-red3.log` | included in 4 passed; `task2-review-green3.log` |
| PropertyReadsAndExpressionGetters / original validator property fixture | 4 failures; `task2-review-red4.log` | 4 passed; `task2-review-green4.log` |
| BlobPublicationRequiresOneContinuousGroupThroughDisposal | 5 failures: invalid group changes accepted and disposal sites absent; `task2-review-red5.log` | 5 passed; `task2-review-green5.log` |
| PropertyReadsAndExpressionGetters / ValidatorAllowsWorkAdmittedFilesystemReads (approved read-policy correction) | 4 failures; `task2-review-red-readpolicy.log` | 4 passed; `task2-review-green-readpolicy.log` |
| Exact nonterminating Saga failure-arm fixture | 1 failure; `task2-review-red-saga.log` | 1 passed; `task2-review-green-saga.log` |
| FactoryBindingFollowsReturnedImplementation | 5 failed / 3 passed; `task2-review-red6.log` | 8 passed; `task2-review-green6.log` |
| SelectedOperationRoundTrips / OverlappingAuthorityClaims | 3 failures; `task2-review-red1-roundtrip.log` | 3 passed; `task2-review-green1-roundtrip.log` |
| ExactSourceAnchorRejectsInsertedSiblingRatherThanRetargeting | exact-span positive assertion failed; `task2-review-anchor-generators.log` | selectors/round-trip/overlap group: 6 passed; `task2-review-green1-anchor.log` |
| ProductionCompilationsResolveGeneratedJsonSymbols | 1 failure showing missing generated Default members; `task2-review-red7-generators.log` | 1 passed, three error-free compilations; `task2-review-green7-generators.log` |
| ExactAggregateBoundaryExecutesItsBoundSourceContract | empty target incorrectly accepted: 1 failed / 3 passed; `task2-review-red7-aggregate.log` | 4 passed in `task2-review-green7.log` (that intermediate command still reported generator-option failures separately); final fixture run also verifies all 4 |
| BatchWriterFieldsRemainInTheCallersContinuousPublicationRegion | 2 failures; `task2-review-red5-batch.log` | local + field-backed region group: 7 passed; `task2-review-green5-batch.log` |
| DeclaredAuthorityRootIsNot / UncalledLocalFunction | 2 failures; `task2-review-red-root-boundary.log` | 2 passed; `task2-review-green-root-boundary.log` |
| ImplicitStreamDisposalAfterGroupLoss / ValidatorRejectsPublicationEndpoints | 2 failures; `task2-review-red5-checked-disposal.log` | publication/round-trip group: 10 passed; `task2-review-green5-checked-disposal.log` |
| ValidatorDoesNotTrust / PositiveAdmissionGuard | 4 failed / 1 passed; `task2-review-red-authority-tags.log` | authority/Saga/round-trip group: 7 passed; `task2-review-green-authority-tags.log` |
| GeneratorOutputSupplies / OnlyJoinedCallbacks / AmbiguousSensitiveInterface | 3 failed / 1 passed; `task2-review-red-lexical.log` | 4 passed; `task2-review-green-lexical.log` |
| NestedTypesAndUnqualifiedGetters | 1 failure; `task2-review-red-identities.log` | 1 passed; `task2-review-green-identities.log` |
| InheritedBlobWriterDisposal | 1 failure against the actual Core abstract writer; `task2-review-red5-inherited.log` | covered by the subsequent 187-passing fixture run |
| Repeated automatic/declared aggregate source proof | positive bound fixture failed while missing/empty/drifted fixtures stayed negative: 1 failed / 3 passed; `task2-review-red7-repeated-proof.log` | exact emitted-evidence counter is now independent of deduplicated catalog identities; verified in final full fixture run |

Intermediate compile errors during implementation (a Process namespace collision and interceptor-option parity) were inspected and corrected, but are not counted as behavioral RED evidence. The independent diamond/cycle and exact-six table checks were added as coverage for already implemented behavior. The obsolete slow preflight testhost was narrowly terminated after its exact PID was inspected; no source/output data was deleted.

### Final verification and review

Final fixture command (exit 0):

```sh
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter 'FullyQualifiedName~HostedGrimoireProducerInventoryTests&FullyQualifiedName!~EveryApplicationHostedServiceHasExactlyOneEntry&FullyQualifiedName!~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce' --logger 'console;verbosity=detailed'
```

Result: **187 passed, 0 failed, 0 skipped**, 21.0483 seconds. Log: `/private/tmp/task2-review-fixtures-final.log`. This includes registration shapes/runtime descriptors, all scanner/validator fixtures, exact-six contracts, publication lifetimes, branch/cycle identities, and generator-complete production compilation checks.

Final compile-only command (exit 0):

```sh
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
```

Result: **build succeeded, 0 warnings, 0 errors**. The final up-to-date confirmation took 0.83 seconds after the successful compile-and-test invocation; the preceding compiled build took 9.75 seconds. Log: `/private/tmp/task2-review-build-final.log`.

Separate registration umbrella command:

```sh
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~EveryApplicationHostedServiceHasExactlyOneEntry' --logger 'console;verbosity=detailed'
```

Separate intentional production-site umbrella command:

```sh
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce' --logger 'console;verbosity=detailed'
```

Both final umbrella logs are kept separately: `/private/tmp/task2-review-registration-final.log` and `/private/tmp/task2-review-sites-final.log`.

- Registration umbrella: **1 passed, 0 failed**, exit 0, total 57.0580 seconds. It continues to account for exactly 23 application services. Measured cold/in-process-first inventory: **56.036 seconds**; measured warm cached inventory: **0.037 milliseconds**. Earlier pre-cache-refactor cold run was approximately 76 seconds. These are observed test-run timings, not controlled performance benchmarks.
- Production-site umbrella: **1 failed, 0 passed**, exit 1, expected at Task 2, total 56.9807 seconds. No aggregate-proof-missing or root-unresolved diagnostics remain in this run. Generator-complete compilation is separately GREEN; remaining unresolved calls are conservative source/DI-boundary diagnostics, not missing generated JsonContext.Default symbols.

Final expected RED output summary:

```text
Uncatalogued contextual sites: 2140
Unique physical sites: 380
Physical sites per exact operation root: 619
HOSTED_SITE_WORK_FRONTIER_MISSING: 158
HOSTED_CALL_TARGET_UNRESOLVED: 123
HOSTED_SITE_EFFECT_FRONTIER_MISSING: 60
HOSTED_SITE_UNCLASSIFIED: 297
HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE: 6
HOSTED_EXTERNAL_OPERATION_UNCATALOGUED: 22
HOSTED_SITE_UNCATALOGUED: 2140
Test Run Failed. Total tests: 1. Failed: 1.
```

Representative actual work-frontier failures name `A2ASendingLeaseRenewer.ExecuteAsync` → `A2ASendingLeaseRenewer.cs:3420` → `CreateAsyncScope`; `GrimoireSchemaTransitionHostedService.ExecuteAsync` → `GrimoireSchemaTransitionHostedService.cs:2159` → `CreateAsyncScope`; `DataRetentionSweepHostedService.ExecuteAsync` → `DataRetentionSweepHostedService.cs:2286` → `IDataRetentionService.ApplyAsync`; and Apprentice/Loremaster provider calls. The six publication diagnostics identify the existing unprotected Batch CreateAsync/field-backed publication paths. These are not treated as blockers: Tasks 3–13 establish the production frontiers and Task 14 completes the explicit site catalog.

The 2,140 count is intentionally a count of exact contexts, not 2,140 independent physical effects. Grouping reduces physical review to 380 sites and 619 root/site combinations, with 2,140 source-edge/frontier contexts to account for. Task 14 therefore has materially more explicit catalog work than the original lossy graph suggested. Static, independently authored catalog helpers can factor common reviewed source/edge declarations, but a source-generated catalog or merging unproved null-authority paths would weaken the requested invariant. This size/cold-run cost is a disclosed tradeoff, not a claim that every contextual path has already been independently qualified.

### Final self-review / remaining concerns

- Reviewed the full diff and ran `git diff --check`; only the three authorized Task 2 files changed. Formatting was scoped to the two test files.
- Kept the original specified records/vocabulary and the 23-service skeleton. No producer behavior was changed to make inventory tests pass. Tasks 3–13 still do not edit inventory files; Task 14 owns final explicit site/proof entries and selector refresh after intentional source movement.
- No textual or name-prefix aggregate exemption was introduced. The six aggregate contracts and additional lifecycle expansion targets are closed exact sets. A duplicate traversal now proves aggregate execution from emitted site evidence, independently of catalog-identity deduplication.
- The source checker is deliberately a conservative supported-shape analyzer, not a general whole-program alias prover. Local retained aliases, successful guard regions, exact constructor-to-field publication ownership and explicit cleanup paths are executable supported shapes. Unsupported factory/ownership/binding forms remain visible diagnostics. A passing fixture is not a claim that an as-yet-unmodified producer is admitted.
- Cold generator evaluation is cached only within the test process via Lazy; warm queries reuse the same compilation/validation objects. No cross-run metadata cache was added, because that could conceal source/project/analyzer drift. Three evaluated project inputs are retained to preserve exactness; the dominant remaining cold cost is semantic call-path discovery, not repeated warm MSBuild evaluation. Member-symbol resolution is cached without caching/suppressing authority-sensitive traversal results.
- Contextual path expansion remains deliberate: automatic/declared copies with the same exact identity deduplicate, but distinct nested edges and retained-work/effect identities remain separate. Unadmitted paths cannot be declared equivalent merely because both currently have null frontier state. A future safe reduction would need executable path-equivalence evidence; it must not resurrect the unsafe-sibling bug. Task 14 can review physical sites grouped by root and factor independently authored static catalog declarations, but must not generate its authority proof/catalog from scanner discovery.
- The site umbrella remains RED because the producer implementations and final exact site catalog are intentionally unfinished. Unprotected scope/provider/publication sites and other unsupported exact bindings stay visible. No full-suite globally GREEN run is claimed.
