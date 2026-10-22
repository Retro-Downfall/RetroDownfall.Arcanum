# Comprehension real-producer fixtures

Files in this folder were produced by real tools, not hand-built, so the container tests exercise bytes a real
producer writes.

## textutil-basic.odt, textutil-basic.docx

- Tool: macOS `textutil` (the export engine behind TextEdit), `/usr/bin/textutil`.
- macOS: 27.2 (build 26B118), from `sw_vers`.
- Input: a one-paragraph RTF document containing the text `Alpha`. HTML input is not used because `textutil`'s
  HTML import hangs in this environment; every call is wrapped in `timeout 30`.
- Exact commands:

```sh
printf '{\\rtf1\\ansi{\\fonttbl\\f0 Helvetica;}\\f0 Alpha\\par}' > in.rtf
timeout 30 textutil -convert odt  in.rtf -output textutil-basic.odt
timeout 30 textutil -convert docx in.rtf -output textutil-basic.docx
```

What they pin:

- The ODT `META-INF/manifest.xml` begins with `<!DOCTYPE manifest:manifest PUBLIC "-//OpenOffice.org//DTD Manifest 1.0//EN" "Manifest.dtd">`.
  That is why the ODF manifest is read with `DtdProcessing.Ignore` while every content part is read with `Prohibit`.
- The DOCX is a transitional OOXML package whose `word/_rels/document.xml.rels` carries a `../docProps/meta.xml`
  customXml target, so it exercises `..` normalization against a real file. It has no `styles.xml`.
