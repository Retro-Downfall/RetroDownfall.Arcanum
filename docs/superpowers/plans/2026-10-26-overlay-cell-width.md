# RV-174 overlay display-cell width

Base: qualified remediation checkpoint9a6807bd. Preserve the memory-search branch. Scope: generic Command Center overlay frame measurement; human prompts already use display cells and remain controls. No terminal startup, host/provider, focus or authority changes.

Acceptance: actual Terminal.Gui view layout fits CJK/mixed-width content whenever terminal and existing frame cap allow; combining/emoji text does not manufacture extra display columns; ASCII behavior and terminal/absolute width caps remain. Resize recomputes width from display cells. Owning DESIGN explains the contract.

TDD sequence: add real view-tree tests and observe behavioral RED; change the generic width calculation to the existing TerminalCellMetrics; run complete overlay/layout/Unicode classes; revert the one behavior line to prove causal failure and restore exactly; normal build, owned formatting and documentation checks; commit immutable candidate for independent review. Native visual/runtime and full CI remain final gates. No install or live model call.

Adjacent defect confirmed by actual layout RED: generic prose was wrapped only on opening; shrinking the terminal left an 80-cell row in a 74-cell viewport. Retain the complete sanitized prose separately, measure and reflow it on width changes, update label and list surfaces, and discard it when switching to picker or human-prompt modes or hiding. Preserve the existing four-row per-preview cap and one-to-one selectable action rows. Verify narrowing, widening, mode changes, and selection before independent review.

The expanded first GREEN attempt exposed a separate shared primitive issue: the composed emoji woman-technologist measures four cells rather than the two-cell policy for joined emoji. Its actual view-tree evidence is preserved in `/private/tmp/arcanum-rv174-green/rv174-green.trx`. RV174 remains scoped to frame measurement and reflow using the existing primitive; use a single emoji control here and track compound-grapheme semantics as a distinct required adjacent repair. This does not close or waive that repair.
