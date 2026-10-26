# RV163 partial terminal initialization cleanup

Base: qualified9a6807bd. Preserve the accounting branch. Only CommandCenterApp lifecycle, tests and owning DESIGN change.

The app must be owned by Run before initialization can fail. Once initialization may touch terminal modes, cleanup must attempt disposal and mode restoration independently, including when disposal throws. A factory failure before an application exists must not report touched terminal modes. Preserve StartFailed, TooSmall, CrashedAfterStart and normal exit semantics.

Tests use an uninitialized IApplication with recording initialization/disposal/restore delegates, never real Init or console writes. First expose only lifecycle-operation injection with the original combined create+init lifecycle still intact; this is behavior-preserving test scaffolding, not GREEN. Prove actual disposal/restore failures through Run. Then separate creation from initialization under the owning try/finally, prove GREEN and causal reversions, run complete lifecycle/viewport/termination classes and relevant source/AOT guards, normal build and owned formatting. Review an immutable candidate before integration. No installation or live models.

The attempted DispatchProxy fixture cannot implement Terminal.Gui's inaccessible IApplication setter. Its two TypeLoad failures are fixture failures, not RED. The replacement inert lifecycle fixture proves two missing-cleanup failures with five passing exit/factory controls. One prior test attempt was blocked by sandbox IPC and was terminated before retrying; neither that attempt nor the initial optional-parameter delegate compile error counts as behavioral evidence.
