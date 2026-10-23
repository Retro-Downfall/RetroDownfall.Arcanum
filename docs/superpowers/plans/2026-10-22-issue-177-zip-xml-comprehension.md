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
- **The replacement is a small reader over the already-read, bounded snapshot bytes.** It validates the end-of-central-directory record and walks the central directory under an entry-count bound *before* allocating per-entry state. It validates each local header against its central-directory twin and inflates only referenced parts. It uses `DeflateStream` over a `MemoryStream` slice, into one buffer allocated at the part's declared length plus a one-byte overflow probe, so no more than the declared length is ever produced or held.
- **The AOT probe outcome** is recorded in the qualification section after `./scripts/verify-aot-il-warnings.sh` runs.

### D2. Registry and labels (Core)

`SessionAttachmentComprehensionPolicy` gains `SessionAttachmentComprehensionFormat(string MimeType, string FormatName)` entries. MIME constants are lowercase and free of quotes and parameters. One normalisation, `NormalizeMimeType`, is used by every lookup and is spelled identically in SQL by `SessionAttachmentDocumentMimeSql.NormalizedExpression` (reconciliation, the gate-off retrieval pre-filter and the Tapestry exclusion): cut at the first `;`, trim only space, tab, CR and LF, fold ASCII case. The lookup compares the result ordinally, so a no-break space, another Unicode space or a non-ASCII letter makes a spelling a non-registry type in C# and in SQL alike.

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
- **Extension mapping only.** `AttachmentMimeDetector` maps the eleven extensions to the registry constants; `.docx`/`.xlsx`/`.pptx` switch to the constants too. There is still no ZIP or package sniffing, so a renamed package (`.bin`, no extension) stays `application/octet-stream` and is ineligible. The detector's existing image and PDF signature checks still run first, and an unrecognised name whose bytes begin with `ARCCOMP1\n` is still the text container. The mapping does not depend on the gate, so the detected type of a document file is the same with Comprehension off, and an operator's `security.allowedUploadMimeTypes` is matched against it either way (documented in the Compendium README).
- **Labels follow the record.** The framing becomes `UNTRUSTED DATA; <label>; layout not preserved` and `source-format: <label>; layout not preserved`, chosen by the record's or chunk's normalized MIME. The text container keeps its label, so existing pins stay valid.

### D3. Host per parser family

- **Static host fields.** `SessionAttachmentDocumentExtractors.For(string mimeType)` returns one of eight `static readonly SessionAttachmentDocumentExtractionHost` fields. Each is constructed with a concrete `new XExtractor().ExtractAsync` method group and the existing five-second deadline. There is no dictionary of delegates.
- **Macro variants share their base family's host.** Quarantine is per parser family.
- **Host selection order.** `SessionAttachmentTextExtractor.ExtractDocumentAsync` uses `host ?? SessionAttachmentDocumentExtractors.For(record.MimeType)`. An injected host still overrides, which existing tests rely on.
- **`Default` keeps its role.** It is now an alias of the text-container host.
- **Slot wait, then budget.** Each host has one extraction slot. Attach, refresh, per-turn reference and pin wait at most a code-owned 30 seconds for it (`DefaultSlotWait`); background indexing never waits, because it holds a Grimoire work lease, and takes the slot only when it is free (`waitForSlot: false`). The wait is not charged to the document. A slot not acquired in time or not free, or a quarantined family (refused at once, and callers already queued are released at the same moment), is the new transient status `Deferred` (`Document extraction is temporarily busy.`) having read and parsed nothing. The five-second budget starts only once the slot is held and covers the snapshot read and the parse; its expiry stays terminal `Failed` and quarantines the family until the parser ends. The first design charged slot queueing to the same clock; the review found that made a healthy document fail behind slow documents of its own family.
- **Deferral is retried, never terminal.** The indexing processor keeps a deferred document `Pending` with the busy reason, no extraction timestamp and `ShouldRetry`, so the existing five-second automatic retry applies without spending an attempt, and reconciliation, which re-queues pending rows, recovers it when that in-memory retry is lost. Attach, refresh, per-turn reference and pin return early on any non-`Extracted` result, before the inject-once acceptance and before anything durable is stamped; `attach_session_file` then tells the model `Not attached: ...` with the reason.

