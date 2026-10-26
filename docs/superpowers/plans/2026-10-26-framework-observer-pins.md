# Framework observer inventory reconciliation

Base `ff0833cf9be48fe2cba60791dc4988af7c650dc9`, branch `codex/review-framework-observer-pins`; preserve monitor and prior harness/grapheme branches. Own CheckerLimitPinTests, its DESIGN explanation and this plan. No runtime/scanner/synthetic-control or future Task10 policy change.

## Independently verified contract

- Existing TapestryProviderPolicy Process/ProcessAsync performs admitted read-only source/model validation within the classified SDK attempt. Only ChatClientFactory.CreateOpenAiCompatibleLease and EmbeddingGeneratorFactory.BuildOpenAiCompatibleGenerator construct it.
- SessionAttachmentStore.AttachmentReadStream wraps the already-open decrypted blob, verifies incremental length/digest and disposes that same stream. Only OpenReadAsync constructs it; no additional file/connection or authority is created.
- StreamingEncryptedBlobWriter is constructed only in private CreateWriterCoreAsync, shared by CreateWriterAsync/CreateNewWriterAsync. The core owns actual key/header/owner-only temporary stream setup and failure cleanup. The old forwarding-method pin is stale; publication admission/completion is unchanged.
- Keep the exact semantic type and construction-site census, every unknown-type refusal and all synthetic twins. The DESIGN count must equal the actual reviewed 21 types.

## TDD and delivery

1. Full unchanged-base CheckerLimitPinTests RED: 28 controls/1 failure proves the two missing types. Apply the reviewed two-entry patch (SHA256 5923a25af872fba8c9a9e261700d488a82e570ce4b7a1eadcbbc746326cbe4f9) only after independently reading actual types/sites/effects.
2. Preserve the next full-class RED: 28 controls/1 failure now reaches the stale writer construction site. Correct only its exact site and reason, then require the complete class GREEN before proceeding. Preserve any further independently confirmed revealed RED rather than masking it.
3. Remove each new entry and revert the writer site independently; each must fail the exact census. Restore all three owned paths byte for byte. Qualify complete relevant architecture/acquisition/runtime-registration/doc classes, normal zero-warning solution build and owned styles.
4. Commit explicit paths and seal exact parent/tree/source census, full-context diff, Git snapshots, commands/logs/TRX and restoration receipts for root review. The earlier monitor baseline failure remains visible until this closure qualifies and integrates; no broader Task10 completion claim.

No children, live provider/model/embedding calls, terminal initialization, installation/operator configuration, other-worktree edits, integration or push.
