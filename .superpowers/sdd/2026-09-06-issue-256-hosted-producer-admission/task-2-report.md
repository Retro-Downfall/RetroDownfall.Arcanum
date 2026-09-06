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

## Fix round 2 — callback, accessor, publication-instance, and authority ownership

Review input: `task-2-rereview-1.md`, four Important findings. Base: `3da6a5336488152a16eaf3287812ed1b52d87818`. Only the two inventory test/support files and this report were edited; no production, project dependency, plan/spec, or Task 1 changes. No subagents/reviewers were dispatched.

### Implementation

- Callback bodies are no longer scanned by lexical containment. A bound synchronous delegate invocation, directly awaited `Task.Run`/`Parallel.ForEachAsync`, or an exact single-use local task joined in the immediately following statement can carry the caller's retained identities. Returning a task through a completion-owned helper is supported. Discarded tasks, detached async helpers, reassigned delegates, returned-delegate factories, unsupported external dispatch APIs, and an exception opportunity before a delayed join fail closed with `HOSTED_CALLBACK_OWNERSHIP_UNPROVEN`. Bound callback bodies are still inventoried without inherited authority when ownership is unproved. Captured admission aliases are checked for explicit/implicit disposal inside the callback. Anonymous-member cycle/admission caches now include the body's source span.
- Property traversal selects the accessor actually executed: simple assignment/init uses the setter, reads use the getter, increment/decrement uses both, and logical negation does not invoke a setter. Expression `using` and `await using` resolve authored `Dispose`/`DisposeAsync` and evaluate work/effect retention at scope exit. Known writer cleanup goes through the checked sensitive-site recorder. An unknown `IDisposable` expression is not silently assumed to be an admission handle; an exact bound admission origin is required for that exception.
- Field-backed publication returns a set of exact mapped writer-creation spans, not a factory-wide boolean exemption. Each returned field must map to its own creation; unmapped/orphan creations are independently checked for completion and disposal under the same retained group. Local writer instances remain independently checked. Explicit and implicit disposal of direct/transitive admission aliases permanently invalidates that handle, including nested `using` declarations and `using(group)` expressions; a later alias cannot resurrect it.
- Whole-member declarations no longer suppress traversal from another caller authority. The only new canonical-root transfer is a typed `HostHandoffProof`: exact hosted `StartAsync`, bound `Task.Run` assignment to one owned Task field with one assignment, a callback containing only a call to one declared ordinary root, and one exact shutdown await of that field (directly or through `Task.WaitAsync`). Arbitrary conditional/loop joins are not accepted. The proof retains dispatch/join anchors and the bound task field; it transfers to the independently admitted root without inheriting startup work/effect handles. Automatic/declared traversal then deduplicates the same canonical identity. Direct callers still retain their own authority context.
- The production-site umbrella now checks `validation.IsValid`, not merely the `HOSTED_SITE_` prefix: callback, disposal, root, aggregate, and unresolved-call diagnostics cannot be left behind while claiming the inventory is GREEN. Its output groups new ownership/disposal diagnostics by exact physical source anchor and semantically bound callee, in addition to contextual counts.

### Strict TDD evidence

Every new round-2 scanner regression uses normal C# xUnit fixtures. `R2Discover` first asserts that the fixture compilation has **zero error diagnostics**; assertion failures below therefore are behavioral RED, not missing-reference or malformed-fixture failures. Commands used the Release test project with `--no-restore --disable-build-servers -m:1` and the filters shown below. All logs are under `/private/tmp/`.

| Increment / focused filter | Observed RED before scanner change | GREEN after minimum change |
| --- | --- | --- |
| `R2CallbacksRequireCompletionOwnership` (initial direct/helper/discard/local/delegate group) | 7 failed / 1 passed, `task2-r2-red1.log` | 8 passed, `task2-r2-green1.log` |
| `R2ExecutedAccessorsAndExpressionDisposalAreInventoried` | 7 failed / 0 passed, `task2-r2-red2.log` | 7 passed, `task2-r2-green2.log` |
| `R2CarrierProof` / `R2DisposedAdmission` / `R2EveryLocalWriter` | 3 failed / 5 passed (orphan + two implicit-alias losses), `task2-r2-red3.log` | 8 passed, `task2-r2-green3.log` |
| `R2DeclaredAuthority` / `R2HostHandoff` | 4 failed / 0 passed, `task2-r2-red4.log` | 4 passed, `task2-r2-green4.log` |
| Callback exception gap / `R2UnknownExpression` | 2 failed / 8 passed, `task2-r2-red5.log` | 10 passed, `task2-r2-green5.log` |
| `R2CapturedAdmission` / `R2DiscardedAsync` | 3 failed / 0 passed, `task2-r2-red6.log` | 3 passed, `task2-r2-green6.log` |
| `R2LogicalNegation` / conditional `R2HostHandoff` | 2 failed / 2 passed, `task2-r2-red7.log` | 4 passed, `task2-r2-green7.log` |
| Unsupported `Task.Factory.StartNew` dispatch | 1 failed / 9 passed, `task2-r2-red8.log` | 10 passed, `task2-r2-green8.log` |
| Factory-produced delegate is not the factory method group | 1 failed / 10 passed, `task2-r2-red9.log` | 11 passed, `task2-r2-green9.log` |
| Final diagnostic audit: out delegate / synchronous `Func<int>` / detached `async void` | 3 failed / 11 passed, `task2-r2-red10.log` | 14 passed, `task2-r2-green10.log` |

The valid multi-writer/finally-cleanup and negative one-writer-missing-completion cases were included in the publication increment; those existing per-local behaviors were already GREEN while the orphan/alias regressions were RED. The obsolete declaration-only authority-boundary fixture was replaced by compile-clean positive/negative caller-context and executable-handoff fixtures, rather than preserving its unsound expectation.

The formatter initially failed because its MSBuild host could not open a sandboxed named pipe. The same narrowly scoped format command succeeded with approved local pipe access; that environmental failure is not counted as TDD evidence. Formatting was restricted to the two authorized test/support files.

### Final verification (round 2)

All commands ran from the requested worktree. Test invocations used approved local VSTest socket access.

```sh
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --no-incremental --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~HostedGrimoireProducerInventoryTests&FullyQualifiedName!~EveryApplicationHostedServiceHasExactlyOneEntry&FullyQualifiedName!~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce' --logger 'console;verbosity=detailed'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~EveryApplicationHostedServiceHasExactlyOneEntry' --logger 'console;verbosity=detailed'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce' --logger 'console;verbosity=detailed'
```

- Non-incremental build: **exit 0, 0 warnings, 0 errors, 51.94 seconds**. Log: `/private/tmp/task2-r2-build-final.log`.
- Complete fixture/registration/validator matrix excluding the two umbrellas: **exit 0, 225 passed, 0 failed, 0 skipped, 27.2854 seconds**. Log: `/private/tmp/task2-r2-fixtures-final.log`.
- Separate 23-service registration umbrella: **exit 0, 1 passed**, total 1.1165 minutes. Cold inventory **65.609 seconds**, warm cached access **0.045 milliseconds**. Log: `/private/tmp/task2-r2-registration-final.log`.
- Separate production-site umbrella: **exit 1, 1 failed**, expected RED, total 1.0871 minutes. Log: `/private/tmp/task2-r2-sites-final.log`. Its failure is now the full `validation.IsValid` assertion. No root-unresolved, aggregate-proof-missing, or generated-JSON-symbol errors were reported.
- The final three test processes ran separately and concurrently after the successful build; these cold timings include that contention and are not controlled benchmarks. No global full-suite GREEN claim is made.

