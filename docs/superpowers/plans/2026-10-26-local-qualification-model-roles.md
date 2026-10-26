# Local qualification model roles

Date: 2026-10-26. Base: `1e71847ec5450ad0bddf764619e8c6dea5ee3eda`.

The user requires Qwen 3.8 27B for vision and Flint 40B for text, Perplexity and PDF workflows, followed by embedding qualification, after full exact-candidate CI and an explicit pause. This slice adds offline-tested model-role configuration to the existing opt-in Native AOT qualification harness. It does not run that harness, install anything, access Ollama, change the operator configuration, or claim PDF/embedding coverage.

Keep the existing `--model` single-model interface and default. Add optional `--text-model` and `--vision-model`; each explicit role overrides `--model` independently regardless of argument order. Reject blank role names before the managed harness starts. The wrapper supplies explicit resolved roles, so unrelated inherited role variables cannot select a model. The managed harness validates both installed tags only after its existing live opt-in, uses the text model for conversation/search/recall and the vision model for the image turn, and records both roles in its isolated provider configuration. A distinct text model is not advertised as vision-capable; a shared model appears once with vision and tools enabled.

TDD sequence:
1. Add executed Bash-wrapper behavioral tests using the existing fake dotnet fixture; prove missing role support and whitespace acceptance failures.
2. Add generated-configuration tests for distinct/shared roles; retain single-model behavior controls. Observe behavioral RED before implementation.
3. Implement narrowly, preserve exact qualification selection, credential isolation, receipt and cleanup checks. Add assertions on durable reply model identities where the existing transcript exposes them.
4. Run complete affected offline packaging classes, normal build and owned formatting; inspect causal role-routing/capability mutations and restore exactly. Independent review before integration.

Follow-on work remains explicit: PDF extraction and follow-up recall through Flint, then embedding qualification against the intended embedding model; all actual provider calls stay behind the user's requested pause. Full final CI and live results are separate evidence.
