# RV-143 current-tree preflight (2026-10-26)

Read-only inspection at controller `4236c3be` (166/272 integrated), while the prior API diagnostics candidate qualifies independently. No implementation, branch, or ledger completion credit yet.

The original finding still exists: private SagaEndpoints.PurgeEveryLabeledSagaAsync reads 128 identity positions per page, but retains every CovenantSensitivePurgeResult in a growing List, including unlabeled and purged results, and accumulates progress that the sole caller never uses. The caller only uses AllUnlabeled/IsBlocked/first blocker. Retaining only Purged/Blocked still grows with protected corpus size, so do not implement that incomplete suggestion.

A closely related issue is visible but needs a behavioral reproduction: a successful protected first page followed by a later Result failure returns before MarkProtectedWhenPurged, losing protected-response marking after partial purge. Preserve the first-page safety fact even if a later page fails or throws.

Proposed route change: make the private walk return Result (no public DTO), mark each successful page's response protected immediately through existing CovenantSensitiveDeletion.MarkProtectedWhenPurged, and stop on the current page's BlockedError before reading another page. Discard each page outcome after handling it. Final ordinary DeleteAll remains unchanged, including its final transactional label guard; keep ordered seek, invalid GUID cursor advancement, cancellation and mixed-owner refusal. No aggregate result or progress is consumed outside the walk.

Behavioral TDD: existing real SagaBulkDeleteWalkTests cover stable ordering/deletions/tied timestamps/invalid GUID cursor. Add real route characterization with a tracking purger and bounded WeakReference survival across many pages (the test's tracking metadata is excluded; old result retention must fail, constant page retention must pass), plus partial-first-page protected mark then later failure; all-unlabeled success, blocked page and request cancellation controls. Use production fixture when feasible; no live embedding call.

The same review finding notes one sensitivity-label query per target. Current coordinator ResolveLabelsAsync still calls TryReadLabelAsync for each target. Investigate an existing batch-read capability before adding one. If absent, prefer bounded per-kind ReadLabelsAsync(SensitiveArtifactKind,IReadOnlyList<Guid>,CancellationToken) on the existing ledger with an exact native indexed parameterized IN lookup, same Materialize decoder and failure semantics. Preserve point rereads after kernel non-erasure, authoritative lease/kernel checks and source label order. A compatible interface fallback may delegate to existing point reads for custom ledgers; the production ledger must use one actual SQL reader per kind/page. Prove real native statement/VM work, corrupt label refusal, known missing labels, mixed kinds, bound errors and cancellation. No broad SQL authority or new schema.

This preflight does not authorize closing the finding before both retained-state and label-read work are addressed and reviewed. Current API diagnostics candidate is frozen; edit only after sealing it on its own branch.

## Behavioral evidence and selected batch contract

The real mapped-route tests reproduced four failures with one control: all 256 tracked early-page results remained alive at the last page for unlabeled, purged and mixed corpora; a later page failure lost the protected-response tuple after an earlier protected purge. Production source snapshots matched `4236c3be`. The route now handles protection/blockers per page and returns a payload-free Result. All five route checks passed in the subsequent run.

The native query regression then showed 128 actual SELECTs for 128 unlabelled targets, while the single-target control passed. Both exercised the existing production coordinator and actual native ledger under SqliteStatementTrace; the first test compilation was rejected by the xUnit Assert.Single analyzer and is not behavioral evidence. No label reader or coordinator implementation had changed before this RED.

Selected API: an overload on the existing ledger, ReadLabelsAsync(kind, artifactIds, token), bounded at 256 same-kind identities, returning existing ArtifactSensitivityLabel records in input order and omitting absent labels. An empty set acquires no connection; an oversized set is refused before acquisition. The default interface lane preserves custom ledger point-read semantics, while the production override uses one native indexed query. The coordinator groups its bounded targets by kind and restores original target order for kernel dispatch; post-kernel point rereads remain unchanged. One code-owned read cap will also own the purge target cap. No new wire DTO or schema.

## Implementation checkpoint

The first owning gate passed 83 cases. Expanded native controls now prove one SELECT for 1, 128 and 256 targets, input-order and duplicate-input behavior, kind isolation, malformed-selected-label refusal without unrelated-row contamination, pre-acquisition cancellation, and zero acquisition for empty or oversized batches. A fixture initially used a lowercase Guid against the ledger's uppercase identity representation; its zero-row corruption attempt is excluded from behavioral evidence and corrected explicitly.

One implementation defect was found by a new behavioral regression: rekeying a mismatched custom-ledger label could classify the requested target as unlabeled. The test failed before the coordinator added exact kind/requested-identity/uniqueness checks; the repaired coordinator returns Covenant.IntegrityFailure before dispatch. The combined seven-class behavioral/source-inventory gate passed 174 cases with no failures or skips. Existing native erasure tests retain mixed dispositions, stale labels, original target order, and first-blocker stopping. No production path or schema changed; benchmark manifest and connection-acquisition inventories passed unchanged, so no count/catalog refresh is warranted.

Full final candidate qualification and independent review remain pending; no original finding credit yet.

The final causal reversion restored the original route and coordinator for eight controls: six expected failures reproduced page retention, lost protected-response marking, and 128/256 individual label queries, while two controls passed. Every owned C# file was then restored byte-for-byte before final qualification.
