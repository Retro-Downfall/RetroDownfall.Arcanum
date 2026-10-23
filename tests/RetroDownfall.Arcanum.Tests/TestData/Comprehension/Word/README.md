# Word real-producer fixtures

Files in this folder were written by real tools, not hand-built, so the Wordprocessing extractor is proven on bytes it
did not build itself. Everything else the Word tests need is built in code with `ComprehensionPackageBuilder`.

Environment: macOS 27.2 (build 26B118), from `sw_vers`; pandoc 3.12.1, from `pandoc --version`. The packages embed
creation timestamps, so regenerating them does not reproduce the bytes; the tests assert extracted text, not bytes.

## textutil-multi.docx (source: textutil-multi.rtf)

- Tool: macOS `textutil` (the export engine behind TextEdit), `/usr/bin/textutil`.
- Input: an RTF document with four paragraphs plus an empty one, bold and italic runs, a tab, and accented text
  (`caf\'e9 na\'efve r\'e9sum\'e9`). HTML input is not used because `textutil`'s HTML import hangs in this environment;
  every call is wrapped in `timeout 30`.
- Command (run in this folder):

```sh
timeout 30 textutil -convert docx textutil-multi.rtf -output textutil-multi.docx
```

What it pins: a transitional package with **no styles part** (the missing-styles path), compact XML with an empty
`w:pPr` on every paragraph, `w:tab` as run content, and `xml:space="preserve"` text runs.

## pandoc-styled.docx (source: pandoc-styled.md)

- Tool: pandoc 3.12.1, markdown to docx with its default reference document.
- Command:

```sh
timeout 30 pandoc pandoc-styled.md -f markdown -t docx -o pandoc-styled.docx
```

What it pins: a real 27 KB `word/styles.xml` (nothing in it hides text), `word/footnotes.xml` with a
`continuationSeparator` note, a `separator` note and one real note, a related but empty `word/comments.xml`, an
external hyperlink relationship (never followed), a table, a bulleted list and a heading, all in indented XML.

## pandoc-revisions.docx (source: pandoc-revisions.md)

- Tool: pandoc 3.12.1, markdown to docx using its tracked-change and comment spans.
- Command:

```sh
timeout 30 pandoc pandoc-revisions.md -f markdown -t docx -o pandoc-revisions.docx
```

What it pins: a real `w:ins` (kept) and `w:del` with `w:delText` (excluded), comment range anchors and a
`w:commentReference` in the body, and a `word/comments.xml` whose text must never reach the output.

## pandoc-math.docx (source: pandoc-math.md)

- Tool: pandoc 3.12.1, markdown to docx with its default reference document.
- Command:

```sh
timeout 30 pandoc pandoc-math.md -f markdown -t docx -o pandoc-math.docx
```

What it pins: Office Math as a real producer writes it: an inline `m:oMath` between two runs of a paragraph (with an
`m:sSup` superscript and an `m:rPr` that must not be read as text), and a display equation as `m:oMathPara` (with an
`m:oMathParaPr`) alone in its own paragraph.
