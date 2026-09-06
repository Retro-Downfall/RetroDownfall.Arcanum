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
