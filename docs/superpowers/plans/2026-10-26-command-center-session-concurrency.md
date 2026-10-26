# Command Center session concurrency remediation

Continue the approved review remediation for RV-167, RV-170, RV-171 and RV-172. Base is the sealed RV166 candidate c94b5c57529840be07d75ffc8b04d48f1d625cea; its independent review is pending and the parent branch remains preserved. This batch owns Host, State, new concurrency tests and the owning DESIGN paragraph. The separate attachment-monitor worker owns no Host/State changes.

## Intended behavior

Session/transcript paging joins the existing action gate so queued operations resolve their cursor only when admitted. A chat submit claims its turn and publishes its cancellation source while holding that same gate briefly. If another action owns it, refuse promptly with a busy status while preserving composer text and staged attachments; do not queue a stale chat intent. Release the action gate before inference, so cancel, quit, human answers and safe actions remain usable. Keep the turn active through post-turn session refresh and cancellation-source cleanup. Serialize that refresh with session actions. Dispose only the completing turn's source, clear its slot by identity, then release turn admission. On completion, refresh the view without moving focus from an overlay or another pane; determine focus at UI callback execution.

## TDD and evidence

1. Deterministic HTTP gates through actual Host dispatch and SessionWorkspaceService reproduce concurrent paging and chat during a held session mutation. Assert request cursors/history and selected session, not merely task timing. Keep cancellation/failure controls.
2. Mechanically extract existing turn-source cleanup into a State overload without changing ordering; preserve exact extraction evidence. A gated disposable source proves admission remains closed until cleanup finishes, and identity tests prevent disposing another source. Then repair ordering/identity and integrate the Host caller.
3. Actual Terminal.Gui ANSI app/window plus fake HTTP stream proves completion preserves palette/sidebar/transcript focus and normal composer focus. Pump queued UI callbacks explicitly; no live host or provider.
4. Run each behavioral RED before its production repair, then complete affected classes. Run targeted causal mutations and restore every owned byte. Finish normal solution build, class census, scoped formatting and independent review on a frozen commit before integration.

No installation or live Ollama/embedding, operator-config edits, main promotion or push. Final whole-campaign CI/native gates remain required.
