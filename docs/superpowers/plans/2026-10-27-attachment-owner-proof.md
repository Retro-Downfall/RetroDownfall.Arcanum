# Attachment owner-prefix proof implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline; no child agents.

**Goal:** Close the RV260 review defect: missing-file authority must bind its held row owner gate to the actual stored path owner prefix.

**Architecture:** Refuse mismatched or owner-only selectors without another gate or selector rewrite. Bound paths require the same 32-hex N-format Session GUID value; Pending paths require `_pending` and the exact ordinal accepted turn id. Require at least one leaf after the owner prefix; leave valid legacy tails untouched. Keep the fresh native proof inside each SQL retry, after connection acquisition, and all native root/parent/conditional-delete lifetime fences.

**Tech stack:** .NET 10, repository SQLCipher, real filesystem gates and encrypted blobs.

**Spec:** Root's review of sealed 354bfd3132bb8a15dcaf482435f4eed09eac16f7; canonical on-disk layout and owner-gate contracts in Arcanum.DESIGN.md and SessionAttachmentStore. Parent seal remains immutable. This discovered review defect carries zero extra original-finding credit.

## RED and acceptance

- [x] Native cross-Session and cross-Pending-turn selectors, new/legacy tails: retain the row while the actual target owner gate is held; restore original topology and prove exact plaintext readability.
- [x] Native owner-only Bound/Pending selectors: missing owner directories are uncertainty, never a file leaf.
- [x] Native Bound/Pending namespace mismatches: retain without crossing another owner's gate.
- [ ] Preserve uppercase N-format GUID value equivalence, exact Pending identity, existing valid legacy tails and real missing-leaf deletion.

Expanded native replay also covers a Pending case alias whose actual directory casing matches the path while its exact gate key differs; namespace tests keep the same second turn segment so the marker check is independently necessary. Source-bound unchanged-parent replay: nine genuine row-loss failures / six controls, exact full restoration.

## Implementation and delivery

- [ ] Bind row-to-path owner prefixes before native root/parent admission in each retry, with no second arbitrary gate and no selector rewriting.
- [ ] Update owning DESIGN; no schema, SQL, public port or acquisition-count changes.
- [ ] Complete affected owning/native/source classes, normal zero-warning build and owned styles.
- [ ] Causal owner-binding/shape/identity checks with exact full tracked+untracked source restoration; post-restore owning checks.
- [ ] Explicit-path commit; descendant and full-base immutable diffs, snapshots, raw RED/GREEN/causal receipts, and independent review packet.