### D4. Rejections and status mapping

Extractors throw `DocumentPackageRejectedException(DocumentPackageRejection)`, a closed enum. The host maps it to a fixed, code-owned reason. Every other exception still maps to `Failed("Document extraction failed.")`. Reasons never echo entry names, part names, XML text or framework messages.

| Rejection | Status | Reason |
|---|---|---|
| `Encrypted`: CFB/OLE2 signature (password-protected OOXML is an OLE2 `EncryptedPackage`), ZIP entry encryption flag, ODF `manifest:encryption-data` | `NotEligible` | `Encrypted document packages are not supported.` |
| `UnsupportedContainer`: ZIP64, multi-disk, compression method other than stored(0) or deflate(8) | `NotEligible` | `Document package uses an unsupported ZIP feature.` |
| `NotZip`: no valid end-of-central-directory record, signature mismatch, overlapping or out-of-range entries, local/central mismatch, size mismatch after inflation, malformed deflate | `Failed` | `Document package is not a valid ZIP container.` |
| `EntryBounds`: more than 8,192 central-directory entries | `Failed` | `Document package exceeds its entry bounds.` |
| `ExpansionBounds`: per-entry, ratio or whole-extraction bound (D5) | `Failed` | `Document package exceeds its expansion bounds.` |
| `UnsafeEntryName`: traversal, absolute, backslash, control character, drive prefix, over-long, or case-insensitive duplicate | `Failed` | `Document package contains an unsafe or duplicate entry name.` |
| `MissingPart`: a required relationship, manifest entry or part is absent | `Failed` | `Document package is missing a required part.` |
| `FormatMismatch`: wrong root element or ODF media type for the declared MIME, or a structure that is ambiguous to a reader (below) | `Failed` | `Document package does not match its declared format.` |
| `MalformedXml`: any `XmlException`, including a prohibited DTD | `Failed` | `Document part is not well-formed XML or declares a DTD.` |
| `StructureBounds`: XML depth, relationship count, logical-cell, sheet, slide, page or master count, style count or chain, or the other structural bounds of D5 | `Failed` | `Document structure exceeds its bounds.` |

- **All terminal, except deferral.** `NotEligible` and `Failed` are both terminal with `ShouldRetry: false`. They carry an extraction timestamp, so reconciliation does not re-queue them. `Deferred` is the one transient outcome (D3).
- **`FormatMismatch` is also fail-closed ambiguity.** A package that leaves it open which content a reader would see is refused with the same fixed reason even when it is a genuine document: a second main-document relationship (Word and PowerPoint), or a Word styles, footnotes or endnotes relationship named twice; a worksheet part shared by two visible sheets, a slide part named by two `p:sldId` entries, a Visio page part named by two `Page` elements (hidden pages included) or a master part named by two `Master` elements; a Word style identifier, or a style's `basedOn`, given twice, or a note id defined twice in one notes part; a Word `w:pPr`, `w:rPr`, `w:tblPr`, `w:trPr`, `w:tcPr` or drawing-group `cNvPr` that is not first among its parent's children; a `mc:Choice` after a `mc:Fallback` or a second `mc:Fallback`; an ODF `content.xml` or `styles.xml` with the wrong root or an `office:body` that is not one body of the declared kind; an ODS `table:table-column` after a row. Earlier drafts of this table listed only a wrong root element or media type.
- **Empty output** is `NotEligible`, as in #162.

### D5. Bounds (code-owned, no public setting)

