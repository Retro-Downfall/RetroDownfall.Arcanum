# OpenDocument fixtures

Files for the `.odt`, `.ods` and `.odp` extractors. One folder up, `textutil-basic.odt` (see `../README.md`) is the
first real-producer file; this folder adds a richer one and the hand-built spreadsheet and presentation packages.
Everything else the OpenDocument tests need is built in code with `ComprehensionPackageBuilder`.

Environment: macOS 27.2 (build 26B118), from `sw_vers`; Python 3.14 standard library only (`python3 -I --version`).
The textutil package embeds creation timestamps, so regenerating it does not reproduce the bytes; the tests assert
extracted text, not bytes. The hand-built packages are deterministic (fixed timestamps).

## textutil-rich.odt (source: textutil-rich.rtf)

- Tool: macOS `textutil` (the export engine behind TextEdit), `/usr/bin/textutil`. This is the real producer.
- Input: an RTF document with a bold 18 pt title, a paragraph with bold, italic and underlined runs, a paragraph with
  two tabs, a paragraph with a line break, a hyperlink field, a two-by-two table and a closing paragraph. HTML input is
  not used because `textutil`'s HTML import hangs in this environment; every call is wrapped in `timeout 30`.
- Command (run in this folder):

```sh
timeout 30 textutil -convert odt textutil-rich.rtf -output textutil-rich.odt
```

What it pins:

- The manifest carries the OpenOffice `DOCTYPE` (read with `DtdProcessing.Ignore`) and the content part carries none.
- Real ODF whitespace: `text:s` between runs, `text:tab`, `text:line-break`, a `text:a` link whose `xlink:href` must
  never reach the output, and whitespace-only text nodes inside `table:table-cell` ahead of the cell's paragraph.
- Styles: automatic paragraph styles with `style:parent-style-name="Standard"` (the parent lives in `styles.xml`),
  character styles for bold, italic and underline, and a table with `table-column` and `table-cell` styles.

What it does not pin: hidden text. `textutil` does not carry the RTF hidden attribute (`\v`) into the ODF it writes: a
probe with `{\v HIDDEN}` came out as ordinary visible text. Every hidden-content rule is therefore covered by
synthetic fixtures built in code, and the hand-built packages below.

## handmade-libreoffice-style.ods and handmade-doctype-manifest.ods

No local tool produces `.ods` (LibreOffice is not installed), so these are written by `build-handmade.py` to the layout
LibreOffice 24.x uses for ODF 1.3: `mimetype` first and stored, a manifest listing every part including
`Configurations2/` and `Thumbnails/thumbnail.png`, `settings.xml`, `meta.xml`, `styles.xml` with a master page whose
header and footer carry text, and `content.xml` with the automatic styles LibreOffice names `co1`, `ro1`, `ta1`, `ce1`.

- `handmade-libreoffice-style.ods` has the realistic manifest.
- `handmade-doctype-manifest.ods` is the same package with the OpenOffice `DOCTYPE` that `textutil` writes added to the
  manifest.
- The visible sheet `Inventory` has a collapsed column (`table:visibility="collapse"`), a collapsed row, a filtered row,
  a formula cell (`table:formula` plus its cached value: the extractor projects the `office:value` attribute, never the `text:p` that caches it), a merged cell with a comment followed by a covered cell, a
  two-paragraph cell, and LibreOffice's trailing `table:number-rows-repeated="1048567"` row of 1,024 repeated empty
  cells. The second sheet `Archive` has `table:display="false"` in its table style.

## handmade-libreoffice-style.odp and handmade-doctype-manifest.odp

Same approach and the same pair of manifests, again by `build-handmade.py`. Slide 1 has a title, an outline list with a
`text:s`, a hidden shape (graphic style `draw:display="none"`), a visible custom shape, a comment and speaker notes;
slide 2 is hidden by its drawing-page style (`presentation:visibility="hidden"`); slide 3 has a table and an image with
`svg:title` and `svg:desc`. The master page in `styles.xml` carries a footer frame and notes, and the presentation has
`presentation:footer-decl` and `presentation:date-time-decl` elements, none of which may be projected.

Regenerate (run in this folder):

```sh
python3 -I build-handmade.py
```
