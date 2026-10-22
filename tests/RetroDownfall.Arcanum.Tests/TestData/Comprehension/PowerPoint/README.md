# PowerPoint real-producer fixture

`python-pptx-three-slides.pptx` was written by a real producer, python-pptx, not hand-built, so the PowerPoint
extractor tests exercise a package this repository did not assemble.

- Tool: python-pptx 1.0.2 (`pptx.__version__`), run with `python3 -I` on Python 3.14.7.
- macOS: 27.2 (build 26B118), from `sw_vers`.
- Generator: `make_python_pptx_three_slides.py` in this folder.
- Exact command, from this folder:

```sh
python3 -I make_python_pptx_three_slides.py python-pptx-three-slides.pptx
```

What the deck contains, and what the tests assert about it:

| Slide | Content | Expected projection |
|---|---|---|
| 1 | Title `Quarterly review`, body `Revenue grew` and `Costs fell`, speaker notes `SPEAKER NOTES ONE` | the title and both body paragraphs; the notes never appear |
| 2 | Title `HIDDEN SLIDE TITLE`, body `HIDDEN SLIDE BODY`, root attribute `show="0"` set after creation | absent; it still counts, so the next slide is `Slide 3` |
| 3 | Title `Results`, a 2x2 table (`Item`, `Count`, `Widgets`, `42`) whose `p:cNvPr` carries `descr="TABLE ALT TEXT"`, a visible text box `Footer note`, a text box `HIDDEN SHAPE TEXT` whose `p:cNvPr` has `hidden="1"`, speaker notes `SPEAKER NOTES THREE` | title, table cells row by row, footer; the hidden text box, alt text and notes never appear |

The deck also carries the default python-pptx slide master and eleven layouts, whose placeholder prompts (for example
`Click to edit Master title style`) must not appear either, plus a printer settings part and a thumbnail that are never
opened. `PresentationRealProducerTests.Fixture_CarriesEveryHiddenMarkerTheExtractionMustHonour` asserts that each marker
is really present in the file, so regenerating the fixture without them cannot make the extraction tests pass vacuously.

python-pptx cannot write macro-enabled packages, so the `.pptm` evidence repackages this same deck with an added,
deliberately undecodable `ppt/vbaProject.bin` that a presentation relationship points at.
