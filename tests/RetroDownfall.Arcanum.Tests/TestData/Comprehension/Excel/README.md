# Excel real-producer fixtures

Files in this folder were written by real spreadsheet libraries, not hand-built, so the Excel extractor tests run on bytes
a real producer wrote. The generator scripts are committed beside the files they produce.

- Python: 3.14.7 (`python3 -I`, isolated mode, from `uv`).
- `openpyxl` 3.1.5 and `xlsxwriter` 3.2.9 (`python3 -I -c "import openpyxl, xlsxwriter; print(openpyxl.__version__, xlsxwriter.__version__)"`).
- macOS: 27.2 (build 26B118), from `sw_vers`.

Exact commands, run from this folder (the inputs are the scripts below; nothing else is read):

```sh
timeout 60 python3 -I generate-openpyxl-basic.py   openpyxl-basic.xlsx
timeout 60 python3 -I generate-xlsxwriter-basic.py xlsxwriter-basic.xlsx
timeout 60 python3 -I generate-xlsxwriter-macro.py xlsxwriter-macro.xlsm
```

## openpyxl-basic.xlsx

Writer: `openpyxl` 3.1.5. This build writes every string as `t="inlineStr"` (there is no `sharedStrings.xml`) and numbers as
`t="n"`; it writes relationship targets as absolute part names (`/xl/worksheets/sheet1.xml`).

| Sheet | Content |
|---|---|
| `Visible` | strings and numbers in A:D, column C hidden (`<col hidden="1" min="3" max="3">`), row 3 hidden, a formula `=B2+B3` (openpyxl writes `<f>` and an empty `<v>`, no cached value), a `2024-01-15` date (written as the serial 45306, `s="1"`), a boolean, a multi-line cell, and a cell comment (`xl/comments/comment1.xml`) |
| `HiddenSheet` | `state="hidden"`, text `HIDDENSHEETTEXT` |
| `VeryHiddenSheet` | `state="veryHidden"`, text `VERYHIDDENSHEETTEXT` |
| `Second` | `Beta` in A1 and `gap` in C1 (B1 absent) |

Markers that must never reach the output: `HIDDENCOLUMNTEXT`, `HIDDENROWTEXT`, `HIDDENSHEETTEXT`, `VERYHIDDENSHEETTEXT`,
`COMMENTTEXT`.

## xlsxwriter-basic.xlsx

Writer: `xlsxwriter` 3.2.9. Writes `xl/sharedStrings.xml` (including a rich string with four runs), formula cells with
cached values (`<f>A2*2</f><v>7</v>`), `t="b"` booleans, a `t="e"` error value (`#DIV/0!`), a hidden column C
(`width="0" hidden="1"`), a hidden row, a hidden second sheet and a cell comment. Relationship targets are relative
(`worksheets/sheet1.xml`).

## xlsxwriter-macro.xlsm

Writer: `xlsxwriter` 3.2.9 with `workbook.add_vba_project(...)`. The macro-enabled main part
(`application/vnd.ms-excel.sheet.macroEnabled.main+xml`) and the `vbaProject` relationship from the workbook part are the
real layout, but `xl/vbaProject.bin` is a PLACEHOLDER of arbitrary bytes, not a VBA project: xlsxwriter embeds whatever it
is given without parsing it. The hand-built macro tests carry the trap that matters (a `vbaProject.bin` that cannot be
inflated); this file shows a producer-written `.xlsm` extracts its cells.