| Bound | Value |
|---|---|
| Input bytes | existing 1 MiB non-image ceiling (unchanged; intake already refuses larger non-image attachments) |
| Central-directory entries | 8,192, checked from the EOCD count before the walk; the walk must also end exactly at the CD end |
| Entry name | 1,024 bytes |
| Inflated bytes per entry | 32 MiB, enforced on bytes produced; produced must equal declared exactly |
| Inflated bytes per extraction (declared bytes of every part read, a re-read of the same part included) | at most 64 MiB and at most 200 times the length of the package handed to the extractor (never less than 1 MiB); a small package cannot fund many inflations of one part |
| Inflation ratio | declared/compressed at most 200:1 once declared exceeds 1 MiB |
| XML | `DtdProcessing.Prohibit` (ODF manifest: `Ignore`, see D6), `XmlResolver = null`, `MaxCharactersFromEntities = 1024`, `MaxCharactersInDocument` = per-entry byte cap, comments and PIs ignored, depth at most 256, cancellation checked every 1,024 nodes |
| Relationships per `.rels` part, manifest entries | 8,192 |
| Logical cells processed (xlsx + ods) | 1,048,576 physical cell elements; repeats and spans never multiply text |
| ODF `text:s` count | clamped to 64 |
| Shared strings retained | 1,048,576 UTF-16 chars; a reference past retention stops extraction and marks the sink truncated |
| Visio master text retained | 262,144 chars, same overflow rule |
| Style inheritance chains | 32 hops, cycle-safe |
| Sheets, slides, Visio pages, Visio masters | 4,096 each, `StructureBounds` (hidden sheets and pages count) |
| Spreadsheet columns and merges | 16,384 `col` elements per worksheet and per ODS sheet; 65,536 `mergeCell` elements per worksheet (unusable references included); a merged-cell lookup that steps over 16,777,216 range marks in one extraction; all `StructureBounds` |
| Word styles and notes | 8,192 styles (and 262,144 characters of style identifiers), field nesting 64 within one story (the body, a note, or a text box), 65,536 note references, all `StructureBounds` |
| ODF styles | 65,536 style definitions and `style:map` targets together, names at most 1,024 characters, `StructureBounds` |
| Visio retention | 8,192 retained master shapes; 512 characters of layer membership per shape and 65,536 in total (beyond that kept as unknown); 1,048,576 pending characters per top-level shape; layer index below 8,192 |
| Held text | 262,144 characters each for a Word drawing or out-of-order note, a PowerPoint slide, an ODF paragraph, table cell or note body, and the ODF notes in total; overflow marks the output truncated |
| Extraction slot wait | 30 seconds per attempt on attach, refresh, per-turn reference and pin (indexing does not wait), code-owned, not charged to the document; the five-second budget starts after the slot is acquired |
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
  - Each visible sheet, slide, Visio page and Visio master names a part of its own; a part named twice is `FormatMismatch` before it is read again.
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

**Comprehension projects only text a reader sees in the document's primary rendered view. Every hidden-content class is excluded deliberately.** Some visible page furniture is also not projected; that is documented, and it never causes hidden text to be emitted. Where the order of a part's markup could let text be read before the flag that hides it, the extractor either holds the text until the flag is known or refuses the package.