Final production diagnostic summary:

```text
Uncatalogued contextual sites: 2056
Unique physical sensitive sites: 381
Physical sensitive sites per exact operation root: 570
HOSTED_SITE_WORK_FRONTIER_MISSING: 158
HOSTED_CALLBACK_OWNERSHIP_UNPROVEN: 11624
HOSTED_CALL_TARGET_UNRESOLVED: 124
HOSTED_SITE_EFFECT_FRONTIER_MISSING: 60
HOSTED_SITE_UNCLASSIFIED: 297
HOSTED_DISPOSAL_TARGET_UNRESOLVED: 183
HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE: 3
HOSTED_EXTERNAL_OPERATION_UNCATALOGUED: 17
HOSTED_SITE_UNCATALOGUED: 2056
Test Run Failed. Total tests: 1. Failed: 1.
```

The 2,056 site identities remain exact caller/root/frontier contexts, not independent physical effects. The decrease from round 1's 2,140 is not an equivalence-based relaxation: callback traversal now follows executable call paths, and the valid host handoff deduplicates its canonical independently admitted runtime root. Unsafe sibling paths still retain distinct edges. Tasks 3–13 still establish production admission; Task 14 owns catalog closure. The three publication diagnostics and ordinary scope/provider failures remain visible.

### Important closure concerns and a bounded Task 14 path

**Important: this fix round is ready for fresh review, not a declaration that Task 2 or the production inventory is approved/complete.** The scanner now exposes previously silent callback/disposal gaps. The initial 7,429 callback contexts expanded after unsupported external dispatches were also made fail-closed. Final counts are **11,624 callback diagnostics across 337 source-anchor/callee pairs at 313 distinct source offsets**, plus **183 disposal diagnostics across 22 source-anchor/callee pairs**. These are not 11,807 independent manual exemptions. The umbrella prints every physical pair and its contextual count as a `PHYSICAL` line; the complete per-source list is in `task2-r2-sites-final.log`. Callees are recorded from the actual bound symbol when the diagnostic is created, not reconstructed from a chain's first token.

A satisfiable closure requires executable mechanisms, not prose or catalog rows that waive diagnostics:

| Class | Concrete closure mechanism / production-shaped evidence | Current status |
| --- | --- | --- |
| Delegate parameters and stable delegate fields (118 physical pairs, 7,288 contexts) | Carry a call-edge binding environment keyed by the target's exact parameter/field symbol, substitute the bound caller lambda/local/method group, and check the invocation's synchronous/awaited completion. Preserve each edge and inherited-handle lifetime. Handle omitted nullable callbacks only when the actual argument/default and guard prove they do not execute. `SqliteBusyRetry.ExecuteAsync` has directly awaited `action`, `retrying`, and `delayAsync` parameters; `CovenantMaintenanceHostedService.RunSweepAsync` similarly awaits its `run` parameter. One supported parameter-binding mechanism can close repeated contexts at these exact source sites. Reassignment/escaping fields must remain unresolved. | **Not supported by the current model. Important reviewer/Task 14 work.** |
| Eager framework callbacks (LINQ terminal predicates/aggregates, list predicates/comparisons, concurrent dictionary factories) | Add closed typed contracts keyed by full bound framework method/overload, asserting that supplied callbacks finish before the call returns; traverse each exact callback under the call's retained identities. Add positive and detached/async-void/reassigned negative fixtures per contract family. Concurrent factories may execute more than once; that does not authorize caching away their call-edge proof. No namespace/name-prefix exemption. | Not yet implemented. Existing false-positive execution assumptions were removed for `out` delegate outputs; remaining rows are conservative unsupported contracts. |
| Deferred LINQ / iterator callbacks | Bind the deferred iterator recipe to an exact enumeration/terminal-consumption site, then prove disposal and caller retention through that consumption. Alternatively refactor a producer to a directly admitted explicit loop. Construction of `Where`/`Select`/`OrderBy` alone must never confer an effect lifetime on later enumeration. | **Not provable by the current model**; requires an executable iterator contract or producer refactor, not a manual exemption. |
| Detached tasks, continuations, background loops, event/options callbacks, and SQLite registered callbacks | Use an exact completion-owned task-local/field join where applicable; otherwise give the callback an independently admitted declared root and a tested task-registration/lifetime-transfer contract. SQLite registered functions and options callbacks need exact registration-to-invocation ownership (or a proven sensitive-effect-free callback body). The existing LRO host handoff is one executable instance, not a blanket rule for all continuations. | General event/native/deferred callback ownership is **unproved**. Review must not infer that these contexts are safe from their names. |
| Inherited authored cleanup (3 physical pairs, 22 contexts) | Resolve the inherited/explicit interface slot, then traverse the authored implementation. Both purpose-specific journal-key lease classes inherit `StableJournalKeyLease.Dispose` in `BackupRestoreJournalKeyProvider.cs`; it atomically takes and zeros the key. A base-slot resolver closes this shared class without a cleanup exemption. | Current expression resolver diagnoses inherited members; straightforward source-backed extension remains. |
| `ConfiguredAsyncDisposable` (4 pairs, 10 contexts) | Unwrap the exact bound `IAsyncDisposable.ConfigureAwait` resource to its original value and traverse that value's disposal at the same exit state. | Not yet supported; no authority should be granted to the wrapper's name alone. |
| Metadata file/handle cleanup (12 pairs, 145 contexts: SafeFileHandle 5/100, FileStream 6/44, Stream 1/1) | Use closed typed disposal contracts with exact creation provenance/capabilities (read-only handle versus flush/write/delete-on-close). A read-only close must not globally require an effect group; a mutating close must retain the publication group. Resolve derived writer cleanup from its actual owner when possible. | **Current model cannot prove arbitrary metadata Stream/handle effects.** Capability contracts or source-visible producer ownership are required. |
| Returned `IDisposable` / enumerator cleanup (3 pairs, 6 contexts) | Reuse exact returned-expression dataflow to bind `TurnAccountingAmbient.Push` to its authored `RestorationScope.Dispose` (two Batch sites), and bind the workspace enumerator to an exact read-only enumeration/disposal contract. | Not yet implemented; a plain `IDisposable` or `IEnumerator<T>` label is insufficient. |

The bounded mechanisms above demonstrate a feasible engineering path, but they are **not proofs already present in this commit**. In particular, arbitrary callback parameters/fields, deferred iterators, registered callbacks, and polymorphic metadata resources cannot be closed merely by filling today's catalog. Fresh review must decide whether Task 14 is the right place for those extensions or whether the harness needs another scoped fix round. No new production refactors were authorized or performed in this round.

