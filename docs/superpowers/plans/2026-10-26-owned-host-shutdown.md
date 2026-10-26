# RV166 bounded owned-host shutdown

Base: `14524e67`. Root owns CommandCenterHost shutdown and its new offline tests; the attachment-monitor worker owns only its monitor. Preserve prior reviewed branches. Independent review precedes integration.

Command Center must attempt to quit only a host it started. That best-effort exit cleanup has its own five-second whole-request cancellation budget, covering credentials, headers and body. The UI run token is already cancelled on normal exit, so cleanup uses a fresh bounded token rather than inheriting immediate cancellation. Failure or expiry cannot replace the CLI's chosen exit code; a safe warning explains expiry. There is no process kill or change to the general API client's five-minute headers policy.

Use the existing registered TimeProvider as a required constructor dependency and the existing ManualTimerTimeProvider test helper. A fake HTTP handler drives the real ArcanumApiClient. First prove RED for held headers and held response body at the five-second boundary, with ownership/success/failure controls. Keep the clock seam behavior-neutral before RED. Then add the minimal deadline and timeout warning, prove timer cleanup and unchanged ownership/exit behavior, kill causal timeout/ownership mutations and restore exact source hashes.

Run complete affected classes and appropriate DI/documentation/deadline guards, normal zero-warning build and both house/Roslyn/diff checks. Update owning DESIGN. No real host service, installation, provider, Ollama, embedding, operator configuration or push action.
