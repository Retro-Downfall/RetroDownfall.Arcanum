# Attachment missing-file proof implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline, with no child agents.

**Goal:** RV260 retains attachment recovery rows unless a current ordinary parent positively proves single-leaf absence.

**Architecture:** Reuse native no-follow directory/leaf helpers under the existing Session/Pending gate. Re-prove after connection acquisition inside every existing SQL retry; retain root and parent handles across the conditional DELETE. Preserve State/RelativePath guards and created-turn rollback behavior.

**Tech stack:** .NET 10, pinned native SQLCipher, platform no-follow handles.

**Spec:** Approved `/private/tmp/arcanum-rv260-absence-proof-preflight.md` and `-design-review.md`; base 93868bd427d242acf6f3d627eb458043b9c02b57.

## Constraints and review focus

No schema/authority API or generic filesystem redesign. Missing, refused or inaccessible parents, links, malformed selectors, changed root identities and moved parents remain uncertain. Canonical configured-root aliases and legacy nested rows remain supported. No path/exception content in new warnings. This is a sequential proof, not an OS-wide mutation lease.

## Task 1: Native absence proof

- [x] Adopt and verify root's seven native behavioral REDs and two controls, with exact unchanged runtime/source snapshots.
- [x] Add private missing-only arm to `SessionAttachmentStore.Lifecycle.cs`; hold ordinary root/parent handles and verify current exact kernel positions/identities.
- [x] GREEN the nine original cases and preserve conditional promotion/created rollback controls.

## Task 2: Boundary controls

- [x] Prove restoration before the gated decision, every-retry freshness, root replacement, parent moves, missing/refused metadata, exact selectors and cancellation with native fixtures.
- [x] Check fixed warning privacy and update the acquisition inventory's exact owning arity only if changed.
- [x] Update the owning attachment design contract.

Root approved the independently reproduced NUL-metadata availability correction: normalize only expected path-construction ArgumentException/NotSupportedException into a fixed InvalidOperationException in ResolveUnderRoot; no exception inner. Native end-to-end controls must prove the invalid row survives while a healthy missing row is still reconciled. AtomicCopyFileAsync remains exclusively owned by the separate RV268 worker.

The native undefined-state control also reproduced an absent-file delete under a numeric state outside Pending/Bound (one failure/one existing whitespace-gate control). Require a defined state before forming missing-file authority; default created-turn rollback behavior stays unchanged.

## Task 3: Qualification and delivery

- [ ] Freeze exact source; run complete actual affected classes, normal zero-warning solution build and owned style/source guards.
- [ ] Run causal mutants with executable bodies/TRX and exact full tracked+untracked source restoration; rebuild and focused post-restore gate.
- [ ] Commit explicit owned paths and seal immutable Git/archive/source/evidence packet for independent review. No integration/full-CI claim.
