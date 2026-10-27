# Covenant family reinitialize resume — RV189 and RV190

Base af1537d; existing authenticated transition and durable checkpoint contracts stay intact. This coordinator still has no production storage transition implementation or exposed start surface.

1. Prove every recorded phase from LocalArtifactsProcessed through ReopenedVerified skips both erasure kernels, while Planned/AdmissionClosed still erase before drop. Prove ReopenedVerified recovery retains reconciliation because publication and writer reopening remain uncheckpointed obligations. Keep never-started/corrupt-checkpoint controls.
2. Guard erasure and its checkpoint as one phase operation using the existing AdvanceAsync path; never skip unrecorded work or repeat recorded erasure. A readable recovery checkpoint always requests the existing resume owner; do not invent a new completed phase/version or mark publication proven by storage verification.
3. Verify smallest GREEN, causal reversal of each fix, exact source restoration, complete owning/coordinator/recovery/checkpoint/gate and architecture/doc tests, normal build and scoped formatting. Preserve exact source evidence and independent review before integration. Full CI remains separate; no install or live provider/embedding tests.
