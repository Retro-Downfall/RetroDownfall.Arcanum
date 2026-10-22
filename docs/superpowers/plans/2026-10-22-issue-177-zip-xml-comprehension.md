# Comprehension ZIP+XML office formats implementation plan

> **For agentic workers:** Use the TDD workflow for each owned change (watch every new test fail for the expected reason before writing production code) and independent review before integration. Run the blank-line formatter on every new or edited `.cs` file.

**Goal:** Deliver issue #177 (under #164 and #161) to `general-enhancements` (the operator moved integration off `memory-enhancement` on 2026-10-22): `.docx`, `.xlsx`, `.pptx`, `.odt`, `.ods`, `.odp` and `.vsdx`, plus the text-only macro-enabled variants `.docm`, `.xlsm`, `.pptm` and `.vsdm`, become bounded untrusted DATA text through the #162 Comprehension seam. All four model-context paths are covered: retrieval, `attach_session_file`/refresh, per-turn reference, and durable pin. The feature stays behind `Features.Comprehension` and `Features.AttachmentRetrieval`, both default off.

**Architecture:** A closed Core format registry replaces the single container MIME. Each MIME carries a human-readable source-format label. One extraction host per parser family binds a concrete method group at construction, so per-format quarantine and the hosted-producer analysis both see a closed set of extractors. The extractors share a first-party bounded ZIP reader, hardened forward-only `XmlReader` settings, and OPC relationship and ODF manifest resolution. Each extractor is pure: bytes in, bounded sink out.

**Tech stack:** .NET 10, Native AOT, `System.IO.Compression.DeflateStream`, and `System.Xml.XmlReader`. No new package, no native dependency, no schema change, no new public configuration.

## Decisions (recorded once for the whole slice)

### D1. First-party bounded ZIP reader, not `ZipArchive`, not `DocumentFormat.OpenXml`

- **`DocumentFormat.OpenXml` was evaluated and declined by the operator (2026-10-22).**
  - **The probe passed.** Version 3.5.1 (MIT) passed `./scripts/verify-aot-il-warnings.sh osx-arm64` with zero SDK warnings and extracted real files in a standalone Native AOT binary.
  - **But it is costly.** It grew the shipping `osx-arm64` executable by 17.6 MB (140.0 to 157.6 MB, +12.6%).
  - **And it gives little.** It covers only OOXML (no ODF, no Visio). Its part reader neither filters hidden content, nor processes markup compatibility, nor bounds depth. Its typed element matching silently drops text under schema-invalid parents. So it would contribute only package navigation, which this reader and `OpcPackageRelationships` already provide.
  - **And it adds risk.** It would put a second ZIP parser (`System.IO.Packaging`) on the same untrusted bytes.
  - **Revisit if Arcanum ever writes Office files.**
- **Library policy across the epic.** The operator wants a parser per file type, using a library wherever it carries the hard part of a format. The survey of free and paid .NET options (2026-10-22) found none that does so for the ZIP+XML formats. The candidates for later slices are PdfPig (or the commercial Docotic.Pdf), MimeKitLite, Sylvan.Data.Excel and OpenMcdf.
- **`System.IO.Compression.ZipArchive` is also rejected for reading.**
  - Its constructor materializes every central-directory record as a `ZipArchiveEntry` before any caller-visible count check. That makes the entry-count bound an after-the-fact check.
  - It honours ZIP64 and Deflate64.
  - Its entry streams are `Stream`-typed values that the hosted-producer analysis cannot prove in-memory.
- **The replacement is a small reader over the already-read, bounded snapshot bytes.** It validates the end-of-central-directory record and walks the central directory under an entry-count bound *before* allocating per-entry state. It validates each local header against its central-directory twin and inflates only referenced parts. It uses `DeflateStream` over a `MemoryStream` slice, into a growable buffer bounded by bytes actually produced.
- **The AOT probe outcome** is recorded in the qualification section after `./scripts/verify-aot-il-warnings.sh` runs.

### D2. Registry and labels (Core)

