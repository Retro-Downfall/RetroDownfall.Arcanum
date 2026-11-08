# Guild workspace fencing implementation plan

**Goal:** Deliver issue #157 under DESIGN §23.13: participating physical mutations cannot overlap a live permit, including after nominal lease expiry.

**Architecture:** Keep the existing Guild authority, admitted SQLCipher transactions, filesystem writers and LRO recovery. Add one physical identity resolver, a durable Guild lease participant, and one host mutation broker. Unsupported process and opaque MCP effect boundaries refuse before launch or dispatch. Guild execution activation remains owned by #150.

**Spec:** `Arcanum.DESIGN.md` §§23.4, 23.6, 23.13 and 23.15; issue #157 and parent #143.

## Constraints and decisions

- Use the existing Core lease types and thematic names. Public lease ids are selectors, never credentials.
- Preserve exact Quest ownership and stable installation-native operator ownership. Missing or forged execution context cannot become operator authority.
- Preserve physical no-follow, fingerprint, hard-link and identity checks. Unknown physical identity or case behavior refuses.
- Resolve the complete effect set before creating parents or staging. Disjoint existing siblings remain independent; shared missing parent creation conflicts.
- Use 15-minute nominal leases and 5-minute renewal through TimeProvider. Renewal is indefinite while held; rejected renewal is never silent reacquisition.
- Keep every permit until in-flight I/O, rollback and safe cleanup finish. Expiry stops new forward commits and never steals an active scope.
- Lock order follows DESIGN §23.4. Physical overlap admission is all-or-nothing and never waits on another holder under a Signet root gate or SQLite transaction.
- Use Guild V8 declared transitions; preserve healthy Core when the optional family is unavailable. Cleanup and reset retain unresolved mutation evidence.
- No public Guild execution activation, PR, full hosted matrix, arbitrary work ceiling or Ward approval flow.
- Integration base is `94ff3cca` on `guild-implementation`; direct delivery to `main` includes its prerequisite commits after local and focused hosted qualification.

## Review focus

1. Parent/root replacement, case aliases, nested registrations and missing ancestors cannot evade physical overlap.
2. Writer cancellation, expiry and late receipt completion cannot free an in-flight permit.
3. Temporary, backup, rollback and native Spell/CODEX mutations cannot bypass the complete plan.
4. Expired/restarted/retained contexts cannot mint new ownership or silently recover unknown process writers.
5. Corrupt/unavailable Guild schema and owner cleanup cannot erase a live writer's exclusion evidence.

## Tasks

- [x] Physical identity resolution: real filesystem RED/GREEN tests for sibling/ancestor conflicts, namespace replacement, no-follow refusal, actual case policy and missing-parent reservations. Implement `IWorkspaceLeaseIdentityResolver` and structured source-generated evidence.
- [x] Durable lease participant: SQLCipher RED/GREEN tests for V7→V8 transition, replay, stale epoch/revision, in-flight draining, schema isolation and owner erasure. Implement `GuildWorkspaceLeaseStore` with admitted transactions and compare-and-swap lifecycle methods.
- [x] Admission broker: RED/GREEN races for exact owner, atomic scope sets, expiry while paused, manual-time renewal/rejection, same-owner concurrent admission, copied permits and installation lock/recovery. Implement `IGuildWorkspaceLeaseService` plus trusted host bindings.
- [x] Managed writer integration: RED/GREEN tests proving conflict before any bytes, complete rename/delete/directory plans, and permit transfer across reversible commit/rollback/cleanup. Cover PhysicalFileSystemWriter, SandboxedFileIo, MultiFileCommitCoordinator, AtomicFile and independent native paths.
- [x] Process/opaque boundaries and lifecycle: tests prove unsupported paths refuse before process/transport start and qualified read-only/private-output checks retain their restrictions; maintenance/recovery must not steal ambiguous ownership.
- [x] Public contracts and documentation: implement the internal authenticated lease service; retain public route/tool activation with #150/#154/#158 and proposed versus implemented distinctions, update DESIGN/API/CLI/config owners and implementation ledger.
**Delivery gate:** targeted tests, local Native AOT and native SQLCipher verification, independent review, and the focused hosted `guild-workspace` profile must pass before direct integration to main. Hosted evidence and the delivered commit are recorded on [the workspace fencing issue](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/issues/157). No PR or full hosted matrix is part of this delivery.

## Verification

The focused profile runs the actual SQLCipher, physical identity, writer, host-lock, authority, process/MCP, lifecycle, architecture and documentation contracts on macOS and Windows. Its TRX validator requires completed execution and passing named fixtures; skipped mandatory cases cannot satisfy qualification. The evidence summary binds its selected classes/cases to the SHA-256 of the exact TRX bytes. The script removes stale evidence before execution.

Local qualification also exercises the generated persistence graph, cursor codec, host lock and physical creation/removal kernel under Native AOT with reflection serialization disabled. The native SQLCipher gate verifies asset provenance and runtime properties. These focused checks do not claim the full product matrix or Windows Native AOT qualification.

Review regressions cover retired registration holds, empty-root grants, missing-parent scope authority, recursive deletion identity adoption, replay after physical path replacement, exact native producer classification, and owner/filter-bound internal continuation. Internal continuations are unauthenticated query metadata; public authenticated watermarks remain with the operator-surface delivery.

```bash
./scripts/qualify-guild-workspace.sh
./scripts/verify-guild-aot.sh --rid osx-arm64
./scripts/verify-native-sqlcipher.sh --rid osx-arm64 --strict
python3 -m unittest discover -s scripts/tests
```
