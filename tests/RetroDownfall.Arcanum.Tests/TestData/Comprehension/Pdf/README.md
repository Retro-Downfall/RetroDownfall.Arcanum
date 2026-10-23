# Comprehension PDF fixtures

Inputs for the PDF text extractor and the worker frame (`Weave/Comprehension/Pdf`). Two kinds:

- **Real producer**: `cups-text.pdf` and `cups-multi.pdf` were written by macOS Quartz through `cupsfilter`, and
  `webkit-float.pdf` and `webkit-ligatures.pdf` by WebKit (the engine behind Safari's Export as PDF), and
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
| `webkit-float.pdf` | (real producer, WebKit) a `float:right` price drawn before the text it sits beside, a right-to-left table row and a `row-reverse` flex row | `Extracted`; `$1,250.00 2 hours consulting`, `RIGHTCELL LEFTCELL`, `FLEXFIRST FLEXSECOND`: the price does not fuse with the `2` that follows it |
| `latin-ligatures.pdf` | single glyphs that a ToUnicode CMap maps to U+FB00 to U+FB06 (ff, fi, fl, ffi, ffl, long s t, st) inside words, then a plain line | `Extracted` as `effect final flow`, `office baffle`, `ſt st` and `plain office`: each ligature code point becomes its letters (U+FB05 becomes a long s and a t, as NFKC does) |
| `webkit-ligatures.pdf` | (real producer, WebKit) Helvetica text; WebKit writes its fi, fl and ffi as ligature glyphs | `Extracted` as `Helvetica: office, figures, affidavit, flow, fluffy waffles.` |
| `rtl-visual-order.pdf` | Hebrew and Arabic the way most producers write them, in visual order (each right-to-left run's glyphs advance left to right across the page), on six lines: a Hebrew sentence with its full stop, an English sentence with one Hebrew word, Hebrew around the number `25.5`, a Latin run `abc` between two Hebrew words, Arabic, and a plain ASCII line | `Extracted`; every right-to-left run is returned in logical order, numbers and Latin runs keep their own order, and the ASCII line is untouched |
| `rtl-logical-order.pdf` | a Hebrew line and an Arabic line written in logical order (each glyph placed to the left of the one before it) | `Extracted` exactly as written: nothing is reversed and no space appears inside a word |
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

- *Word breaks.* Letters are joined in content-stream order. A space goes between two letters when the next starts a word
  space or more to the right of the previous one's end (PdfPig's rule), or starts a word space or more to the left of the
  previous one's *start*: a float, a right-to-left table row or a page number drawn before its title steps back past a whole
  glyph, while kerning and an accent drawn over the letter after it step back by less than the previous glyph is wide.
- *Ligatures.* Only U+FB00 to U+FB06 are spelled out (ff, fi, fl, ffi, ffl, long s t, st). Nothing else is normalized, because
  the worker runs with invariant globalization: Arabic presentation forms (U+FB50 to U+FDFF, U+FE70 to U+FEFF) come out as the
  producer's code points, not folded to the base letters.
- *Right-to-left text.* Most producers write Hebrew and Arabic in visual order. A run whose glyphs step rightwards is reversed
  to logical order; one whose glyphs step leftwards was written in logical order and is left alone. A line with no
  left-to-right letters is one run. In a line that has both, each right-to-left run is reversed where it stands and the runs
  keep their order, so a right-to-left sentence that holds a Latin word reads `Latin right-to-left-words`. Numbers and Latin
  runs keep their own order. A file carries no paragraph direction, and bracket glyphs it draws mirrored may come out as
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
| `bad-trailer.pdf` | the trailer's `/Size` and `/Root` are not numbers or references |
| `missing-root.pdf` | the trailer has no `/Root` |
| `xref-stream-garbled.pdf` | `xref-stream.pdf` with the entries of its cross-reference stream garbled: the objects it indexes are inside an object stream, so there is nothing for a repair scan to find |

`bad-startxref.pdf` and `bad-xref-offsets.pdf` have a wrong `startxref` and cross-reference offsets. PdfPig rebuilds the
table by scanning even with lenient parsing off, so they are `Extracted` (`BROKEN-XREF-TEXT`), not `Malformed`. A damaged
cross-reference table is therefore `Failed` only when the parser cannot rebuild it: a table whose objects are written at
top level is repaired by scanning, while `xref-stream-garbled.pdf` (objects inside an object stream), an unreadable trailer
and a missing `/Root` are not.

### Page content that refers to an undefined object

ISO 32000-1 7.3.10 says a reference to an object the file does not define is the null object, not an error. The extractor
reads an undefined `/Contents` (or one undefined element of a `/Contents` array) as empty content, and nothing else: PdfPig
refuses such a page even with lenient parsing, and the repair covers only a reference that resolves to nothing.

| File | Shows | Outcome |
| --- | --- | --- |
| `contents-undefined-page-two.pdf` | page 1 `GOOD-PAGE-ONE`; page 2 has `/Contents 20 0 R`, an object the file does not define | `Extracted`, two entries; the second is empty |
| `contents-undefined-element.pdf` | `/Contents [4 0 R 35 0 R]`: a stream `ARRAY-ELEMENT-TEXT` and an undefined object | `Extracted` as `ARRAY-ELEMENT-TEXT` |
| `contents-undefined-only-page.pdf` | the only page has an undefined `/Contents` | `NoText` |
| `contents-undefined-in-cyclic-page-tree.pdf` | a page tree that lists itself as a kid, beside a page whose `/Contents` is undefined | `NoText`: the repair walks the tree and terminates |
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
