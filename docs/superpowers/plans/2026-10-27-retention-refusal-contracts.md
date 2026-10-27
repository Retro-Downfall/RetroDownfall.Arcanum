# Retention refusal contracts — 2026-10-27

Scope: RV246, RV247 and RV254. Preserve aa995fb2 source and prove native/CLI behavioral RED before changing product code. RV255 guidance is separate pending work.

An existing Pending attachment must retain its row, bytes and derived rows and return the existing Data.Blocked apply contract, with its AttachmentInFlight blocker explanation. A truly missing target remains Data.NotFound. Refuse blockers and conflicts before inferring absence from an empty plan.

A Campaign-targeted reset of Entry, Attachments or Workspace is invalid input, not a runtime retention hold. Preserve the descriptive preview blocker, return Data.InvalidRequest/HTTP400 on apply, name Saga, Lexicon and Campaign summaries correctly, and stop CLI preview blockers/conflicts before confirmation or apply. Keep valid Campaign resets and no-op resets unchanged. Covenant's separate scoped lifecycle remains unchanged.

1. Add real SQLCipher Pending/missing/valid-target controls and unsupported/supported Campaign reset cases. Add CLI refused-preview cases with recording confirmation and HTTP handlers, including invalid and ordinary blockers/conflicts.
2. Execute RED on unchanged product bytes and record raw TRX/source hashes. Implement the smallest classification/order/message changes. No schema, provider, delegation or authority change.
3. Revert each causal fix separately and require the new behavior tests to fail. Restore exact hashes. Run complete owning native, API and CLI classes plus authority/boundary/source-catalog guards, normal build and owned formatting.
4. Independently review immutable candidate/evidence; only credit after combined integration. Final whole-campaign CI remains separate. Pause before installation/live Ollama/embedding tests.

RED evidence: baseline aa995 produced19 expected failures and4 controls across23 native/API/CLI cases; restored fix passes23/23. The first GREEN attempt retained16pass/4test-oracle failures because it called a general API mapper that does not own retention statuses. The corrected evidence uses real native-backed HTTP routes and keeps that failed attempt. Whole-product baseline3546paths and complete phase snapshots are retained.