`SessionAttachmentComprehensionPolicy` gains `SessionAttachmentComprehensionFormat(string MimeType, string FormatName)` entries. MIME constants are lowercase, so SQL compares against `lower(trim(...))`. MIME parameters are stripped before lookup and comparison is case-insensitive.

| MIME (registry constant, lowercase) | Extension | Source-format label | Parser family / host |
|---|---|---|---|
| `application/vnd.arcanum.comprehension-text` | `.arccomp` | Comprehension text container | text container (unchanged) |
| `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | `.docx` | Word document | Wordprocessing |
| `application/vnd.ms-word.document.macroenabled.12` | `.docm` | Word macro-enabled document (macros not read) | Wordprocessing |
| `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | `.xlsx` | Excel workbook | Spreadsheet |
| `application/vnd.ms-excel.sheet.macroenabled.12` | `.xlsm` | Excel macro-enabled workbook (macros not read) | Spreadsheet |
| `application/vnd.openxmlformats-officedocument.presentationml.presentation` | `.pptx` | PowerPoint presentation | Presentation |
| `application/vnd.ms-powerpoint.presentation.macroenabled.12` | `.pptm` | PowerPoint macro-enabled presentation (macros not read) | Presentation |
| `application/vnd.oasis.opendocument.text` | `.odt` | OpenDocument text | OpenDocument text |
| `application/vnd.oasis.opendocument.spreadsheet` | `.ods` | OpenDocument spreadsheet | OpenDocument spreadsheet |
| `application/vnd.oasis.opendocument.presentation` | `.odp` | OpenDocument presentation | OpenDocument presentation |
| `application/vnd.ms-visio.drawing` | `.vsdx` | Visio drawing | Visio |
| `application/vnd.ms-visio.drawing.macroenabled.12` | `.vsdm` | Visio macro-enabled drawing (macros not read) | Visio |

- **Macro-enabled variants are accepted text-only.** Parts are resolved through the same relationships. `vbaProject.bin`, `customUI`, ActiveX and every embedding are never opened or inflated.
- **Not registered:** templates (`.dotx`/`.dotm`/`.xltx`/`.xltm`/`.potx`/`.potm`/`.vstx`/`.vstm`/`.ott`/`.ots`/`.otp`), flat/OOo 1.x ODF, legacy binary, PDF, RTF, EPUB and MIME formats. They stay `Binary`/`NotEligible`.
- **Extension mapping only.** `AttachmentMimeDetector` maps the eleven extensions to the registry constants; `.docx`/`.xlsx`/`.pptx` switch to the constants too. There is still no ZIP content sniffing, so a renamed package (`.bin`, no extension) stays `application/octet-stream` and is ineligible.
- **Labels follow the record.** The framing becomes `UNTRUSTED DATA; <label>; layout not preserved` and `source-format: <label>; layout not preserved`, chosen by the record's or chunk's normalized MIME. The text container keeps its label, so existing pins stay valid.

### D3. Host per parser family

- **Static host fields.** `SessionAttachmentDocumentExtractors.For(string mimeType)` returns one of eight `static readonly SessionAttachmentDocumentExtractionHost` fields. Each is constructed with a concrete `new XExtractor().ExtractAsync` method group and the existing five-second deadline. There is no dictionary of delegates.
- **Macro variants share their base family's host.** Quarantine is per parser family.
- **Host selection order.** `SessionAttachmentTextExtractor.ExtractDocumentAsync` uses `host ?? SessionAttachmentDocumentExtractors.For(record.MimeType)`. An injected host still overrides, which existing tests rely on.
- **`Default` keeps its role.** It is now an alias of the text-container host.

### D4. Rejections and status mapping

Extractors throw `DocumentPackageRejectedException(DocumentPackageRejection)`, a closed enum. The host maps it to a fixed, code-owned reason. Every other exception still maps to `Failed("Document extraction failed.")`. Reasons never echo entry names, part names, XML text or framework messages.

