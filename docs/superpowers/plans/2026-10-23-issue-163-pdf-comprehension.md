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
  - Valid only when the entry assembly is the Cli: the same capability rule as `IsBrokerCapableHost`.
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
- **Extraction:** `ContentOrderTextExtractor` per page. The nearest-neighbour word extractor is superlinear, so it is never used.
- **Process limits (code-owned, not configuration):**
  - footprint memory 768 MB (macOS monitor, Windows Job Object);
  - CPU 15 s;
  - wall clock 10 s, including cold start;
  - one process;
  - 64 file descriptors;
  - stdout cap 2 MiB (enough for 262,144 characters in worst-case UTF-8, plus the frame); stderr cap small.
- **Sandbox:**
  - `RequireAppliedFilesystemJail = true`, never escaped. The `workspace_check` precedent applies, and `AllowUnsandboxedToolChildren` is not honoured.
  - Read-execute root: only the binary's directory. Read-write: none beyond the per-run temp directory.
  - macOS: a deny-network profile variant for this worker.
  - Windows: the AppContainer has no capabilities, so no network.
- **Environment:** a new `ChildProcessEnvironmentProfile.DocumentWorker` that clears the environment entirely; the jail supplies `TMPDIR`.
- **Executable identity:** the file identity of `Environment.ProcessPath` is captured at host start and revalidated in `preStartValidation`. If the binary was replaced while the host runs, the worker is refused.
- **Unavailable environment:** if the jail is unavailable, the limits cannot be applied, or the binary is not launchable, the result is terminal `Failed` with the code-owned reason "PDF extraction is unavailable on this host." Retrying would only respawn; the operator re-indexes after fixing the host.

### D3. Seam

- **Sibling host:** a new isolated host sits beside the in-process host, behind one abstraction that `SessionAttachmentDocumentExtractors.For` returns. The purity contract of in-process extractors is unchanged.
- **How the isolated host runs a document:**
  1. Bounded family slot: the indexer takes Deferred immediately; interactive paths keep the bounded wait.
  2. Authenticated snapshot read under the caller's admission.
  3. Await `IDocumentExtractionWorker.RunAsync` **inline**: no `Task.Run`, no quarantine, because the runner kills the process tree.
- **The worker interface is the reviewed seam:** one Vocabulary entry as `ProviderCall`. The runner and parser are not traversed by the hosted-producer analysis.
- **Indexer:** isolated extraction runs inside `workLease.TryBeginExternalEffectGroup`. A refusal is `DeferredForMaintenance`. The worker call is linked to the lease's `MaintenanceRevocation`, so maintenance kills the worker and the document returns as `Deferred` (durably `Pending`, retried) instead of blocking the drain.
- **Registry:** `application/pdf`, label "PDF document", `.pdf` mapped by the existing detector. The `%PDF-` magic already detects it.

### D4. Policies (from #163 and the operator)

- **Invisible text** (#163 decision): every text-showing run is extracted in content-stream order, with no visibility filter. The materialization label states it: `UNTRUSTED DATA; PDF document; layout not preserved; may include text a reader would not see`. A white-on-white or off-page marker must be extracted and retrievable.
- **Encryption** (operator, 2026-10-22): a PDF that opens with the empty user password (permission-only restrictions) is extracted. A PDF needing a real password is terminal `Failed`, "Password-protected PDFs are not supported." No guessing, no stripping, no prompt.
- **Image-only:** no extractable glyph on any page resolves `NotEligible` (empty output), the defined input for later OCR.
- **Malformed input:** truncated or malformed PDFs and broken cross-reference tables are terminal `Failed` with code-owned reasons, using strict (non-lenient) parsing. No parser exception type, file path or text reaches a reason.
- **Active content:** JavaScript, launch actions, embedded files, URIs and forms are never executed, resolved, followed or emitted. The worker has no network and no writable roots beyond its temp directory.
- **Page provenance:**
  - The sink gains `BeginPage(int)`, and the extraction result carries page marks.
  - The indexer stores the page of `CharacterStart` and of `CharacterEnd - 1` in `StartLine`/`EndLine`, for formats whose range unit is pages (PDF only in this slice). The pipeline fingerprint becomes `comprehension-v2` for those formats.
  - Retrieval renders "pages" for them.
  - The text also carries `Page <n>` before each page with text, so attach, reference and pin show pages too.

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
