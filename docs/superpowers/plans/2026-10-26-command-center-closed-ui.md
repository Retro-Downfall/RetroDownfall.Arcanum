# Command Center closed-UI submit cleanup

Adjacent lifecycle defect suspected while implementing the approved session concurrency batch. Preserve sealed f56bae1cac3dbc098787fd104008f39a33e93087 and use this new branch. Existing streaming/coalescer writes tolerate UI shutdown; inspect whether Host final/duplicate-submit writes still fault after channel completion. No extra original-finding credit.

Use actual Host/ChatRunner/ArcanumApiClient with controlled HTTP, one retained Host, completed UI channel and both normal/cancelled completion. Require no unobserved submit exception, no spurious host Error log and released turn/source ownership; duplicate submit remains a refusal preserving intent. Run RED against unchanged production, repair only best-effort UI delivery/caller-cancellation handling, mutate causal lines, then rerun complete affected classes/build/style. Review the frozen candidate before integration. No live provider/install or operator state changes.