| Class | Decision |
|---|---|
| Formatting-hidden text: OOXML `w:vanish`/`w:specVanish` (direct, character style or paragraph style, honouring `basedOn`, `w:docDefaults` and explicit `val="0"` overrides; style levels are a union, so the result only hides more than Word's toggle rule); a Word table whose style has a conditional format (`w:tblStylePr`) that sets vanish hides the whole table; ODF `text:display="none"` (automatic and common styles with parent chain), `text:hidden-text`, `text:hidden-paragraph` (unless `text:is-hidden="false"`; it hides its paragraph even inside hidden text), `text:section` with `text:display="none"` or `"condition"`; Visio `HideText`. An ODF element's own style name resolves among automatic styles first and then common styles; `style:parent-style-name` and the common `Standard` paragraph style resolve among common styles only. A style name defined more than once in one table is judged definition by definition and hidden wins; a style is also hidden when a `style:map` target on it or on a style in its parent chain resolves hidden (conditions are not evaluated, and a map on a target is not followed) | excluded |
| Hidden spreadsheet structure: a workbook whose every `workbookView` is hidden or `veryHidden` (nothing is projected); hidden/`veryHidden` sheets and unknown sheet states (never read); hidden rows and columns (`hidden="1"`, ODF `table:visibility` `collapse`/`filter`, ODS table style `table:display="false"`); merged-away cells (xlsx `mergeCell` ranges except the top-left cell; ODS covered cells); an xlsx cell hidden or merged away by the row its own reference names as well as by its row element; ODS rows and cells beyond the sheet's last row and column (hidden rows and collapsed-group rows still occupy their index) | excluded |
| Hidden slides, shapes and drawings: hidden slides (`show="0"`, ODP drawing-page `presentation:visibility="hidden"`); PowerPoint shapes, groups, pictures, connectors and graphic frames whose non-visual `p:cNvPr` is `hidden="1"` wherever the marker sits relative to the shape's text and inside `mc:AlternateContent` wrappers (the slide's text is held until the slide ends); PowerPoint merged-away table cells; Word drawings whose `wp:docPr` or, for `wp:anchor`, own `hidden` attribute is on (the drawing's text is held until it ends, so `docPr` may follow the graphic); Word drawing text is reached only through the chain Word renders (drawing object, graphic data of a shape, group or canvas kind, shape or group, text box; or a picture or object, a VML shape, `v:textbox`); Word VML `visibility:hidden`/`display:none`; ODF `draw:display` `none` or `printer`, and shapes on a hidden `draw:layer` | excluded |
| Hidden Visio structure: shapes on an invisible layer (conservative: any invisible layer hides the shape and its sub-shapes), whether stated or inherited from the master shape; locally deleted shapes and guides; pages hidden from the interface (`UIVisibility`, no `Page:` header) | excluded |
| Visio group `DisplayMode` 0 (`visGrpDispModeNone`), stated or inherited: the group contributes none of its own text; its sub-shapes are projected unless hidden otherwise. An unreadable value or a formula with no cached value hides. Read on `Type='Group'` shapes only, as a direct cell | excluded |
| Revisions | deletions (`w:del`, `w:delText`, `w:moveFrom`, tracked-deleted table rows `w:trPr/w:del` and cells `w:tcPr/w:cellDel`, ODF `text:tracked-changes`) excluded; insertions (`w:ins`, `w:moveTo`, inserted rows and cells) included, because they are the current text; previous properties in `w:trPrChange`/`w:tcPrChange` are not current |
| Comments and annotations (`comments.xml`, threaded comments, `office:annotation`, Visio comments) | excluded, never resolved |
| Speaker notes (`notesSlides`, `presentation:notes`) | excluded, never resolved |
| Non-rendered markup: field instructions (`w:instrText`), the unselected `mc:AlternateContent` branch, alt text and descriptions (`svg:title`/`svg:desc`, `descr`, ODF `table:title`/`table:desc`), phonetic runs (`rPh`), index-source templates (`text:*-source`), ODF form controls (`office:forms`), PowerPoint extension lists and custom data, Visio shape data and properties, Word drawing and picture markup Word never renders, and PowerPoint graphic data of a kind other than a table, extension elements, foreign-namespace elements, and text bodies or tables outside a shape or a table cell | excluded |
| Markup compatibility | Word resolves `mc:AlternateContent` wherever it occurs: the first `mc:Choice` whose `Requires` prefixes (at most 16) all resolve to understood namespaces, otherwise the `mc:Fallback`; never both. A `mc:Choice` after the fallback, or a second fallback, is `FormatMismatch`. The emoji extension `w16se` is not understood, so its fallback (the character) is projected. Excel resolves it in every container it reads by the same rule (SpreadsheetML and the Excel `x12ac`, `x14`, `x14ac`, `x15` and `x15ac` extension namespaces are understood). PowerPoint projects the first `mc:Choice` only |
| ODF frames and foreign markup | a `draw:frame` projects only its first content child, and only a text box or table; foreign-namespace elements inside a paragraph's own content are transparent unless `office:process-content="false"`; elsewhere (and in ODF's own metadata, script and form namespaces) they are skipped with their content, so text in a block-level foreign element is treated as hidden; only text inside paragraph content is projected, so characters directly inside a block container (text box, table, row, cell, list, section, shape, group, image map) belong to no paragraph and are never projected |
| Formulas and cached content | formulas excluded; the cached value is included. A Word content control projects its cached `w:sdtContent` even when `w:dataBinding` binds it (the custom XML part is never opened) |
| Metadata (`docProps`, `meta.xml`), embedded objects, macros, external targets | excluded, never opened |
| Visible furniture and content not projected: Word headers and footers, the document background and anything beside `w:body`, ODF master-page and `styles.xml` content, slide layouts and masters, charts, SmartArt, Excel worksheet drawings and text boxes (ODS shapes are projected), PowerPoint equations, Word `w:altChunk` content (a body that is only an `altChunk` is empty and `NotEligible`), Word `w:sym` glyphs, automatic list and heading numbers | not projected (documented limitation) |
| Visible text included: body, tables, text boxes, Word equations, notes, sheet names, cell values (raw cached values: no number formats, so Excel dates appear as serials and ODS dates as ISO values), slide and page text, Visio shape text and field values | included |
| Word equations (`m:oMath`, `m:oMathPara`) | linear text of `m:t` and of any `w:t` in a math run; attribute glyphs (`m:chr`, `m:begChr`, `m:endChr`) and `*Pr` elements are not projected; parts are concatenated without separators; math runs honour run hiding; a phantom with `m:show` off, `m:degHide` degrees and `m:subHide`/`m:supHide` limits are excluded, properties first; each equation in an `m:oMathPara` is on its own line |
| Footnotes and endnotes | Word: after the body, only when a shown reference (not deleted, not hidden by any style level, not in field code, not in a hidden drawing) reaches the note, once each in first-reference order, footnotes then endnotes; separator, continuation, unreferenced and hidden-only notes are never projected; at most 65,536 references; a related notes part is always read in full. ODF: note bodies after all other text, footnotes then endnotes, each class in document order; a note in a hidden paragraph or hidden text is dropped. No format projects the reference mark or note number |
| Colour and size tricks (white-on-white, one-point text) | not detected; a residual risk recorded beside the HTML projection |

