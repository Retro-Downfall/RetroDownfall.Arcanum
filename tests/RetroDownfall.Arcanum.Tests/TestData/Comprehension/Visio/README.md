# Visio fixtures (hand-built, not from a Visio producer)

No tool on the development machines writes `.vsdx`/`.vsdm`: Microsoft Visio does not run on macOS, LibreOffice is not
installed (and cannot export Visio in any case), and no Python Visio package is installed. These files are therefore
**hand-built from the MS-VSDX file format** (the Visio file format reference on Microsoft Learn: package layout, the
`visio/document.xml`, `pages.xml`, `masters.xml`, page and master parts, the OPC relationship types under
`http://schemas.microsoft.com/visio/2010/relationships/`, and the `Shape`, `Cell`, `Text`, `fld` and `Master`/`MasterShape`
element and attribute definitions). They were NOT written by Visio, so they pin the extractor to the specification, not to
anything a real producer is observed to do. Most Visio coverage uses in-code packages from `VisioPackageBuilder`
(see `tests/RetroDownfall.Arcanum.Tests/Weave/Comprehension/VisioPackageBuilderTests.cs`) for the same reason.

- Environment: macOS 27.2 (build 26B118), from `sw_vers`; `python3 -I` is Python 3.14.7 (standard library only).
- Generator: the script at the end of this file. Save it outside the repository as `build_visio_fixtures.py` and run it from the
  repository root:

```sh
python3 -I build_visio_fixtures.py tests/RetroDownfall.Arcanum.Tests/TestData/Comprehension/Visio
```

## hand-built-stencil.vsdx

Three pages and two masters. The stencil label `Process step` lives only on master 2, which is instantiated on pages 1 and
3 (`Master='2'`, no `Text`), never on page 2. Master 3 (`UNUSED-DECISION-LABEL`) is never instantiated. Page 1 also has a
shape whose `HideText` cell is true. Page 3 has a `fld` field value.

Expected projection (D9):

```text
Page: Page-1
Process step
Review the plan
Page: Page-2
Notes without a stencil
Page: Page-3
Process step
Updated: 2026-10-22
```

## hand-built-stencil.vsdm

The same drawing as a macro-enabled package: the document relationships also reference `visio/vbaProject.bin`. The
`vbaProject.bin` here is a stand-in with an OLE2 signature and the marker `VBA-MACRO-MARKER`, which must never reach the
output. (The "never inflated" proof uses an entry whose deflate data is garbage, built in code, because a stand-in that
inflates cleanly could not tell an extractor that reads it from one that does not.)

## build_visio_fixtures.py

```python
# Builds the hand-made MS-VSDX fixtures in tests/RetroDownfall.Arcanum.Tests/TestData/Comprehension/Visio.
# Usage: python3 -I build_visio_fixtures.py <output-directory>
import sys
import zipfile
from xml.sax.saxutils import escape

MAIN = "http://schemas.microsoft.com/office/visio/2012/main"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PKG = "http://schemas.openxmlformats.org/package/2006/relationships"
VIS = "http://schemas.microsoft.com/visio/2010/relationships/"
VBA = "http://schemas.microsoft.com/office/2006/relationships/vbaProject"


def part(root, inner, extra=""):
    return (
        "<?xml version='1.0' encoding='utf-8' ?>"
        f"<{root} xmlns='{MAIN}' xmlns:r='{R}' xml:space='preserve' {extra}>{inner}</{root}>"
    )


def rels(*items):
    body = "".join(f"<Relationship Id='{i}' Type='{t}' Target='{escape(g)}'/>" for i, t, g in items)
    return f"<?xml version='1.0' encoding='UTF-8' standalone='yes'?><Relationships xmlns='{PKG}'>{body}</Relationships>"


def shape(sid, text=None, extra="", cells="", children=""):
    body = f"<Shape ID='{sid}' Type='Shape' {extra}>{cells}"
    if text is not None:
        body += f"<Text>{text}</Text>"
    if children:
        body += f"<Shapes>{children}</Shapes>"
    return body + "</Shape>"


def build(path, macro):
    pages = [
        ("Page-1", shape(1, extra="Master='2'") + shape(2, "Review the plan")
         + shape(3, "Hidden note", cells="<Cell N='HideText' V='1'/>")),
        ("Page-2", shape(1, "Notes without a stencil")),
        ("Page-3", shape(1, extra="Master='2'")
         + shape(2, "<cp IX='0'/>Updated: <fld IX='0' Type='5' Format='0'>2026-10-22</fld>")),
    ]
    entries = [
        ("[Content_Types].xml",
         "<?xml version='1.0' encoding='UTF-8' standalone='yes'?>"
         "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>"
         "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>"
         "<Default Extension='xml' ContentType='application/xml'/>"
         + ("<Default Extension='bin' ContentType='application/vnd.ms-office.vbaProject'/>" if macro else "")
         + "<Override PartName='/visio/document.xml' ContentType='application/vnd.ms-visio.drawing"
         + (".macroEnabled" if macro else "") + ".main+xml'/></Types>"),
        ("_rels/.rels", rels(("rId1", VIS + "document", "visio/document.xml"))),
        ("visio/document.xml", part("VisioDocument", "<DocumentSettings TopPage='0' DefaultTextStyle='3'/>")),
        ("visio/_rels/document.xml.rels", rels(
            ("rId1", VIS + "pages", "pages/pages.xml"),
            ("rId2", VIS + "masters", "masters/masters.xml"),
            ("rId3", VIS + "windows", "windows.xml"),
            *((("rId5", VBA, "vbaProject.bin"),) if macro else ()))),
        ("visio/windows.xml", part("Windows", "<Window ID='0' WindowType='Drawing'>WINDOW-MARKER</Window>")),
        ("visio/pages/pages.xml", part("Pages", "".join(
            f"<Page ID='{n}' Name='{name}' NameU='{name}'><PageSheet><Cell N='PageWidth' V='8.5'/></PageSheet><Rel r:id='rId{n + 1}'/></Page>"
            for n, (name, _) in enumerate(pages)))),
        ("visio/pages/_rels/pages.xml.rels", rels(*((f"rId{n + 1}", VIS + "page", f"page{n + 1}.xml") for n in range(len(pages))))),
        *((f"visio/pages/page{n + 1}.xml", part("PageContents", f"<Shapes>{body}</Shapes>")) for n, (_, body) in enumerate(pages)),
        ("visio/masters/masters.xml", part("Masters",
            "<Master ID='2' NameU='Process' Name='Process'><PageSheet/><Rel r:id='rId1'/></Master>"
            "<Master ID='3' NameU='Decision' Name='Decision'><PageSheet/><Rel r:id='rId2'/></Master>")),
        ("visio/masters/_rels/masters.xml.rels", rels(("rId1", VIS + "master", "master1.xml"), ("rId2", VIS + "master", "master2.xml"))),
        ("visio/masters/master1.xml", part("MasterContents", "<Shapes>" + shape(1, "Process step") + "</Shapes>")),
        ("visio/masters/master2.xml", part("MasterContents", "<Shapes>" + shape(1, "UNUSED-DECISION-LABEL") + "</Shapes>")),
    ]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, content in entries:
            archive.writestr(name, content)
        if macro:
            # A real vbaProject.bin is an OLE2 compound file; this stand-in has its signature and a VBA-looking marker.
            archive.writestr("visio/vbaProject.bin", b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1" + b'Attribute VB_Name = "VBA-MACRO-MARKER"')


output = sys.argv[1]
build(f"{output}/hand-built-stencil.vsdx", macro=False)
build(f"{output}/hand-built-stencil.vsdm", macro=True)
```
