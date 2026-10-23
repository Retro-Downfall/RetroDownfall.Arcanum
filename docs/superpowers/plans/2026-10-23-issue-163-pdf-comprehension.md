# Comprehension PDF text-layer extraction implementation plan

> **For agentic workers:** TDD for every owned change (watch each new test fail for the expected reason first), mutation checks for every rule and bound, independent adversarial review before integration, blank-line formatter on every edited `.cs` file.

**Goal:** deliver issue #163 (under #161) to `general-enhancements`. A PDF with a text layer becomes bounded, untrusted DATA text with page provenance through the Comprehension seam, on all four model-context paths: retrieval, `attach_session_file`/refresh, per-turn reference and durable pin. The feature stays behind `Features.Comprehension` and `Features.AttachmentRetrieval`, both default off.

**Architecture:** PdfPig parses PDFs **only in a separate process**: the shipping `arcanum` binary re-launched in a hidden worker mode. That worker runs under the existing `CappedChildProcessRunner`, with code-owned memory, CPU and wall-clock caps, a required filesystem sandbox, no network on macOS, and PDF bytes delivered over stdin. The host side reaches the worker only through one reviewed interface. A crash, hang or memory blow-up becomes a bounded `Failed` attachment, never a dead or stalled server.

## Decisions

### D1. Dependency: PdfPig, pinned

- **Package:** `PdfPig` (Apache-2.0, fully managed, no native assets), pinned to the nightly `0.1.17-alpha-202610200037-ad829`.
- **Why that version:** stable `0.1.16` dies with an uncatchable stack overflow on tiny cyclic files (a 583-byte reference loop, a 1 KB XObject cycle). The nightly turns all of them into exceptions. Move to the first stable release that contains those fixes when it ships.
- **AOT probe (2026-10-22):**
  - `./scripts/verify-aot-il-warnings.sh osx-arm64` passed for both versions, with zero warnings naming PdfPig.
  - Shipping executable cost: about +6.9 MB, mostly embedded CMap and font-metric resources.
  - A native AOT smoke on real and hostile PDFs matched the JIT behaviour.
