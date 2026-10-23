# Comprehension PDF fixtures

Inputs for the PDF text extractor and the worker frame (`Weave/Comprehension/Pdf`). Two kinds:

- **Real producer**: `cups-text.pdf` and `cups-multi.pdf` were written by macOS Quartz through `cupsfilter`, and
  `webkit-float.pdf`, `webkit-ligatures.pdf`, `webkit-rotated.pdf`, `webkit-rtl-numbers.pdf` and `webkit-rtl-runs.pdf` by
  WebKit (the engine behind Safari's Export as PDF), and
  `quartz-hebrew.pdf` by CoreText into a Quartz PDF context (what macOS Save as PDF uses), so the extractor is proven on
  bytes this repository did not build.
- **Hand-written**: everything else comes from `generate-handwritten.py`. Each file isolates one rule, so a failing test
  names the rule. The output is deterministic (no timestamps, fixed document ids and IVs, a seeded random source).

Every committed `.pdf` is at most 1 MiB, the attachment intake ceiling for non-image files, because the worker refuses
more than that on stdin. `PdfFixtureReadmeTests` fails if a `.pdf` is missing from the tables below or is larger.

## Exact commands

Tool versions used: macOS 27.2 (build 26B118), Python 3.14.7, `cryptography` 50.0.1 (only for the AES fixtures).

```sh
cd tests/RetroDownfall.Arcanum.Tests/TestData/Comprehension/Pdf

# Real producer. The .txt files are the committed sources; cupsfilter wraps lines at 80 columns and pads them with spaces,
# and it stamps a creation date, so a regenerated PDF differs byte for byte. The committed bytes are the reference.
cupsfilter -m application/pdf cups-text.txt  > cups-text.pdf
cupsfilter -m application/pdf cups-multi.txt > cups-multi.pdf

# Real producers, WebKit and Quartz. generate-macos.swift needs macOS (Xcode command line tools). The files are not deterministic.
swiftc -O -o "$TMPDIR/macos-fixtures" generate-macos.swift
"$TMPDIR/macos-fixtures" webkit webkit-float.html webkit-float.pdf
"$TMPDIR/macos-fixtures" webkit webkit-ligatures.html webkit-ligatures.pdf
"$TMPDIR/macos-fixtures" webkit webkit-rotated.html webkit-rotated.pdf
"$TMPDIR/macos-fixtures" webkit webkit-rtl-numbers.html webkit-rtl-numbers.pdf
"$TMPDIR/macos-fixtures" webkit webkit-rtl-runs.html webkit-rtl-runs.pdf
"$TMPDIR/macos-fixtures" hebrew quartz-hebrew.pdf

# Hand-written. Run after the lines above: truncated.pdf is the first half of cups-text.pdf.
python3 -I generate-handwritten.py .

# Optional, never committed: the hostile files that are larger than the intake ceiling (about 30 MB in all).
python3 -I generate-handwritten.py /some/scratch/dir --large
```

`cups-text.txt` is a short English document with accented letters, an em dash and a URL. `cups-multi.txt` is 300 numbered
lines that fill ten pages, ending with the codeword `LASTLINE-OBSIDIAN-9915` on page 10.

## Fixtures and what they pin

Expected outcomes are for the production extractor with its default limits unless a test says otherwise.

### Text

| File | Shows | Outcome |
| --- | --- | --- |
| `cups-text.pdf` | one page of prose (real producer) | `Extracted`, one page; every non-whitespace character of `cups-text.txt` in order |
| `cups-multi.pdf` | ten pages (real producer) | `Extracted`, ten pages; the codeword only on page 10 |
| `multi-page.pdf` | page 1 `PAGE-ONE-ALPHA`, page 2 **no content**, page 3 `PAGE-THREE-MARKER-GAMMA`, page 4 `PAGE-FOUR-DELTA` | `Extracted`, four entries, the second empty, so ordinals stay true. 14, 0, 23 and 15 glyphs. |
| `content-order.pdf` | three runs shown bottom, top, middle | `Extracted`, in content-stream order |
| `same-line-order.pdf` | pairs of runs on one baseline: `RIGHTWORD` drawn before the `LEFTWORD` to its left; the page number `17` drawn before the title `Annual Report`; a kerned `WA`, `VE` pair; `FIRSTRUN` and `SECONDRUN` drawn at the same place; an accent `^` drawn over the `u` that follows it in `Muller` | `Extracted`; the runs stay separate words (`RIGHTWORD LEFTWORD`, `17 Annual Report`, `FIRSTRUN SECONDRUN`) and the kerned pair and the accented word stay one word (`WAVE`, `M^uller`) |
| `rotated-text.pdf` | Helvetica runs whose glyphs do not advance rightwards: upside down, mirrored, turned 150 degrees, and upside down with its words apart only by `TJ` kerning (no space glyph); then an upside-down run with an upright one to its left, and a run turned 90 degrees followed by one that starts off its baseline | `Extracted` as `Ordinary line of text`, `Invoice total due`, `Mirrored words here`, `Rotated one fifty`, `Spaced by kerning`, `Flipped Upright`, `Up the side Turned`: no letter-spacing, and a change of direction still separates words |
| `webkit-rotated.pdf` | (real producer, WebKit) CSS `rotate(180deg)`, `rotate(150deg)` and `scaleX(-1)` text beside an ordinary line | `Extracted` as `Normal line here`, `Upside down label text`, `Rotated one fifty`, `Mirrored words here` |
| `line-feed-glyph.pdf` | a glyph mapped to a line feed alone on the first line (some producers draw a paragraph's line feed), then `AB` on the line below | `Extracted` as `\nAB`: the line below a glyph that is not a letter does not start with a space |
| `webkit-float.pdf` | (real producer, WebKit) a `float:right` price drawn before the text it sits beside, a right-to-left table row and a `row-reverse` flex row | `Extracted`; `$1,250.00 2 hours consulting`, `RIGHTCELL LEFTCELL`, `FLEXFIRST FLEXSECOND`: the price does not fuse with the `2` that follows it |
| `latin-ligatures.pdf` | single glyphs that a ToUnicode CMap maps to U+FB00 to U+FB06 (ff, fi, fl, ffi, ffl, long s t, st) inside words, then a plain line | `Extracted` as `effect final flow`, `office baffle`, `st st` and `plain office`: each ligature code point becomes what NFKC gives (U+FB05 becomes `st`: its decomposition is a long s and a t, and the long s decomposes again to `s`) |
| `webkit-ligatures.pdf` | (real producer, WebKit) Helvetica text; WebKit writes its fi, fl and ffi as ligature glyphs | `Extracted` as `Helvetica: office, figures, affidavit, flow, fluffy waffles.` |
| `rtl-visual-order.pdf` | Hebrew and Arabic the way most producers write them, in visual order (each right-to-left run's glyphs advance left to right across the page), on six lines: a Hebrew sentence with its full stop, an English sentence with one Hebrew word, Hebrew around the number `25.5`, a Latin run `abc` between two Hebrew words, Arabic, and a plain ASCII line | `Extracted`; every right-to-left run is returned in logical order, numbers and Latin runs keep their own order, and the ASCII line is untouched |
| `rtl-logical-order.pdf` | a Hebrew line and an Arabic line written in logical order (each glyph placed to the left of the one before it) | `Extracted` exactly as written: nothing is reversed and no space appears inside a word |
| `rtl-runs.pdf` | right-to-left lines drawn as several runs, the right run first, each in visual order: a table row of three Hebrew cells; a Hebrew name and the price to its left; two Hebrew cells and an English one; a heading whose second line is drawn 10 pt below the first (too close to be a new line) and starts to the left of where the first ended; and the control, one line drawn left to right as two runs with a gap ahead and no space glyph | `Extracted`; each run in logical order and the runs in content order (first, second, third; apple, 12.50; apple, price, Apple; the heading's first line, then its second); the control is one run, turned round whole |
| `webkit-rtl-runs.pdf` | (real producer, WebKit) from `webkit-rtl-runs.html`: right-to-left tables (three cells; product and price over apple and 12.50; two Hebrew cells and an English one), a right-to-left flex row, a paragraph with a bold word, and a heading with line height 0.8 that wraps to three lines | `Extracted`; cells, flex items and the heading's lines in content order, each in logical order; the paragraph, whose runs WebKit draws left to right, reads as before |
| `rtl-numbers.pdf` | twelve right-to-left lines with a number, drawn in the visual order the Unicode bidirectional algorithm gives (UAX #9 W2, W4, W5): Arabic-Indic `١٢٫٥` and Persian `۱۲٫۵` (decimal separator U+066B), `١٬٢٥٠` (thousands separator U+066C); after an Arabic letter, `50%`, `٥٠%`, `٥٠٪` and `$25`, whose sign is drawn outside the number; an Arabic line that starts with `50%`; in Hebrew, `25°`, `50%`, `$25` and `2026-10-01`, whose sign or hyphen is drawn inside it | `Extracted`; every number with its digits, separators and signs in logical order, as typed |
| `webkit-rtl-numbers.pdf` | (real producer, WebKit) the same twelve lines from `webkit-rtl-numbers.html` | `Extracted`, the same text as `rtl-numbers.pdf` |
| `rtl-upside-down.pdf` | a Hebrew line drawn upside down in visual order (each glyph a glyph's width further along its own, leftward, direction), and one drawn upside down in logical order (each glyph behind the one before) | `Extracted`; both lines read the same in logical order, without spaces inside the words |
| `quartz-hebrew.pdf` | (real producer, Quartz and CoreText) a Hebrew paragraph with a Latin marker, and an English sentence with one Hebrew word | `Extracted`; the Hebrew words read in logical order |
| `astral-plane.pdf` | `AB`, U+1F600, `CD` through a ToUnicode CMap | `Extracted`, the pair intact; used for surrogate-safe cuts |
| `astral-multi-page.pdf` | page 1 `AB`, U+1F600, `CD`; page 2 `EFGHIJKL`; page 3 no content | `Extracted`, three entries; a cut inside page 1's pair must stop without reading page 2 |
| `cjk-tounicode.pdf` | CJK on two lines and U+20BB7 on a third | `Extracted` |
| `cjk-predefined-cmap.pdf` | Japanese through the predefined `90ms-RKSJ-H` CMap | `Extracted` (PdfPig's embedded CMap resources) |
| `control-characters.pdf` | glyphs mapped to `A`, NUL, U+000C, `B`, `C` | `Extracted` as `ABC` |
| `non-embedded-font.pdf` | a TrueType font with a descriptor, not embedded, installed on no machine | `Extracted`; the text never depends on a host font |
| `missing-font.pdf` | a run whose font the page does not define, then a good run | `Extracted` with the good run only |
| `huge-count.pdf` | a page tree whose `/Count` claims 2,000,000,000 pages; one is real | `Extracted`, one page |
| `deep-pages-1k.pdf` | the page is 1,000 levels down the page tree | `Extracted` |
| `xref-stream.pdf` | PDF 1.5: the catalog, page tree, page and font sit in a compressed object stream, found only through an intact cross-reference stream | `Extracted` (`XREF-STREAM-TEXT`); the control for `xref-stream-garbled.pdf` |

**What the page join does to the text** (`PdfPageText`; the rows above pin each rule).

- *Word breaks.* Letters are joined in content-stream order. Distances are measured along the direction the previous glyph
  advances in (rightwards for upright text, leftwards for upside-down or mirrored text), so turned text is not letter-spaced.
  A space goes between two letters when the next starts a word space or more ahead of the previous one's end (PdfPig's
  rule), or a word space or more behind the previous one's *start*: a float, a right-to-left table row or a page number
  drawn before its title steps back past a whole glyph, while kerning and an accent drawn over the letter after it step back
  by less than the previous glyph is wide. A letter that starts off a visible previous letter's baseline by more than a line
  allows is a new word too, which for turned text page height alone does not show.
- *Ligatures.* Only U+FB00 to U+FB06 are spelled out (ff, fi, fl, ffi, ffl, long s t, st). Nothing else is normalized, because
  the worker runs with invariant globalization: Arabic presentation forms (U+FB50 to U+FDFF, U+FE70 to U+FEFF) come out as the
  producer's code points, not folded to the base letters.
- *Right-to-left text.* Most producers write Hebrew and Arabic in visual order. A run whose glyphs step ahead along their
  own direction (rightwards, for upright text) is reversed to logical order; one whose glyphs step back was written in
  logical order and is left alone. A line is turned round one drawn run at a time and the runs keep their order: a letter
  that starts behind the previous one with a gap begins a new run (the next cell of a right-to-left table row, which WebKit
  draws right cell first, a flex item, or the next line of a heading set too tightly to split). A drawn run with no
  left-to-right letters is turned round whole. In a line that has both, each right-to-left run is reversed where it stands
  and the runs keep their order, so a right-to-left sentence that holds a Latin word reads `Latin right-to-left-words`. Numbers and Latin
  runs keep their own order. What a producer draws as one number follows the bidirectional algorithm: a separator between
  digits (the Arabic U+066B and U+066C included) is inside it; a sign next to it (currency, `%`, `°`, `#`, `±`) is inside
  only after a Hebrew letter or at the start of the line, because after an Arabic letter the digits are Arabic numbers and
  the sign is drawn outside them. A hyphen between digits is always kept inside, although the algorithm leaves it outside
  an Arabic number, because producers differ there (WebKit follows the algorithm; the macOS Arabic license document keeps
  `227.7202-1` whole). A file carries no paragraph direction, and bracket glyphs it draws mirrored may come out as
  the other bracket.

### Text a reader does not see (all extracted: #163 decides every text-showing run is)

| File | Shows | Outcome |
| --- | --- | --- |
| `hidden.pdf` | visible, render mode 3 (invisible), white fill, 0.5 pt, stroke-only, and off-page runs | all seven lines, in stream order |
| `actual-text.pdf` | glyphs inside marked content whose `/ActualText` says something else | the glyphs; `/ActualText` is never used |
| `active-content.pdf` | a JavaScript open action and name-tree script, a launch action, a URI action, an embedded file and a form field with an appearance stream, plus one line of page text | only `ACTIVE-CONTENT-VISIBLE-TEXT` |

### No text

| File | Shows | Outcome |
| --- | --- | --- |
| `image-only.pdf` | page 1 draws an image, page 2 is empty | `NoText` |
| `whitespace-only.pdf` | five spaces | `NoText`; `Bounds` if the output ceiling is under 5 |
| `cyclic-pages.pdf`, `cyclic-pages-self.pdf` | a page tree that only leads back to itself | `NoText` (PdfPig reports zero pages) |

### Encryption

| File | User password | Owner password | Outcome |
| --- | --- | --- | --- |
| `encrypted-rc4-empty.pdf`, `encrypted-aes-empty.pdf` | empty | `owner` | `Extracted` (permission-only restrictions) |
| `encrypted-rc4-password.pdf`, `encrypted-aes-password.pdf` | `secret` | `owner` | `PasswordRequired` |
| `encrypted-rc4-owner-empty.pdf`, `encrypted-aes-owner-empty.pdf` | `secret` | empty | `Extracted` (known limitation, below) |
| `encrypted-pubsec.pdf` | none: public-key (certificate) security, `/Filter /Adobe.PubSec`, which PdfPig has no handler for | none | `PasswordRequired`, not `Malformed`: the file is well formed and protected |
| `encrypted-pubsec-xref-stream.pdf` | none: the same certificate security in a PDF 1.5 file, declared by the cross-reference stream's dictionary | none | `PasswordRequired` |

A file PdfPig cannot open is `PasswordRequired` only when a trailer declares encryption: an `/Encrypt` key directly in a
`trailer` dictionary or in a cross-reference stream's dictionary. The name anywhere else (page text, a string, a comment, a
stream, another dictionary) does not count, so a damaged file that mentions it stays `Malformed`
(`truncated-mentions-encrypt.pdf`).

**Known limitation.** The extractor never supplies a password, but PdfPig tries the empty string as the *owner* password
as well as the user password, and an owner session decrypts the whole file. A file that needs a real user password but
has an empty owner password therefore opens. The `*-owner-empty` tests pin this so a change is noticed. Closing it needs
a PdfPig change or reading the encryption dictionary ourselves.

### Not a readable PDF (`Malformed`)

| File | What is wrong |
| --- | --- |
| `empty.pdf` | zero bytes |
| `garbage.pdf` | 4,096 seeded random bytes |
| `header-only.pdf` | `%PDF-1.4` and nothing else |
| `truncated.pdf` | the first half of `cups-text.pdf` (no trailer) |
| `truncated-mentions-encrypt.pdf` | an unencrypted one-page file whose page text names the `/Encrypt` key, cut before its cross-reference table (no trailer): damaged, not protected, so not `PasswordRequired` |
| `bad-trailer.pdf` | the trailer's `/Size` and `/Root` are not numbers or references |
| `missing-root.pdf` | the trailer has no `/Root` |
| `xref-stream-garbled.pdf` | `xref-stream.pdf` with the entries of its cross-reference stream garbled: the objects it indexes are inside an object stream, so there is nothing for a repair scan to find |

`bad-startxref.pdf` and `bad-xref-offsets.pdf` have a wrong `startxref` and cross-reference offsets. PdfPig finds
the objects by scanning even with lenient parsing off (it builds no table from the scan, see the object bound below), so they
are `Extracted` (`BROKEN-XREF-TEXT`), not `Malformed`. A damaged cross-reference table is therefore `Failed` only when the
parser cannot recover: objects written at top level are found by scanning, while `xref-stream-garbled.pdf` (objects inside an
object stream), an unreadable trailer and a missing `/Root` are not.

### Declared objects (the object bound)

`PdfExtractionLimits.MaxObjects` is checked right after the open, before the page tree is counted or any page is read. The
count is `Structure.CrossReferenceTable.ObjectOffsets.Count` of the opened document: the entries the cross-reference data
lists as in use. Measured on the pinned nightly:

- An object stored inside an object stream is a type 2 entry of the cross-reference stream and is counted. `xref-stream.pdf`
  lists seven: three written at top level, four (catalog, page tree, page, font) that exist only inside the object stream.
- Free entries are not counted, and the trailer's `/Size` is not the count: `xref-stream.pdf` says `/Size 8` (entry 0 is free)
  and `contents-free-entry.pdf` lists six against `/Size 8`. A file chooses `/Size` freely, so using it would refuse a small
  honest file for a number it wrote and still miss a hostile one that wrote a small number.
- `bad-xref-offsets.pdf` lists five objects at offsets that are wrong by 7. PdfPig keeps the offsets as written and still reads
  the objects, so the five are counted. `bad-startxref.pdf` has a wrong `startxref`; PdfPig finds the table anyway (five).
- PdfPig does not build a table from a scan. With no usable table it holds an empty one and finds each object by scanning the
  file when it looks for it; with a short table it keeps what the table lists and scans for the rest. The count is therefore
  what the cross-reference data lists, and an object the file writes but the data does not list is not counted
  (`xref-short-table.pdf`, `xref-no-table.pdf`; pinned as a known limitation). A scan cannot find an object stored inside an
  object stream (`xref-stream-garbled.pdf`), and an object written out takes 13 bytes at the least (`n g obj` and `endobj`), so
  the 1 MiB intake ceiling holds at most 80,659 of them. The input ceiling, not the object bound, limits what a scan can find,
  and that is fewer than the default below.
- The count is read after the open, which has already read the table. The bound limits what is traversed afterwards; the
  worker's memory, CPU and wall-clock limits bound the parser while it opens.

| File | Shows | Outcome |
| --- | --- | --- |
| `xref-short-table.pdf` | five objects written; the table lists the first two; the trailer still says `/Size 6` | `Extracted` (`SHORT-TABLE-TEXT`); counts 2 |
| `xref-no-table.pdf` | the same five objects with no table (the trailer stays, `startxref` is 0) | `Extracted` (`SHORT-TABLE-TEXT`); counts 0 |
| `xref-stream-at-object-bound.pdf` | `xref-stream.pdf` whose Flate-encoded cross-reference stream lists 99,993 more compressed objects, none of them read: 100,000 in all, in 1,032 bytes | `Extracted` (`XREF-STREAM-TEXT`) |
| `xref-stream-over-object-bound.pdf` | the same with one more entry: 100,001 | `Bounds` |

**Choosing the default (100,000).** Declared objects, counted on the pinned nightly on 2026-10-23:

| Documents | Size | Declared objects |
| --- | --- | --- |
| the committed real-producer fixtures | 9,751 to 40,742 bytes | 11 (`cups-text.pdf`, `webkit-float.pdf`, `webkit-ligatures.pdf`, `webkit-rotated.pdf`) to 40 (`cups-multi.pdf`, ten pages); `quartz-hebrew.pdf` 16, `webkit-rtl-runs.pdf` 22, `webkit-rtl-numbers.pdf` 25 |
| the other committed fixtures, the object-bound pair aside | 0 to 400,660 bytes | at most 35 (`xobject-fan-out.pdf`, a chain of 30 forms), except `deep-pages-1k.pdf` with 1,004 (a page tree 1,000 deep) |
| text through `cupsfilter` (Quartz): 18,800 numbered lines of six to fourteen words, not committed | 1,012,404 bytes | 1,607 on 509 pages (1.6 per KiB) |
| a page of links through WebKit (`generate-macos.swift webkit`): a table of 500 rows of six hyperlinked cells, 3,000 link annotations, not committed | 1,037,431 bytes | 9,012 on 3 pages (8.9 per KiB), the densest realistic file found |
| the 1,912 readable PDFs of at most 1 MiB that macOS, Xcode and the command line tools ship (icons, manuals, licence lists, dictionaries) | up to 731 KB | median 11, 99th percentile 95, largest 2,975 (a 61-page, 550 KB acknowledgments file: 5.5 per KiB) |

The default leaves the densest 1 MiB document measured (9,012) a factor of eleven inside it, and is above the 80,659 objects
a 1 MiB file can write out, so a file whose objects are all written out never reaches it. Only a cross-reference stream
that lists objects stored in object streams can declare more, which `xref-stream-over-object-bound.pdf` does in 1,032 bytes.
Raise the bound only for a measured legitimate document that needs it.

### Page content that refers to an undefined object

ISO 32000-1 7.3.10 says a reference to an object the file does not define is the null object, not an error. The extractor
reads an undefined `/Contents` (or one undefined element of a `/Contents` array) as empty content, and nothing else: PdfPig
refuses such a page even with lenient parsing, and the repair covers only a reference that the cross-reference data does
not list as in use (no entry, a free entry, or a number past the table) and the parser cannot find, or an object written as
`null`. An object the table lists as in use that the parser cannot read is damaged, not undefined, and stays `Malformed`.

| File | Shows | Outcome |
| --- | --- | --- |
| `contents-undefined-page-two.pdf` | page 1 `GOOD-PAGE-ONE`; page 2 has `/Contents 20 0 R`, an object the file does not define | `Extracted`, two entries; the second is empty |
| `contents-undefined-element.pdf` | `/Contents [4 0 R 35 0 R]`: a stream `ARRAY-ELEMENT-TEXT` and an undefined object | `Extracted` as `ARRAY-ELEMENT-TEXT` |
| `contents-undefined-only-page.pdf` | the only page has an undefined `/Contents` | `NoText` |
| `contents-undefined-in-cyclic-page-tree.pdf` | a page tree that lists itself as a kid, beside a page whose `/Contents` is undefined | `NoText`: the repair walks the tree and terminates |
| `contents-free-entry.pdf` | page 1 `GOOD-PAGE-ONE`; page 2 has `/Contents 6 0 R`, which the cross-reference table lists as free | `Extracted`, two entries; the second is empty |
| `contents-null-object.pdf` | page 1 `GOOD-PAGE-ONE`; page 2 has `/Contents 7 0 R`, an object written as `null` | `Extracted`, two entries; the second is empty |
| `contents-damaged-only-page.pdf` | the only page's content stream is overwritten with spaces; the cross-reference table still lists it as in use | `Malformed`: damaged, not undefined, so not a blank page (`NoText`) |
| `contents-damaged-page-two.pdf` | page 1 `GOOD-PAGE-ONE`; page 2's content stream has its `obj` keyword garbled (`7 0 obX`); the table lists it as in use | `Malformed`: page 2 is not silently blank |
| `contents-undefined-beside-refused-page.pdf` | page 1 as in `contents-undefined-page-two.pdf`; page 2 has arrays nested 300 deep | `Malformed` (nesting depth on page 2): the repair does not forgive a second failure |
| `contents-not-a-stream.pdf` | `/Contents [4 0 R 6 0 R]` where object 6 exists and is a string | `Malformed`: an object that exists but is not a stream is the wrong type, not null |

### Hostile structure that PdfPig 0.1.17-alpha-202610200037 turns into exceptions

Stable 0.1.16 dies with an uncatchable stack overflow on several of these, which is why the nightly is pinned. They are
cheap and safe in-process on the pinned version.

| File | Structure | Outcome |
| --- | --- | --- |
| `cyclic-ref-chain.pdf` | `/Contents` is an object that refers to another that refers back | `Malformed` (nesting depth) |
| `page-failure-mentions-encrypt.pdf` | the `/Contents` chain of `cyclic-ref-chain.pdf`, plus an unreferenced note whose text mentions the `/Encrypt` name | `Malformed` (nesting depth), not `PasswordRequired`: only a failure to open can be an unsupported security handler |
| `deep-array-content.pdf` | 200,000 nested arrays in the content stream (400 KB) | `Malformed` (nesting depth) |
| `cyclic-xobject.pdf`, `cyclic-xobject-ab.pdf` | a form that invokes itself, two forms that invoke each other | `Malformed`; lenient parsing would extract them, which is what the strict-parsing test pins |
| `cyclic-length.pdf` | a stream whose `/Length` is an object that refers to itself | `Extracted` (`LENGTH-CYCLE`) |
| `cyclic-pages-with-page.pdf` | a page tree that lists itself as a kid, beside one real page | `Extracted` (`PAGE-BESIDE-CYCLE`) |

## Worker-only: never load these in an in-process test

They cost seconds to minutes and up to gigabytes in the parser. They exist to prove the worker's memory, CPU and wall-clock limits kill
the process, and they are small on disk because they are compressed.

| File | Size | What happens in an unlimited process |
| --- | --- | --- |
| `flate-bomb.pdf` | 195 KB | inflates to 200 MB of spaces before one visible run; about 660 MB resident, 1.8 s |
| `flate-letters-1m.pdf` | 1.6 KB | one page of 1,000,000 glyphs; about 530 MB, 3 s. Over the 250,000-glyph page bound. |
| `flate-letters-5m.pdf` | 5.5 KB | 5,000,000 glyphs; over 2 GB, still running after 20 s |
| `flate-letters-20m.pdf` | 20 KB | 20,000,000 glyphs; over 6.5 GB, still running after 20 s |
| `xobject-fan-out.pdf` | 5.9 KB | a chain of 30 form XObjects, each invoking the next twice: 2^29 invocations of the last, which draws nothing. No cycle, nested well inside the parser's 50-level limit, one line of visible text. About 40 minutes of CPU and under 60 MB, so only a clock ends it; the worker's own 8 s deadline does |

The `--large` generator output (`deep-dict-info.pdf`, `deep-dict-resources.pdf`, `deep-pages-100k.pdf`,
`deep-xobject-chain-5k.pdf`, `deep-xobject-chain-50k.pdf`, `q-nesting-1m.pdf`) is larger than the intake ceiling, so the
worker would refuse it at stdin before PdfPig saw it; regenerate it when a worker test needs the input-size refusal or a
parser stress run. Measured on the pinned nightly in a JIT console process (8 MB main-thread stack), one file each:

| File | Result | Wall time | Peak resident |
| --- | --- | --- | --- |
| `deep-dict-info.pdf` | nesting-depth exception | 0.14 s | 66 MB |
| `deep-dict-resources.pdf` | extracts `DEEP-DICT-VISIBLE` | 0.18 s | 75 MB |
| `deep-pages-100k.pdf` | extracts `DEEP-PAGE-LEAF` | 0.72 s | 219 MB |
| `deep-xobject-chain-5k.pdf` | exception: form nested more than 50 levels | 0.22 s | 76 MB |
| `deep-xobject-chain-50k.pdf` | the same exception | 0.25 s | 107 MB |
| `q-nesting-1m.pdf` | extracts `INSIDE-Q-NEST` | 1.18 s | 628 MB |