**Visio master inheritance.**
- An instance shape with its own `<Text>` element uses that text; an empty `<Text/>` overrides to empty.
- An instance with no `<Text>` emits the text of its master shape, once per instance, where it appears:
  - a top-level instance with `Master` and no `MasterShape` inherits from the master's top-level shape only when the master has exactly one top-level shape that is neither deleted nor a guide; with several it inherits nothing for itself;
  - a sub-shape uses `MasterShape` within its group's master;
  - a shape with neither attribute inherits nothing, even inside an instantiated group.
- Text, `HideText`, group `DisplayMode` and `LayerMember` are inherited when the instance does not state its own; a hiding master's text is not retained, so an overriding instance shows only its own text.
- Master parts are never emitted standalone, so a stencil label is neither dropped nor duplicated per page.

### D9. Projection shape

- **Paragraphs and breaks.** Paragraphs and headings end with `\n`. Tabs become `\t`. Line breaks become `\n`.
- **Tables (Word, PowerPoint, ODT, ODP).** A row is one line: its cells joined by `\t`, then `\n`. A cell's paragraphs and line breaks are joined by single spaces, and a tab or line break inside a cell becomes a space. A nested table flattens into its cell. Empty cells keep their place; merged-away and covered cells are not cells; a PowerPoint row with no visible cell writes no line; an ODF row drops trailing empty cells, writes nothing when wholly empty, and is capped at 262,144 characters.
- **Text boxes (Word).** A text box is separated from the paragraph text around it by a newline on each side (none where the line is empty or the box projects nothing; a single space inside a table cell).
- **Spreadsheets.** Each visible sheet that has a non-empty row starts with `Sheet: <name>\n`; an empty visible sheet writes nothing. Each non-empty visible row is its visible non-empty cells joined by `\t`, then `\n`. A line break, tab or other control character (C0, DEL, C1, U+2028, U+2029) in a cell or a sheet name becomes one space (CRLF counts once, except in an ODS sheet name, which turns each into its own space). Excel text is decoded as `ST_Xstring` (`_xHHHH_` escapes). An ODS float, percentage or currency cell projects its raw `office:value`, a date its `office:date-value`, a time its `office:time-value`, a boolean `TRUE`/`FALSE`, never its cached `text:p`; a malformed value or one over 128 characters projects nothing.
- **Presentations.** `Slide <n>\n` precedes each visible slide, where `n` is the 1-based position in presentation order (a hidden slide still counts).
- **Visio.** `Page: <name>\n` precedes each page not hidden from the interface. Each shape's text is followed by `\n`.
- **ODF whitespace.** ODF text nodes collapse whitespace runs to one space (ODF white-space processing). `text:s`, `text:tab` and `text:line-break` are materialized.
- **Output is pure text.** No markup, attribute values other than sheet and page names (and ODS value attributes), or part names reach the output.
- **Truncation is announced.** When extraction was truncated, or the per-item byte ceiling cut the text, the attach, refresh, per-turn reference and pin notices read `UNTRUSTED DATA; <label>; layout not preserved; truncated at the extraction limit` and a pin reports `Truncated`; retrieval chunks are unchanged.