- **Library survey:** commercial Aspose.PDF fails Native AOT. Aspose.PDF.FOSS is AOT-clean but alpha. Docotic.Pdf is the paid fallback if PdfPig ever fails the bar (decision memo in the #177 plan).
- **Isolation regardless of version:** the probe measured unbounded memory (a 20 KB PDF reached 9 GB), no cancellation, and a superlinear word extractor. Isolation is therefore mandatory (operator decision, 2026-10-22).

### D2. Worker process

- **Hidden mode:** `arcanum __comprehension-worker pdf`.
  - Dispatched in `Program.Main` before configuration, installation probing or System.CommandLine, next to `SandboxExecHelper.TryHandle`.
  - Valid only when the entry assembly is the Cli or the DevHost, which both dispatch it: the same capability rule as `IsBrokerCapableHost`.
  - Not advertised in help, completion or the command map. It is documented in DESIGN beside the sandbox broker.
- **Input:** stdin carries exactly the snapshot bytes. The worker reads at most the 1 MiB non-image intake ceiling plus one byte, and refuses more.
- **Output:** UTF-8 on stdout in a versioned frame. The host treats a missing or malformed frame, a truncated stream or any non-zero exit as `Failed`.
  - Header line: `ARCANUM-COMPREHENSION/1 <status>`.
  - Body: page texts separated by U+000C. U+000C and NUL are scrubbed from page text.
  - Trailer: `END <utf16-length> <page-count>`.
- **Exit codes:** a small code-owned space that avoids 126 and anything at or above 128.
- **Worker-side bounds** (defence in depth under the OS limits):
  - pages at most 2,000;
  - letters at most 250,000 per page and 2,000,000 per document;
  - output at most 262,144 UTF-16 characters, surrogate-safe;
  - a self-deadline below the host's wall clock.
- **Extraction:** a content-order join of each page's letters, first-party `PdfPageText`: PdfPig's `ContentOrderTextExtractor` rules plus word-break, ligature and right-to-left changes (see the review fix wave below). The nearest-neighbour word extractor is superlinear, so it is never used.
- **Process limits (code-owned, not configuration; what enforces each differs by platform):**
  - memory 768 MB (macOS footprint monitor, Windows Job Object);
  - CPU 15 s (`ulimit -t` on macOS; a Job Object on Windows, where a CPU kill has no stable exit code, so it reads as `PDF extraction failed.` and the wall clock is the reliable bound);
  - wall clock 10 s, including cold start;
  - one process: macOS has no process-count rlimit, so the jail's profile denies `process-fork`; Windows uses the Job Object's active-process limit;
  - 64 file descriptors on macOS (`ulimit -n`); Windows has no equivalent and applies none;
  - stdout cap 2 MiB (enough for 262,144 characters in worst-case UTF-8, plus the frame); stderr cap small.
- **Sandbox:**
  - `RequireAppliedFilesystemJail = true`, never escaped. The `workspace_check` precedent applies, and `AllowUnsandboxedToolChildren` is not honoured.
  - Read-execute root: only the binary's directory. Read-write: none, and no write anywhere (see items 12 to 14 of the review fix wave below).
  - macOS: a deny-network profile variant for this worker, which also denies `process-fork` and confines directory listing and file metadata to its roots (see the review fix wave below).
  - Windows: the AppContainer has no capabilities, so no network.
- **Environment:** a new `ChildProcessEnvironmentProfile.DocumentWorker` that clears the environment entirely (on Windows alone `SystemRoot` and `windir` are read back from the host); the jail supplies `TMPDIR`, a private directory the worker can read but not write.
- **Executable identity:** the file identity of `Environment.ProcessPath` is captured at host start and revalidated in `preStartValidation`. If the binary was replaced while the host runs, the worker is refused.
- **Unavailable environment:** if the jail is unavailable, the limits cannot be applied, or the binary is not launchable, the result is terminal `Failed` with the code-owned reason "PDF extraction is unavailable on this host." Retrying would only respawn; the operator re-indexes after fixing the host.

### D3. Seam

- **Sibling host:** a new isolated host sits beside the in-process host, behind one abstraction that `SessionAttachmentDocumentExtractors.For` returns. The purity contract of in-process extractors is unchanged.
- **How the isolated host runs a document:**
  1. Bounded family slot: the indexer takes Deferred immediately; interactive paths keep the bounded wait.
  2. Authenticated snapshot read under the caller's admission.
  3. Await `IDocumentExtractionWorker.RunAsync` **inline**: no `Task.Run`, no quarantine, because the runner kills the process tree.
- **The worker interface is the reviewed seam:** one Vocabulary entry as `ProviderCall`. The runner and parser are not traversed by the hosted-producer analysis.
- **Indexer:** isolated extraction runs inside `workLease.TryBeginExternalEffectGroup`. A refusal is `DeferredForMaintenance`. The worker call is linked to the lease's `MaintenanceRevocation`, so maintenance kills the worker and the document returns as `Deferred` (durably `Pending`, retried) instead of blocking the drain. **Delivered differently under the production gate: see Delivery notes, decision 6.**
- **Registry:** `application/pdf`, label "PDF document", `.pdf` mapped by the existing detector. The `%PDF-` magic already detects it.

### D4. Policies (from #163 and the operator)

- **Invisible text** (#163 decision): every text-showing run is extracted in content-stream order, with no visibility filter. The materialization label states it: `UNTRUSTED DATA; PDF document; layout not preserved; may include text a reader would not see`. A white-on-white or off-page marker must be extracted and retrievable.
- **Encryption** (operator, 2026-10-22): a PDF that opens with the empty user password (permission-only restrictions) is extracted. A PDF needing a real password is terminal `Failed`, "Password-protected PDFs are not supported." No guessing, no stripping, no prompt.
- **Image-only:** no extractable glyph on any page resolves `NotEligible` (empty output), the defined input for later OCR.
- **Malformed input:** truncated or malformed PDFs and broken cross-reference tables are terminal `Failed` with code-owned reasons, using strict (non-lenient) parsing. No parser exception type, file path or text reaches a reason.
- **Active content:** JavaScript, launch actions, embedded files, URIs and forms are never executed, resolved, followed or emitted. The worker has no network and no write access at all.
- **Page provenance:**
  - The sink gains `BeginPage(int)`, and the extraction result carries page marks.
  - The indexer stores the page of `CharacterStart` and of `CharacterEnd - 1` in `StartLine`/`EndLine`, for formats whose range unit is pages (PDF only in this slice). The pipeline fingerprint becomes `comprehension-v2` for those formats.
  - Retrieval renders "pages" for them.
  - The text also carries `[PDF page <n>]` before each page with text (first delivered as `Page <n>`; see the review fix wave below), so attach, reference and pin show pages too.

### D5. Docs, gates and delivery

- **Docs:** DESIGN §10.2.5, §21.4, §21.5 and §21.8 (worker, bounds, sandbox, policies, provenance); the child-stdin paragraph and runner sections; API, Design.Human, Compendium, README and the descriptor text.
- **Gates:**
  - hosted-producer analysis (Vocabulary seam, effect group, manifest, headroom);
  - admission catalog and digest;
  - the two benchmark `packages.lock.json` files (new package);
  - the command-map and CLI surface (hidden mode stays out);
  - `ChildProcessSecretScrubCallSiteTests`;
  - full suites;
  - the AOT IL gate.
- **Qualification:** a local Native AOT run against Ollama with real PDFs, which proves the worker re-launches the real AOT binary under the real sandbox.
- **Delivery:** `--no-ff` merge into `general-enhancements`, push, close #163, fast-forward `main` after the Ollama run.

## Delivery notes

Recorded at the documentation pass from the four stage reports and a re-read of the code on `claude/163-docs`. DESIGN §10.2.5, §11.7, §11.15, §13.1, §21.4, §21.5 and §21.8 carry the resulting behaviour; this section records why.

### Stage-2 decisions made without a spec

1. **One abstract host base.** `SessionAttachmentDocumentHost` (private-protected constructor, abstract `ExtractAsync`, the shared `BusyReason`, `DefaultSlotWait`, `Failed` and `Deferred`) is what `SessionAttachmentDocumentExtractors.For` returns. The in-process host keeps its parse as a non-virtual `ExtractInProcessAsync`; the isolated host adds `ExtractIsolatedAsync(..., revocation)`. Interactive callers go through the abstraction. The indexer names each host by its concrete type, so the worker call is reachable only from inside the effect group and the hosted-producer analysis can tell the two paths apart.
2. **Page headings.** The isolated host projects each page that has text as `[PDF page <n>]` (first delivered as `Page <n>`), a line break, the text, a line break, with `BeginPage(n)` called before the heading. The heading makes pages visible on every path (attach, refresh, reference, pin) as well as in the stored `StartLine`/`EndLine` columns. Retrieval always renders `pages A-B`, never `page N`.
3. **Whitespace-only pages emit nothing.** A page with no non-whitespace character gets no heading, and its number is never reused, so the surviving numbers are the document's own page positions.
4. **Five-second snapshot read.** Once the family slot is held, the authenticated snapshot read has its own 5 s budget (`DefaultReadTimeout`); the worker has its own 10 s wall clock. An overrun is terminal `Failed` with its own fixed reason.
5. **PDF reasons live in the format-generic host.** The password, malformed and bounds reasons sit in `SessionAttachmentIsolatedDocumentHost`, not in the PDF-specific code. If a second isolated format ever arrives they move.
6. **Maintenance ruling (operator).** The spec said maintenance kills the worker and the document returns `Deferred`. Under the production admission gate that link cannot fire: the gate deliberately does not cancel a lease whose effect group is open and delivers revocation only when the group releases. The link is kept, since it works for any lease that does signal. The operator accepted the consequence and chose to document it, over changing the gate contract or shortening the wall clock below the checkpoint: a drain landing during an in-flight PDF worker (at most 10 s after a snapshot read of at most 5 s) can outlast the 5 s stage-one checkpoint and report the retryable `Grimoire.WorkDrainTimeout`; no new worker starts once maintenance is pending (the effect group is refused before anything is read, and maintenance that arrives during the snapshot read is caught by a check just before the worker would start); and when the group closes with maintenance pending the document stands down as `DeferredForMaintenance`, with no attempt spent and no index generation written. A real-gate test pins this. The other two options were an interruptible effect group (a gate contract change) and a wall clock under 5 s.
7. **Managed-host behaviour.** The worker is sized for the Native AOT binary. A managed apphost (DevHost, `dotnet run`) gets a cleared environment, so it cannot find a Homebrew-installed runtime; the production profile grants no `/opt/homebrew`; the managed runtime needs more than 64 descriptors; and Homebrew's compression library links Homebrew brotli, so under the jail Flate content can decode to nothing and the PDF reads as `NoText`. Delivered first as a worker that failed closed, usually with `PDF extraction failed.`; since the review fix wave a host the runtime does not report as Native AOT has no worker launch at all, starts no process, and ends every PDF as `PDF extraction is unavailable on this host.`. Re-checked in the documentation pass: the built Cli apphost started with no `DOTNET_ROOT` exits 131 ("You must install .NET to run this application"). Only the test vehicle (`UseExecutableForTests`) grants extra roots and 256 descriptors. The AOT behaviour was checked with a stand-alone AOT PdfPig probe binary under the real jail, deny-network, a cleared environment and 16 descriptors, not through the worker mode itself, so the local Native AOT qualification run stays required.

### Where delivery departs from the issue text

- **Cycles.** There is no first-party visited-object set. Termination rests on PdfPig's nesting ceiling (pinned at 256), strict parsing, and the pinned nightly turning its stack overflows into exceptions. A cyclic page tree yields zero pages (`NoText`); a self-invoking form or a reference chain past the ceiling is `Malformed`. Reproduced in the documentation pass: stable 0.1.16 ends the process with an uncatchable stack overflow (exit 134) on `cyclic-ref-chain.pdf` and `cyclic-xobject-ab.pdf`; the pinned nightly raises exceptions.
- **Malformed cross-reference table.** PdfPig rebuilds a broken cross-reference table by scanning even with lenient parsing off, so `bad-startxref.pdf` and `bad-xref-offsets.pdf` are extracted. A table it cannot rebuild is `Malformed`: a cross-reference stream whose entries are garbled (the objects sit inside an object stream, so a scan finds nothing), as are an unreadable trailer, a missing `/Root`, truncation, a non-PDF, and structure the strict parser refuses (a form that invokes itself, a reference chain or content nesting past the ceiling).
- **Empty owner password.** PdfPig also tries the empty string as the owner password, so a file with a real user password and an empty owner password opens. Pinned by the `*-owner-empty` fixtures; closing it needs a PdfPig change or reading the encryption dictionary here.
- **Decode damage looks like a scan.** A stream PdfPig cannot decode silently yields no text, so it resolves `NotEligible`, not `Failed`.
- **Frame.** The truncated flag travels in the status token (`extracted-truncated`) rather than as a separate field.
- **Self-deadline.** The worker's own deadline exit (code 5) maps to the deadline reason, not the generic failure.
- **Output cap.** The 2 MiB cap in D2 is the runner's total, split evenly between the streams, so stdout holds 1 MiB, still above the largest legal frame (786,688 bytes: three bytes per UTF-16 unit of the 262,144 ceiling plus 256).
- **Added beyond the brief.** A `DenyNetwork` child is refused when the jail is not applied; the sink caps page marks at 2,000 and truncates rather than misattributing; a pages-unit extraction without a mark at offset 0 is terminal `Failed` (`Document page provenance is unavailable.`); pages-unit formats use pipeline fingerprint `comprehension-v2`; `ChildProcessEnvironmentScrubber` throws for a profile with no case of its own.
- **Dependency record.** Package id `PdfPig`, Apache-2.0, version `0.1.17-alpha-202610200037-ad829`, referenced by Infrastructure only; probe result in D1.

### Checked by hand in the documentation pass

The built Cli apphost run as `__comprehension-worker pdf` with `DOTNET_ROOT` set wrote the expected frames for `multi-page.pdf` (`extracted`, four pages, the second empty), `image-only.pdf` and `whitespace-only.pdf` (`no-text`), `encrypted-rc4-password.pdf` (`password-required`), `garbage.pdf` (`malformed`) and `encrypted-rc4-empty.pdf` (`extracted`), each with exit 0 and empty stderr; extra, wrong or missing arguments exit 2. A shell shows that a `set -m` job keeps the shell's stdin while an unmonitored background job reads nothing.

### Review fix wave

Recorded at the second documentation pass, after the review of the delivered slice. Each item changes behaviour stated above; DESIGN §10.20.3, §11.7, §11.15, §13.7, §21.5 and §21.8 carry the result.

1. **Page headings.** Pages are headed `[PDF page <n>]`. The bracketed form is not how a document prints its own page numbers, so a footer reading `Page 1` drawn first on PDF page 3 is page text under `[PDF page 3]`, never the page's provenance.
2. **Text join.** `PdfPageText` replaces PdfPig's `ContentOrderTextExtractor` call with a port of its join and three changes: a space when a letter starts a word space or more to the left of where the previous one started (a float, a right-to-left table row, a run drawn over another); the Latin ligature code points U+FB00 to U+FB06 spelled out; and right-to-left text written in visual order put back in logical order run by run. Limits: no paragraph direction (the order of whole runs in a mixed line is the stream's), Arabic presentation forms not folded, mirrored bracket glyphs may come out as the other bracket. Lines break with `\n`.
3. **Certificate-protected files.** PdfPig reads every `/Encrypt` dictionary as a Standard-handler one, so a public-key file failed to open as `Malformed`. A failed open whose bytes carry an `/Encrypt` name is now `PasswordRequired` (the reason text is unchanged and the kind enum keeps five members); a page failure is never reclassified that way.
4. **Undefined references.** A page whose `/Contents` (or one array element) refers to an undefined object is a blank page, as the null object. After the first such refusal the extractor walks the page tree once, replaces each such reference with an empty stream and reads the page again; any other failure, and a second one, stands.
5. **Maintenance.** The gate cancels the lease's revocation only when an open group closes, so the host also asks the lease's pending-maintenance flag just before it would start the worker. Maintenance that arrives during the snapshot read now starts no worker; a verdict that returns while maintenance is pending is discarded and the document is read again.
6. **Frame bytes.** The runner hands stdout back raw (`rawStandardOutput`: same per-stream cap, never spilled, nothing decoded), so the frame check runs on the exact bytes the worker wrote. A UTF-16 stream, a UTF-8 byte-order mark and an invalid byte are protocol failures, where a text reader had accepted all three.
7. **Per-platform limits.** The limits are no longer described as one list. macOS: footprint monitor, `ulimit -t`, `ulimit -n`, and `(deny process-fork)` for the one-process rule, because `setrlimit` cannot bound a process count there. Windows: Job Object memory, CPU time and active-process count; no descriptor limit; a CPU kill is not attributed and reads as the generic failure.
8. **File-size ceiling.** The worker asks the runner for 1 MiB on any one file it writes (`ulimit -f`, `RLIMIT_FSIZE`), which fails closed; a write past it raises SIGXFSZ, which the worker maps to the resource-limits reason. It bounds each file, not how many, and Windows has no equivalent. Item 12 below then denies every write, which leaves the ceiling as defence in depth.
9. **Listing and metadata.** `ConfineMetadataToRoots` drops the macOS profile's global directory-listing and file-metadata grants and adds back only the metadata of each root's ancestors, the root directory's own entry (every process start needs it) and the `/bin/sh` selector link. What a worker still sees is the listing of `/` and its roots' ancestors.
10. **Windows environment.** `DocumentWorker` still clears the environment, but on Windows `SystemRoot` and `windir` are read back from the host, because Windows cannot start the process without its system directory.
11. **Non-AOT hosts.** A host the runtime does not report as Native AOT has no launch: no process is started and every PDF ends `Failed` with `PDF extraction is unavailable on this host.`.
12. **Nowhere to write.** `DenyFileWrites` (a request only the worker makes) leaves the macOS profile with no `file-write*`, socket-creation, `file-clone` or `file-link` grant on any root, the run's private temp directory included, and with `(allow file-write-data (literal "/dev/null"))` in place of write access to `/dev`; the Windows AppContainer is granted the temp directory read-only. The jail refuses a request that sets it and names a read-write root. A per-file size ceiling does not bound how many files a worker creates (before this change a stand-in created 22,266 files of 1 MiB in 3 s), so the ceiling stays only as defence in depth. Neither the Native AOT binary nor a managed apphost needs any device write.
13. **Object-id paths.** `ConfineMetadataToRoots` also ends the macOS profile with `(deny syscall-unix (syscall-number SYS_fsgetpath SYS_fsgetpath_ext))`. No file operation mediates those two calls, so a worker given a volume id and any object id could name any file on the volume and sweep millions of ids in seconds, which the listing and metadata denial had been meant to prevent. Denying only one of the two is not enough. The Native AOT binary and the managed apphost still extract every fixture under it. What stays visible to a confined worker, none of it file content: the listing of `/`, the metadata of its roots' ancestors, mount-point names (`getfsstat`, which dyld's own system profile allows) and, because `process*` and `sysctl*` stay allowed, other processes' executable paths and their argument vectors and environments (`proc_pidpath`, `KERN_PROCARGS2`). Narrow denials of `kern.procargs2` by `sysctl-name` did not close that channel, and the process-information operations closed only part of it, so no partial rule was shipped.
14. **Forged headings.** The heading is in-band, so the host writes every `[PDF page` inside a page's own text as `\[PDF page`; only its own lines read `[PDF page <n>]`. Chunk page ranges never depended on the text, since they come from the sink's marks.