Exact bound-callee grouping (physical = source-anchor/callee pair, contexts = current authority/call-edge diagnostics):

| Diagnostic | Bound callee | Physical | Contexts |
| --- | --- | ---: | ---: |
| Callback | ``System.Action`1.Invoke`` | 52 | 1177 |
| Callback | ``System.Linq.Enumerable.Select`` | 50 | 257 |
| Callback | ``System.Func`2.Invoke`` | 29 | 4591 |
| Callback | ``System.Linq.Enumerable.Where`` | 27 | 94 |
| Callback | ``System.Linq.Enumerable.Any`` | 27 | 3031 |
| Callback | ``System.Func`1.Invoke`` | 20 | 542 |
| Callback | ``System.Linq.Enumerable.OrderBy`` | 18 | 134 |
| Callback | ``System.Collections.Concurrent.ConcurrentDictionary`2.GetOrAdd`` | 13 | 50 |
| Callback | ``System.Linq.Enumerable.ToDictionary`` | 9 | 33 |
| Callback | ``System.Action.Invoke`` | 8 | 42 |
| Callback | ``System.Linq.Enumerable.Sum`` | 7 | 7 |
| Callback | ``System.Collections.Concurrent.ConcurrentDictionary`2.AddOrUpdate`` | 6 | 40 |
| Callback | ``System.Linq.Enumerable.FirstOrDefault`` | 6 | 239 |
| Callback | ``System.Linq.Enumerable.ThenBy`` | 6 | 22 |
| Disposal | ``Microsoft.Win32.SafeHandles.SafeFileHandle.Dispose`` | 5 | 100 |
| Callback | ``System.Func`3.Invoke`` | 4 | 478 |
| Callback | ``System.Threading.Tasks.Task.Run`` | 4 | 11 |
| Callback | ``System.Linq.ImmutableArrayExtensions.FirstOrDefault`` | 4 | 11 |
| Disposal | ``System.Runtime.CompilerServices.ConfiguredAsyncDisposable.DisposeAsync`` | 4 | 10 |
| Disposal | ``System.IO.FileStream.DisposeAsync`` | 3 | 5 |
| Disposal | ``System.IO.FileStream.Dispose`` | 3 | 39 |
| Callback | ``System.Linq.ImmutableArrayExtensions.Select`` | 3 | 254 |
| Callback | ``System.Linq.Enumerable.GroupBy`` | 3 | 22 |
| Callback | ``System.Collections.Generic.List`1.Sort`` | 3 | 12 |
| Callback | ``System.Linq.Enumerable.All`` | 2 | 7 |
| Callback | ``System.Linq.Enumerable.Aggregate`` | 2 | 5 |
| Callback | ``System.Func`4.Invoke`` | 2 | 448 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionJournalFileStore.ProveAllAbsentAsync`` | 2 | 4 |
| Disposal | ``RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreJournalKeyLease.Dispose`` | 2 | 20 |
| Disposal | ``System.IDisposable.Dispose`` | 2 | 2 |
| Callback | ``System.Threading.Tasks.Task.ContinueWith`` | 2 | 12 |
| Callback | ``System.Array.Exists`` | 2 | 12 |
| Callback | ``System.Collections.Generic.List`1.FindIndex`` | 1 | 9 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunSimulacrumBranchAsync`` | 1 | 8 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync`` | 1 | 8 |
| Callback | ``System.Linq.Enumerable.OrderByDescending`` | 1 | 6 |
| Callback | ``System.Linq.Enumerable.Count`` | 1 | 5 |
| Callback | ``System.Func`5.Invoke`` | 1 | 5 |
| Disposal | ``System.Collections.Generic.IEnumerator<string>.Dispose`` | 1 | 4 |
| Callback | ``System.Func`6.Invoke`` | 1 | 4 |
| Callback | ``System.Linq.Enumerable.DistinctBy`` | 1 | 3 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager.RunGlobalInitOperationAsync`` | 1 | 3 |
| Callback | ``Microsoft.Data.Sqlite.SqliteConnection.CreateFunction`` | 1 | 22 |
| Disposal | ``RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionJournalKeyLease.Dispose`` | 1 | 2 |
| Disposal | ``System.IO.Stream.DisposeAsync`` | 1 | 1 |
| Callback | ``System.Linq.ImmutableArrayExtensions.Where`` | 1 | 1 |
| Callback | ``System.Collections.Generic.List`1.RemoveAll`` | 1 | 1 |
| Callback | ``System.Collections.Generic.List`1.ConvertAll`` | 1 | 1 |
| Callback | ``System.Collections.Generic.HashSet`1.RemoveWhere`` | 1 | 1 |
| Callback | ``System.Action`2.Invoke`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentTextExtractor.ReadChunksAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Mcp.McpConnectionManager.EnsureGlobalLoadedAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.RetryAfterDelayAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.RunSweepLoopAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Hosting.ChronicleHub.SubscribeAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Infrastructure.Hosting.CampaignLoggerQueue.ReadAllAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Core.Mcp.IMcpConnectionManager.StopAllAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.WatchForCancellationAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Api.Intelligence.BatchProcessingService.EnumerateRequestPagesAsync`` | 1 | 1 |
| Callback | ``RetroDownfall.Arcanum.Api.Intelligence.BatchJsonlRecordReader.ReadAsync`` | 1 | 1 |
| Callback | ``Microsoft.Extensions.Options.IOptionsMonitor`1.OnChange`` | 1 | 1 |

### Round 2 self-review

- All four rereview findings have direct compile-clean RED/GREEN regression evidence, plus bounded audit cases. Existing Saga success-branch, deep unsafe sibling, exact selector, aggregate, generator-complete compilation, and registration fixtures remain GREEN.
- Checked the complete scoped diff and `git diff --check`; only the two inventory files and this report changed. The closed vocabulary/catalog skeleton, production sources, plan/spec, and project dependencies are unchanged.
- A diagnostic is a refusal to prove safety, not evidence of an actual production bug. The new grouped callback/disposal rows include supported-by-the-runtime but unsupported-by-this-analyzer shapes. They are deliberately retained, and the strengthened umbrella cannot pass with them outstanding.
- No source-derived catalog generation, generic prose proof, factory-wide publication exemption, declaration-only authority cut, or namespace-wide callback/disposal waiver was introduced.
- This is still a conservative supported-shape analyzer rather than a general alias/control-flow theorem prover. The Important closure work above is material and remains visible for the reviewer. The current site inventory and proof obligations are intentionally RED pending production admission and executable final closure.

## Fix round 3: exact joins, cross-call handles, carrier terminals, and handoff writes

Reviewed `task-2-rereview-2.md` completely before changes. Base commit: `d36e0bc9242d75da0e3bc127700a868e12817cf4`. Only the two inventory files and this report belong to this fix. No production, plan, spec, Task 1, or dependency edits were made; no subagents/reviewers were dispatched.

### Round 3 implementation

- Completion ownership now accepts an exact awaited task, parentheses, and semantically bound framework `Task`/`ValueTask.ConfigureAwait` only. A stored task's one join must be a standalone unconditional statement in the declaration block with no intervening statement. Conditional operands, `WhenAny`, and conditional join statements cannot lend caller authority to the callback.
- Authored call edges carry semantic actual-to-formal admission-origin bindings. Simple declaration and later assignment aliases form conservative may-alias sets; explicit/implicit disposal and nested helper disposal invalidate only the matching work/effect instance, both inside the callee and after returning to the caller. May-alias information can invalidate authority but never establish retention. Ref/out and opaque escapes fail closed; returned or heap-stored handles also end the supported lifetime continuation. Recursion-path tracking prevents cycles from silently preserving a handle.
- Field-carrier publication proof binds only the completion overload actually invoked and the disposal implementation selected by the language's `IDisposable`/`IAsyncDisposable` slot. Each mapped field must have exact bound writer completion and disposal calls in those executable targets. Conditional/unreachable terminals do not count. Both the caller-to-carrier and carrier-to-writer asynchronous terminal must have a completion-preserving join; discarded asynchronous leaves do not count. Orphan creation checks remain independent.
- The host handoff rejects any semantic reference to the tracked task field passed by ref/out/in, exposed through a ref expression/address, or written through a further assignment/compound/deconstruction target. The supported single-dispatch/exact Stop join still yields one runtime-root traversal; task replacement retains the unsafe caller context and emits the callback diagnostic.

### Round 3 strict compile-clean RED/GREEN evidence

Every new case is normal C# compiled by `R2Discover`, which asserts zero compiler errors before discovery. Each regression group was run and its behavioral assertion failure inspected before the corresponding scanner edit. Common command prefix: `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter`.

| Increment/filter | Observed pre-fix RED | Post-fix GREEN | Local evidence |
| --- | --- | --- | --- |
| `FullyQualifiedName~R3TaskLocalRequiresExactUnconditionalJoin` | 3 failed / 2 passed: conditional await, WhenAny, conditional statement falsely owned | 18 passed including previous callback ownership forms | `/private/tmp/task2-r3-red1.log`, `task2-r3-green1.log` |
| `FullyQualifiedName~R3AdmissionIdentitySurvivesAssignmentsAndFormalParameters` | 6 failed / 2 passed: assigned aliases, formal disposal, returned caller state, nested forwarding, ref/out escape falsely retained effect authority | 14 passed including previous captured/disposed alias cases | `/private/tmp/task2-r3-red2.log`, `task2-r3-green2.log` |
| `FullyQualifiedName~R3EachWriterRequiresTheActuallyInvokedCarrierTerminals` | 3 failed / 1 passed: unrelated overloads, conditional and unreachable second-writer terminals falsely completed | 8 passed including previous mapped/orphan carrier fixtures | `/private/tmp/task2-r3-red3.log`, `task2-r3-green3.log` |
| `FullyQualifiedName~R3HostHandoffJoinsTheExactDispatchedTask` | 5 failed / 1 passed: Interlocked, ref, out, ref-local, deconstruction replacements falsely owned | 11 passed including previous authority/handoff fixtures | `/private/tmp/task2-r3-red4.log`, `task2-r3-green4.log` |
| Publication self-review supplement, same R3 carrier filter | 1 failed / 5 passed: discarded asynchronous writer completion falsely counted | 10 passed with previous carrier/Batch fixtures; directly awaited leaf also covered | `/private/tmp/task2-r3-red3-async.log`, `task2-r3-green3-async.log` |
| Alias self-review supplement, same R3 alias filter | 2 failed / 8 passed: returned and heap-stored retained parameter falsely survived later disposal | 10 passed | `/private/tmp/task2-r3-red2-escape.log`, `task2-r3-green2-escape.log` |

The first complete matrix after the four required groups was 248 passed (`/private/tmp/task2-r3-fixtures-first.log`). The bounded self-review supplements add four cases. The successful callback/local join, no-disposal formal helper, multi-writer carrier, and exact host handoff positives protect against blanket rejection. Alias negatives also assert that an unrelated work lease remains retained when only its effect group ends.

### Round 3 final verification

```bash
dotnet format whitespace tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --include tests/RetroDownfall.Arcanum.Tests/Support/HostedGrimoireProducerInventory.cs tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests.cs --verbosity quiet
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --no-incremental --disable-build-servers -m:1
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~HostedGrimoireProducerInventoryTests&FullyQualifiedName!~EveryApplicationHostedServiceHasExactlyOneEntry&FullyQualifiedName!~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce' --logger 'console;verbosity=detailed'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~EveryApplicationHostedServiceHasExactlyOneEntry' --logger 'console;verbosity=detailed'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~EveryDiscoveredProducerSiteIsCataloguedExactlyOnce' --logger 'console;verbosity=detailed'
```

- Formatting: exit 0, scoped to the two inventory files.
- Clean/no-incremental build: exit 0, **0 warnings, 0 errors**, 52.23 seconds. `/private/tmp/task2-r3-build-final.log`.
- Complete scanner/registration/validator fixture matrix excluding the two umbrellas: exit 0, **252 passed, 0 failed, 0 skipped**, 26.9272 seconds. `/private/tmp/task2-r3-fixtures-final.log`.
- Separate 23-service registration umbrella: exit 0, **1 passed**, 1.1566 minutes; cold first inventory 68.151 seconds, cached warm call 0.038 milliseconds. `/private/tmp/task2-r3-registration-final.log`. The two umbrella processes ran concurrently, so the cold number includes resource contention; caching was not weakened.
- Separate production-site umbrella: expected exit 1, **1 failed**, 1.1654 minutes. `/private/tmp/task2-r3-sites-final.log`. The failing assertion still requires `validation.IsValid`; no category is waived. No globally GREEN full suite was run or claimed.

Final staged-RED categories:

| Diagnostic | Count |
| --- | ---: |
| HOSTED_SITE_WORK_FRONTIER_MISSING | 158 |
| HOSTED_CALLBACK_OWNERSHIP_UNPROVEN | 11,625 |
| HOSTED_CALL_TARGET_UNRESOLVED | 124 |
| HOSTED_SITE_EFFECT_FRONTIER_MISSING | 60 |
| HOSTED_SITE_UNCLASSIFIED | 297 |
| HOSTED_DISPOSAL_TARGET_UNRESOLVED | 183 |
| HOSTED_ADMISSION_HANDLE_ESCAPE | 1 |
| HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE | 3 |
| HOSTED_EXTERNAL_OPERATION_UNCATALOGUED | 17 |
| HOSTED_SITE_UNCATALOGUED | 2,056 |

There remain **381 physical sensitive sites / 570 physical sites per exact operation root**, unchanged from round 2. Callback refusals cover **338 unique physical anchor/callee pairs** (one more); disposal refusals remain **22 pairs**. There are no new root, aggregate, generated-symbol, or service-registration diagnostics. The production catalog remains intentionally empty at the site level until final executable proof closure.

### Round 3 self-review and Important Task 14 concerns

- Read the scoped scanner diff and verified `git diff --check`. Existing accessor/setter/init, expression disposal, Saga success branch, mixed-selector, deep sibling, generator, registration, and validator tests remain GREEN in the 252-case matrix. Exact site identities and independent discovery/validation were preserved.
- The newly rejected production callback is `Loremaster.ExecuteAsync -> ConsumeQueueAsync` at `Loremaster.cs:1377` (source offset), whose task is joined through `Task.WhenAll`. This is a conservative supported-shape refusal, not evidence of a production race. Task 14 needs an exact all-input-tasks completion contract, including exception and group-lifetime proof; merely appearing beneath `await` remains insufficient. This adds one contextual callback row, not a catalog explosion.
- The new handle-escape row is `SessionAttachmentIndexProcessor.cs:2738` (source offset), the exact `System.ArgumentNullException.ThrowIfNull(workLease)` call. The current opaque-callee rule conservatively refuses to preserve a passed authority handle even though this framework guard is non-escaping on normal return. Task 14 must add an exact bound non-escaping handle-call contract (with a positive fixture and a wrong-symbol/escaping negative), or a producer shape avoiding the unsupported pass. It must not erase all opaque-handle diagnostics. Returning/storing authority handles is also conservatively rejected; broad heap alias analysis is not claimed.
- The prior Important Task 14 closure work remains: typed callback relevance/binding and symmetric disposal capability contracts for the grouped physical rows. The new `WhenAll` and pure guard rows fit that same small-mechanism path. Catalog filling alone cannot make the umbrella GREEN. These are explicit reviewer/Task 14 concerns, not unverifiable source-text exemptions.
- Carrier unconditional-terminal proof deliberately supports a narrow straight-line shape. Conditional cleanup or more elaborate successful-path control flow may require an executable exact contract or producer refactor. No same-name overload, uncalled member, arbitrary await ancestor, disposed alias, or replaced host task is accepted as proof by the new cases.
- Two untracked, unrelated `docs/superpowers/{plans,specs}/2026-09-06-issue-256-turnstile-fast-path-addendum.md` files appeared during final verification. Their owner was notified; neither file was edited, staged, or removed. Only the three authorized Task 2 files will be committed. The review package covers the base-to-new-commit range and excludes those unrelated artifacts.

## Fix round 4: receiver/composite origins and one-path carrier proof

Reviewed `task-2-rereview-3.md` completely before continuing the recovered work. The scoped implementation closes both remaining Important findings without a waiver or production edit:

- Admission actuals now come from Roslyn's semantic invocation arguments, including the reduced-extension receiver's exact formal binding. A bounded semantic value walk collects each local/parameter reference once for conditional, coalescing, switch, tuple, array/index, query, and other composite expressions; opaque instance receivers and heap-composed receivers fail closed. Exact `TryBeginExternalEffectGroup` use preserves its work-lease receiver, and an authored reduced extension such as `held.Keep()` retains authority only when its bound body proves the handle remains live.
- Origin lookup has an exact cached zero-seed proof and collision-safe contextual expression cache. Seed discovery uses semantically normalized admission calls plus captured/local alias declarations. Cache keys include source path, exact member/span, expression span, and a length-prefixed stable parameter/sorted-origin binding identity; results are immutable. Recursive partial-path queries are never cached. Deferred nested function bodies are not scanned as composite values; source- or converted-type delegate recognition instead leaves opaque callback capture as an explicit ownership refusal.
- Field-backed writer carriers require readonly instance fields, one unconditional simple constructor assignment per field, one unique unmodified factory local/creation per constructor parameter, and no ref/assignment escape. The exact invoked completion and language-selected disposal targets must contain every mapped writer terminal together in one supported straight-line successful path. Mutually exclusive `try`/`catch`, `try`/`finally`, conditionals, switches, loops, `goto`, locks, using containers, checked containers, later overwrites, conditional construction, mutable fields, and ref mutation all fail closed.

### Round 4 strict RED/GREEN evidence

All fixtures compile through `R2Discover`, whose first assertion requires zero compiler errors. The recovered local logs show the tests failing for behavior before each minimum scanner change:

| Increment | Observed RED | GREEN |
| --- | --- | --- |
| Reduced receiver and composite may-origin binding | 7 failed / 4 passed (`/private/tmp/task2-r4-red1.log`) | 21 passed (`task2-r4-green1.log`) |
| Stable writer-to-field mapping | 6 failed / 2 passed (`task2-r4-red2-behavior.log`) | 18 passed (`task2-r4-green2.log`) |
| One common executable terminal path | 6 failed / 1 passed (`task2-r4-red3.log`) | 25 passed (`task2-r4-green3.log`) |
| Opaque/heap-composed receiver supplement | 2 failed / 12 passed (`task2-r4-red1-receivers.log`) | included in final 36-case R4 GREEN |
| Opaque callback captured behind object conversion | 1 failed / 33 passed after deferred-body pruning | included in final 36-case R4 GREEN via source-or-converted delegate refusal |

The original unconditional descendant-identifier union made the cold production inventory exceed four minutes. Controlled isolation removed receiver evaluation without fixing the delay, then removed only that union and restored the 1m04s baseline. Instrumentation measured 1,753,088 origin calls and 1,308,632 semantic walks by roughly 60 seconds before cancellation. The exact contextual cache reduced the latest pre-completion sample to 2,520 semantic walks with 1,583,695 immutable cache hits across 1,609,728 calls; the instrumented registration umbrella then passed in 1m05s. All instrumentation was removed before final verification. The optimization briefly erased captured admission disposal; the existing two-case `R2CapturedAdmissionDisposalEndsInheritedAuthority` fixture went RED, and exact captured/local seed tracing restored both cases before the full matrix.

### Round 4 final verification

Final instrumentation-free evidence after scoped formatting:

- Every `R2`/`R3`/`R4` fixture: **102 passed, 0 failed**.
- Complete non-umbrella inventory matrix: **288 passed, 0 failed, 0 skipped**, 24 seconds.
- Registration umbrella: **1 passed**, 1m04s.
- Production-site umbrella: **expected RED, 1 failed**, 1m04s. It still requires `validation.IsValid`; no diagnostic is suppressed. Inventory remains **2,056 contextual sites / 381 physical sites / 570 physical sites per exact operation root**. Stricter source-type delegate recognition exposes **11,645 callback ownership refusals across 345 physical anchors** for Tasks 3-14 to close with executable evidence.
- Clean no-incremental test-project build: **0 warnings, 0 errors**.

### Round 4 self-review

- Mutation coverage includes the exact reviewer counterexamples, named constructor arguments, parameter/local/field writes, semantic ref mutation, asynchronous terminal joins, a query-clause wrapper whose origin is behind non-expression syntax nodes, different binding sets through the same authored helper, and an opaque callback capture converted to `object`.
- The expression cache cannot cross-contaminate different call-edge bindings: binding identity is structural and length-prefixed, not hash-only; origins are sorted; immutable sets are stored; only top-level empty-path results are cached.
- The terminal proof is deliberately conservative. A more elaborate correct carrier method may be refused, but mutually exclusive or unsupported control flow cannot silently count terminals from different paths. The one accepted carrier shape proves all mapped writers in the same exact completion/disposal member.
- No production file, dependency, catalog row, suppression, addendum, or unrelated untracked file changed. The production umbrella remains intentionally RED for the later producer and Task 14 work.

## Fix round 5: exclusive carrier transfer and cache object identity

Reviewed `task-2-rereview-4.md` completely before changing the scanner. This final fix round closes the remaining Important false negative and the review's cache-isolation follow-up without a production edit or diagnostic waiver.

### Round 5 implementation

- Field-carrier ownership now uses a semantic reference allowlist rather than a ref-only escape predicate. Each created writer local must have exactly one reference: its exact bound constructor argument. Each constructor parameter must have exactly one reference: the right side of its one unconditional readonly-field assignment. Every carrier-field reference must be either that one assignment target or the direct receiver of an exact supported `CompleteAsync`, `Dispose`, or `DisposeAsync` terminal. Any authored/opaque by-value call, store, return, capture, alias, write, or ref use outside those nodes rejects the carrier proof.
- Direct writer terminals in unrelated overloads remain harmless uses but cannot prove the invoked carrier terminal. The existing exact-invoked-member/common-path checks still require all mapped writers to terminate together in a caller-invoked completion member and language-selected disposal member.
- Admission-call, seed, and origin caches now include integer identities assigned through reference-equality dictionaries for both the exact `Compilation` and exact `SyntaxTree`, followed by member/expression spans and the existing structural binding identity. Origin values remain immutable, and recursive partial-path lookups remain uncached.
- Authored members retain every same-`MethodKey` context. Resolution first matches exact source assembly and semantic symbol identity; a metadata call across project compilations may fall back only to one unambiguous authored candidate. Multiple different source contexts with the same method key no longer overwrite one another or silently cross-bind.

### Round 5 strict RED/GREEN evidence

All new cases compile through `R2Discover`, which asserts zero compiler errors before discovery.

| Increment | Observed RED | GREEN |
| --- | --- | --- |
| Exclusive local/parameter/field carrier flow | **10 failed / 1 passed**: factory and constructor authored dispose/store plus opaque by-value calls, and completion/disposal field stores/opaque calls were falsely accepted; the exact stable carrier remained GREEN | **11 passed** |
| Same-path/same-span distinct-tree cache isolation | **2 failed / 0 passed**: one ordering treated both contexts as unadmitted and the reverse treated both as admitted | **2 passed** after exact compilation/tree identity and context-aware resolution |

The cache fixture runs admitted and same-length non-admission helpers in separate compilations. It asserts distinct `SyntaxTree` objects, identical `src/Fixture.cs` paths, and identical helper spans, then proves each operation receives only its own effect-frontier result in both input orderings.

The first cross-compilation implementation used assembly-reference identity as the only member lookup. The production-site inventory then changed incorrectly to 2,039 contextual / 381 physical / 564 root-specific sites, proving that metadata symbols in a consuming compilation still need the one exact authored definition from the producing project. A subsequent cold registration run exposed multiple syntax entries for one semantic method via `Sequence contains more than one matching element`. The final resolver uses exact semantic-symbol identity for same-compilation duplicates and the one-candidate cross-project fallback above. Both failures were corrected before final evidence.

### Round 5 final verification

- Scoped whitespace formatting: exit 0.
- Clean no-incremental test-project build: **0 warnings, 0 errors**, 51.72 seconds.
- Every `R2`/`R3`/`R4`/`R5` fixture after the final resolver correction: **115 passed, 0 failed, 0 skipped**, 12 seconds.
- Complete non-umbrella inventory matrix after the final resolver correction: **301 passed, 0 failed, 0 skipped**, 26 seconds.
- Cold registration umbrella: **1 passed**, 1m06s, within the prior round's 1m04s-1m05s baseline.
- Production-site umbrella: **expected RED, 1 failed**, 1m07s. It still asserts `validation.IsValid`; no diagnostic is suppressed. The established inventory is restored exactly: **2,056 contextual sites / 381 physical sites / 570 physical sites per exact operation root**.

### Round 5 self-review

- The ten carrier negatives name the production mutation they catch: accepting a second local/parameter reference or a non-terminal field reference. Authored helpers actually dispose or store the real writer; opaque calls are intentionally unsupported ownership escapes. The stable carrier and production Batch fixture remain positive.
- Field terminals in unrelated overloads are allowed only as exact direct writer-terminal receivers and never contribute proof unless the caller invokes that exact carrier member. Arbitrary overload bodies, callbacks, aliases, and by-value calls remain rejected.
- Cache keys use object identity, not source path, source content, or a hash. Binding identity remains collision-safe and sorted; immutable cached sets cannot be mutated between call edges.
- Reviewed the complete two-file diff and ran `git diff --check`. No production file, catalog entry, suppression, dependency, addendum, or unrelated untracked file changed.

## Local publication proof completion

Reviewed `task-2-final-review.md` completely and replaced the remaining member-wide local-writer terminal-presence check with one conservative semantic lifetime proof. This bounded correction changes only the two inventory files and this report; production and the unrelated turnstile addendum remain untouched.

### Strict RED and implementation

The first compile-clean R6 matrix ran before the scanner change. `R2Discover` asserted zero compilation errors for every fixture. The current scanner produced **21 failed / 9 passed** across 30 cases. Confirmed false negatives included every by-value/opaque/alias/field/callback/return escape, conditional and unsupported creation, conditional and unreachable completion, disposal before completion, discarded Task/ValueTask completion, discarded Task/ValueTask disposal, duplicate terminals, wrong overloads, and unsafe straight-line completion/disposal. Existing group-loss negatives and awaited/using controls provided non-regression baselines.

The completed proof now requires:

- one exact local initialized directly by synchronous creation or by an exact Task/ValueTask await; only the framework Task/ValueTask `ConfigureAwait` chain is transparent;
- an exclusive semantic reference allowlist containing only the exact completion and cleanup receivers—aliasing, storing, returning, capturing, opaque calls, authored calls, and unrelated writer operations all fail closed;
- the exact bound zero-supplied-argument `CompleteAsync`, including the real optional-cancellation-token signature, completion-owned through a direct await when it returns Task/ValueTask;
- exactly one language-selected lexical cleanup, one exact protected-finally cleanup, or the narrow exact catch-cleanup-and-rethrow plus normal-cleanup state shape; asynchronous cleanup must also be directly awaited;
- one retained effect-group identity at creation, completion, and every cleanup point.

Straight-line `CompleteAsync; Dispose` is deliberately invalid because a completion exception skips cleanup. Lexical `using`/`await using` is valid because the compiler generates the cleanup `finally`. Explicit multi-writer cleanup uses nested acquisition and nested `try/finally`: the first writer is protected before the second creation starts, and each writer owns one cleanup level, so a later creation, completion, or cleanup exception cannot skip an already-live writer's cleanup. Sequential sibling disposals in one `finally` are not accepted because the first disposal could throw before the second runs. An exact catch with awaited cleanup and bare rethrow is accepted on the exceptional path; catch fallthrough is rejected as possible use-after-disposal.

The old multi-writer fixture no longer double-disposes `using` locals. It now uses ordinary locals in the nested exhaustive shape. The old plain explicit-disposal positive was corrected to negative. Field-carrier proof still owns its existing exclusive transfer contract; no local exemption was added for Batch. Batch's exact `StreamWriter(..., leaveOpen: true)` borrow and Completed/Aborted artifact owner remain Task 5 work, so conditional-empty publication stays visible rather than being blessed here.

A final fail-closed self-review added a lexical-using loop before completion. It was compile-clean and initially produced **1 failed / 30 passed**, proving the first implementation still borrowed a terminal after unsupported control flow. Restricting the supported lexical region to straight-line expression/local-declaration statements (plus the containing method's final return, whose language cleanup is guaranteed) made that case GREEN. The existing ordinary using fixture then exposed an over-restriction because `FixtureSource` appends its final `return Task.CompletedTask`; that focused regression was observed RED and corrected without permitting conditionals, loops, switches, or deferred bodies.

### Final evidence

- Focused publication matrix: **52 passed, 0 failed, 0 skipped**, 5 seconds.
- All R2-R6 fixtures: **160 passed, 0 failed, 0 skipped**, 13 seconds.
- Complete non-umbrella inventory matrix: **346 passed, 0 failed, 0 skipped**, 27 seconds.
- Clean no-incremental test-project build: **0 warnings, 0 errors**, 51.97 seconds.
- Cold registration umbrella: **1 passed**, cold inventory 64.730 seconds and cached call 0.040 milliseconds—within the established roughly 1m05 cold bound.
- Production-site umbrella: expected RED, **1 failed**, 1m06s. It still asserts `validation.IsValid`; no category is waived. Site discovery remains exactly **2,056 contextual / 381 physical / 570 physical per exact operation root**. Publication refusals remain visible at 3; all diagnostic counts are recorded in `/private/tmp/task2-r6-final-sites.trx`.

Final expected-RED diagnostic counts:

| Diagnostic | Count |
| --- | ---: |
| HOSTED_SITE_WORK_FRONTIER_MISSING | 158 |
| HOSTED_CALLBACK_OWNERSHIP_UNPROVEN | 11,645 |
| HOSTED_CALL_TARGET_UNRESOLVED | 124 |
| HOSTED_SITE_EFFECT_FRONTIER_MISSING | 60 |
| HOSTED_SITE_UNCLASSIFIED | 297 |
| HOSTED_DISPOSAL_TARGET_UNRESOLVED | 183 |
| HOSTED_ADMISSION_HANDLE_ESCAPE | 11 |
| HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE | 3 |
| HOSTED_EXTERNAL_OPERATION_UNCATALOGUED | 17 |
| HOSTED_SITE_UNCATALOGUED | 2,056 |

### Final self-review

- Read the complete scoped diff after formatting. The exact semantic local reference allowlist is shared with the field-carrier proof; cache identities and the cold production scan path were not duplicated or weakened.
- Conditional/unreachable terminals cannot be borrowed from deferred local functions or lambdas. Direct Task/ValueTask creation, completion, and cleanup joins are tested; blocking `GetResult`, discarded awaitables, custom/opaque uses, explicit completion arguments, wrong overloads/instances, cross-writer substitutions, duplicate terminals, and group loss all fail closed.
- The proof intentionally supports only lexical using, nested protected-finally state flow, and exact catch-cleanup-rethrow state flow. Unsupported control flow yields `HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE`; it cannot combine terminals from mutually exclusive paths.
- Lexical using accepts only a straight-line region and an optional final method return. The language-generated cleanup therefore still covers both normal return and exceptions, while a terminal after a loop or conditional cannot be borrowed as proof.
- `git diff --check` is clean. No production source, catalog row, dependency, suppression, plan/spec, or unrelated addendum was changed.

## Whole-rereview correction: exact graph identity and closed writer terminals

### Root cause and strict RED

The whole-rereview identity matrix was compile-clean and initially failed **3/3** tests. Graph state used documentation ID, source path, and span without authored assembly identity, so distinct methods from separate compilations could collapse into one lifecycle member, selected root, visited node, or cached admission lifetime. Target resolution also compared assembly symbols by object identity and then silently returned no target when multiple same-key first-party candidates existed.

The terminal-contract matrix was compile-clean and initially failed **4/7** controls: an optional extra completion parameter, the wrong Task result, a custom completion awaitable, and a custom cleanup awaitable were accepted. That proved the scanner was recognizing lookalikes rather than the closed production contract.

### Implementation and regression findings

One exact graph-member identity now carries authored AssemblyIdentity, compilation identity, syntax-tree identity, method documentation ID, and member span. Lifecycle membership, selected roots, overlap, recursion, admission lifetime, origins, and caches all use that identity. Consuming symbols resolve through authored assembly identity plus documentation ID. Exact source-symbol matches win; a unique retargeted authored body is accepted; ambiguous in-scope first-party candidates fail closed with HOSTED_CALL_TARGET_UNRESOLVED.

The first GREEN attempt exposed a real regression: bodyless interface declarations occupied the identity index and caused **10 failures out of 355** in interface, aggregate, and factory fixtures. Restricting the index to executable authored bodies and retaining the exact dependency-injection bound-implementation fallback when no executable candidate exists restored those cases; the focused interface group then passed **15/15**.

Writer completion is now the exact zero-or-one optional CancellationToken production slot returning Task<EncryptedBlobDescriptor>. Cleanup is the exact zero-argument IDisposable.Dispose returning void or IAsyncDisposable.DisposeAsync returning ValueTask slot, including the corresponding exact EncryptedBlobWriter implementation slot. Positive fixtures use those signatures. Optional-overload, wrong-result, Task cleanup, and custom-awaitable fixtures remain explicit negatives.

### Final correction verification

- Focused identity, terminal-contract, and interface regression set: **25 passed / 0 failed**.
- R2-R7 scanner matrix: **169 passed / 0 failed** in 14 seconds.
- Complete non-umbrella inventory suite: **355 passed / 0 failed** in 29 seconds.
- Clean no-incremental Release build: **0 warnings / 0 errors** in 49.99 seconds.
- Cold registration umbrella: **1 passed**; cold inventory **68.995 seconds**, cached call **0.042 milliseconds**. This remains near the established roughly 1m05 cold bound and improves the interim 73.590-second resolver.
- Production-site umbrella: expected RED, **1 failed**, about 1m09s. Physical discovery remains exactly **381 sites**. Exact graph identity exposes previously collapsed contexts, so contextual/root counts honestly rise to **2,152 contextual / 626 physical per exact operation root**. Evidence is recorded in `/private/tmp/task2-r7-final-sites.trx`.

Final expected-RED diagnostic counts after the correction:

| Diagnostic | Count |
| --- | ---: |
| HOSTED_SITE_WORK_FRONTIER_MISSING | 158 |
| HOSTED_CALLBACK_OWNERSHIP_UNPROVEN | 12,022 |
| HOSTED_CALL_TARGET_UNRESOLVED | 124 |
| HOSTED_SITE_EFFECT_FRONTIER_MISSING | 60 |
| HOSTED_SITE_UNCLASSIFIED | 297 |
| HOSTED_DISPOSAL_TARGET_UNRESOLVED | 185 |
| HOSTED_ADMISSION_HANDLE_ESCAPE | 11 |
| HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE | 6 |
| HOSTED_EXTERNAL_OPERATION_UNCATALOGUED | 22 |
| HOSTED_SITE_UNCATALOGUED | 2,152 |

### Correction self-review

- The three-compilation aliases remain distinct even with identical source path, span, and documentation ID. A lifecycle member cannot hide a same-key non-lifecycle caller, and ambiguous concrete first-party targets produce a diagnostic rather than a silent skip.
- Ambiguous resolution is never cached. The common unique-identity path retains one cached lookup, keeping the cold scanner close to its prior bound.
- Terminal acceptance is closed over the real production method and interface slots. Custom awaitables and merely compatible optional overloads cannot impersonate completion or cleanup.
- No physical production site disappeared. The contextual increase is the intended consequence of removing cross-compilation graph-node collapse, not a discovery regression.
- No production source, catalog row, dependency, suppression, plan/spec, ledger, or unrelated addendum was changed.

## Final semantic consolidation: exact DI bindings and carrier terminals

Read `task-2-graph-identity-rereview.md` before changing the scanner and verified both Important findings against the current code. The single architectural root cause was two parallel lossy semantic models bypassing the graph's exact identity and terminal contracts: dependency-injection fallback collapsed bindings to display strings and selected the first matching body, while carrier publication separately recognized terminals by method name and treated every unknown return as synchronous completion.

### Strict RED/GREEN evidence

All new fixtures compile through `R2Discover`, which first asserts that the entire multi-compilation fixture is compiler-error-free.

- The aliased DI matrix uses two service compilations with the same interface/implementation full names and distinct sensitive bodies, two extern aliases, both compilation-list orders, and repeated calls. Before implementation all **4/4** cases failed: one order lost both expected `File.Exists` sites, the other invented an extra one, and neither unmappable twin emitted the two required unresolved diagnostics. After exact binding/resolution, all **4/4** pass.
- The carrier matrix uses the production `CompleteAsync(CancellationToken = default) -> Task<EncryptedBlobDescriptor>` contract and inherited `MemoryStream` sync/async cleanup controls. Before implementation **6/7** cases failed: both valid Stream controls were rejected, while the optional completion overload, wrong Task result, custom awaitable, and wrong cleanup were accepted. The already-correct discarded-completion negative stayed GREEN. After consolidation all **7/7** pass.
- One older Batch carrier control still modeled `CompleteAsync` as a synchronous `void` lookalike. It correctly went RED under the closed contract and was repaired to use and await the real production-shaped Task result; its retained/lost-group pair now passes **2/2**.

### Implementation

- DI registrations now retain exact contract and implementation identities: authored assembly identity, authored compilation identity when provable, and full type identity. Interface fallback collects every eligible exact implementation and accepts only one graph member; it never selects the first ambiguity. Calls whose referenced assembly cannot be mapped uniquely emit `HOSTED_CALL_TARGET_UNRESOLVED` on every occurrence and are never entered into a success cache.
- Successful resolution caching is scoped to the exact consuming `Compilation` object, exact bound `IAssemblySymbol` object, and exact documentation method identity. This preserves distinct aliases and cache order. A separate negative cache applies only to method identities proved outside both the authored-member index and the registered-interface index; it cannot suppress an in-scope ambiguity.
- Exact authored members are indexed by assembly, compilation, and containing type. This keeps the stricter DI fallback bounded to the eligible implementation type rather than repeatedly scanning the whole authored graph.
- Carrier publication now reuses the closed writer predicates. Completion must bind the exact optional-token `Task<EncryptedBlobDescriptor>` slot and reach a proven Task/ValueTask join. Cleanup must bind the exact zero-argument `IDisposable.Dispose -> void` or `IAsyncDisposable.DisposeAsync -> ValueTask` slot for the writer's static type, including inherited `Stream` implementations. Unknown/custom returns are never presumed synchronous. The exact invoked and joined carrier completion plus language-selected cleanup still have to cover every readonly mapped field on one supported unconditional path and under the caller's retained effect group.

### Performance correction

The first correct reference-aware resolver regressed cold inventory from the established roughly 69-second bound to 94.894 seconds. Temporary counters printed only after the measured call showed **3,197,702** resolutions, **3,212,010** authored-map calls, **1,983,250** full mapping scans, and **2,449,987** definitely out-of-scope concrete misses. An exact early authored/registered-contract gate reduced map calls to 751,503 and scans to 18,562, but remained materially slower.

A disposable clone at exact pre-change commit `a5a5d230` provided a contemporaneous baseline of **71.817s cold / 0.041ms warm** on the same host. Adding the exact authored-type implementation index and exact positive resolution cache produced **70.068s cold / 0.038ms warm** with instrumentation, then **69.330s cold / 0.069ms warm** after all counters/output were removed. Performance therefore returned to or slightly improved upon the actual pre-change baseline without caching ambiguity.

### Final verification

- Scoped whitespace formatting: exit 0.
- Clean no-incremental Release test-project build: **0 warnings, 0 errors**, 53.18 seconds.
- Focused final R8 semantic controls: **11 passed, 0 failed**.
- Carrier/writer publication regression surface: **99 passed, 0 failed**.
- Complete R2-R8 scanner matrix: **180 passed, 0 failed**.
- Complete non-umbrella inventory matrix: **366 passed, 0 failed**, 31 seconds.
- Registration umbrella: **1 passed**; cold inventory **69.330 seconds**, cached warm call **0.069 milliseconds**.
- Production-site umbrella: expected RED, **1 failed**, 1m11s. It still asserts `validation.IsValid`; no category or catalog requirement is waived. Inventory and all prior diagnostic counts are unchanged: **2,152 contextual / 381 physical / 626 physical per exact operation root**. Evidence is in `/private/tmp/task2-r8-final-results/task2-r8-final-sites.trx`.

Final expected-RED diagnostic counts:

| Diagnostic | Count |
| --- | ---: |
| HOSTED_SITE_WORK_FRONTIER_MISSING | 158 |
| HOSTED_CALLBACK_OWNERSHIP_UNPROVEN | 12,022 |
| HOSTED_CALL_TARGET_UNRESOLVED | 124 |
| HOSTED_SITE_EFFECT_FRONTIER_MISSING | 60 |
| HOSTED_SITE_UNCLASSIFIED | 297 |
| HOSTED_DISPOSAL_TARGET_UNRESOLVED | 185 |
| HOSTED_ADMISSION_HANDLE_ESCAPE | 11 |
| HOSTED_SITE_PUBLICATION_REGION_INCOMPLETE | 6 |
| HOSTED_EXTERNAL_OPERATION_UNCATALOGUED | 22 |
| HOSTED_SITE_UNCATALOGUED | 2,152 |

### Final self-review

- Both list orders and repeated-call/cache-order controls are compile-clean. Exact known aliases cannot cross-bind; identical unmappable authored twins remain unresolved and uncached.
- Carrier validity is closed over the production completion result and interface/Stream cleanup slots. Optional overloads, wrong Task results, custom awaitables, wrong cleanup returns, and discarded asynchronous completion all fail closed.
- No temporary counter or timing output remains. `git diff --check` is clean. No production source, catalog row, suppression, dependency, addendum, ledger, or unrelated file changed.