### D10. Data and SQL

- **Set membership.** The single-MIME SQL becomes set membership over the registry, each site comparing `SessionAttachmentDocumentMimeSql.NormalizedExpression(column)` (D2) rather than a bare `lower(trim(...))`:
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

- **Every format reaches model context.** Each of the eleven MIMEs reaches model context through retrieval, attach, refresh, per-turn reference and pin, asserting the known source string and the per-format label. Attach is proven through the real `ArcanumInternalToolServer` tool, `McpBridgeTool` and `ToolExecutionPipeline` to the injected `TextContent`, and a per-turn reference through `PrepareAsync`, `MaterializeReferenceAsync` and the real `WizardIntelligenceProvider`; a gate off or an ineligible Binary keeps the exact existing refusal on both paths. (The first review found both paths refusing every document because they tested the durable kind alone.)
- **Truncation is announced on all four paths.** For attach, refresh, per-turn reference and pin, a text container past the 262,144-character cap and a workbook past shared-string retention each produce the `; truncated at the extraction limit` notice (a pin also reports `Truncated`), after the fixture first proves the extractor reports `WasTruncated`; a complete document keeps the existing notice.
- **One MIME normalisation.** `NormalizeMimeType` and `SessionAttachmentDocumentMimeSql.NormalizedExpression` agree byte for byte; tab, CR and LF around a registry MIME behave as the document in C# and in the reconciliation, gate-off retrieval and Tapestry SQL, while a no-break space, form feed, vertical tab, NEL, em space and dotless ı do not.
- **Eligibility is sensitive per format.** Removing one format from the registry makes its path tests fail.
- **Visio master inheritance.** The fixture shows a master label once per instance-bearing page, never on a page without an instance, and never standalone.
- **ODS repeat clamp.** `table:number-columns-repeated="16384"` (and rows repeated) over a few cells stays bounded on extracted length and on allocated bytes (`GC.GetAllocatedBytesForCurrentThread`); removing the clamp fails the test.
- **ZIP bombs.**
  - High ratio and high entry count are each `Failed` with `ShouldRetry: false`.
  - An understated declared length is bounded on bytes produced.
  - Repeated reads of one part are charged against a whole-extraction budget of min(64 MiB, max(1 MiB, 200 x input length)), measured on the bytes handed in rather than their backing array; a small package that lists one sub-floor part many times is refused cheaply, and a deflated part is inflated into one buffer of its declared size.
  - A part named by more than one sheet, slide, Visio page or Visio master is `FormatMismatch` before it is read again, under any case, leading-slash, dot-segment or percent-escape spelling.
  - Removing the produced-bytes check, or the local-header and range validation, fails a test deterministically, with no hang.
