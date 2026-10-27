# Retention log inventory — 2026-10-27

RV239 and RV241, from aa995fb2. Factory capture and reconciliation currently traverse only cleanup quarantine, omitting retention quarantine; prune planning uses broad Win32 wildcards and accepts names its checkpoint/apply parser refuses. Source inspection also shows factory simple wildcards accept eight-letter non-date names, contradicting dated-log ownership.

Use the existing exact basename/stem/eight-ASCII-digit grammar consistently for planned log files. Preserve the existing numeric grammar (no new calendar parsing). Share the recognized cleanup/retention quarantine prefix list between factory capture and count/reconciliation, including mixed nested prefixes. Preserve strict no-symlink identity and scope checks; unknown directories/non-date files remain outside the log-deletion inventory. This does not claim to repair the separate RV248 request-path rollback/recovery state machine.

1. Native RED: prune plan/apply/replan preserve invalid names; factory inventory and supported V0 recovery cover both prefix families/mixed nesting and preserve unrelated directories; actual authenticated current factory route proves retention-quarantined bytes are included/deleted.
2. Minimal inventory/name-selection repair; GREEN; causal omissions for quarantine family and date grammar; exact full-source restoration.
3. Docs and complete owning/native filesystem/authority/source guard gates, normal build and owned static checks; seal and independent review. Full campaign CI remains separate; no installation/live calls.

Evidence so far:16native RED/2controls; original current-route additions were2 fixture403 failures (missing required loopback transport), retained and not product RED. With the existing authenticated LoopbackTransportPeer fixture, exact aa995 production produced18genuineRED/2controls: both actual current factory routes returned reconciled success while leaving quarantined bytes, then failed the file-absence assertion. Restored fix passes20/20. Two quarantine-link controls now verify strict containment across both recognized prefix families. No live external provider calls.
