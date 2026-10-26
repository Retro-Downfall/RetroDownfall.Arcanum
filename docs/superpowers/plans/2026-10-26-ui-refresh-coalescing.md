# RV173 queued UI refresh coalescing

Base: `c2de747d`; preserve the independently reviewed RV163 branch. Existing user authorization covers this original finding and necessary adjacent repairs. Root implements; a separate reviewer approves before integration.

The marshal callback queues asynchronously. Keep one mutable pending refresh for each consecutive refresh run until its UI callback claims it. Further refreshes widen that pending kind; a focus action closes the run and remains its own ordered one-shot callback. Claiming occurs before apply, so updates arriving during an apply produce at most one following refresh for that run. A failed hand-off releases its pending slot; the existing apply exception guard remains on the UI thread. Channel completion and cancellation preserve the existing nonblocking hand-off contract rather than waiting for an already-stopped UI.

Use deterministic channel-read/wait observation and an inert queued marshal to prove real RED: separately arriving refreshes while UI work is queued, widening, ordered focus barriers, refreshes during an active apply, and recovery from a throwing hand-off. Existing asynchronous apply-failure and cancellation controls remain. No real Terminal.Gui initialization, provider or installation work.

After minimal implementation, kill causal mutations for disabled pending reuse, lost widening, focus crossing and stuck hand-off state. Restore exact owned bytes. Run the complete owning/affected classes, zero-warning build, house/compact/Roslyn/diff gates and independent review. Update DESIGN in the same change. No public API, dependency, schema, configuration or authority changes.