- **XML entities.** External entity and billion-laughs parts fail deterministically; the `Parse` flip fails the marker test.
- **Encryption.** Encrypted ODF and OLE2-wrapped OOXML resolve `NotEligible` with no ciphertext in output.
- **Smuggled parts.** An unreferenced smuggled part is never parsed: its text is absent, and a deliberately malformed smuggled part does not fail extraction.
- **Traversal names.** A traversal entry name is rejected with a reason that does not contain the name.
- **Hidden content.** Per-family fixtures carry `w:vanish` (direct, style, and inside alternate content), a hidden sheet, row and column, a hidden workbook window, merged-away cells, speaker notes, a hidden slide, `text:tracked-changes`, `office:annotation`, comments and `HideText`.
- **Order independence.** A hidden marker that arrives after the text it hides still hides it: PowerPoint `p:cNvPr` after the text body (every shape kind, in alternate content, and over the hold bound), Word `wp:docPr` and anchor `hidden` after the graphic, Visio `DisplayMode`, `HideText` and layer cells after the text and sub-shapes, an ODF `text:hidden-paragraph` inside hidden text. A Word property element that follows the content it governs is `FormatMismatch`.
- **Projection shape.** Tables in Word, PowerPoint, ODT and ODP are tab-separated lines with a cell's paragraphs joined by spaces; Excel cell line breaks, tabs and `_xHHHH_` escapes fold or decode; ODS value cells project raw values; Word equations project linear text; notes follow the body and unreferenced or hidden-only Word notes are absent; the marks and numbers of note references are never projected.
- **Macros and embeddings.** Macro and embedding parts are never inflated: a referenced `vbaProject.bin` and embedding with a bomb-sized declaration do not fail the extraction. External relationships are never followed.
- **Healthy work proceeds.** A poison package does not block a healthy document queued behind it. A healthy document queued behind a long slot holder gets its own full five-second budget; a slot not acquired within the slot wait, or a quarantined family, is `Deferred` having read and parsed nothing; caller cancellation while waiting propagates; and through the real `SessionAttachmentIndexingService` a healthy workbook is eventually indexed behind eight slow valid workbooks and behind a quarantined family.

## Review and fix wave