| Rejection | Status | Reason |
|---|---|---|
| `Encrypted`: CFB/OLE2 signature (password-protected OOXML is an OLE2 `EncryptedPackage`), ZIP entry encryption flag, ODF `manifest:encryption-data` | `NotEligible` | `Encrypted document packages are not supported.` |
| `UnsupportedContainer`: ZIP64, multi-disk, compression method other than stored(0) or deflate(8) | `NotEligible` | `Document package uses an unsupported ZIP feature.` |
| `NotZip`: no valid end-of-central-directory record, signature mismatch, overlapping or out-of-range entries, local/central mismatch, size mismatch after inflation, malformed deflate | `Failed` | `Document package is not a valid ZIP container.` |
| `EntryBounds`: more than 8,192 central-directory entries | `Failed` | `Document package exceeds its entry bounds.` |
| `ExpansionBounds`: per-entry, aggregate or ratio bound | `Failed` | `Document package exceeds its expansion bounds.` |
| `UnsafeEntryName`: traversal, absolute, backslash, control character, drive prefix, over-long, or case-insensitive duplicate | `Failed` | `Document package contains an unsafe or duplicate entry name.` |
| `MissingPart`: a required relationship, manifest entry or part is absent | `Failed` | `Document package is missing a required part.` |
| `FormatMismatch`: wrong root element or ODF media type for the declared MIME | `Failed` | `Document package does not match its declared format.` |
| `MalformedXml`: any `XmlException`, including a prohibited DTD | `Failed` | `Document part is not well-formed XML or declares a DTD.` |
| `StructureBounds`: XML depth, relationship count, logical-cell, sheet, slide or page count, or style chain | `Failed` | `Document structure exceeds its bounds.` |

- **All terminal.** `NotEligible` and `Failed` are both terminal with `ShouldRetry: false`. They carry an extraction timestamp, so reconciliation does not re-queue them.
- **Empty output** is `NotEligible`, as in #162.

### D5. Bounds (code-owned, no public setting)

| Bound | Value |
|---|---|
| Input bytes | existing 1 MiB non-image ceiling (unchanged; intake already refuses larger non-image attachments) |
| Central-directory entries | 8,192, checked from the EOCD count before the walk; the walk must also end exactly at the CD end |
| Entry name | 1,024 bytes |
| Inflated bytes per entry | 32 MiB, enforced on bytes produced; produced must equal declared exactly |
| Inflated bytes per extraction (aggregate of opened parts) | 64 MiB |
| Inflation ratio | declared/compressed at most 200:1 once declared exceeds 1 MiB |
| XML | `DtdProcessing.Prohibit` (ODF manifest: `Ignore`, see D6), `XmlResolver = null`, `MaxCharactersFromEntities = 1024`, `MaxCharactersInDocument` = per-entry byte cap, comments and PIs ignored, depth at most 256, cancellation checked every 1,024 nodes |
| Relationships per `.rels` part, manifest entries | 8,192 |
| Logical cells processed (xlsx + ods) | 1,048,576 physical cell elements; repeats and spans never multiply text |
| ODF `text:s` count | clamped to 64 |
| Shared strings retained | 1,048,576 UTF-16 chars; a reference past retention stops extraction and marks the sink truncated |
| Visio master text retained | 262,144 chars, same overflow rule |
| Style inheritance chains | 32 hops, cycle-safe |
| Output | existing 262,144-char sink; deadline five seconds (unchanged) |

`ISessionAttachmentDocumentTextSink` gains `void MarkTruncated()` so a retention overflow is reported honestly as truncation.

### D6. DTD posture, and the one evidence-driven deviation

