# Retention recovery guidance — 2026-10-27

RV255 only. Current command/API source takes precedence over the October24 suggested remedy: delete-session, delete-attachment and reset-memory do not expose --dry-run; reset-memory obtains its preview automatically. Prune and factory-reset (including workspace scope) expose dry-run.

Select actionable refresh advice from the actual operation. Direct deletion reruns rebuild and revalidate the plan. Memory reset callers can obtain a fresh preview or rerun reset-memory with the same scope. Preserve dry-run advice for prune and factory/workspace reset. Shared deletion helpers invoked by prune must retain the outer prune operation's advice. Completion-bookkeeping failure says the data change was applied, not that every operation was pruning. Preserve all errorcodes, statuses, retry eligibility and mutation/checkpoint behavior. No new concepts/schema/SQL/config.

1. Native RED for stale expected-plan refusals across five operations, post-preview deletion races with retained rows/bytes, and refused completion across the same five ordinary operations. Prove existing prune/workspace guidance controls.
2. Make the smallest message-routing change and run GREEN. Reverse guidance and finalization fixes independently; retain executable mutants, rawTRX and exact source restoration.
3. Update DESIGN/API/Command Reference, run complete affected classes/boundaries/source inventories and owned static checks, seal and independently review. Integrate only with combined evidence. No installation or live provider/embedding tests.

Initial new cases produced8 message REDs and2 fixture-only failures: changing cached ByteLength did not alter the authoritative file-byte snapshot. Correcting the race to change real owned bytes produced10 genuine message REDs and12 controls (including10 existing recovery-guidance cases); all22 passed after the fix. Two explicit prune/shared-helper controls were added, and the adjacent obsolete two-store test comment now matches the three supported stores. All earlier evidence is retained.
