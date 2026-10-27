# Campaign marker refusal diagnostics (RV192)

Base: cbe576994b788bed342b3cdcdb873762f3cbe06f. Scope is the dormant marker lifecycle and its existing full-reset coordinator crossings. No activation, Campaign-path writer, schema, wire, acquisition, authority, admission or recovery-state change.

1. Prove blocked restore/restart/full-reset native failures lose their already-held GUIDs/closed facts, and disposal logging leaks raw exception or can stop remaining cleanup. Authoritative initial RED: 12 failures/5 controls, full unchanged production snapshot retained.
2. Add closed JsonIgnore CampaignMarkerFailureDetails with explicit enum validation and existing GUIDs only. Preserve Error.IsNone/equality and exact source-generated JSON behavior.
3. Origin warnings use only fixed stage/reason and known GUIDs; no path, digest, exception object or collaborator text. Wrappers propagate only validated details; unknown/invalid details receive a fixed local fallback. Audit all 60 full-reset Inert sites against actual branches; ambiguous key/read/proof guards use coarse truthful reasons.
4. Test all four coordinator crossings. Additional genuine tests caught the nested JournalCleanupVector collapse, corrected as propagation-only; unrelated coordinator errors retain prior treatment. Preserve pre-journal cancellation, borrowed transaction rollback, recovery-owned None tokens, retained roots and terminal receipts.
5. Remove raw-exception release logging and prove second-root cleanup survives both disposer and logger failure. This adjacent privacy repair earns no additional original-finding credit.
6. Prove native controls, origin-log-once, both blocked observation arms/unknown blocker, Error wire compatibility and hostile collaborator text. Use bounded causal reversals with full exact source restoration, complete affected classes/guards, normal zero-warning build and scoped styles. Seal immutable source/diff/raw evidence for independent review; global Hosted/catalog and fullCI remain outside this task.

No unbuilt repair command is advertised. Preserve the installation profile, marker and journal; restore access to the original Campaign root/required key when applicable, then retry the existing recovery operation.

A post-GREEN native probe of malformed blocked observations found a diagnostic-only null dereference in both arms (2 RED/9 controls). Missing evidence now maps to Unknown while preserving ManualRecoveryRequired, complete refusal and no writes; it never supplies root authority.

A targeted warning probe also confirmed the shared refusal template mislabeled best-effort root-release failure (1 RED/3 controls). Its fixed release template now describes continued cleanup without claiming the operation was refused or requires recovery. The logger-throw containment is unchanged.