- **Content parts use `DtdProcessing.Prohibit`.** Any DOCTYPE in an OOXML part, `content.xml` or `styles.xml` fails deterministically.
- **The ODF `META-INF/manifest.xml` uses `DtdProcessing.Ignore`.** macOS `textutil` (TextEdit's export engine) writes `<!DOCTYPE manifest:manifest PUBLIC "-//OpenOffice.org//DTD Manifest 1.0//EN" "Manifest.dtd">` into current ODF packages, so `Prohibit` would reject every ODT that macOS produces, not a vanishingly rare vintage.
  - `Ignore` processes no declaration. No entity can be declared, expanded or resolved; any non-predefined entity reference fails the part. The resolver is still null.
  - The manifest's values are only compared against closed path and media-type sets.
- **A test proves both halves:**
  - an entity-bearing DOCTYPE in `content.xml` fails;
  - an internal-entity marker in the manifest also fails;
  - flipping either setting to `Parse` makes its test fail, because the marker text then appears.

### D7. Part resolution (no scanning)

**OOXML and Visio.**
- Start from `_rels/.rels`, following the `officeDocument` relationship (transitional and strict URIs) or, for Visio, `http://schemas.microsoft.com/visio/2010/relationships/document`.
- Then follow source-part relationship files `<dir>/_rels/<name>.rels` by type:
  - Word: `styles`, `footnotes`, `endnotes`.
  - Workbook: `worksheet` by `r:id` in `<sheets>` order; `sharedStrings`.
  - Presentation: `slide` by `r:id` in `<p:sldIdLst>` order.
  - Visio: `pages`, `masters`, then `page` and `master` by `r:id` in `pages.xml`/`masters.xml` order.
- Resolution rules:
  - `TargetMode="External"` is never followed.
  - Targets resolve relative to the source part's folder (or the package root for a leading `/`).
  - `.` and `..` segments are normalized, and escaping the root is rejected.
  - Lookup is case-insensitive per OPC. One percent-unescape is tried when the literal name is absent.
- `[Content_Types].xml` is not consulted. The declared format is checked by the main part's root element instead.

**ODF.**
- `META-INF/manifest.xml` must declare a root entry `/` whose media type equals the registry MIME (otherwise `FormatMismatch`), and must declare `content.xml`. `styles.xml` is read only when declared.
- Any `manifest:encryption-data` (or `manifest:keyinfo`) means `Encrypted`.
- The `mimetype` entry is not trusted.

**Never parsed:** unreferenced entries, smuggled parts, `docProps/*`, `meta.xml`, thumbnails, media, embeddings and `vbaProject.bin`.

### D8. Hidden-content posture (decided once for all fourteen #164 formats)

**Comprehension projects only text a reader sees in the document's primary rendered view. Every hidden-content class is excluded deliberately.** Some visible page furniture is also not projected; that is documented, and it never causes hidden text to be emitted.

| Class | Decision |
|---|---|
| Formatting-hidden text: OOXML `w:vanish`/`w:specVanish` (direct, character style or paragraph style, honouring `basedOn`, `w:docDefaults` and explicit `val="0"` overrides); ODF `text:display="none"` (automatic and common styles with parent chain), `text:hidden-text`, `text:hidden-paragraph` (unless `text:is-hidden="false"`), `text:section` with `text:display="none"` or `"condition"`; Visio `HideText` | excluded |
| Hidden structure: hidden/`veryHidden` sheets, hidden rows and columns (`hidden="1"`, ODF `table:visibility` `collapse`/`filter`, ODS table style `table:display="false"`), hidden slides (`show="0"`, ODP drawing-page `presentation:visibility="hidden"`), hidden shapes (`p:cNvPr`/`wp:docPr hidden="1"`, ODF `draw:display="none"`), shapes on an invisible Visio layer (conservative: any invisible layer hides the shape), ODS covered (merged-away) cells | excluded |
| Revisions | deletions (`w:del`, `w:delText`, `w:moveFrom`, ODF `text:tracked-changes`) excluded; insertions (`w:ins`, `w:moveTo`) included, because they are the current text |
| Comments and annotations (`comments.xml`, threaded comments, `office:annotation`, Visio comments) | excluded, never resolved |
| Speaker notes (`notesSlides`, `presentation:notes`) | excluded, never resolved |
| Non-rendered markup: field instructions (`w:instrText`), `mc:Fallback` (first `mc:Choice` only), alt text and descriptions (`svg:title`/`svg:desc`, `descr`), phonetic runs (`rPh`), index-source templates (`text:*-source`), Visio shape data and properties | excluded |
| Formulas | excluded; the cached value is included |
| Metadata (`docProps`, `meta.xml`), embedded objects, macros, external targets | excluded, never opened |
| Visible furniture not projected: Word headers and footers, ODF master-page and `styles.xml` content, slide layouts and masters, charts and SmartArt parts | not projected (documented limitation) |
| Visible text included: body, tables, text boxes, Word footnotes and endnotes (after the body, skipping separator notes), ODF notes inline, sheet names, cell values (raw cached values: no number formats, so dates appear as serials), slide and page text, Visio shape text and field values | included |
| Colour and size tricks (white-on-white, one-point text) | not detected; a residual risk recorded beside the HTML projection |

**Visio master inheritance.**
- An instance shape with its own `<Text>` element uses that text; an empty `<Text/>` overrides to empty.
- An instance with no `<Text>` emits the text of its master shape, once per instance, where it appears:
  - a top-level instance with `Master` and no `MasterShape` uses the master's single top-level shape;
  - a sub-shape uses `MasterShape` within its group's master.
- Master parts are never emitted standalone, so a stencil label is neither dropped nor duplicated per page.
- Master `HideText` hides inherited text.

### D9. Projection shape

- **Paragraphs and breaks.** Paragraphs and headings end with `\n`. Tabs become `\t`. Line breaks become `\n`.
- **Spreadsheets.** Each visible sheet starts with `Sheet: <name>\n`. Each non-empty visible row is its visible non-empty cells joined by `\t`, then `\n`.
- **Presentations.** `Slide <n>\n` precedes each visible slide, where `n` is the 1-based position in presentation order.
- **Visio.** `Page: <name>\n` precedes each page. Each shape's text is followed by `\n`.
- **ODF whitespace.** ODF text nodes collapse whitespace runs to one space (ODF white-space processing). `text:s`, `text:tab` and `text:line-break` are materialized.
- **Output is pure text.** No markup, attribute values other than sheet and page names, or part names reach the output.

### D10. Data and SQL

- **Set membership.** The single-MIME SQL becomes set membership over the registry:
  - `SessionAttachmentIndexRepository` reconciliation: `IN (@documentMime0..N)`;
  - `DivinationService` gate-off: `NOT IN (@documentMime0..N)`;
  - `TapestryStore`: an inline literal `IN ('…')` list built once from the code-owned registry, as `static readonly`, with a guard that no MIME contains `'`.
- **Prior rows become eligible.** Prior `.docx`/`.xlsx`/`.pptx` rows stored as `Binary`/`NotEligible` with a null extraction timestamp become re-queued when the gate is on, and that path is tested.
- **Tapestry exclusion is unchanged.** Document chunks stay out of the Tapestry until a later change adds support.

### D11. Uncovered issues fixed in this change

- `Production_graph_lane_is_closed_and_complete` omitted #162's `ProductionAttachmentExtractionRetainsItsExactAdmittedLifetime`.
- `DocumentationIssueReferenceTests` flags the tracker references #162 left in DESIGN §10.2.5 and §21.8.
- The Compendium `features.comprehension` descriptor text says Office is unavailable.
- `docs/Arcanum.Engineering.md` carries the stale "PDFs, Office files … remain unindexed" sentences.

## Work breakdown

1. **Stage 1A, plumbing (TDD).**
   - Core registry, labels and `MarkTruncated`.
   - Detector mappings.
   - The host registry (`SessionAttachmentDocumentExtractors`) and `ExtractDocumentAsync` dispatch.
   - Rejection mapping in the host.
   - The five label sites.
   - The three SQL sites with Theory rows.
   - The reconciliation test for prior OOXML rows.
   - Per-family hosts are wired to the text container until Stage 3 replaces them. No new MIME is registered until its extractor lands; Stage 1A adds the registry shape only.
2. **Stage 1B, container library (TDD, new files only).**
   - Under `Infrastructure/Weave/Comprehension/`: `BoundedZipPackage`, `BoundedXmlReader`, `OpcPackageRelationships`, `OpenDocumentManifest`, and `DocumentPackageRejectedException`/`DocumentPackageRejection`.
   - Extractors advance XML only through `BoundedXmlReader.Read()` and `SkipElement()`. `XmlReader` completes some text nodes lazily, so `Read()` forces `Value` inside its `XmlException` translation; a direct `Reader.Read()`, `Skip()` or early `Value` access would bypass it.
   - The test-side `ComprehensionPackageBuilder`, which builds valid packages and raw adversarial ZIP bytes.
   - Adversarial tests: entry count; ratio; understated length; overlapping entries; ZIP64; encryption flag; CFB; traversal; duplicate names; local/central mismatch; DTD and entities, including the manifest `Ignore` posture.
3. **Stage 2, extractors (parallel, TDD, new files only).**
   - OOXML Wordprocessing, Spreadsheet and Presentation.
   - OpenDocument text, spreadsheet and presentation.
   - Visio.
   - Each family gets synthetic fixture tests per D8 and D9, plus real-producer fixtures under `tests/RetroDownfall.Arcanum.Tests/TestData/Comprehension/` (`textutil` `.docx`/`.odt`, `openpyxl` `.xlsx`, `python-pptx` `.pptx`; ODS, ODP and VSDX are hand-built per spec).
4. **Stage 3, integration.**
   - Wire the registry MIMEs to the family hosts.
   - Four-path per-format Theories asserting each known source string and label.
   - Poison isolation: a ZIP-bomb `.docx` ahead of a healthy `.xlsx` through the real indexing service.
   - Format-mismatch and renamed-package tests.
   - Docs.
   - Uncovered issues (D11).
   - Hosted-producer reviewed-list entries, headroom, manifest regeneration and classification.
   - Admission input catalog and digest.
   - Formatter.
5. **Review.** Independent adversarial reviews (security bounds, format correctness against real files, TDD sensitivity, docs), then fix waves.
6. **Qualification and delivery.** Focused suites once; the AOT IL gate; local Native AOT run against Ollama; `--no-ff` merge into `general-enhancements`; push; mark #177 complete; fast-forward `main` after the Ollama run.

## Required test evidence (acceptance mapping)

- **Every format reaches model context.** Each of the eleven MIMEs reaches model context through retrieval, attach, refresh, per-turn reference and pin, asserting the known source string and the per-format label.
- **Eligibility is sensitive per format.** Removing one format from the registry makes its path tests fail.
- **Visio master inheritance.** The fixture shows a master label once per instance-bearing page, never on a page without an instance, and never standalone.
- **ODS repeat clamp.** `table:number-columns-repeated="16384"` (and rows repeated) over a few cells stays bounded on extracted length and on allocated bytes (`GC.GetAllocatedBytesForCurrentThread`); removing the clamp fails the test.
- **ZIP bombs.**
  - High ratio and high entry count are each `Failed` with `ShouldRetry: false`.
  - An understated declared length is bounded on bytes produced.
  - Removing the produced-bytes check, or the local-header and range validation, fails a test deterministically, with no hang.
- **XML entities.** External entity and billion-laughs parts fail deterministically; the `Parse` flip fails the marker test.
- **Encryption.** Encrypted ODF and OLE2-wrapped OOXML resolve `NotEligible` with no ciphertext in output.
- **Smuggled parts.** An unreferenced smuggled part is never parsed: its text is absent, and a deliberately malformed smuggled part does not fail extraction.
- **Traversal names.** A traversal entry name is rejected with a reason that does not contain the name.
- **Hidden content.** Per-family fixtures carry `w:vanish` (direct and style), a hidden sheet, row and column, speaker notes, a hidden slide, `text:tracked-changes`, `office:annotation`, comments and `HideText`.
- **Macros and embeddings.** Macro and embedding parts are never inflated: a referenced `vbaProject.bin` and embedding with a bomb-sized declaration do not fail the extraction. External relationships are never followed.
- **Healthy work proceeds.** A poison package does not block a healthy document queued behind it.

## Verification commands

```bash
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --filter "FullyQualifiedName~Comprehension|FullyQualifiedName~SessionAttachment|FullyQualifiedName~AttachmentMimeDetectorTests|FullyQualifiedName~TapestryStoreTests|FullyQualifiedName~SessionContextPinMaterializerTests" </dev/null
python3 scripts/align_csharp_blanklines.py --repo . --check <changed .cs files>
```

The full CI gate is waived for this change by the operator (2026-10-22). Delivery uses focused suites, the guard suites the change touches (hosted-producer lane, admission catalog, documentation, boundary tests), the macOS Native AOT IL gate, and a local Native AOT run against Ollama.
