# RTF fixture provenance

`textutil-basic.rtf` was generated independently of Arcanum's parser with the macOS `/usr/bin/textutil -convert rtf` producer. Its header identifies Apple Cocoa RTF 2908. The plain UTF-8 input was:

```text
Textutil RTF fixture
Visible paragraphs preserve café and Ελληνικά.
Second paragraph contains a tab:   value.
```

There is one literal tab between `:` and `value.` in the input and a final newline. The checked-in producer output contains a Windows-1252 hex escape, scoped Unicode escapes, font/color tables, an unknown starred color destination, and escaped source newlines. Parser tests assert the literal expected plain text, independently of the fixture producer.

To regenerate, save the input above as UTF-8 with the indicated tab, then run:

```sh
textutil -convert rtf -output textutil-basic.rtf input.txt
```

The RTF grammar follows Microsoft's [RTF syntax](https://learn.microsoft.com/en-us/previous-versions/office/developer/office2000/aa140284(v=office.10)) and [RTF reader conventions](https://learn.microsoft.com/en-us/previous-versions/office/developer/office2000/aa140286(v=office.10)). The parser deliberately rejects unsupported encodings and out-of-bound syntax rather than interpreting opaque payloads.