### Left for the gate stage and later

- Admission input catalog and digest, hosted-producer manifest and headroom (four `Pdf/` sources from stage 1 and five Infrastructure files from stage 2).
- Closing the process-information channel of item 13 needs the profile's `(allow sysctl*)` replaced by an allowlist of the sysctls the runtime reads, which is a larger change with its own risk of breaking the worker on another macOS version.
- Windows: the read-only temp grant of item 12 and its test have not been run on Windows, and the per-run AppContainer profile's own storage folder has not been checked for a write the worker could still make.
- The Windows route (broker double hop, the Windows-only tests) has not been run on Windows. Both Windows lanes in `.github/workflows/ci.yml` select `CappedChildProcessRunnerStandardInputTests`; the worker tests drive POSIX stand-ins and are not selected there.
- Optional `Arcanum.ConstraintInventory.json` entries (64 KiB stdin chunk, 2 s writer grace, worker limits).
- Worker stderr is discarded and nothing is logged; the registry is static and has no logger.
- `UseExecutableForTests` and `UseSubstituteForTests` are test-only seams in production types.
- Real-producer PDFs pad every line to 80 columns, so about 53 such pages reach the 262,144 ceiling; trimming trailing whitespace is a later decision.
- Move to the first stable PdfPig that carries the cycle fixes.