After stage 3, independent adversarial reviews (security bounds, fidelity against real producers' files, TDD sensitivity, the documentation, and the model-context seams) reported 69 issues. Each finding was ruled on, and one fix group per area (seams, host, Word, Spreadsheet, PowerPoint, OpenDocument, Visio) fixed its share, each code fix watched failing first and each mutation of a fix killed by a test. The decisions above (D1 to D10) were rewritten to match. What changed, and why:

- **The model-context seams were closed.** `attach_session_file` and per-turn references refused every document, because both tested the durable `Binary` kind alone and the post-tool paths that could extract never ran. Both now accept `IsEligibleDocument(record, settings)` against the current settings. The truncation notice (`; truncated at the extraction limit`) is now on attach, refresh, per-turn reference and pin, with a `Truncated` pin; before, a cut document looked complete.
- **One MIME normalisation.** C# trimmed all Unicode white space while the SQL trimmed a different set, so one spelling could be a document in one place and not in another. There is now one rule, spelled in `SessionAttachmentDocumentMimeSql` for every SQL site.
- **Contention no longer fails healthy documents.** Charging the extraction-slot queue to a document's five-second clock let slow documents of one family fail healthy ones behind them. The wait for the slot is now separate and bounded (30 seconds), contention is the transient `Deferred` status that indexing retries, and only the document's own budget is terminal.
- **Expansion is bounded across reads.** Re-reading one sub-floor part many times in a tiny package cost far more than the package's own size, and inflation doubled its buffer. The whole-extraction budget (D5) and the single declared-size buffer closed both.
- **Hiding no longer depends on element order.** PowerPoint, Word drawings, Visio group cells and ODF paragraph fields could leak text when the hiding flag came after it; each family now holds the text until the flag is known or refuses the package (`FormatMismatch`). Word also gained alternate-content resolution everywhere, tracked-deleted rows and cells, anchor `hidden`, document-background exclusion, and note references that count only where shown.
- **The projection now matches what a reader sees.** Tables are tab-separated lines; Word equations are linear text; text boxes stand on their own lines; notes follow the body without their marks; Excel hides merged-away cells and hidden workbook windows, decodes `_xHHHH_` and folds line breaks; ODS projects raw values, and rejects rows past the last row; ODF frames project one representation; Visio honours group `DisplayMode`, inherits layer membership, and inherits from a master only when one shape could be meant.
- **Shared parts are refused.** A sheet, slide, page or master part referenced twice is `FormatMismatch` before it is read again, and `FormatMismatch` is documented as also covering ambiguous structure.
- **Documented limitations (ruled, not fixed):** a Word table style with a conditional vanish hides the whole table; data-bound content controls project their cached content; `w:altChunk`, `w:sym`, automatic numbering, PowerPoint equations and Excel drawings and text boxes are not projected; `.docm`, `.xlsm`, `.pptm`, `.odt`, `.ods`, `.odp`, `.vsdx` and `.vsdm` attachments stored before this change are `application/octet-stream` and need a new upload; an upload MIME allowlist that admitted `application/octet-stream` no longer admits them even with Comprehension off (Compendium README); `arcanum memory search` still searches stored document chunks after the gate is turned off; the conditions of an ODF `style:map` are not evaluated, so a style is hidden if any map target hides, and a map on a map target is not followed.
- **Command Center stages no document as text.** The Command Center `@path` and `/attach` paths read any non-image file as lenient UTF-8 text, so a document reached the model as raw package bytes instead of through Comprehension. They now refuse a registry document by its name and name the `arcanum attachment add` route.
- **Closing fixes (final whole-branch review).** A busy-family deferral was recorded `Failed`, which reconciliation never re-selects, so a restart during its backoff, a full queue or a gate toggle lost a healthy document; it now stays `Pending` without spending an attempt. The indexer queued for its family's slot inside its work lease for up to the 30-second wait, so a maintenance drain under same-family contention failed with `WorkDrainTimeout`; indexing now takes the slot only when it is free, while interactive paths keep the bounded wait. A caller already queued when its family was quarantined waited out the whole slot wait; a quarantine now releases it as `Deferred` at once. `attach_session_file` promised injection even when nothing could be extracted; when the post-tool path materializes nothing it now answers `Not attached: '<name>' vN.` with the code-owned reason (extraction reason, busy reason with a retry hint, image policy message, the existing refusal, or already in context). The tool judged the gates by the host's current settings and the materializer by the turn's snapshot; the tool now accepts a registry document by its format alone and the gates are judged once, by the snapshot. `arcanum run --with @path` read a document as UTF-8 text; it now refuses one with the Command Center wording, shared through `ComprehensionDocumentPaths`. The `attach_session_file` description now says that eligible documents are attached as extracted text when Comprehension is enabled.
- **Closing fixes in the format families (Word, PowerPoint, Excel, OpenDocument).** Word complex fields are scoped to the story that holds them (body, note, text box), and Word and PowerPoint text is read only where a viewer gets it (the rendered drawing chain, shapes and table cells, never extension lists, unknown graphic data or stray paragraphs). Excel resolves `mc:AlternateContent` in every container, hides a cell whose own reference names a hidden row, and its line-break folding is pinned. OpenDocument projects only text inside paragraph content, judges duplicate style definitions and `style:map` targets with hidden winning, lets a hidden-paragraph field act inside a foreign element in hidden text, and folds U+2028, U+2029 and C1 controls in cells and sheet names.

## Verification commands

```bash
dotnet build tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --filter "FullyQualifiedName~Comprehension|FullyQualifiedName~SessionAttachment|FullyQualifiedName~AttachmentMimeDetectorTests|FullyQualifiedName~TapestryStoreTests|FullyQualifiedName~SessionContextPinMaterializerTests" </dev/null
python3 scripts/align_csharp_blanklines.py --repo . --check <changed .cs files>
```

The full CI gate is waived for this change by the operator (2026-10-22). Delivery uses focused suites, the guard suites the change touches (hosted-producer lane, admission catalog, documentation, boundary tests), the macOS Native AOT IL gate, and a local Native AOT run against Ollama.
